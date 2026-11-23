using NepPOS.Application.Stock;
using Xunit;

namespace NepPOS.Application.Tests.Stock;

public class StockMathTests
{
    [Fact]
    public void Average_cost_weighs_stock_on_hand_against_the_delivery()
    {
        // 283 singles at $2.42, then a case of 24 at $2.50 each.
        Assert.Equal(2.4263m, StockMath.AverageCost(283, 2.42m, 24, 2.50m));
    }

    [Fact]
    public void Negative_stock_counts_as_none_and_a_first_delivery_sets_the_cost()
    {
        Assert.Equal(2.50m, StockMath.AverageCost(-5, 2.00m, 24, 2.50m));
        Assert.Equal(2.50m, StockMath.AverageCost(0, null, 24, 2.50m));
    }

    [Fact]
    public void With_nothing_on_hand_afterwards_the_old_average_stays()
    {
        Assert.Equal(2.00m, StockMath.AverageCost(0, 2.00m, 0.5m, 9.99m));
    }

    [Theory]
    [InlineData(5.50, 1, 2.42, false, 51.6)]     // Asahi single: 5.00 ex GST, 2.42 cost
    [InlineData(72.99, 24, 2.42, false, 12.5)]   // Asahi case of 24
    [InlineData(3.50, 1, 2.80, true, 20.0)]      // GST-free: no GST to take off
    public void Gross_profit_is_measured_on_the_price_without_GST(double price, int units, double cost, bool gstFree, double expected)
    {
        Assert.Equal((decimal)expected, StockMath.GrossProfitPercent((decimal)price, units, (decimal)cost, gstFree));
    }

    [Fact]
    public void No_price_or_no_cost_means_no_margin()
    {
        Assert.Null(StockMath.GrossProfitPercent(0, 1, 2m, false));
        Assert.Null(StockMath.GrossProfitPercent(5m, 1, null, false));
        Assert.Equal("none", StockMath.MarginLevel(null));
        Assert.Equal("thin", StockMath.MarginLevel(12.5m));
        Assert.Equal("good", StockMath.MarginLevel(51.6m));
    }

    [Fact]
    public void Reversing_a_delivery_works_the_average_back()
    {
        // 283 at $2.42, then 24 at $2.50, then 30 sold: taking the 24 back out leaves about $2.42 again.
        var after = StockMath.AverageCost(283, 2.42m, 24, 2.50m);
        Assert.Equal(2.4193m, StockMath.ReverseAverageCost(307 - 30, after, 24, 2.50m));   // (277 × 2.4263 − 24 × 2.50) ÷ 253
    }

    [Theory]
    [InlineData(20, 24)]    // most of the delivery already sold: too little stock left to say
    [InlineData(0, 24)]
    [InlineData(-5, 24)]
    public void With_too_little_stock_left_the_average_stays(int stockNow, int removed) =>
        Assert.Equal(2.45m, StockMath.ReverseAverageCost(stockNow, 2.45m, removed, 2.50m));

    [Fact]
    public void A_result_of_zero_or_less_keeps_the_average() =>
        Assert.Equal(1.00m, StockMath.ReverseAverageCost(30, 1.00m, 24, 5.00m));
}
