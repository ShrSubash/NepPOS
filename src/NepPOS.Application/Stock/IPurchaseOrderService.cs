using NepPOS.Domain.Entities;

namespace NepPOS.Application.Stock;

/// <summary>A product that has fallen to its reorder point, with how much to order (in packs).</summary>
public sealed record ReorderSuggestion(int ProductId, string ProductName, decimal StockOnHand, decimal ReorderPoint,
    string PackName, int PackUnits, decimal Packs, decimal CostPerPack);

/// <summary>
/// A product's reorder settings, for editing on the purchase order screen, with how many it sells a week
/// (the last <see cref="ReorderMath.SalesDays"/> days, refunds taken off).
/// </summary>
public sealed record ReorderLevel(int ProductId, string ProductName, decimal StockOnHand, decimal? ReorderPoint, decimal? ReorderQuantity,
    decimal SoldPerWeek = 0);

/// <summary>
/// Reorder points from sales: reorder when a week's sales are left (time to order and deliver), and order two
/// weeks' worth. Whole singles, rounded up, so a slow seller still gets 1.
/// </summary>
public static class ReorderMath
{
    /// <summary>Sales are averaged over the last four weeks, so one busy day doesn't swing it.</summary>
    public const int SalesDays = 28;

    public static decimal SoldPerWeek(decimal soldInPeriod, int days = SalesDays) =>
        days <= 0 || soldInPeriod <= 0 ? 0m : Math.Round(soldInPeriod * 7m / days, 1, MidpointRounding.AwayFromZero);

    /// <summary>The reorder point and order quantity for a product selling <paramref name="soldPerWeek"/>; null when it didn't sell.</summary>
    public static (decimal ReorderPoint, decimal OrderQuantity)? FromSales(decimal soldPerWeek, decimal reorderWeeks = 1m, decimal orderWeeks = 2m)
    {
        if (soldPerWeek <= 0)
            return null;
        return (Math.Ceiling(soldPerWeek * reorderWeeks), Math.Max(1m, Math.Ceiling(soldPerWeek * orderWeeks)));
    }
}

/// <summary>Marks drafts made from low stock, so the screen can ask the manager to check them.</summary>
public static class AutoDraft
{
    public const string Note = "Made from low stock. Check the quantities, then send.";

    public static bool IsAuto(PurchaseOrder order) => order.Status == PurchaseOrderStatus.Draft && order.Note == Note;
}

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

    /// <summary>
    /// Makes a draft order for each supplier with products at or below their reorder point (that supplier's own
    /// products only), unless that supplier already has an order waiting (draft or sent). Nothing goes to the
    /// supplier: a manager checks the draft and sends it. Returns the drafts made.
    /// </summary>
    Task<IReadOnlyList<PurchaseOrder>> DraftLowStockAsync(string? employeeName, CancellationToken cancellationToken = default);

    Task SetReorderLevelAsync(int productId, decimal? reorderPoint, decimal? reorderQuantity, CancellationToken cancellationToken = default);
}
