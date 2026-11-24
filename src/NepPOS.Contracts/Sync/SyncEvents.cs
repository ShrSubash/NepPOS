using System.Text.Json;
using System.Text.Json.Serialization;

namespace NepPOS.Contracts.Sync;

/// <summary>Event type names. A name never changes meaning; a new shape gets a new <c>Version</c>.</summary>
public static class SyncEventTypes
{
    /// <summary>A sale was completed at a register (it has an invoice number). Payload: <see cref="SaleCompletedV1"/>.</summary>
    public const string SaleCompleted = "sale.completed";

    /// <summary>Something was written to a register's journal: sign-in, void, no sale, Z… Payload: <see cref="JournalRecordedV1"/>.</summary>
    public const string JournalRecorded = "journal.recorded";

    /// <summary>A product was added, edited or deactivated at a register. Payload: <see cref="ProductChangedV1"/>.</summary>
    public const string ProductChanged = "product.changed";

    /// <summary>A customer was added or edited. Payload: <see cref="CustomerChangedV1"/>. Shared with every register.</summary>
    public const string CustomerChanged = "customer.changed";

    /// <summary>A gift card or store credit balance changed. Payload: <see cref="GiftCardMovedV1"/>. Shared with every register.</summary>
    public const string GiftCardMoved = "giftcard.moved";

    /// <summary>A line on a customer's account (charge, payment, credit). Payload: <see cref="AccountEntryV1"/>. Shared with every register.</summary>
    public const string AccountEntry = "account.entry";

    /// <summary>
    /// Events Head Office passes on to the owner's other registers (GET /api/v1/sync/shared), so customers,
    /// accounts and gift cards are the same at every store.
    /// </summary>
    public static readonly IReadOnlyList<string> Shared = new[] { CustomerChanged, GiftCardMoved, AccountEntry };
}

/// <summary>
/// The envelope every store event travels in (docs/integration/events.md).
/// <paramref name="EventId"/> makes a resend harmless; <paramref name="Sequence"/> counts up by one
/// per register with no gaps, so Head Office can tell if one is missing.
/// </summary>
public sealed record SyncEvent(
    Guid EventId,
    string Type,
    int Version,
    string StoreId,
    string RegisterId,
    long Sequence,
    DateTime OccurredAtUtc,
    JsonElement Payload);

/// <summary>
/// Version 1 of <c>sale.completed</c>. Money is in AUD, GST inclusive unless the name says otherwise.
/// A return is the same event with negative lines (and a negative total for a refund) and
/// <c>ReturnOfInvoice</c> set when it came back with a receipt.
/// </summary>
public sealed record SaleCompletedV1(
    Guid SaleId,
    long InvoiceNumber,
    DateTimeOffset CreatedAtUtc,
    int? EmployeeId,
    string? EmployeeName,
    decimal Total,
    decimal Gst,
    decimal CashRounding,
    IReadOnlyList<SaleLineV1> Lines,
    IReadOnlyList<SalePaymentV1> Payments,
    long? ReturnOfInvoice = null)
{
    public const int Version = 1;
}

public sealed record SaleLineV1(
    string Sku,
    string Name,
    string? Category,
    int Quantity,
    decimal UnitPriceIncGst,
    decimal GstRate,
    decimal LineTotalIncGst);

/// <summary>One tender. Card details are the masked number and auth code only: never a full card number.</summary>
public sealed record SalePaymentV1(
    string Tender,
    decimal Amount,
    decimal Tendered,
    decimal Change,
    string? AuthCode,
    string? CardType,
    string? MaskedPan,
    string? TenderName = null);

/// <summary>Version 1 of <c>journal.recorded</c>: one journal line, as the register's Journal screen shows it.</summary>
public sealed record JournalRecordedV1(
    Guid JournalId,
    long JournalSequence,
    string EventType,
    string Detail,
    int? EmployeeId,
    string? EmployeeName,
    Guid? SaleId,
    long? InvoiceNumber,
    decimal? Amount)
{
    public const int Version = 1;
}

/// <summary>
/// Version 1 of <c>product.changed</c>: the whole product as it is now, not just what changed, so the
/// latest event is always enough. <paramref name="ProductId"/> is the product's id across systems
/// (the register's own number stays local). Prices are AUD inc GST. Cost price is never sent.
/// </summary>
public sealed record ProductChangedV1(
    Guid ProductId,
    string Name,
    string Category,
    decimal Price,
    string? Barcode,
    string? Plu,
    bool EnableOnWebsite,
    bool IsActive,
    IReadOnlyList<ProductVariantV1> Variants)
{
    public const int Version = 1;
}

