using NepPOS.Application.Stock;
using Xunit;

namespace NepPOS.Application.Tests.Stock;

public class ReorderMathTests
{
    [Fact]
    public void Sold_per_week_averages_the_last_four_weeks()
    {
        Assert.Equal(14m, ReorderMath.SoldPerWeek(56));
        Assert.Equal(0.3m, ReorderMath.SoldPerWeek(1));
        Assert.Equal(0m, ReorderMath.SoldPerWeek(-3));   // more refunded than sold
    }

    [Fact]
    public void Reorder_when_a_week_is_left_and_order_two_weeks_rounded_up()
    {
        Assert.Equal((14m, 28m), ReorderMath.FromSales(14m));
        Assert.Equal((1m, 1m), ReorderMath.FromSales(0.3m));   // a slow seller still gets one
        Assert.Equal((4m, 7m), ReorderMath.FromSales(3.4m));
        Assert.Null(ReorderMath.FromSales(0m));
    }
}
