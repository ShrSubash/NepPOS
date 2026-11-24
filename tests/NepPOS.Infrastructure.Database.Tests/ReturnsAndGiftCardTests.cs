using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Catalog;
using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Catalog;
using NepPOS.Infrastructure.Database.Sales;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Gift card balances, over-return checks and faulty returns written off, against a real (in-memory) database.</summary>
public class ReturnsAndGiftCardTests : IDisposable
{
    private static readonly RegisterTerminal Terminal = new("STORE-001", "REG-001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private long _invoice;

    public ReturnsAndGiftCardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
    }

    private async Task SaveAsync(SaleTransaction sale, IPaymentSession payments)
    {
        sale.RecordPayments(payments.Payments, payments.RoundingAdjustment);
        sale.Complete(++_invoice);
        await using var db = new NepPosDbContext(_options);
        await new SaleTransactionRepository(db).AddAsync(sale);
    }

    private async Task<decimal?> BalanceAsync(string code)
    {
        await using var db = new NepPosDbContext(_options);
        return (await new GiftCardService(db).GetAsync(code))?.Balance;
    }

    /// <summary>Sells a $50 gift card for cash.</summary>
    private async Task SellGiftCardAsync(string code, decimal amount)
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("GIFTCARD", "Gift card", 1, amount, 0m, giftCardCode: code));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(amount);
        await SaveAsync(sale, tender);
    }

    [Fact]
    public async Task Selling_then_spending_a_gift_card_moves_its_balance()
    {
        await SellGiftCardAsync("GC-111111", 50m);
        Assert.Equal(50m, await BalanceAsync("gc-111111"));

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 30m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddVoucher(TenderType.GiftCard, "GC-111111", 0m, 50m);
        await SaveAsync(sale, tender);

        Assert.Equal(20m, await BalanceAsync("GC-111111"));
        await using var db = new NepPosDbContext(_options);
        Assert.Equal(2, (await new GiftCardService(db).HistoryAsync("GC-111111")).Count);
    }

    [Fact]
    public async Task A_sale_can_never_overspend_a_card()
    {
        await SellGiftCardAsync("GC-222222", 10m);

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 30m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddVoucher(TenderType.GiftCard, "GC-222222", 30m, 30m);   // the screen was told a wrong balance

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(sale, tender));
        Assert.Equal(10m, await BalanceAsync("GC-222222"));
    }

    [Fact]
    public async Task A_cancelled_card_cannot_be_spent()
    {
        await SellGiftCardAsync("GC-333333", 10m);
        await using (var db = new NepPosDbContext(_options))
            await new GiftCardService(db).CancelAsync("GC-333333");

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 5m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddVoucher(TenderType.GiftCard, "GC-333333", 0m, 10m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(sale, tender));
    }

    [Fact]
    public async Task A_refund_to_store_credit_creates_the_card()
    {
        var sold = new SaleTransaction("STORE-001", "REG-001");
        sold.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 20m, 0.10m));
        var tender = new TenderSession(sold.Id, sold.GrandTotal);
        tender.AddCash(20m);
        await SaveAsync(sold, tender);

        var back = new SaleTransaction("STORE-001", "REG-001");
        back.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", -1, 20m, 0.10m, returnOfLineId: sold.LineItems[0].Id));
        back.MarkAsReturnOf(sold.Id, sold.InvoiceNumber!.Value);
        var refund = new RefundSession(back.Id, back.GrandTotal);
        refund.RefundToVoucher(TenderType.StoreCredit, "SC-444444", 20m);
        await SaveAsync(back, refund);

        Assert.Equal(20m, await BalanceAsync("SC-444444"));
    }

    [Fact]
    public async Task An_item_cannot_be_returned_twice()
    {
        var sold = new SaleTransaction("STORE-001", "REG-001");
        sold.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 2, 20m, 0.10m));
        var tender = new TenderSession(sold.Id, sold.GrandTotal);
        tender.AddCash(40m);
        await SaveAsync(sold, tender);

        async Task ReturnAsync(int quantity)
        {
            var back = new SaleTransaction("STORE-001", "REG-001");
            back.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", -quantity, 20m, 0.10m, returnOfLineId: sold.LineItems[0].Id));
            var refund = new RefundSession(back.Id, back.GrandTotal);
            refund.RefundCash();
            await SaveAsync(back, refund);
        }

        await ReturnAsync(1);
        await ReturnAsync(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReturnAsync(1));   // both already back
    }

    [Fact]
    public async Task A_faulty_return_comes_back_and_goes_out_as_wastage()
    {
        var plan = LegacyPluImport.Plan(
            "Number\tdescription\tlongdescrip\tSOH\tpacksize\tsplitsize\taveragecost\tsingleprice\tsplitprice\tpackprice\tprice4\tsplit2price\tsupplier\tdept\tStatus\tscancodes1\tscancodes2\tscancodes3\tscancodes4\tsuppcodes1\ttaxcode\n" +
            "37\tASAHI SUPER DRY 5%\tASAHI SUPER DRY\t10\t24\t6\t2.42\t5.50\t0\t0\t0\t0\t3\t3\t3\t\t\t\t\t\tB\n",
            "Number\tdpn\n3\tBEER\n", "Number\tsupplier\n3\tCUB\n", Array.Empty<string>(), Array.Empty<string>());
        int productId;
        await using (var db = new NepPosDbContext(_options))
        {
            await new ProductManagementService(db, Terminal).ImportLegacyAsync(plan, "Owner");
            productId = (await db.Products.SingleAsync(p => p.Plu == "37")).Id;
        }

        var sold = new SaleTransaction("STORE-001", "REG-001");
        sold.AddLineItem(new SaleLineItem("37", "ASAHI SUPER DRY", 2, 5.50m, 0.10m, productId: productId));
        var tender = new TenderSession(sold.Id, sold.GrandTotal);
        tender.AddCash(11m);
        await SaveAsync(sold, tender);

        var back = new SaleTransaction("STORE-001", "REG-001");
        back.AddLineItem(new SaleLineItem("37", "ASAHI SUPER DRY", -1, 5.50m, 0.10m, productId: productId,
            returnOfLineId: sold.LineItems[0].Id, returnReason: "Faulty", restock: false));
        var refund = new RefundSession(back.Id, back.GrandTotal);
        refund.RefundCash();
        await SaveAsync(back, refund);

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(8m, (await check.Products.SingleAsync(p => p.Id == productId)).StockOnHand);   // 10 − 2 + 1 − 1
        Assert.Equal(-1m, (await check.StockMovements.SingleAsync(m => m.ProductId == productId && m.Type == StockMovementType.Wastage)).Quantity);
    }

    public void Dispose() => _connection.Dispose();
}
