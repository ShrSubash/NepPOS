using NepPOS.Domain.Entities;
using Xunit;

namespace NepPOS.Domain.Tests;

public class SaleLineItemTests
{
    [Fact]
    public void Constructor_WithNegativeQuantity_DoesNotThrow_ButZeroQuantity_Throws()
    {
        // Negative quantity is valid — it represents a return (Section 5: returns are
        // normal transactions, not special reversals).
        var returnLine = new SaleLineItem("SKU-001", "Test Product", -1, 10.00m, 0.10m);
        Assert.Equal(-1, returnLine.Quantity);
    }

    [Fact]
    public void Constructor_WithZeroQuantity_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SaleLineItem("SKU-001", "Test Product", 0, 10.00m, 0.10m));
    }

    [Fact]
    public void GstAmount_CalculatedFromTaxInclusivePrice_MatchesExpected()
    {
        // $11.00 inc. GST at 10% → GST component should be $1.00
        var lineItem = new SaleLineItem("SKU-001", "Test Product", 1, 11.00m, 0.10m);
        Assert.Equal(1.00m, Math.Round(lineItem.GstAmount, 2));
    }

    [Fact]
    public void Discount_comes_off_the_line_total_and_keeps_its_reason()
    {
        var line = new SaleLineItem("SKU-001", "Wine", 2, 20.00m, 0.10m, discount: 4.005m, discountReason: " Staff discount ");

        Assert.Equal(4.01m, line.Discount);
        Assert.Equal(35.99m, line.LineTotalIncGst);
        Assert.Equal("Staff discount", line.DiscountReason);
    }

    [Theory]
    [InlineData(1, -1)]       // negative
    [InlineData(1, 10.01)]    // more than the line
    [InlineData(-1, 1)]       // on a return
    public void Bad_discounts_are_refused(int quantity, double discount)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SaleLineItem("SKU-001", "Wine", quantity, 10.00m, 0.10m, discount: (decimal)discount));
    }
}
