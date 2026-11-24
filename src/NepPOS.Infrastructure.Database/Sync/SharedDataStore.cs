using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Orders;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Sync;

/// <summary>
/// Applies what the owner's other registers shared through Head Office: customers (the newest edit wins),
/// account lines and gift card movements (added once each, by their event id). Everything in a batch and the
/// cursor are saved together, so a batch is either fully applied or will simply be fetched again.
/// </summary>
public sealed class SharedDataStore : ISharedDataStore
{
    public const string CursorName = "shared";
    public const string WebsiteCursorName = "shared-web";

    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal? _terminal;

    /// <param name="terminal">This register's store, so website orders and transfers for other stores are left alone.</param>
    public SharedDataStore(NepPosDbContext db, RegisterTerminal? terminal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal;
    }

    public async Task<SharedCursor> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        var cursors = await _db.SyncCursors.AsNoTracking()
            .Where(c => c.Name == CursorName || c.Name == WebsiteCursorName)
            .ToDictionaryAsync(c => c.Name, c => c.Value, cancellationToken);
        return new SharedCursor(cursors.GetValueOrDefault(CursorName), cursors.GetValueOrDefault(WebsiteCursorName));
    }

    public async Task<int> ApplyAsync(IReadOnlyList<SharedEvent> events, SharedCursor next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var changed = 0;

        foreach (var shared in events)
        {
            var e = shared.Event;
            var where = shared.FromWebsite ? "Website" : $"{e.StoreId} / {e.RegisterId}";
            try
            {
                changed += e.Type switch
                {
                    SyncEventTypes.CustomerChanged => await ApplyCustomerAsync(Read<CustomerChangedV1>(e), cancellationToken) ? 1 : 0,
                    SyncEventTypes.AccountEntry => await ApplyAccountEntryAsync(Read<AccountEntryV1>(e), where, cancellationToken) ? 1 : 0,
                    SyncEventTypes.GiftCardMoved => await ApplyGiftCardAsync(e.EventId, Read<GiftCardMovedV1>(e), where, cancellationToken) ? 1 : 0,
                    SyncEventTypes.OrderPlaced => await ApplyOrderPlacedAsync(Read<WebOrderPlacedV1>(e), cancellationToken) ? 1 : 0,
                    SyncEventTypes.OrderStatus => await ApplyOrderStatusAsync(Read<WebOrderStatusV1>(e), cancellationToken) ? 1 : 0,
                    SyncEventTypes.TransferSent => await ApplyTransferSentAsync(Read<TransferSentV1>(e), cancellationToken) ? 1 : 0,
                    SyncEventTypes.TransferReceived => await ApplyTransferReceivedAsync(Read<TransferReceivedV1>(e), cancellationToken) ? 1 : 0,
                    _ => 0   // a newer kind of event this version doesn't know: skip it
                };
            }
            catch (JsonException)
            {
                // A payload this version can't read: skip it rather than block everything after it.
            }
        }

        await MoveCursorAsync(CursorName, next.Registers, cancellationToken);
        await MoveCursorAsync(WebsiteCursorName, next.Website, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private async Task MoveCursorAsync(string name, long value, CancellationToken cancellationToken)
    {
        var cursor = await _db.SyncCursors.FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
        if (cursor is null)
            _db.SyncCursors.Add(new SyncCursor { Name = name, Value = value });
        else if (value > cursor.Value)
            cursor.Value = value;
    }

    private async Task<bool> ApplyCustomerAsync(CustomerChangedV1 c, CancellationToken cancellationToken)
    {
        var customer = await FindCustomerAsync(c.CustomerId, cancellationToken);
        var updated = DateTime.SpecifyKind(c.UpdatedUtc, DateTimeKind.Utc);
        if (customer is not null && customer.UpdatedUtc >= updated)
            return false;   // this register already has the same or a newer edit

        if (customer is null)
        {
            customer = new Customer { Id = c.CustomerId, CreatedUtc = DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc) };
            _db.Customers.Add(customer);
        }

        // Two stores could (very rarely) give different customers the same number: keep both, renumber this copy.
        var code = c.Code.Trim().ToUpperInvariant();
        var id = c.CustomerId;
        if (_db.Customers.Local.Any(x => x.Code == code && x.Id != id) ||
            await _db.Customers.AnyAsync(x => x.Code == code && x.Id != id, cancellationToken))
            code = "CUST-" + id.ToString("N")[..8].ToUpperInvariant();
        customer.Code = code;
        customer.Name = c.Name;
        customer.Phone = c.Phone;
        customer.Email = c.Email;
        customer.Company = c.Company;
        customer.Abn = c.Abn;
        customer.Address = c.Address;
        customer.Notes = c.Notes;
        customer.IsAccount = c.IsAccount;
        customer.CreditLimit = c.CreditLimit;
        customer.TermsDays = c.TermsDays;
        customer.IsActive = c.IsActive;
        customer.UpdatedUtc = updated;
        return true;
    }

    private async Task<bool> ApplyAccountEntryAsync(AccountEntryV1 a, string where, CancellationToken cancellationToken)
    {
        if (_db.CustomerAccountEntries.Local.Any(x => x.Id == a.EntryId) ||
            await _db.CustomerAccountEntries.AnyAsync(x => x.Id == a.EntryId, cancellationToken))
            return false;

        // The customer normally arrives first; if not, keep the line on a placeholder until they do.
        var customer = await FindCustomerAsync(a.CustomerId, cancellationToken);
        if (customer is null)
        {
            customer = new Customer
            {
                Id = a.CustomerId,
                Code = "CUST-" + a.CustomerId.ToString("N")[..8].ToUpperInvariant(),
                Name = "Customer from " + where,
                IsAccount = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)   // any real edit replaces this
            };
            _db.Customers.Add(customer);
        }

        _db.CustomerAccountEntries.Add(new CustomerAccountEntry
        {
            Id = a.EntryId,
            CustomerId = a.CustomerId,
            Kind = Enum.TryParse<AccountEntryKind>(a.Kind, out var kind) ? kind : AccountEntryKind.Adjustment,
            Amount = a.Amount,
            SaleId = a.SaleId,
            InvoiceNumber = a.InvoiceNumber,
            Where = where.Length > 100 ? where[..100] : where,
            AtUtc = DateTime.SpecifyKind(a.AtUtc, DateTimeKind.Utc),
            DueUtc = a.DueUtc is { } due ? DateTime.SpecifyKind(due, DateTimeKind.Utc) : null,
            Reference = a.Reference,
            Note = a.Note
        });
        customer.Balance += a.Amount;
        return true;
    }

    private async Task<bool> ApplyGiftCardAsync(Guid eventId, GiftCardMovedV1 g, string where, CancellationToken cancellationToken)
    {
        if (_db.GiftCardMovements.Local.Any(m => m.EventId == eventId) ||
            await _db.GiftCardMovements.AnyAsync(m => m.EventId == eventId, cancellationToken))
            return false;

        var code = g.Code.Trim().ToUpperInvariant();
        var card = _db.GiftCards.Local.FirstOrDefault(c => c.Code == code)
                   ?? await _db.GiftCards.FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
        var at = DateTime.SpecifyKind(g.AtUtc, DateTimeKind.Utc);
        if (card is null)
        {
            card = new GiftCard
            {
                Code = code,
                Kind = Enum.TryParse<GiftCardKind>(g.Kind, out var kind) ? kind : GiftCardKind.GiftCard,
                IssuedUtc = at
            };
            _db.GiftCards.Add(card);
        }

        // Another store's movement is applied even if it takes the card below zero (two stores spent it while
        // offline): the balance then shows what's really owed, and the card can't be spent again here.
        card.Balance += g.Amount;
        if (g.Cancelled)
            card.IsActive = false;
        if (card.LastUsedUtc is null || at > card.LastUsedUtc)
            card.LastUsedUtc = at;
        card.Movements.Add(new GiftCardMovement
        {
            EventId = eventId,
            Amount = g.Amount,
            SaleId = g.SaleId,
            InvoiceNumber = g.InvoiceNumber,
            AtUtc = at,
            Note = g.Note.Length > 200 ? g.Note[..200] : g.Note,
            Where = where.Length > 100 ? where[..100] : where
        });
        return true;
    }

    private bool IsThisStore(string? storeCode) =>
        _terminal is not null && string.Equals(storeCode?.Trim(), _terminal.StoreId, StringComparison.OrdinalIgnoreCase);

    /// <summary>A website order for this store arrives once; for another store it's ignored.</summary>
    private async Task<bool> ApplyOrderPlacedAsync(WebOrderPlacedV1 o, CancellationToken cancellationToken)
    {
        if (!IsThisStore(o.StoreCode) || o.OrderId == Guid.Empty || o.Lines is not { Count: > 0 })
            return false;
        if (_db.WebOrders.Local.Any(w => w.Id == o.OrderId) || await _db.WebOrders.AnyAsync(w => w.Id == o.OrderId, cancellationToken))
            return false;

        var order = new WebOrder
        {
            Id = o.OrderId,
            Reference = Cut(o.Reference, 50) ?? o.OrderId.ToString("N")[..8],
            CustomerName = Cut(o.CustomerName, 200) ?? "Website customer",
            Phone = Cut(o.Phone, 50),
            Email = Cut(o.Email, 200),
            CustomerId = o.CustomerId,
            Fulfilment = string.Equals(o.Fulfilment, Fulfilment.Delivery, StringComparison.OrdinalIgnoreCase) ? Fulfilment.Delivery : Fulfilment.Pickup,
            Address = Cut(o.Address, 500),
            Note = Cut(o.Note, 1000),
            PlacedUtc = DateTime.SpecifyKind(o.PlacedUtc, DateTimeKind.Utc),
            WantedUtc = o.WantedUtc is { } w ? DateTime.SpecifyKind(w, DateTimeKind.Utc) : null,
            Total = o.Total,
            Prepaid = Math.Max(0, o.Prepaid),
            OnAccount = Math.Max(0, o.OnAccount),
            Status = WebOrderStatus.Placed,
            UpdatedUtc = DateTime.UtcNow
        };
        for (var i = 0; i < o.Lines.Count; i++)
        {
            var line = o.Lines[i];
            order.Lines.Add(new WebOrderLine
            {
                LineNumber = i,
                ProductSyncId = line.ProductId,
                Name = Cut(line.Name, 200) ?? "Item",
                Sku = Cut(line.Sku, 50),
                Quantity = Math.Max(0, line.Quantity),
                UnitPrice = line.UnitPrice
            });
        }
        _db.WebOrders.Add(order);
        return true;
    }

    /// <summary>Another register (or the website, for a cancel) moved an order on. A finished order never reopens.</summary>
    private async Task<bool> ApplyOrderStatusAsync(WebOrderStatusV1 s, CancellationToken cancellationToken)
    {
        var order = _db.WebOrders.Local.FirstOrDefault(w => w.Id == s.OrderId)
                    ?? await _db.WebOrders.Include(w => w.Lines).FirstOrDefaultAsync(w => w.Id == s.OrderId, cancellationToken);
        if (order is null || !order.IsOpen || !Enum.TryParse<WebOrderStatus>(s.Status, ignoreCase: true, out var status) || status == order.Status)
            return false;

        order.Status = status;
        order.UpdatedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(s.By) && status is WebOrderStatus.Picking or WebOrderStatus.Ready)
            order.PickedBy = Cut(s.By, 200);
        if (status == WebOrderStatus.Cancelled)
            order.CancelReason = Cut(s.Note, 500);
        if (status == WebOrderStatus.Completed)
            (order.FinalTotal, order.RefundDue, order.InvoiceNumber) = (s.FinalTotal, s.RefundDue, s.InvoiceNumber);
        foreach (var picked in s.Picked ?? Array.Empty<WebOrderPickedV1>())
            if (order.Lines.FirstOrDefault(l => l.LineNumber == picked.Line) is { } line)
                line.Picked = Math.Clamp(picked.Quantity, 0, line.Quantity);
        return true;
    }

    /// <summary>Stock on its way to this store: waits here to be counted in.</summary>
    private async Task<bool> ApplyTransferSentAsync(TransferSentV1 t, CancellationToken cancellationToken)
    {
        if (!IsThisStore(t.ToStore) || t.TransferId == Guid.Empty || t.Lines is not { Count: > 0 })
            return false;
        if (_db.StockTransfers.Local.Any(x => x.Id == t.TransferId) || await _db.StockTransfers.AnyAsync(x => x.Id == t.TransferId, cancellationToken))
            return false;

        var transfer = new StockTransfer
        {
            Id = t.TransferId,
            Number = Cut(t.Number, 50) ?? "TR",
            FromStore = Cut(t.FromStore, 50) ?? "?",
            ToStore = Cut(t.ToStore, 50)!,
            Outgoing = false,
            SentUtc = DateTime.SpecifyKind(t.SentUtc, DateTimeKind.Utc),
            SentBy = Cut(t.By, 200),
            Note = Cut(t.Note, 500)
        };
        for (var i = 0; i < t.Lines.Count; i++)
        {
            var line = t.Lines[i];
            transfer.Lines.Add(new StockTransferLine
            {
                LineNumber = i,
                ProductSyncId = line.ProductId,
                ProductName = Cut(line.Name, 200) ?? "Item",
                Barcode = Cut(line.Barcode, 50),
                Sku = Cut(line.Sku, 50),
                Quantity = line.Quantity,
                UnitCost = line.UnitCost
            });
        }
        _db.StockTransfers.Add(transfer);
        return true;
    }

    /// <summary>
    /// The other store counted a transfer in: the sender sees what arrived; another register of the receiving
    /// store just stops showing it as waiting.
    /// </summary>
    private async Task<bool> ApplyTransferReceivedAsync(TransferReceivedV1 r, CancellationToken cancellationToken)
    {
        var transfer = _db.StockTransfers.Local.FirstOrDefault(x => x.Id == r.TransferId)
                       ?? await _db.StockTransfers.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == r.TransferId, cancellationToken);
        if (transfer is null || transfer.Status == StockTransferStatus.Received)
            return false;

        transfer.Status = StockTransferStatus.Received;
        transfer.ReceivedUtc = DateTime.SpecifyKind(r.ReceivedUtc, DateTimeKind.Utc);
        transfer.ReceivedBy = Cut(r.By, 200);
        foreach (var line in transfer.Lines)
            line.Received = r.Lines?.FirstOrDefault(l => l.Line == line.LineNumber)?.Quantity ?? 0m;
        return true;
    }

    private static string? Cut(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private async Task<Customer?> FindCustomerAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Customers.Local.FirstOrDefault(c => c.Id == id)
        ?? await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    private static T Read<T>(SyncEvent e) =>
        e.Payload.Deserialize<T>(SyncJson.Options) ?? throw new JsonException("Empty payload.");
}
