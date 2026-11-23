using NepPOS.Domain.Entities;

namespace NepPOS.Application.Stock;

/// <summary>One line of a delivery being entered.</summary>
public sealed record ReceiptLineInput(int ProductId, int PackUnits, string PackName, decimal Quantity, decimal CostPerPack)
{
    /// <summary>The supplier's item code (from an e-invoice). Saved against the product so the next invoice matches it.</summary>
    public string? SupplierItemCode { get; init; }

    /// <summary>Supplier units per product single for that code (see <see cref="Domain.Entities.ProductSupplierCode.SupplierUnitsPerSingle"/>).</summary>
    public int SupplierUnitsPerSingle { get; init; } = 1;
}

/// <summary>A delivery ready to post.</summary>
public sealed record ReceiptInput(int? SupplierId, string SupplierName, string InvoiceNumber, DateOnly InvoiceDate, IReadOnlyList<ReceiptLineInput> Lines)
{
    /// <summary>True when <see cref="SupplierId"/> is null and the supplier should be added (found by account number or name first).</summary>
    public bool AddSupplier { get; init; }

    /// <summary>The shop's customer number with the supplier, from an e-invoice.</summary>
    public string? SupplierAccountNumber { get; init; }

    /// <summary>Supplier items to skip on every future e-invoice.</summary>
    public IReadOnlyList<SkipItem> SkipAlways { get; init; } = Array.Empty<SkipItem>();
}

/// <summary>A supplier item code that e-invoice import should always skip.</summary>
public sealed record SkipItem(string Code, string Description);

/// <summary>How an e-invoice line was matched to a product.</summary>
public enum InvoiceLineMatch
{
    /// <summary>No product found: the user links, creates or skips it.</summary>
    None,
    /// <summary>The supplier's item code was linked to a product before.</summary>
    SupplierCode,
    /// <summary>The line's barcode belongs to a product or one of its packs.</summary>
    Barcode,
    /// <summary>Marked "always skip" for this supplier.</summary>
    Skipped,
    /// <summary>Nothing was supplied (0 units): there's nothing to receive.</summary>
    NotSupplied
}

/// <param name="UnitsPerSingle">Supplier units in one of the product's singles (1 normally; 10 when the product is the 10-pack).</param>
public sealed record InvoiceLineMatchResult(SupplierInvoiceLine Line, InvoiceLineMatch MatchedBy, Product? Product, int UnitsPerSingle);

/// <param name="SupplierId">The existing supplier, or null when it's new (added on posting).</param>
/// <param name="AlreadyReceived">This invoice number from this supplier is already in.</param>
public sealed record InvoiceMatchResult(SupplierInvoice Invoice, int? SupplierId, string SupplierName, bool AlreadyReceived, IReadOnlyList<InvoiceLineMatchResult> Lines);

/// <summary>A product whose cost per single changed on this delivery, with what it does to the margin.</summary>
public sealed record CostChange(int ProductId, string ProductName, decimal? OldCost, decimal NewCost, decimal Price, decimal? GpBefore, decimal? GpAfter);

public sealed record ReceiptPosted(int ReceiptId, decimal Singles, decimal TotalCost, IReadOnlyList<CostChange> CostChanges);

/// <summary>A delivery in the list.</summary>
public sealed record ReceiptSummary(int Id, DateTime PostedUtc, string SupplierName, string InvoiceNumber, DateOnly InvoiceDate, int Lines, decimal TotalCost, string? EmployeeName)
{
    public bool Reversed { get; init; }
}

/// <param name="Singles">Singles taken back out of stock.</param>
/// <param name="NegativeStock">Products whose stock went below zero: part of the delivery was already sold.</param>
public sealed record ReceiptReversed(decimal Singles, decimal TotalCost, IReadOnlyList<string> NegativeStock);

/// <summary>Receiving supplier deliveries into stock.</summary>
public interface IStockReceivingService
{
    Task<IReadOnlyList<Supplier>> GetSuppliersAsync();

    /// <summary>Adds a supplier, or returns the active one that already has this name.</summary>
    Task<Supplier> AddSupplierAsync(string name);

    /// <summary>The product's packs (6-pack, case…), to receive by the pack.</summary>
    Task<IReadOnlyList<ProductVariant>> GetPacksAsync(int productId);

    /// <summary>True when this supplier's invoice number has already been received.</summary>
    Task<bool> InvoiceExistsAsync(int? supplierId, string supplierName, string invoiceNumber);

    /// <summary>
    /// Posts a delivery in one transaction: a Receipt stock movement per line, last cost and average cost
    /// per product. Throws <see cref="InvalidOperationException"/> for a repeated invoice or an empty delivery.
    /// </summary>
    Task<ReceiptPosted> PostAsync(ReceiptInput receipt, string? employeeName);

    Task<IReadOnlyList<ReceiptSummary>> GetRecentAsync(int take = 50);

    /// <summary>
    /// Takes a posted delivery back out: stock down by what it added, last and average cost put back,
    /// receipt marked reversed (kept for the record). Its invoice number can then be received again.
    /// </summary>
    Task<ReceiptReversed> ReverseAsync(int receiptId, string reason, string? employeeName);

    /// <summary>
    /// Finds the supplier (account number, then name) and each line's product: the supplier's item code
    /// linked before, then the barcode. Changes nothing; links are saved when the delivery is posted.
    /// </summary>
    Task<InvoiceMatchResult> MatchInvoiceAsync(SupplierInvoice invoice);

    Task<StockReceipt?> GetAsync(int id);
}
