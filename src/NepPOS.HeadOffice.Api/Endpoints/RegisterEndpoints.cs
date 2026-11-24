using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Accounts;
using NepPOS.HeadOffice.Api.Catalog;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Licensing;
using NepPOS.HeadOffice.Api.Orders;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>What a register calls: activate once, then send its outbox events and a heartbeat.</summary>
public static class RegisterEndpoints
{
    public static void MapRegisterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/activate", ActivateAsync).WithTags("Registers");

        var signedIn = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Registers");
        signedIn.MapPost("/sync/events", ReceiveEventsAsync);
        signedIn.MapPost("/heartbeat", HeartbeatAsync);
        signedIn.MapGet("/sync/shared", SharedAsync);
        signedIn.MapGet("/sync/stores", StoresAsync);
    }

    /// <summary>
    /// The owner's shared events (customers, account lines, gift card movements) from their other registers after
    /// Head Office's number <paramref name="after"/>, and from their website after <paramref name="afterWeb"/>,
    /// oldest first. Each register keeps its own place.
    /// </summary>
    /// <summary>The owner's stores (code and name), for sending stock to another store.</summary>
    internal static async Task<IResult> StoresAsync(HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var caller = RegisterIdentity.From(http.User);
        var stores = await db.Stores.AsNoTracking()
            .Where(s => s.TenantId == caller.TenantId)
            .OrderBy(s => s.StoreCode)
            .Select(s => new NepPOS.Contracts.Orders.StoreRef(s.StoreCode, s.Name))
            .ToListAsync(ct);
        return Results.Ok(stores);
    }

    internal static async Task<IResult> SharedAsync(long? after, long? afterWeb, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var caller = RegisterIdentity.From(http.User);
        var from = Math.Max(0, after ?? 0);
        var fromWeb = Math.Max(0, afterWeb ?? 0);
        var types = SyncEventTypes.Shared.ToList();

        // Read up to the newest number now, so an event arriving meanwhile is picked up next time, never skipped.
        var newest = await db.ReceivedEvents.AsNoTracking().Where(e => e.TenantId == caller.TenantId).MaxAsync(e => (long?)e.Id, ct) ?? 0;

        var rows = await db.ReceivedEvents.AsNoTracking()
            .Where(e => e.TenantId == caller.TenantId && e.Id > from && e.Id <= newest
                        && e.RegisterId != caller.RegisterId && types.Contains(e.Type))
            .OrderBy(e => e.Id)
            .Take(SharedEventsResponse.MaxEvents + 1)
            .Join(db.Registers.AsNoTracking(), e => e.RegisterId, r => r.Id, (e, r) => new { Event = e, r.RegisterCode, r.StoreId })
            .Join(db.Stores.AsNoTracking(), x => x.StoreId, s => s.Id, (x, s) => new { x.Event, x.RegisterCode, s.StoreCode })
            .ToListAsync(ct);

        var more = rows.Count > SharedEventsResponse.MaxEvents;
        var page = rows.Take(SharedEventsResponse.MaxEvents).OrderBy(x => x.Event.Id).ToList();
        var events = page
            .Select(x => new SharedEvent(x.Event.Id, new SyncEvent(
                x.Event.EventId, x.Event.Type, x.Event.Version, x.StoreCode, x.RegisterCode, x.Event.Sequence,
                DateTime.SpecifyKind(x.Event.OccurredAtUtc, DateTimeKind.Utc),
                System.Text.Json.JsonDocument.Parse(x.Event.Payload).RootElement.Clone())))
            .ToList();

        // When everything fitted, move the caller past events it doesn't need (its own, sales…) up to the newest.
        var lastId = more ? events[^1].Id : newest;

        // The website's changes (sign-ups, gift cards spent online), numbered separately.
        var web = await db.WebsiteEvents.AsNoTracking()
            .Where(e => e.TenantId == caller.TenantId && e.Id > fromWeb)
            .OrderBy(e => e.Id)
            .Take(SharedEventsResponse.MaxEvents + 1)
            .ToListAsync(ct);
        var moreWeb = web.Count > SharedEventsResponse.MaxEvents;
        web = web.Take(SharedEventsResponse.MaxEvents).ToList();
        events.AddRange(web.Select(w => new SharedEvent(w.Id, new SyncEvent(
            w.EventId, w.Type, w.Version, "WEB", "WEBSITE", w.Id, DateTime.SpecifyKind(w.OccurredAtUtc, DateTimeKind.Utc),
            System.Text.Json.JsonDocument.Parse(w.Payload).RootElement.Clone()), FromWebsite: true)));
        var lastWebId = web.Count > 0 ? web[^1].Id : fromWeb;

        return Results.Ok(new SharedEventsResponse(events, Math.Max(from, lastId), more || moreWeb, lastWebId));
    }

    /// <summary>
    /// Swaps a one-time activation code for this register's API key. A code works once and only
    /// before it expires; re-activating a register code that is already in use is refused until
    /// the old one is revoked, so a second PC can't take over a register by accident.
    /// </summary>
    internal static async Task<IResult> ActivateAsync(ActivateRequest request, HeadOfficeDbContext db, LicenceService licences, CancellationToken ct)
    {
        var registerCode = (request.RegisterCode ?? "").Trim().ToUpperInvariant();
        if (registerCode.Length is 0 or > 50)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["registerCode"] = new[] { "Enter the register code, e.g. REG-001." } });

        var codeHash = Secrets.Hash(Secrets.NormaliseCode(request.Code));
        var activation = await db.ActivationCodes.FirstOrDefaultAsync(a => a.CodeHash == codeHash, ct);
        if (activation is null || activation.UsedUtc is not null || activation.ExpiresUtc < DateTime.UtcNow)
            return Results.Problem("That activation code is wrong, already used or expired. Ask for a new one.", statusCode: 400);

        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == activation.StoreId, ct);

        // Checked before the code is used, so a mix-up doesn't waste it.
        if (!string.IsNullOrWhiteSpace(request.StoreCode) &&
            !string.Equals(request.StoreCode.Trim(), store.StoreCode, StringComparison.OrdinalIgnoreCase))
            return Results.Problem($"This code is for store {store.StoreCode} ({store.Name}), but this register is set up as {request.StoreCode.Trim().ToUpperInvariant()}.", statusCode: 409);

        if (await db.Registers.AnyAsync(r => r.StoreId == activation.StoreId && r.RegisterCode == registerCode && !r.IsRevoked, ct))
            return Results.Problem($"{registerCode} is already active at this store. Revoke it first, or use another register code.", statusCode: 409);

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == activation.TenantId, ct);

        // The licence must be in force and have a register to spare. Checked before the code is used.
        var licence = await licences.GetOrStartTrialAsync(tenant.Id, ct);
        var state = LicenceService.StateOf(licence, tenant.Name, DateTime.UtcNow);
        if (state.IsLocked)
            return Results.Problem($"{state.Headline}. Registers can't be activated until it's renewed in NepPOS Portal.", statusCode: 403);
        var inUse = await db.Registers.CountAsync(r => r.TenantId == tenant.Id && !r.IsRevoked, ct);
        if (inUse >= licence.MaxRegisters)
            return Results.Problem($"{tenant.Name}'s plan allows {licence.MaxRegisters} {(licence.MaxRegisters == 1 ? "register" : "registers")} and all are in use. Revoke one that's no longer used, or change the plan.", statusCode: 409);

        var apiKey = Secrets.NewApiKey();
        db.Registers.Add(new Register
        {
            TenantId = activation.TenantId,
            StoreId = activation.StoreId,
            RegisterCode = registerCode,
            DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? null : request.DeviceName.Trim()[..Math.Min(100, request.DeviceName.Trim().Length)],
            ApiKeyHash = Secrets.Hash(apiKey),
            ApiKeyPrefix = apiKey[..10]
        });
        activation.UsedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var (signed, publicKey) = await licences.SignAsync(licence, tenant.Name, ct);
        return Results.Ok(new ActivateResponse(tenant.Id, tenant.Name, store.Id, store.StoreCode, store.Name, registerCode, apiKey, signed, publicKey));
    }

    /// <summary>
    /// Stores each event once. A repeat of an event Head Office already has is reported as a
    /// duplicate (so the register can mark it sent); anything for another store or register, or
    /// reusing a sequence number with a different event, is rejected with the reason.
    /// </summary>
    internal static async Task<IResult> ReceiveEventsAsync(SyncEventsRequest request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var caller = RegisterIdentity.From(http.User);
        var events = request.Events ?? Array.Empty<SyncEvent>();
        if (events.Count > SyncEventsRequest.MaxEvents)
            return Results.Problem($"Send at most {SyncEventsRequest.MaxEvents} events at a time.", statusCode: 413);

        var accepted = new List<Guid>();
        var duplicates = new List<Guid>();
        var rejected = new List<RejectedEvent>();
        var productChanges = new List<(ReceivedEvent Row, ProductChangedV1 Product)>();
        var sharedChanges = new List<SyncEvent>();
        var stockChanges = new List<(ReceivedEvent Row, StockChangedV1 Stock)>();
        var orderChanges = new List<NepPOS.Contracts.Orders.WebOrderStatusV1>();

        var eventIds = events.Select(e => e.EventId).Distinct().ToList();
        var sequences = events.Select(e => e.Sequence).Distinct().ToList();

        // What Head Office already holds, in two queries rather than one per event.
        var known = await db.ReceivedEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.EventId))
            .Select(e => new { e.EventId, e.RegisterId })
            .ToListAsync(ct);
        var usedSequences = (await db.ReceivedEvents.AsNoTracking()
                .Where(e => e.RegisterId == caller.RegisterId && sequences.Contains(e.Sequence))
                .Select(e => e.Sequence)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var e in events)
        {
            var existing = known.FirstOrDefault(k => k.EventId == e.EventId);
            if (existing is not null)
            {
                if (existing.RegisterId == caller.RegisterId) duplicates.Add(e.EventId);
                else rejected.Add(new RejectedEvent(e.EventId, "This event id belongs to another register."));
                continue;
            }

            ProductChangedV1? product = null;
            var productProblem = e.Type == SyncEventTypes.ProductChanged ? CatalogProjection.Read(e, out product) : null;
            var sharedProblem = SharedAccounts.Check(e);
            StockChangedV1? stock = null;
            string? stockProblem = null;
            if (e.Type == SyncEventTypes.StockChanged)
            {
                try { stock = e.Payload.Deserialize<StockChangedV1>(SyncJson.Options); }
                catch (System.Text.Json.JsonException ex) { stockProblem = $"Stock payload can't be read: {ex.Message}"; }
                if (stockProblem is null && (stock is null || stock.ProductId == Guid.Empty))
                    stockProblem = "A stock change needs a productId.";
            }
            var orderProblem = WebOrderProjection.Read(e, out var orderStatus);

            var problem = e switch
            {
                _ when e.EventId == Guid.Empty => "Missing eventId.",
                _ when !string.Equals(e.StoreId, caller.StoreCode, StringComparison.OrdinalIgnoreCase) => $"Event is for store {e.StoreId}, but this key belongs to {caller.StoreCode}.",
                _ when !string.Equals(e.RegisterId, caller.RegisterCode, StringComparison.OrdinalIgnoreCase) => $"Event is for register {e.RegisterId}, but this key belongs to {caller.RegisterCode}.",
                _ when string.IsNullOrWhiteSpace(e.Type) || e.Type.Length > 50 => "Missing or too long event type.",
                _ when e.Version < 1 => "Version must be 1 or more.",
                _ when e.Sequence < 1 => "Sequence must be 1 or more.",
                _ when e.Payload.ValueKind != System.Text.Json.JsonValueKind.Object => "Payload must be a JSON object.",
                _ when usedSequences.Contains(e.Sequence) => $"Sequence {e.Sequence} is already used by a different event from this register.",
                _ when productProblem is not null => productProblem,
                _ when sharedProblem is not null => sharedProblem,
                _ when stockProblem is not null => stockProblem,
                _ when orderProblem is not null => orderProblem,
                _ => null
            };
            if (problem is not null)
            {
                rejected.Add(new RejectedEvent(e.EventId, problem));
                continue;
            }

            var row = new ReceivedEvent
            {
                EventId = e.EventId,
                TenantId = caller.TenantId,
                StoreId = caller.StoreId,
                RegisterId = caller.RegisterId,
                Type = e.Type,
                Version = e.Version,
                Sequence = e.Sequence,
                OccurredAtUtc = DateTime.SpecifyKind(e.OccurredAtUtc, DateTimeKind.Utc),
                Payload = e.Payload.GetRawText()
            };
            db.ReceivedEvents.Add(row);
            if (product is not null)
                productChanges.Add((row, product));
            if (SharedAccounts.IsShared(e.Type))
                sharedChanges.Add(e);
            if (stock is not null)
                stockChanges.Add((row, stock));
            if (orderStatus is not null)
                orderChanges.Add(orderStatus);
            usedSequences.Add(e.Sequence);
            known.Add(new { e.EventId, caller.RegisterId });
            accepted.Add(e.EventId);
        }

        // The store's catalog changes in the same save as the events, so it can't fall behind them.
        await CatalogProjection.ApplyAsync(db, caller, productChanges, ct);
        // Ids first, so each stock level can remember the event that set it.
        if (stockChanges.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var (row, stock) in stockChanges)
            {
                var level = db.StockLevels.Local.FirstOrDefault(s => s.StoreId == caller.StoreId && s.ProductId == stock.ProductId)
                            ?? await db.StockLevels.FirstOrDefaultAsync(s => s.StoreId == caller.StoreId && s.ProductId == stock.ProductId, ct);
                if (level is null)
                {
                    level = new StockLevelRow { TenantId = caller.TenantId, StoreId = caller.StoreId, ProductId = stock.ProductId };
                    db.StockLevels.Add(level);
                }
                else if (level.UpdatedUtc > row.OccurredAtUtc)
                {
                    continue;   // an older count arriving late
                }
                level.StockOnHand = stock.StockOnHand;
                level.UpdatedUtc = row.OccurredAtUtc;
                level.LastEventId = row.Id;
            }
        }
        foreach (var status in orderChanges)
            await WebOrderProjection.ApplyAsync(db, caller.TenantId, status, ct);
        foreach (var shared in sharedChanges)
            await SharedAccounts.ApplyAsync(db, caller.TenantId, shared.EventId, shared.Type, shared.Payload,
                $"{caller.StoreCode} / {caller.RegisterCode}", ct);
        await db.SaveChangesAsync(ct);

        var lastSequence = await db.ReceivedEvents.AsNoTracking()
            .Where(e => e.RegisterId == caller.RegisterId)
            .MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        return Results.Ok(new SyncEventsResponse(accepted, duplicates, rejected, lastSequence));
    }

    /// <summary>Marks the register online and hands it a freshly signed licence (so renewals and suspensions arrive within a minute).</summary>
    internal static async Task<IResult> HeartbeatAsync(HttpContext http, HeadOfficeDbContext db, LicenceService licences, CancellationToken ct)
    {
        var caller = RegisterIdentity.From(http.User);
        var now = DateTime.UtcNow;

        await db.Registers.Where(r => r.Id == caller.RegisterId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastSeenUtc, now), ct);

        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == caller.TenantId).Select(t => t.Name).FirstAsync(ct);
        var licence = await licences.GetOrStartTrialAsync(caller.TenantId, ct);
        var (signed, publicKey) = await licences.SignAsync(licence, tenantName, ct);

        return Results.Ok(new HeartbeatResponse(now, signed, publicKey));
    }
}
