using NepPOS.Domain.Entities;

namespace NepPOS.Application.Stock;

/// <summary>A product that has fallen to its reorder point, with how much to order (in packs).</summary>
public sealed record ReorderSuggestion(int ProductId, string ProductName, decimal StockOnHand, decimal ReorderPoint,
    string PackName, int PackUnits, decimal Packs, decimal CostPerPack);

/// <summary>A product's reorder settings, for editing on the purchase order screen.</summary>
public sealed record ReorderLevel(int ProductId, string ProductName, decimal StockOnHand, decimal? ReorderPoint, decimal? ReorderQuantity);

/// <summary>
/// Purchase orders: suggested from reorder points, saved as drafts, printed or sent, then received — which posts a
/// normal delivery (stock, costs, the supplier's bill) for what actually arrived.
/// </summary>
public interface IPurchaseOrderService
{
    Task<IReadOnlyList<PurchaseOrder>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default);

    Task<PurchaseOrder?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The supplier's products at or below their reorder point (products with no supplier: all suppliers).</summary>
    Task<IReadOnlyList<ReorderSuggestion>> SuggestAsync(int supplierId, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a draft (Id 0 = new; gets the next PO number).</summary>
    Task<PurchaseOrder> SaveDraftAsync(PurchaseOrder order, CancellationToken cancellationToken = default);

    Task MarkSentAsync(int id, CancellationToken cancellationToken = default);

    Task CancelAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives it: posts a delivery with the supplier's invoice number for the quantities that arrived (by line id;
    /// missing = as ordered) and marks the order received.
    /// </summary>
    Task<ReceiptPosted> ReceiveAsync(int id, string invoiceNumber, DateOnly invoiceDate, IReadOnlyDictionary<int, decimal>? arrived,
        string? employeeName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReorderLevel>> ReorderLevelsAsync(int supplierId, CancellationToken cancellationToken = default);

    Task SetReorderLevelAsync(int productId, decimal? reorderPoint, decimal? reorderQuantity, CancellationToken cancellationToken = default);
}
