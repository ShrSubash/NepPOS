using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Orders;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Orders;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Endpoints;

/// <summary>
/// Website orders (website key): the website sends an order for one of its stores, can cancel it until the store
/// starts picking, and reads back where each order is up to. Orders reach the store's registers as <c>order.placed</c>.
/// </summary>
public static class WebsiteOrderEndpoints
{
    private const int MaxChanges = 200;

    public static void MapWebsiteOrderEndpoints(this RouteGroupBuilder website)
    {
        website.MapPost("/orders", PlaceAsync);
        website.MapGet("/orders/changes", ChangesAsync);
        website.MapGet("/orders/{id:guid}", GetAsync);
        website.MapPost("/orders/{id:guid}/cancel", CancelAsync);
    }

    internal static async Task<IResult> PlaceAsync(PlaceWebOrderRequest request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var problems = Validate(request);
        if (problems.Count > 0)
            return Results.ValidationProblem(problems);

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId && s.TenantId == tenantId, ct);
        if (store is null)
            return Results.NotFound();

        // Sent before (a retry): the order as it is now.
        var existing = await db.WebOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == request.OrderId, ct);
        if (existing is not null)
            return existing.TenantId == tenantId ? Results.Ok(await ViewAsync(db, existing, ct)) : Results.Conflict();

        if (request.OnAccount > 0)
        {
            var customer = await db.SharedCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CustomerId == request.CustomerId, ct);
            if (customer is not { IsAccount: true, IsActive: true })
                return Results.Problem("This customer can't buy on account.", statusCode: 409);
            if (customer.CreditLimit > 0)
            {
                var waiting = (await db.WebOrders.AsNoTracking()
                        .Where(o => o.TenantId == tenantId && o.CustomerId == customer.CustomerId)
                        .Select(o => new { o.Status, o.OnAccount })
                        .ToListAsync(ct))
                    .Where(o => !WebOrderProjection.IsFinished(o.Status))
                    .Sum(o => o.OnAccount);
                var available = customer.CreditLimit - customer.Balance - waiting;
                if (request.OnAccount > available)
                    return Results.Problem($"That's over the account's limit: {Math.Max(0, available):0.00} available.", statusCode: 409);
            }
        }

        var now = DateTime.UtcNow;
        var placed = new WebOrderPlacedV1(request.OrderId, store.StoreCode, request.Reference.Trim(), request.CustomerName.Trim(), request.Phone,
            request.Email, request.CustomerId, request.Fulfilment, request.Address, request.Note, now, request.WantedUtc, request.Lines,
            request.Total, request.Prepaid, request.OnAccount);
        var row = new WebOrderRow
        {
            OrderId = request.OrderId,
            TenantId = tenantId,
            StoreId = store.Id,
            Reference = placed.Reference.Length > 50 ? placed.Reference[..50] : placed.Reference,
            Status = WebOrderStatuses.Placed,
            Total = request.Total,
            OnAccount = request.OnAccount,
            CustomerId = request.CustomerId,
            PlacedUtc = now,
            UpdatedUtc = now
        };
        db.WebOrders.Add(row);
        db.WebOrderChanges.Add(new WebOrderChange { TenantId = tenantId, OrderId = row.OrderId });
        db.WebsiteEvents.Add(new WebsiteEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = tenantId,
            WebsiteKeyId = WebsiteApiKeyHandler.KeyOf(http.User),
            Type = SyncEventTypes.OrderPlaced,
            Version = WebOrderPlacedV1.Version,
            OccurredAtUtc = now,
            Payload = JsonSerializer.Serialize(placed, SyncJson.Options)
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, row, ct));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var row = await db.WebOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == id && o.TenantId == tenantId, ct);
        return row is null ? Results.NotFound() : Results.Ok(await ViewAsync(db, row, ct));
    }

    /// <summary>Orders that changed after change number <paramref name="after"/>, each once with its latest state.</summary>
    internal static async Task<IResult> ChangesAsync(long? after, int? limit, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var from = Math.Max(0, after ?? 0);
        var take = Math.Clamp(limit ?? MaxChanges, 1, MaxChanges);

        var changes = await db.WebOrderChanges.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id > from)
            .OrderBy(c => c.Id)
            .Take(take + 1)
            .ToListAsync(ct);
        var more = changes.Count > take;
        changes = changes.Take(take).ToList();

        var latest = changes.GroupBy(c => c.OrderId).ToDictionary(g => g.Key, g => g.Max(c => c.Id));
        var ids = latest.Keys.ToList();
        var rows = await db.WebOrders.AsNoTracking().Where(o => ids.Contains(o.OrderId)).ToListAsync(ct);
        var views = rows.Select(o => WebOrderProjection.View(o, latest[o.OrderId])).OrderBy(v => v.ChangeNumber).ToList();
        return Results.Ok(new WebOrderChanges(views, changes.Count > 0 ? changes[^1].Id : from, more));
    }

    /// <summary>The customer changed their mind: refused once the store has started picking.</summary>
    internal static async Task<IResult> CancelAsync(Guid id, CancelWebOrderRequest? request, HttpContext http, HeadOfficeDbContext db, CancellationToken ct)
    {
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var row = await db.WebOrders.FirstOrDefaultAsync(o => o.OrderId == id && o.TenantId == tenantId, ct);
        if (row is null)
            return Results.NotFound();
        if (row.Status == WebOrderStatuses.Cancelled)
            return Results.Ok(await ViewAsync(db, row, ct));
        if (row.Status != WebOrderStatuses.Placed)
            return Results.Problem($"The store has already started on this order ({row.Status.ToLowerInvariant()}). Ask the store to cancel it.", statusCode: 409);

        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "Cancelled on the website" : request!.Reason!.Trim();
        var status = new WebOrderStatusV1(id, WebOrderStatuses.Cancelled, DateTime.UtcNow, Note: reason.Length > 500 ? reason[..500] : reason, By: "Website");
        db.WebsiteEvents.Add(new WebsiteEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = tenantId,
            WebsiteKeyId = WebsiteApiKeyHandler.KeyOf(http.User),
            Type = SyncEventTypes.OrderStatus,
            Version = WebOrderStatusV1.Version,
            OccurredAtUtc = status.AtUtc,
            Payload = JsonSerializer.Serialize(status, SyncJson.Options)
        });
        await WebOrderProjection.ApplyAsync(db, tenantId, status, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, row, ct));
    }

    private static async Task<WebOrderView> ViewAsync(HeadOfficeDbContext db, WebOrderRow row, CancellationToken ct)
    {
        var change = await db.WebOrderChanges.AsNoTracking().Where(c => c.OrderId == row.OrderId).MaxAsync(c => (long?)c.Id, ct) ?? 0;
        return WebOrderProjection.View(row, change);
    }

    private static Dictionary<string, string[]> Validate(PlaceWebOrderRequest r)
    {
        var problems = new Dictionary<string, string[]>();
        void Add(string key, string message) => problems[key] = new[] { message };

        if (r.OrderId == Guid.Empty) Add("orderId", "Send the order's id (a GUID that stays the same if you send it again).");
        if (string.IsNullOrWhiteSpace(r.Reference) || r.Reference.Length > 50) Add("reference", "The order number, up to 50 characters.");
        if (string.IsNullOrWhiteSpace(r.CustomerName)) Add("customerName", "Who the order is for.");
        if (r.Fulfilment is not (Fulfilment.Pickup or Fulfilment.Delivery)) Add("fulfilment", "\"Pickup\" or \"Delivery\".");
        if (r.Lines is not { Count: > 0 } || r.Lines.Count > PlaceWebOrderRequest.MaxLines)
            Add("lines", $"Between 1 and {PlaceWebOrderRequest.MaxLines} lines.");
        else if (r.Lines.Any(l => l.Quantity < 1 || l.UnitPrice < 0 || decimal.Round(l.UnitPrice, 2) != l.UnitPrice || string.IsNullOrWhiteSpace(l.Name)))
            Add("lines", "Every line needs a name, a quantity of 1 or more and a price in cents.");
        else if (Math.Abs(r.Lines.Sum(l => l.Quantity * l.UnitPrice) - r.Total) > 0.01m)
            Add("total", "The total must be the sum of the lines (put fees in as lines).");
        if (r.Prepaid < 0 || r.OnAccount < 0 || r.Prepaid + r.OnAccount > r.Total + 0.01m)
            Add("prepaid", "Prepaid and on account can't be negative or add up to more than the total.");
        if (r.OnAccount > 0 && r.CustomerId is null)
            Add("customerId", "An order on account needs the NepPOS customer.");
        return problems;
    }
}
