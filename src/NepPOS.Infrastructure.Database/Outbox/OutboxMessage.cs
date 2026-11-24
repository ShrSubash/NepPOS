using System.Text.Json;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Outbox;

/// <summary>
/// One event waiting to go to Head Office. It is written in the same database transaction as
/// the sale or journal entry it describes, so the two can never disagree: if the sale is saved,
/// its event is too, and if the save fails, neither exists.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// Database row number. Rows are never deleted and a failed save leaves no row behind,
    /// so it counts 1, 2, 3… with no gaps: it is sent as the event's <c>Sequence</c>.
    /// </summary>
    public long Id { get; private set; }

    public Guid EventId { get; private set; }
    public string Type { get; private set; } = null!;
    public int Version { get; private set; }
    public string StoreId { get; private set; } = null!;
    public string RegisterId { get; private set; } = null!;
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>The event's payload as JSON (camelCase, see <see cref="SyncJson"/>).</summary>
    public string Payload { get; private set; } = null!;

    /// <summary>When Head Office confirmed it. Null = still to send (integration build step 3).</summary>
    public DateTime? SentAtUtc { get; private set; }

    /// <summary>When Head Office refused it (wrong store, reused sequence…). Not sent again; see LastError.</summary>
    public DateTime? RejectedAtUtc { get; private set; }

    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    // EF Core requires a parameterless constructor for materialization.
    private OutboxMessage() { }

    private OutboxMessage(string type, int version, string storeId, string registerId, DateTime occurredAtUtc, string payload)
    {
        EventId = Guid.NewGuid();
        Type = type;
        Version = version;
        StoreId = storeId;
        RegisterId = registerId;
        OccurredAtUtc = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc);
        Payload = payload;
    }

    /// <summary>The <c>sale.completed</c> event for a completed sale.</summary>
    public static OutboxMessage SaleCompleted(SaleTransaction sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        if (sale.Status != SaleTransactionStatus.Completed || sale.InvoiceNumber is not { } invoiceNumber)
            throw new InvalidOperationException("Only a completed sale with an invoice number is sent to Head Office.");

        static decimal Cents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

        var payload = new SaleCompletedV1(
            sale.Id,
            invoiceNumber,
            sale.CreatedAtUtc,
            sale.EmployeeId,
            sale.EmployeeName,
            Cents(sale.GrandTotal),
            Cents(sale.GstTotal),
            sale.CashRounding,
            sale.LineItems
                .Select(li => new SaleLineV1(li.Sku, li.ProductName, li.Category, li.Quantity,
                    li.UnitPriceIncGst, li.GstRate, Cents(li.LineTotalIncGst)))
                .ToList(),
            sale.Payments
                .Select(p => new SalePaymentV1(p.TenderType.ToString(), p.Amount, p.Tendered, p.Change,
                    p.AuthCode, p.CardType, p.MaskedPan, p.TenderName))
                .ToList(),
            sale.ReturnOfInvoice);

        return new OutboxMessage(SyncEventTypes.SaleCompleted, SaleCompletedV1.Version,
            sale.StoreId, sale.RegisterId, DateTime.UtcNow, JsonSerializer.Serialize(payload, SyncJson.Options));
    }

    /// <summary>The <c>journal.recorded</c> event for a journal line.</summary>
    public static OutboxMessage JournalRecorded(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = new JournalRecordedV1(
            entry.Id,
            entry.Sequence,
            entry.EventType.ToString(),
            entry.Detail,
            entry.EmployeeId,
            entry.EmployeeName,
            entry.SaleId,
            entry.InvoiceNumber,
            entry.Amount);

        return new OutboxMessage(SyncEventTypes.JournalRecorded, JournalRecordedV1.Version,
            entry.StoreId, entry.RegisterId, entry.OccurredAtUtc, JsonSerializer.Serialize(payload, SyncJson.Options));
    }

    /// <summary>The <c>product.changed</c> event: the whole product as it is now, with its pack sizes.</summary>
    public static OutboxMessage ProductChanged(Product product, IEnumerable<ProductVariant> variants, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(variants);
        if (product.SyncId == Guid.Empty)
            throw new InvalidOperationException("A product needs a SyncId before it is sent to Head Office.");

        var payload = new ProductChangedV1(
            product.SyncId,
            product.Name,
            product.Category,
            product.Price,
            Blank(product.Barcode),
            Blank(product.Plu),
            product.EnableOnWebsite,
            product.IsActive,
            variants
                .Select(v => new ProductVariantV1(v.PackType.ToString(), Blank(v.Barcode), Blank(v.Plu), v.UnitQuantity, v.Price, v.IsActive))
                .ToList());

        return new OutboxMessage(SyncEventTypes.ProductChanged, ProductChangedV1.Version,
            storeId, registerId, DateTime.UtcNow, JsonSerializer.Serialize(payload, SyncJson.Options));

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The <c>stock.changed</c> event: a product's stock on hand at this store now (for the website).</summary>
    public static OutboxMessage StockChanged(Product product, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(product);
        var payload = new StockChangedV1(product.SyncId, product.StockOnHand);
        return new OutboxMessage(SyncEventTypes.StockChanged, StockChangedV1.Version,
            storeId, registerId, DateTime.UtcNow, JsonSerializer.Serialize(payload, SyncJson.Options));
    }

    /// <summary>The <c>customer.changed</c> event: the whole customer as it is now (shared with every register).</summary>
    public static OutboxMessage CustomerChanged(Customer customer, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var payload = new CustomerChangedV1(customer.Id, customer.Code, customer.Name, customer.Phone, customer.Email,
            customer.Company, customer.Abn, customer.Address, customer.Notes, customer.IsAccount, customer.CreditLimit,
            customer.TermsDays, customer.IsActive, customer.CreatedUtc, customer.UpdatedUtc);
        return new OutboxMessage(SyncEventTypes.CustomerChanged, CustomerChangedV1.Version,
            storeId, registerId, DateTime.UtcNow, JsonSerializer.Serialize(payload, SyncJson.Options));
    }

    /// <summary>
    /// The <c>giftcard.moved</c> event for one movement (shared with every register). The movement takes the
    /// event's id, so other registers can tell it's the same one.
    /// </summary>
    public static OutboxMessage GiftCardMoved(GiftCard card, GiftCardMovement movement, string storeId, string registerId, bool cancelled = false)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(movement);
        var payload = new GiftCardMovedV1(card.Code, card.Kind.ToString(), movement.Amount, movement.SaleId,
            movement.InvoiceNumber, movement.AtUtc, movement.Note, cancelled);
        var message = new OutboxMessage(SyncEventTypes.GiftCardMoved, GiftCardMovedV1.Version,
            storeId, registerId, movement.AtUtc, JsonSerializer.Serialize(payload, SyncJson.Options));
        movement.EventId = message.EventId;
        return message;
    }

    /// <summary>The <c>account.entry</c> event for one line on a customer's account (shared with every register).</summary>
    public static OutboxMessage AccountEntry(CustomerAccountEntry entry, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var payload = new AccountEntryV1(entry.Id, entry.CustomerId, entry.Kind.ToString(), entry.Amount, entry.SaleId,
            entry.InvoiceNumber, entry.AtUtc, entry.DueUtc, entry.Reference, entry.Note);
        return new OutboxMessage(SyncEventTypes.AccountEntry, AccountEntryV1.Version,
            storeId, registerId, entry.AtUtc, JsonSerializer.Serialize(payload, SyncJson.Options));
    }

    /// <summary>The <c>order.status</c> event: a website order moved on (shared, and passed to the website).</summary>
    public static OutboxMessage WebOrderStatus(NepPOS.Contracts.Orders.WebOrderStatusV1 status, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new OutboxMessage(SyncEventTypes.OrderStatus, NepPOS.Contracts.Orders.WebOrderStatusV1.Version,
            storeId, registerId, status.AtUtc, JsonSerializer.Serialize(status, SyncJson.Options));
    }

    /// <summary>The <c>transfer.sent</c> event: stock on its way to another store.</summary>
    public static OutboxMessage TransferSent(NepPOS.Contracts.Orders.TransferSentV1 transfer, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        return new OutboxMessage(SyncEventTypes.TransferSent, NepPOS.Contracts.Orders.TransferSentV1.Version,
            storeId, registerId, transfer.SentUtc, JsonSerializer.Serialize(transfer, SyncJson.Options));
    }

    /// <summary>The <c>transfer.received</c> event: what this store counted in from a transfer.</summary>
    public static OutboxMessage TransferReceived(NepPOS.Contracts.Orders.TransferReceivedV1 received, string storeId, string registerId)
    {
        ArgumentNullException.ThrowIfNull(received);
        return new OutboxMessage(SyncEventTypes.TransferReceived, NepPOS.Contracts.Orders.TransferReceivedV1.Version,
            storeId, registerId, received.ReceivedUtc, JsonSerializer.Serialize(received, SyncJson.Options));
    }

    /// <summary>The envelope Head Office receives (integration build step 3 sends it).</summary>
    public SyncEvent ToSyncEvent() => new(
        EventId, Type, Version, StoreId, RegisterId, Id,
        DateTime.SpecifyKind(OccurredAtUtc, DateTimeKind.Utc),
        JsonDocument.Parse(Payload).RootElement.Clone());
}
