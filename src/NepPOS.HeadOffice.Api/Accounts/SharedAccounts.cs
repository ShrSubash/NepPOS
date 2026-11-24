using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;

namespace NepPOS.HeadOffice.Api.Accounts;

/// <summary>
/// Keeps Head Office's copy of customers, account lines and gift cards (what the website reads) up to date from
/// the shared events, whether a register or the website sent them. The same rules as a register: customers are
/// replaced only by a newer edit; account and gift card lines are added once each by their id.
/// It runs inside the same SaveChanges as storing the events, so the copy never misses one.
/// </summary>
public static class SharedAccounts
{
    public static bool IsShared(string type) => SyncEventTypes.Shared.Contains(type);

    /// <summary>Checks a shared event's payload can be read. Returns why it's refused, or null.</summary>
    public static string? Check(SyncEvent e)
    {
        if (!IsShared(e.Type))
            return null;
        if (e.Version != 1)
            return $"{e.Type} version {e.Version} isn't supported; this Head Office reads version 1.";
        try
        {
            return e.Type switch
            {
                SyncEventTypes.CustomerChanged when e.Payload.Deserialize<CustomerChangedV1>(SyncJson.Options) is not
                    { CustomerId: var id, Name: { Length: > 0 and <= 200 }, Code: { Length: > 0 and <= 20 } } || id == Guid.Empty
                    => "A customer needs an id, a name (up to 200) and a code (up to 20).",
                SyncEventTypes.AccountEntry when e.Payload.Deserialize<AccountEntryV1>(SyncJson.Options) is not
                    { EntryId: var entry, CustomerId: var customer } || entry == Guid.Empty || customer == Guid.Empty
                    => "An account line needs an entryId and a customerId.",
                SyncEventTypes.GiftCardMoved when e.Payload.Deserialize<GiftCardMovedV1>(SyncJson.Options) is not
                    { Code: { Length: > 0 and <= 20 } }
                    => "A gift card movement needs a code (up to 20).",
                _ => null
            };
        }
        catch (JsonException ex)
        {
            return $"{e.Type} payload can't be read: {ex.Message}";
        }
    }

    /// <summary>Applies one shared event (already checked) to the copy. <paramref name="where"/>: "STORE-001 / REG-001" or "Website".</summary>
    public static async Task ApplyAsync(HeadOfficeDbContext db, Guid tenantId, Guid eventId, string type, JsonElement payload,
        string where, CancellationToken ct)
    {
        where = where.Length > 100 ? where[..100] : where;
        switch (type)
        {
            case SyncEventTypes.CustomerChanged:
                await ApplyCustomerAsync(db, tenantId, payload.Deserialize<CustomerChangedV1>(SyncJson.Options)!, ct);
                break;
            case SyncEventTypes.AccountEntry:
                await ApplyAccountLineAsync(db, tenantId, payload.Deserialize<AccountEntryV1>(SyncJson.Options)!, where, ct);
                break;
            case SyncEventTypes.GiftCardMoved:
                await ApplyGiftCardAsync(db, tenantId, eventId, payload.Deserialize<GiftCardMovedV1>(SyncJson.Options)!, where, ct);
                break;
        }
    }

    /// <summary>
    /// When the copy is empty but shared events exist (a Head Office from before this version), builds it once
    /// from every stored event, oldest first.
    /// </summary>
    public static async Task RebuildIfEmptyAsync(HeadOfficeDbContext db, CancellationToken ct = default)
    {
        if (await db.SharedCustomers.AnyAsync(ct) || await db.SharedGiftCards.AnyAsync(ct))
            return;

        var types = SyncEventTypes.Shared.ToList();
        var events = await db.ReceivedEvents.AsNoTracking()
            .Where(e => types.Contains(e.Type))
            .OrderBy(e => e.Id)
            .Join(db.Registers.AsNoTracking(), e => e.RegisterId, r => r.Id, (e, r) => new { e.TenantId, e.EventId, e.Type, e.Payload, r.RegisterCode, r.StoreId })
            .Join(db.Stores.AsNoTracking(), x => x.StoreId, s => s.Id, (x, s) => new { x.TenantId, x.EventId, x.Type, x.Payload, Where = s.StoreCode + " / " + x.RegisterCode })
            .ToListAsync(ct);
        if (events.Count == 0)
            return;

        foreach (var e in events)
        {
            using var doc = JsonDocument.Parse(e.Payload);
            try { await ApplyAsync(db, e.TenantId, e.EventId, e.Type, doc.RootElement, e.Where, ct); }
            catch (JsonException) { /* an unreadable old event: skip it */ }
        }
        await db.SaveChangesAsync(ct);
    }

