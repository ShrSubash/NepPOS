using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Stock;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

public class StockReceivingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly int _asahiId;
    private readonly int _supplierId;

    public StockReceivingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
        var supplier = new Supplier { Name = "CUB" };
        var asahi = new Product { Name = "Asahi Super Dry", Price = 5.50m, CostPrice = 2.42m, StockOnHand = 283, Category = "Beer" };
        db.Suppliers.Add(supplier);
        db.Products.Add(asahi);
        db.SaveChanges();
        (_asahiId, _supplierId) = (asahi.Id, supplier.Id);
    }

    private ReceiptInput Delivery(string invoice, decimal cases, decimal costPerCase) =>
        new(_supplierId, "CUB", invoice, new DateOnly(2026, 11, 24),
            new[] { new ReceiptLineInput(_asahiId, 24, "Case of 24", cases, costPerCase) });

    [Fact]
    public async Task Posting_adds_stock_in_singles_and_moves_last_and_average_cost()
    {
        ReceiptPosted posted;
        await using (var db = new NepPosDbContext(_options))
            posted = await new StockReceivingService(db).PostAsync(Delivery("INV-1", 2, 60.00m), "Sam");

        Assert.Equal(48m, posted.Singles);
        Assert.Equal(120.00m, posted.TotalCost);

        await using var check = new NepPosDbContext(_options);
        var asahi = await check.Products.SingleAsync(p => p.Id == _asahiId);
        Assert.Equal(331m, asahi.StockOnHand);
        Assert.Equal(2.50m, asahi.LastCost);
        Assert.Equal(StockMath.AverageCost(283, 2.42m, 48, 2.50m), asahi.CostPrice);
        Assert.Equal(_supplierId, asahi.SupplierId);

        var movement = await check.StockMovements.SingleAsync(m => m.ProductId == _asahiId);
        Assert.Equal((StockMovementType.Receipt, 48m, 2.50m, "Invoice INV-1 (CUB)", "Sam"),
            (movement.Type, movement.Quantity, movement.UnitCost!.Value, movement.Reference, movement.EmployeeName));

        var change = Assert.Single(posted.CostChanges);
        Assert.Equal((2.42m, 2.50m), (change.OldCost!.Value, change.NewCost));
    }

    [Fact]
    public async Task The_same_invoice_from_the_same_supplier_is_refused()
    {
        await using (var db = new NepPosDbContext(_options))
            await new StockReceivingService(db).PostAsync(Delivery("INV-2", 1, 58.08m), null);

        await using var again = new NepPosDbContext(_options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new StockReceivingService(again).PostAsync(Delivery(" INV-2 ", 1, 58.08m), null));
        Assert.Contains("already been received", error.Message);
        Assert.Equal(307m, (await again.Products.SingleAsync(p => p.Id == _asahiId)).StockOnHand);
    }

    [Fact]
    public async Task Recent_deliveries_list_their_totals()
    {
        await using (var db = new NepPosDbContext(_options))
            await new StockReceivingService(db).PostAsync(Delivery("INV-3", 3, 58.08m), null);

        await using var check = new NepPosDbContext(_options);
        var summary = Assert.Single(await new StockReceivingService(check).GetRecentAsync());
        Assert.Equal(("CUB", "INV-3", 1, 174.24m), (summary.SupplierName, summary.InvoiceNumber, summary.Lines, summary.TotalCost));
        Assert.Single((await new StockReceivingService(check).GetAsync(summary.Id))!.Lines);
    }

    [Fact]
    public async Task Reversing_takes_stock_out_puts_costs_back_and_frees_the_invoice()
    {
        int receiptId;
        await using (var db = new NepPosDbContext(_options))
            receiptId = (await new StockReceivingService(db).PostAsync(Delivery("INV-1", 2, 60.00m), "Sam")).ReceiptId;

        ReceiptReversed reversed;
        await using (var db = new NepPosDbContext(_options))
            reversed = await new StockReceivingService(db).ReverseAsync(receiptId, "Wrong quantities", "Sam");
        Assert.Equal(48m, reversed.Singles);
        Assert.Empty(reversed.NegativeStock);

        await using (var check = new NepPosDbContext(_options))
        {
            var asahi = await check.Products.SingleAsync(p => p.Id == _asahiId);
            // Nothing happened in between: exactly as before the delivery.
            Assert.Equal((283m, 2.42m, (decimal?)null), (asahi.StockOnHand, asahi.CostPrice!.Value, asahi.LastCost));
            var receipt = await check.StockReceipts.SingleAsync();
            Assert.Equal(("Wrong quantities", "Sam"), (receipt.ReversalReason, receipt.ReversedBy));
            Assert.Contains(check.StockMovements, m => m.Type == StockMovementType.ReceiptReversal && m.Quantity == -48m);

            var recent = Assert.Single(await new StockReceivingService(check).GetRecentAsync());
            Assert.True(recent.Reversed);
        }

        // The same invoice can now be received again, correctly.
        await using (var db = new NepPosDbContext(_options))
            await new StockReceivingService(db).PostAsync(Delivery("INV-1", 1, 60.00m), "Sam");
        await using var final = new NepPosDbContext(_options);
        Assert.Equal(307m, (await final.Products.SingleAsync(p => p.Id == _asahiId)).StockOnHand);
    }

    [Fact]
    public async Task A_delivery_can_only_be_reversed_once()
    {
        int receiptId;
        await using (var db = new NepPosDbContext(_options))
        {
            var service = new StockReceivingService(db);
            receiptId = (await service.PostAsync(Delivery("INV-1", 1, 60.00m), "Sam")).ReceiptId;
            await service.ReverseAsync(receiptId, "Entered twice", "Sam");
        }

        await using var again = new NepPosDbContext(_options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new StockReceivingService(again).ReverseAsync(receiptId, "Entered twice", "Sam"));
        Assert.Contains("already reversed", error.Message);
        Assert.Equal(283m, (await again.Products.SingleAsync(p => p.Id == _asahiId)).StockOnHand);
    }

    [Fact]
    public async Task After_sales_the_average_is_worked_back_and_negative_stock_is_reported()
    {
        int receiptId;
        await using (var db = new NepPosDbContext(_options))
            receiptId = (await new StockReceivingService(db).PostAsync(Delivery("INV-1", 2, 60.00m), "Sam")).ReceiptId;

        // 300 sold after the delivery arrived.
        await using (var db = new NepPosDbContext(_options))
        {
            var asahi = await db.Products.SingleAsync(p => p.Id == _asahiId);
            db.StockMovements.Add(asahi.MoveStock(StockMovementType.Sale, -300, "Sales", DateTime.UtcNow.AddSeconds(1)));
            await db.SaveChangesAsync();
        }

        ReceiptReversed reversed;
        await using (var db = new NepPosDbContext(_options))
            reversed = await new StockReceivingService(db).ReverseAsync(receiptId, "Wrong supplier or invoice", "Sam");

        Assert.Equal("Asahi Super Dry", Assert.Single(reversed.NegativeStock));
        await using var check = new NepPosDbContext(_options);
        var product = await check.Products.SingleAsync(p => p.Id == _asahiId);
        Assert.Equal(-17m, product.StockOnHand);
        // 31 left before reversing: too few to work back from, so the average stays.
        Assert.Equal(StockMath.AverageCost(283, 2.42m, 48, 2.50m), product.CostPrice);
    }

    [Fact]
    public async Task The_database_refuses_the_same_invoice_twice()
    {
        await using var db = new NepPosDbContext(_options);
        db.StockReceipts.Add(new StockReceipt { SupplierId = _supplierId, SupplierName = "CUB", InvoiceNumber = "INV-9", PostedUtc = DateTime.UtcNow });
        db.StockReceipts.Add(new StockReceipt { SupplierId = _supplierId, SupplierName = "CUB", InvoiceNumber = "INV-9", PostedUtc = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    public void Dispose() => _connection.Dispose();
}
