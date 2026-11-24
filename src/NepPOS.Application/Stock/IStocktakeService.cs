using NepPOS.Domain.Entities;

namespace NepPOS.Application.Stock;

/// <summary>One counted product with what the system expects now and the difference (singles and value at cost).</summary>
public sealed record StocktakeRow(int ProductId, string ProductName, decimal Counted, decimal Expected, decimal UnitCost)
{
    public decimal Difference => Counted - Expected;
    public decimal Value => decimal.Round(Difference * UnitCost, 2, MidpointRounding.AwayFromZero);
}

/// <summary>What posting changed: products set to their count, units up/down and the net value at cost.</summary>
public sealed record StocktakePosted(int Products, decimal UnitsUp, decimal UnitsDown, decimal Value);

/// <summary>
/// Stock counts. Count by scanning (each scan adds a single or a pack) or by typing a number; only products that were
/// counted change when it's posted, so a count can cover one shelf or department at a time.
/// </summary>
public interface IStocktakeService
{
    /// <summary>The count in progress, if any.</summary>
    Task<Stocktake?> CurrentAsync(CancellationToken cancellationToken = default);

    Task<Stocktake> StartAsync(string name, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="singles"/> to the product's count, or sets it when <paramref name="replace"/>.</summary>
    Task<StocktakeRow> CountAsync(int stocktakeId, int productId, decimal singles, bool replace, CancellationToken cancellationToken = default);

    Task RemoveAsync(int stocktakeId, int productId, CancellationToken cancellationToken = default);

    /// <summary>Counted products with the stock expected now and the difference, biggest value difference first.</summary>
    Task<IReadOnlyList<StocktakeRow>> RowsAsync(int stocktakeId, CancellationToken cancellationToken = default);

    /// <summary>Sets each counted product's stock to its count (a Stocktake movement for the difference).</summary>
    Task<StocktakePosted> PostAsync(int stocktakeId, string? employeeName, CancellationToken cancellationToken = default);

    Task CancelAsync(int stocktakeId, CancellationToken cancellationToken = default);

    /// <summary>Posted and cancelled counts, newest first.</summary>
    Task<IReadOnlyList<Stocktake>> HistoryAsync(int take = 20, CancellationToken cancellationToken = default);
}
