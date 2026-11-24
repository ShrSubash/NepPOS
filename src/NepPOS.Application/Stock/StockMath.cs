namespace NepPOS.Application.Stock;

/// <summary>
/// The cost and margin rules the old Stock program used, so owners see the same numbers after switching.
/// All costs are per single, ex GST. GST is 10% included in sell prices unless the product is GST-free.
/// </summary>
public static class StockMath
{
    public const decimal GstRate = 0.10m;

    /// <summary>
    /// Takes a delivery back out of the average cost: the average before it, worked back from the average now.
    /// Keeps the current average when there's too little stock left to say (under one single) or the result
    /// would be $0 or less, which happens when much of the delivery has already been sold.
    /// </summary>
    public static decimal? ReverseAverageCost(decimal stockNow, decimal? averageNow, decimal removed, decimal unitCost)
    {
        if (averageNow is null)
            return null;

        var remaining = stockNow - removed;
        if (remaining < 1 || stockNow <= 0)
            return averageNow;

        var before = (stockNow * averageNow.Value - removed * unitCost) / remaining;
        return before > 0 ? Math.Round(before, 4) : averageNow;
    }

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
