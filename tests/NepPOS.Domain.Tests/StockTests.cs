using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Domain.Tests;

public class StockTests
{
    [Fact]
    public void Moving_stock_changes_stock_on_hand_and_returns_the_movement_that_explains_it()
    {
        var product = new Product { Id = 7, StockOnHand = 283 };
        var at = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

        var movement = product.MoveStock(StockMovementType.Sale, -24, "Invoice 1042", at, employeeName: "Sam");

        Assert.Equal(259, product.StockOnHand);
        Assert.Equal((7, StockMovementType.Sale, -24m, "Invoice 1042", "Sam", at),
            (movement.ProductId, movement.Type, movement.Quantity, movement.Reference, movement.EmployeeName, movement.AtUtc));
    }

    [Fact]
    public void A_case_line_takes_singles_off_and_a_refund_puts_them_back()
    {
        var sale = new SaleLineItem("37", "Asahi (Case of 24)", 2, 72.99m, 0.10m, "Beer", productId: 7, packUnits: 24);
        var refund = new SaleLineItem("37", "Asahi (Case of 24)", -1, 72.99m, 0.10m, "Beer", productId: 7, packUnits: 24);

        Assert.Equal(48, sale.StockUnits);
        Assert.Equal(-24, refund.StockUnits);
    }

    [Fact]
    public void A_pack_holds_at_least_one_single()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaleLineItem("1", "x", 1, 1m, 0.10m, packUnits: 0));
    }

    [Theory]
    [InlineData("82184045954", "82184045954")]
    [InlineData("082184045954", "82184045954")]
    [InlineData("0082184045954", "82184045954")]
    [InlineData(" abc-12 ", "ABC-12")]
    [InlineData("000", "0")]
    public void Barcodes_match_with_or_without_leading_zeros(string code, string key)
    {
        Assert.Equal(key, ProductBarcode.KeyFor(code));
    }
}