/// <summary>An extra pack size of a product (six-pack, carton…), with its own barcode and price.</summary>
public sealed record ProductVariantV1(
    string PackType,
    string? Barcode,
    string? Plu,
    int UnitQuantity,
    decimal Price,
    bool IsActive);

/// <summary>The one JSON format for every event: camelCase names, enums as text.</summary>
public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

// ---- Head Office API: register activation, sending events, heartbeat (docs/integration/README.md) ----

/// <summary>
/// POST /api/v1/activate: a one-time code from Head Office turns a PC into a register of one store.
/// <paramref name="StoreCode"/> (optional) is the store this register is set up as; if the code is for
/// another store, Head Office refuses without using the code up.
/// </summary>
public sealed record ActivateRequest(string Code, string RegisterCode, string? DeviceName, string? StoreCode = null);

/// <summary>
/// The register keeps <paramref name="ApiKey"/> (encrypted) and sends it as "Authorization: Bearer …".
/// It is shown only this once.
/// </summary>
/// <remarks><paramref name="Licence"/> is the business's signed licence; <paramref name="LicencePublicKey"/> checks it
/// (a register built with a fixed key ignores this one).</remarks>
public sealed record ActivateResponse(Guid TenantId, string TenantName, Guid StoreId, string StoreCode, string StoreName, string RegisterCode, string ApiKey,
    Licensing.SignedLicence? Licence = null, string? LicencePublicKey = null);

/// <summary>POST /api/v1/sync/events: up to <see cref="MaxEvents"/> outbox events, oldest first.</summary>
public sealed record SyncEventsRequest(IReadOnlyList<SyncEvent> Events)
{
    public const int MaxEvents = 500;
}

/// <summary>
/// What happened to each event. Accepted and Duplicate both mean "Head Office has it": the register
/// marks those sent. A rejected event stays in the outbox with the reason, for a person to look at.
/// <paramref name="LastSequence"/> is the highest sequence Head Office holds for this register.
/// </summary>
public sealed record SyncEventsResponse(
    IReadOnlyList<Guid> Accepted,
    IReadOnlyList<Guid> Duplicates,
    IReadOnlyList<RejectedEvent> Rejected,
    long LastSequence);

public sealed record RejectedEvent(Guid EventId, string Reason);

/// <summary>
/// POST /api/v1/heartbeat, every minute: tells Head Office (and later the website) the register is online.
/// The answer carries a freshly signed licence, so a renewal or suspension reaches the register within a minute,
/// and the key to check it with (registers activated before licences existed never got one at activation).
/// </summary>
public sealed record HeartbeatResponse(DateTime ServerTimeUtc, Licensing.SignedLicence? Licence = null, string? LicencePublicKey = null);

/// <summary>Version 1 of <c>customer.changed</c>: the whole customer as it is now. The newest <c>UpdatedUtc</c> wins.</summary>
public sealed record CustomerChangedV1(
    Guid CustomerId,
    string Code,
    string Name,
    string? Phone,
    string? Email,
    string? Company,
    string? Abn,
    string? Address,
    string? Notes,
    bool IsAccount,
    decimal CreditLimit,
    int TermsDays,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public const int Version = 1;
}

/// <summary>
/// Version 1 of <c>giftcard.moved</c>: one change to a card's balance (positive = loaded, negative = spent).
/// <c>Kind</c> is "GiftCard" or "StoreCredit". <c>Cancelled</c> = the card was cancelled (amount 0).
/// </summary>
public sealed record GiftCardMovedV1(
    string Code,
    string Kind,
    decimal Amount,
    Guid? SaleId,
    long? InvoiceNumber,
    DateTime AtUtc,
    string Note,
    bool Cancelled = false)
{
    public const int Version = 1;
}

/// <summary>
/// Version 1 of <c>account.entry</c>: one line on a customer's account. <c>Kind</c> is Charge, Payment, Credit or
/// Adjustment; positive amounts add to what they owe.
/// </summary>
public sealed record AccountEntryV1(
    Guid EntryId,
    Guid CustomerId,
    string Kind,
    decimal Amount,
    Guid? SaleId,
    long? InvoiceNumber,
    DateTime AtUtc,
    DateTime? DueUtc,
    string? Reference,
    string? Note)
{
    public const int Version = 1;
}

/// <summary>One event from another register, with Head Office's running number for it.</summary>
public sealed record SharedEvent(long Id, SyncEvent Event);

/// <summary>
/// Answer to GET /api/v1/sync/shared?after=N: the owner's shared events from other registers after number N,
/// oldest first. <c>LastId</c> is where to continue; <c>More</c> = ask again straight away.
/// </summary>
public sealed record SharedEventsResponse(IReadOnlyList<SharedEvent> Events, long LastId, bool More)
{
    public const int MaxEvents = 500;
}
