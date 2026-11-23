using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Catalog;
using NepPOS.Application.Scanning;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Catalog;
using NepPOS.Infrastructure.Database.Scanning;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Stock ledger, pack barcodes and the old POS import, against a real (in-memory) database.</summary>
public class StockLedgerTests : IDisposable
{
    private static readonly RegisterTerminal Terminal = new("STORE-001", "REG-001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;

    public StockLedgerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
    }

    private const string OldPosProducts =
        "Number\tdescription\tlongdescrip\tSOH\tpacksize\tsplitsize\taveragecost\tsingleprice\tsplitprice\tpackprice\tprice4\tsplit2price\tsupplier\tdept\tStatus\tscancodes1\tscancodes2\tscancodes3\tscancodes4\tsuppcodes1\ttaxcode\n" +
        "37\tASAHI SUPER DRY 5%\tASAHI SUPER DRY\t283\t24\t6\t2.42\t5.50\t0\t72.99\t27.99\t0\t3\t3\t3\t9329982021065     P7\t9329982021072     P3\t9329982021058     P1\t4901004021861     P1\tCUB94148\tB\n" +
        "935\tJACK DANIELS\tJACK DANIELS BOURBON 700ML\t4\t12\t6\t47.58\t64.99\t0\t0\t0\t0\t2\t11\t3\t82184045954       P1\t\t\t\tILG3050600P\tB\n";

    private async Task ImportAsync()
    {
        var plan = LegacyPluImport.Plan(OldPosProducts, "Number\tdpn\n3\tBEER IMPORTED\n11\tSPIRITS\n", "Number\tsupplier\n2\tILG\n3\tCUB\n",
            Array.Empty<string>(), Array.Empty<string>());
        await using var db = new NepPosDbContext(_options);
        Assert.Equal(2, await new ProductManagementService(db, Terminal).ImportLegacyAsync(plan, "Owner"));
    }

    private async Task<ScanResolution> ScanAsync(string code)
    {
        await using var db = new NepPosDbContext(_options);
        return await new ProductScanResolver(db).ResolveAsync(code);
    }

    [Fact]
    public async Task The_old_POS_import_saves_packs_barcodes_suppliers_and_opening_stock()
    {
        await ImportAsync();

        await using var db = new NepPosDbContext(_options);
        var asahi = await db.Products.SingleAsync(p => p.Plu == "37");
        Assert.Equal(("ASAHI SUPER DRY", 5.50m, 283m, "BEER IMPORTED", "9329982021058"), (asahi.Name, asahi.Price, asahi.StockOnHand, asahi.Category, asahi.Barcode));

        var packs = await db.ProductVariants.Where(v => v.ProductId == asahi.Id).OrderBy(v => v.UnitQuantity).ToListAsync();
        Assert.Equal(new[] { (6, 27.99m, "9329982021065"), (24, 72.99m, "9329982021072") }, packs.Select(v => (v.UnitQuantity, v.Price, v.Barcode!)));

        var extra = Assert.Single(await db.ProductBarcodes.Where(b => b.ProductId == asahi.Id).ToListAsync());
        Assert.Equal(("4901004021861", (int?)null), (extra.Code, extra.VariantId));

        var supplier = await db.Suppliers.SingleAsync(s => s.Id == asahi.SupplierId);
        Assert.Equal(("CUB", "3"), (supplier.Name, supplier.LegacyNumber));
        Assert.Equal("CUB94148", (await db.ProductSupplierCodes.SingleAsync(c => c.ProductId == asahi.Id)).Code);

        var opening = await db.StockMovements.SingleAsync(m => m.ProductId == asahi.Id);
        Assert.Equal((StockMovementType.Opening, 283m, 2.42m), (opening.Type, opening.Quantity, opening.UnitCost!.Value));
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Every_barcode_scans_to_its_pack_with_or_without_leading_zeros()
    {
        await ImportAsync();

        Assert.IsType<Product>((await ScanAsync("4901004021861")).Payload);                       // extra single barcode
        Assert.Equal(24, Assert.IsType<ProductVariant>((await ScanAsync("9329982021072")).Payload).UnitQuantity);
        Assert.Equal("935", Assert.IsType<Product>((await ScanAsync("082184045954")).Payload).Plu);   // UPC scanned with its leading 0
        Assert.Equal("37", Assert.IsType<Product>((await ScanAsync("37")).Payload).Plu);              // PLU typed
        Assert.Equal(ScanResultType.NotFound, (await ScanAsync("9999999999999")).Type);
    }

    [Fact]
    public async Task Selling_a_case_takes_24_singles_off_and_says_why()
    {
        await ImportAsync();
        int asahiId;
        await using (var db = new NepPosDbContext(_options))
            asahiId = (await db.Products.SingleAsync(p => p.Plu == "37")).Id;

        var sale = new SaleTransaction("STORE-001", "REG-001", 1, "Sam");
        sale.AddLineItem(new SaleLineItem("37", "Asahi (Case of 24)", 1, 72.99m, 0.10m, "Beer", asahiId, 24));
        sale.AddLineItem(new SaleLineItem("37", "Asahi", 2, 5.50m, 0.10m, "Beer", asahiId, 1));
        sale.Complete(1042);
        await using (var db = new NepPosDbContext(_options))
            await new SaleTransactionRepository(db).AddAsync(sale);

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(283m - 26m, (await check.Products.SingleAsync(p => p.Id == asahiId)).StockOnHand);
        var movement = await check.StockMovements.SingleAsync(m => m.ProductId == asahiId && m.Type == StockMovementType.Sale);
        Assert.Equal((-26m, "Invoice 1042", "Sam"), (movement.Quantity, movement.Reference, movement.EmployeeName));
    }

    [Fact]
    public async Task Changing_stock_by_hand_records_the_difference()
    {
        await ImportAsync();
        await using (var db = new NepPosDbContext(_options))
        {
            var service = new ProductManagementService(db, Terminal);
            var jack = (await service.GetAllAsync()).Single(p => p.Plu == "935");
            await service.UpdateAsync(new Product { Id = jack.Id, Name = jack.Name, Plu = jack.Plu, Price = jack.Price, Category = jack.Category, StockOnHand = 10, IsActive = true, SupplierId = jack.SupplierId });
        }

        await using var check = new NepPosDbContext(_options);
        var jackId = (await check.Products.SingleAsync(p => p.Plu == "935")).Id;
        Assert.Equal(10m, (await check.Products.SingleAsync(p => p.Id == jackId)).StockOnHand);
        var adjustment = await check.StockMovements.SingleAsync(m => m.ProductId == jackId && m.Type == StockMovementType.Adjustment);
        Assert.Equal(6m, adjustment.Quantity);
        Assert.Equal(10m, await check.StockMovements.Where(m => m.ProductId == jackId).SumAsync(m => m.Quantity));
    }

    [Fact]
    public async Task Importing_again_leaves_existing_products_alone()
    {
        await ImportAsync();
        await using var db = new NepPosDbContext(_options);
        var service = new ProductManagementService(db, Terminal);

        var plan = LegacyPluImport.Plan(OldPosProducts, null, null,
            (await service.GetAllAsync()).Select(p => p.Plu ?? "").ToList(), await service.GetAllBarcodesAsync());

        Assert.Empty(plan.New);
        Assert.Equal(2, plan.AlreadyHere);
    }

    public void Dispose() => _connection.Dispose();
}
