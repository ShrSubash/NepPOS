namespace NepPOS.Application.Stock;

/// <summary>
/// The cost and margin rules the old Stock program used, so owners see the same numbers after switching.
/// All costs are per single, ex GST. GST is 10% included in sell prices unless the product is GST-free.
/// </summary>
public static class StockMath
{
    public const decimal GstRate = 0.10m;

    /// <summary>
    /// Weighted average cost after receiving <paramref name="received"/> singles at <paramref name="unitCost"/>:
    /// (old stock × old average + received × cost) ÷ (old stock + received). Negative stock counts as none
    /// (it was sold before it was received), and with no stock on hand afterwards the old average stays.
    /// </summary>
    public static decimal AverageCost(decimal stockBefore, decimal? averageBefore, decimal received, decimal unitCost)
    {
        if (received <= 0)
            return averageBefore ?? unitCost;

        var onHand = Math.Max(0, stockBefore);
        var average = averageBefore ?? unitCost;
        if (onHand + received < 1)
            return average;

        return Math.Round((onHand * average + received * unitCost) / (onHand + received), 4);
    }

    /// <summary>
    /// Gross profit as a percent of the price ex GST, for a price that sells <paramref name="units"/> singles
    /// (1 for the single, 24 for a case). Null when there's no price or no cost to work it out from.
    /// </summary>
    public static decimal? GrossProfitPercent(decimal price, int units, decimal? unitCost, bool gstFree)
    {
        if (price <= 0 || unitCost is null || units < 1)
            return null;

        var exGst = gstFree ? price : price / (1 + GstRate);
        return Math.Round((exGst - units * unitCost.Value) / exGst * 100, 1);
    }

    /// <summary>How a margin reads: under 15% is thin, under 25% is fair, otherwise healthy.</summary>
    public static string MarginLevel(decimal? gp) => gp switch
    {
        null => "none",
        < 15 => "thin",
        < 25 => "fair",
        _ => "good"
    };
}
