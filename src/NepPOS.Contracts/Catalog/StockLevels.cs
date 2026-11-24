namespace NepPOS.Contracts.Catalog;

/// <summary>A product's stock on hand (singles) at a store, and Head Office's change number for it.</summary>
public sealed record StockLevel(Guid ProductId, decimal StockOnHand, DateTime UpdatedAtUtc, long ChangeNumber);

/// <summary>
/// Answer to GET /api/v1/stock/changes?storeId=…&amp;after=N (website key): stock levels changed after change number N,
/// oldest first. Keep <c>Next</c> and send it as <c>after</c> next time; while <c>More</c>, ask again straight away.
/// </summary>
public sealed record StockChanges(IReadOnlyList<StockLevel> Levels, long Next, bool More);
