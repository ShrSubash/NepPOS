using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Promotions;
using NepPOS.Infrastructure.Database.Sales;
using NepPOS.Infrastructure.Database.Stock;
using NepPOS.Infrastructure.Database.Sync;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Hold/recall, stocktakes, purchase orders, promotions, discounts saved on lines, and stock sent to Head Office.</summary>
public class OpsBundleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly int _asahiId;
    private readonly int _chipsId;
    private readonly int _supplierId;

    public OpsBundleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
        var supplier = new Supplier { Name = "CUB" };
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        var asahi = new Product { Name = "Asahi Super Dry", Price = 5.50m, CostPrice = 2.40m, StockOnHand = 30, Category = "Beer", SupplierId = supplier.Id, ReorderPoint = 48 };
        var chips = new Product { Name = "Chips", Price = 3m, CostPrice = 1m, StockOnHand = 10, Category = "Snacks" };
        db.Products.AddRange(asahi, chips);
        db.SaveChanges();
        db.ProductVariants.Add(new ProductVariant { ProductId = asahi.Id, PackType = PackType.FullCase, UnitQuantity = 24, Price = 60m });
        db.SaveChanges();
        (_asahiId, _chipsId, _supplierId) = (asahi.Id, chips.Id, supplier.Id);
    }

    [Fact]
    public async Task Held_sale_comes_back_once()
    {
        Guid id;
        await using (var db = new NepPosDbContext(_options))
            id = (await new HeldSaleService(db).HoldAsync("Table 4", 11m, 2, "{\"lines\":[]}", "Sam")).Id;

        await using (var db = new NepPosDbContext(_options))
        {
            var service = new HeldSaleService(db);
            Assert.Single(await service.ListAsync());
            var recalled = await service.RecallAsync(id);
            Assert.Equal(("Table 4", "{\"lines\":[]}"), (recalled!.Label, recalled.CartJson));
            Assert.Null(await service.RecallAsync(id));
            Assert.Empty(await service.ListAsync());
        }
    }

    [Fact]
    public async Task Stocktake_sets_counted_products_and_leaves_the_rest()
    {
        await using (var db = new NepPosDbContext(_options))
        {
            var service = new StocktakeService(db);
            var count = await service.StartAsync("Beer fridge", "Sam");
            await service.CountAsync(count.Id, _asahiId, 24, replace: false);
            var row = await service.CountAsync(count.Id, _asahiId, 2, replace: false);
            Assert.Equal((26m, 30m, -4m), (row.Counted, row.Expected, row.Difference));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync("Second", "Sam"));

            var posted = await service.PostAsync(count.Id, "Sam");
            Assert.Equal((1, 0m, 4m, -9.60m), (posted.Products, posted.UnitsUp, posted.UnitsDown, posted.Value));
        }

        await using (var db = new NepPosDbContext(_options))
        {
            Assert.Equal(26m, (await db.Products.FindAsync(_asahiId))!.StockOnHand);
            Assert.Equal(10m, (await db.Products.FindAsync(_chipsId))!.StockOnHand);
            Assert.Single(db.StockMovements.Where(m => m.Type == StockMovementType.Stocktake));
            Assert.Null(await new StocktakeService(db).CurrentAsync());
        }
    }

    [Fact]
    public async Task Purchase_order_suggests_by_the_case_and_receives_what_arrived()
    {
        await using var db = new NepPosDbContext(_options);
        var service = new PurchaseOrderService(db);

        // 30 on hand, reorder at 48: top up to 96 = 66 singles = 3 cases of 24.
        var suggestion = Assert.Single(await service.SuggestAsync(_supplierId));
        Assert.Equal(("Asahi Super Dry", 24, 3m, 57.60m), (suggestion.ProductName, suggestion.PackUnits, suggestion.Packs, suggestion.CostPerPack));

        var order = new PurchaseOrder { SupplierId = _supplierId, EmployeeName = "Sam" };
        order.Lines.Add(new PurchaseOrderLine
        {
            ProductId = suggestion.ProductId, ProductName = suggestion.ProductName, PackName = suggestion.PackName,
            PackUnits = suggestion.PackUnits, Quantity = suggestion.Packs, CostPerPack = suggestion.CostPerPack
        });
        var saved = await service.SaveDraftAsync(order);
        Assert.Equal(("PO-00001", "CUB"), (saved.Number, saved.SupplierName));
        await service.MarkSentAsync(saved.Id);

        var lineId = saved.Lines.Single().Id;
        var posted = await service.ReceiveAsync(saved.Id, "INV-77", new DateOnly(2026, 11, 24), new Dictionary<int, decimal> { [lineId] = 2 }, "Sam");

        Assert.Equal(48m, posted.Singles);
        Assert.Equal(78m, (await db.Products.AsNoTracking().FirstAsync(p => p.Id == _asahiId)).StockOnHand);
        var received = await service.GetAsync(saved.Id);
        Assert.Equal((PurchaseOrderStatus.Received, posted.ReceiptId), (received!.Status, received.StockReceiptId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(saved.Id));
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task Reorder_levels_are_saved_per_product()
    {
        await using var db = new NepPosDbContext(_options);
        var service = new PurchaseOrderService(db);

        await service.SetReorderLevelAsync(_asahiId, 12, 48);
        var level = Assert.Single(await service.ReorderLevelsAsync(_supplierId));

        Assert.Equal((12m, 48m), (level.ReorderPoint, level.ReorderQuantity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetReorderLevelAsync(_asahiId, -1, null));
    }

    [Fact]
    public async Task Promotions_save_and_only_active_ones_reach_the_register()
    {
        await using var db = new NepPosDbContext(_options);
        var service = new PromotionService(db);

        await service.SaveAsync(new Promotion { Name = "2 for $5", Kind = PromotionKind.MultiBuy, BuyQuantity = 2, Price = 5m, ProductIds = _chipsId.ToString(), IsActive = true });
        await service.SaveAsync(new Promotion { Name = "Old", Kind = PromotionKind.PercentOff, Percent = 10, Category = "Beer", IsActive = false });

        Assert.Equal(2, (await service.ListAsync()).Count);
        Assert.Equal("2 for $5", Assert.Single(await service.ActiveAsync()).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new Promotion { Name = "Nothing on", Kind = PromotionKind.PercentOff, Percent = 10, IsActive = true }));
    }

    [Fact]
    public async Task Line_discount_is_saved_with_the_sale()
    {
        Guid saleId;
        await using (var db = new NepPosDbContext(_options))
        {
            var sale = new SaleTransaction("STORE-001", "REG-001");
            sale.AddLineItem(new SaleLineItem("CH", "Chips", 2, 3m, 0.10m, productId: _chipsId, discount: 1m, discountReason: "2 for $5"));
            sale.Complete(1);
            db.SaleTransactions.Add(sale);
            await db.SaveChangesAsync();
            saleId = sale.Id;
        }

        await using (var db = new NepPosDbContext(_options))
        {
            var line = (await db.SaleTransactions.Include(s => s.LineItems).FirstAsync(s => s.Id == saleId)).LineItems.First();
            Assert.Equal((1m, "2 for $5", 5m), (line.Discount, line.DiscountReason, line.LineTotalIncGst));
        }
    }

    [Fact]
    public async Task Stock_changes_are_queued_once()
    {
        await using var db = new NepPosDbContext(_options);
        var publisher = new StockPublisher(db, new RegisterTerminal("STORE-001", "REG-001"));

        Assert.Equal(2, await publisher.QueueChangesAsync());   // never sent before
        Assert.Equal(0, await publisher.QueueChangesAsync());

        var chips = await db.Products.FirstAsync(p => p.Id == _chipsId);
        chips.StockOnHand = 7;
        await db.SaveChangesAsync();

        Assert.Equal(1, await publisher.QueueChangesAsync());
        Assert.Equal(3, await db.OutboxMessages.CountAsync(m => m.Type == SyncEventTypes.StockChanged));
    }

    public void Dispose() => _connection.Dispose();
}
