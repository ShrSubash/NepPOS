using NepPOS.Domain.Entities;

namespace NepPOS.Application.Stock;

/// <summary>One line of a delivery being entered.</summary>
public sealed record ReceiptLineInput(int ProductId, int PackUnits, string PackName, decimal Quantity, decimal CostPerPack);

/// <summary>A delivery ready to post.</summary>
public sealed record ReceiptInput(int? SupplierId, string SupplierName, string InvoiceNumber, DateOnly InvoiceDate, IReadOnlyList<ReceiptLineInput> Lines);

/// <summary>A product whose cost per single changed on this delivery, with what it does to the margin.</summary>
public sealed record CostChange(int ProductId, string ProductName, decimal? OldCost, decimal NewCost, decimal Price, decimal? GpBefore, decimal? GpAfter);

public sealed record ReceiptPosted(int ReceiptId, decimal Singles, decimal TotalCost, IReadOnlyList<CostChange> CostChanges);

/// <summary>A delivery in the list.</summary>
public sealed record ReceiptSummary(int Id, DateTime PostedUtc, string SupplierName, string InvoiceNumber, DateOnly InvoiceDate, int Lines, decimal TotalCost, string? EmployeeName);

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

    Task<StockReceipt?> GetAsync(int id);
}
