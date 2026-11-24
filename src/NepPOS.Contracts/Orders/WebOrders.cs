namespace NepPOS.Contracts.Orders;

/// <summary>Where a website order is up to. Text, so the website can show it as it is.</summary>
public static class WebOrderStatuses
{
    /// <summary>Paid or accepted on the website, waiting for the store.</summary>
    public const string Placed = "Placed";

    /// <summary>Someone at the store is picking it.</summary>
    public const string Picking = "Picking";

    /// <summary>Picked and packed: ready to collect, or waiting for the driver.</summary>
    public const string Ready = "Ready";

    /// <summary>Collected or sent out for delivery. The store has rung it up as a sale.</summary>
    public const string Completed = "Completed";

    /// <summary>Cancelled by the store or the website. Nothing was sold.</summary>
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All = new[] { Placed, Picking, Ready, Completed, Cancelled };
}

/// <summary>How the customer gets the order.</summary>
public static class Fulfilment
{
    public const string Pickup = "Pickup";
    public const string Delivery = "Delivery";
}

/// <summary>
/// One line of a website order. <paramref name="ProductId"/> is the NepPOS product (from the catalog link); null for
/// something the store doesn't stock as a product (a delivery fee), rung up as a non-stock line.
/// <paramref name="UnitPrice"/> includes GST and is what the customer was charged on the website.
/// </summary>
public sealed record WebOrderLineV1(Guid? ProductId, string Name, int Quantity, decimal UnitPrice, string? Sku = null);

/// <summary>
/// Version 1 of <c>order.placed</c>. <paramref name="Prepaid"/> was taken on the website (card, gift card);
/// <paramref name="OnAccount"/> goes on the customer's NepPOS account when the order is completed; anything left
/// of <paramref name="Total"/> is paid at the store (collect and pay, cash on delivery).
/// </summary>
public sealed record WebOrderPlacedV1(
    Guid OrderId,
    string StoreCode,
    string Reference,
    string CustomerName,
    string? Phone,
    string? Email,
    Guid? CustomerId,
    string Fulfilment,
    string? Address,
    string? Note,
    DateTime PlacedUtc,
    DateTime? WantedUtc,
    IReadOnlyList<WebOrderLineV1> Lines,
    decimal Total,
    decimal Prepaid,
    decimal OnAccount)
{
    public const int Version = 1;
}

/// <summary>How many of a line (by its position in the order, from 0) the store actually had.</summary>
public sealed record WebOrderPickedV1(int Line, int Quantity);

/// <summary>
/// Version 1 of <c>order.status</c>. On <c>Completed</c>: what was picked, the final total, the invoice number and
/// any <paramref name="RefundDue"/> (prepaid but not supplied) for the website to pay back.
/// </summary>
public sealed record WebOrderStatusV1(
    Guid OrderId,
    string Status,
    DateTime AtUtc,
    IReadOnlyList<WebOrderPickedV1>? Picked = null,
    decimal? FinalTotal = null,
    decimal? RefundDue = null,
    long? InvoiceNumber = null,
    string? Note = null,
    string? By = null)
{
    public const int Version = 1;
}

/// <summary>
/// POST /api/v1/orders (website key): send an order to one of the business's stores. Sending the same
/// <paramref name="OrderId"/> again is harmless and answers with the order as it is now.
/// </summary>
public sealed record PlaceWebOrderRequest(
    Guid OrderId,
    Guid StoreId,
    string Reference,
    string CustomerName,
    string? Phone,
    string? Email,
    Guid? CustomerId,
    string Fulfilment,
    string? Address,
    string? Note,
    DateTime? WantedUtc,
    IReadOnlyList<WebOrderLineV1> Lines,
    decimal Total,
    decimal Prepaid,
    decimal OnAccount)
{
    public const int MaxLines = 200;
}

/// <summary>POST /api/v1/orders/{id}/cancel (website key). Refused once the store has started picking.</summary>
public sealed record CancelWebOrderRequest(string? Reason);

/// <summary>A website order as Head Office last heard from the store.</summary>
public sealed record WebOrderView(
    Guid OrderId,
    Guid StoreId,
    string Reference,
    string Status,
    decimal Total,
    decimal? FinalTotal,
    decimal? RefundDue,
    long? InvoiceNumber,
    string? Note,
    IReadOnlyList<WebOrderPickedV1>? Picked,
    DateTime UpdatedAtUtc,
    long ChangeNumber);

/// <summary>
/// Answer to GET /api/v1/orders/changes?after=N (website key): orders whose status changed after change number N,
/// oldest first. Keep <c>Next</c> and send it as <c>after</c> next time; while <c>More</c>, ask again straight away.
/// </summary>
public sealed record WebOrderChanges(IReadOnlyList<WebOrderView> Orders, long Next, bool More);

/// <summary>
/// One line of a transfer, in singles at the sender's cost. The receiving store finds the product by
/// <paramref name="ProductId"/>, then barcode, then PLU.
/// </summary>
public sealed record TransferLineV1(Guid ProductId, string Name, string? Barcode, string? Sku, decimal Quantity, decimal UnitCost);

/// <summary>Version 1 of <c>transfer.sent</c>: stock that left <paramref name="FromStore"/> for <paramref name="ToStore"/> (store codes).</summary>
public sealed record TransferSentV1(
    Guid TransferId,
    string Number,
    string FromStore,
    string ToStore,
    DateTime SentUtc,
    string? By,
    string? Note,
    IReadOnlyList<TransferLineV1> Lines)
{
    public const int Version = 1;
}

/// <summary>How many of a transfer line (by position, from 0) arrived.</summary>
public sealed record TransferReceivedLineV1(int Line, decimal Quantity);

/// <summary>Version 1 of <c>transfer.received</c>: what the receiving store counted in.</summary>
public sealed record TransferReceivedV1(Guid TransferId, string ToStore, DateTime ReceivedUtc, string? By, IReadOnlyList<TransferReceivedLineV1> Lines)
{
    public const int Version = 1;
}

/// <summary>One of the business's stores, for choosing where to send a transfer (GET /api/v1/sync/stores).</summary>
public sealed record StoreRef(string StoreCode, string Name);
