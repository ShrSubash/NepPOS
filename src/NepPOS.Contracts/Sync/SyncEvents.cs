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

/// <summary>Version 1 of <c>sale.completed</c>. Money is in AUD, GST inclusive unless the name says otherwise.</summary>
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
    IReadOnlyList<SalePaymentV1> Payments)
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
    string? MaskedPan);

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

/// <summary>The one JSON format for every event: camelCase names, enums as text.</summary>
public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