    public static async Task<SharedCustomer?> FindCustomerAsync(HeadOfficeDbContext db, Guid tenantId, Guid id, CancellationToken ct) =>
        db.SharedCustomers.Local.FirstOrDefault(c => c.TenantId == tenantId && c.CustomerId == id)
        ?? await db.SharedCustomers.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CustomerId == id, ct);

    public static async Task<SharedGiftCard?> FindGiftCardAsync(HeadOfficeDbContext db, Guid tenantId, string code, CancellationToken ct) =>
        db.SharedGiftCards.Local.FirstOrDefault(g => g.TenantId == tenantId && g.Code == code)
        ?? await db.SharedGiftCards.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Code == code, ct);

    private static async Task ApplyCustomerAsync(HeadOfficeDbContext db, Guid tenantId, CustomerChangedV1 c, CancellationToken ct)
    {
        var updated = DateTime.SpecifyKind(c.UpdatedUtc, DateTimeKind.Utc);
        var customer = await FindCustomerAsync(db, tenantId, c.CustomerId, ct);
        if (customer is not null && customer.UpdatedUtc >= updated)
            return;
        if (customer is null)
        {
            customer = new SharedCustomer { TenantId = tenantId, CustomerId = c.CustomerId, CreatedUtc = DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc) };
            db.SharedCustomers.Add(customer);
        }

        customer.Code = c.Code.Trim().ToUpperInvariant();
        customer.Name = c.Name;
        customer.Email = string.IsNullOrWhiteSpace(c.Email) ? null : c.Email.Trim().ToLowerInvariant();
        customer.Phone = c.Phone;
        customer.IsAccount = c.IsAccount;
        customer.CreditLimit = c.CreditLimit;
        customer.TermsDays = c.TermsDays;
        customer.IsActive = c.IsActive;
        customer.UpdatedUtc = updated;
    }

    private static async Task ApplyAccountLineAsync(HeadOfficeDbContext db, Guid tenantId, AccountEntryV1 a, string where, CancellationToken ct)
    {
        if (db.SharedAccountLines.Local.Any(l => l.EntryId == a.EntryId) ||
            await db.SharedAccountLines.AnyAsync(l => l.EntryId == a.EntryId, ct))
            return;

        var customer = await FindCustomerAsync(db, tenantId, a.CustomerId, ct);
        if (customer is null)
        {
            // The customer normally arrives first; keep the line on a placeholder until they do.
            customer = new SharedCustomer
            {
                TenantId = tenantId, CustomerId = a.CustomerId, Code = "CUST-" + a.CustomerId.ToString("N")[..8].ToUpperInvariant(),
                Name = "Customer from " + where, IsAccount = true, CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
            };
            db.SharedCustomers.Add(customer);
        }

        db.SharedAccountLines.Add(new SharedAccountLine
        {
            EntryId = a.EntryId,
            TenantId = tenantId,
            CustomerId = a.CustomerId,
            Kind = a.Kind.Length > 20 ? a.Kind[..20] : a.Kind,
            Amount = a.Amount,
            InvoiceNumber = a.InvoiceNumber,
            AtUtc = DateTime.SpecifyKind(a.AtUtc, DateTimeKind.Utc),
            DueUtc = a.DueUtc is { } due ? DateTime.SpecifyKind(due, DateTimeKind.Utc) : null,
            Where = where,
            Note = a.Note is { Length: > 300 } note ? note[..300] : a.Note
        });
        customer.Balance += a.Amount;
    }

    private static async Task ApplyGiftCardAsync(HeadOfficeDbContext db, Guid tenantId, Guid eventId, GiftCardMovedV1 g, string where, CancellationToken ct)
    {
        if (db.SharedGiftCardLines.Local.Any(l => l.EventId == eventId) ||
            await db.SharedGiftCardLines.AnyAsync(l => l.EventId == eventId, ct))
            return;

        var code = g.Code.Trim().ToUpperInvariant();
        var at = DateTime.SpecifyKind(g.AtUtc, DateTimeKind.Utc);
        var card = await FindGiftCardAsync(db, tenantId, code, ct);
        if (card is null)
        {
            card = new SharedGiftCard { TenantId = tenantId, Code = code, Kind = g.Kind == "StoreCredit" ? "StoreCredit" : "GiftCard" };
            db.SharedGiftCards.Add(card);
        }

        card.Balance += g.Amount;
        if (g.Cancelled)
            card.IsActive = false;
        if (card.LastUsedUtc is null || at > card.LastUsedUtc)
            card.LastUsedUtc = at;

        db.SharedGiftCardLines.Add(new SharedGiftCardLine
        {
            EventId = eventId,
            TenantId = tenantId,
            Code = code,
            Amount = g.Amount,
            AtUtc = at,
            Note = g.Note is { Length: > 200 } note ? note[..200] : g.Note,
            Where = where
        });
    }
}
