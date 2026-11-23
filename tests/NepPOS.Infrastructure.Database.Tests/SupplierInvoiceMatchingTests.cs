using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Stock;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

public class SupplierInvoiceMatchingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly int _poetsId;
    private readonly int _suntoryTenPackId;
    private readonly int _coopersId;

    public SupplierInvoiceMatchingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
        // Poets: barcode with leading zero dropped. Suntory: the shop sells the 10-pack as one item.
        var poets = new Product { Name = "Poets Crisp 375ml", Price = 3.50m, Barcode = "9347937002587", Category = "Cider" };
        var suntory = new Product { Name = "-196 Lemon 10pk", Price = 52.00m, Barcode = "9316417007961", Category = "RTD" };
        var coopers = new Product { Name = "Coopers Pale Ale 375ml", Price = 4.20m, Category = "Beer" };
        db.Products.AddRange(poets, suntory, coopers);
        db.SaveChanges();
        (_poetsId, _suntoryTenPackId, _coopersId) = (poets.Id, suntory.Id, coopers.Id);
    }

    private static SupplierInvoiceLine Line(int no, string code, string? barcode, int multiple, int units, decimal costExGst) =>
        new(no, code, code, $"ITEM {code}", barcode, multiple, 24, units, costExGst, Math.Round(costExGst / 10, 2), false);

    private static SupplierInvoice Invoice(string number, params SupplierInvoiceLine[] lines) =>
        new("ALM", "11112222", number, new DateOnly(2026, 8, 17), null, lines);

    private static readonly SupplierInvoiceLine Poets = Line(1, "00069664", "9347937002587", 1, 120, 180.80m);
    private static readonly SupplierInvoiceLine Suntory = Line(6, "00283386", "9316417007961", 10, 30, 109.53m);
    private static readonly SupplierInvoiceLine Coopers = Line(8, "00504460", "93302036", 1, 216, 461.43m);
    private static readonly SupplierInvoiceLine Handles = Line(9, "00724235", "7242359", 1, 336, 157.41m);

    [Fact]
    public async Task Matches_by_barcode_and_leaves_unknown_lines_for_the_user()
    {
        await using var db = new NepPosDbContext(_options);
        var match = await new StockReceivingService(db).MatchInvoiceAsync(Invoice("SI1", Poets, Suntory, Coopers));

        Assert.Null(match.SupplierId);           // ALM isn't a supplier yet
        Assert.Equal("ALM", match.SupplierName);
        Assert.Equal((InvoiceLineMatch.Barcode, _poetsId, 1), (match.Lines[0].MatchedBy, match.Lines[0].Product!.Id, match.Lines[0].UnitsPerSingle));
        // 30 cans of a 10-pack barcode on a product that IS the 10-pack: 10 invoiced units per single.
        Assert.Equal((InvoiceLineMatch.Barcode, _suntoryTenPackId, 10), (match.Lines[1].MatchedBy, match.Lines[1].Product!.Id, match.Lines[1].UnitsPerSingle));
        Assert.Equal(InvoiceLineMatch.None, match.Lines[2].MatchedBy);
    }

    [Fact]
    public async Task Posting_adds_the_supplier_and_remembers_codes_and_skips_for_next_time()
    {
        await using (var db = new NepPosDbContext(_options))
        {
            var input = new ReceiptInput(null, "ALM", "SI1", new DateOnly(2026, 8, 17), new[]
            {
                new ReceiptLineInput(_poetsId, 1, "Single", 120, 1.5067m) { SupplierItemCode = Poets.ItemCode },
                new ReceiptLineInput(_suntoryTenPackId, 1, "Single", 3, 36.51m) { SupplierItemCode = Suntory.ItemCode, SupplierUnitsPerSingle = 10 },
                // Linked by hand on the screen: the invoice had no barcode the shop knew.
                new ReceiptLineInput(_coopersId, 24, "Case of 24", 9, 51.27m) { SupplierItemCode = Coopers.ItemCode },
            })
            {
                AddSupplier = true,
                SupplierAccountNumber = "11112222",
                SkipAlways = new[] { new SkipItem(Handles.ItemCode, "PakTech 6pk Can Handle") }
            };
            await new StockReceivingService(db).PostAsync(input, "Sam");
        }

        await using var check = new NepPosDbContext(_options);
        var alm = await check.Suppliers.SingleAsync();
        Assert.Equal(("ALM", "11112222"), (alm.Name, alm.AccountNumber));
        Assert.Equal(120m, (await check.Products.SingleAsync(p => p.Id == _poetsId)).StockOnHand);
        Assert.Equal(3m, (await check.Products.SingleAsync(p => p.Id == _suntoryTenPackId)).StockOnHand);

        // The next invoice from ALM: found by account number, every line known without asking.
        var next = await new StockReceivingService(check).MatchInvoiceAsync(Invoice("SI2", Poets, Suntory, Coopers, Handles));
        Assert.Equal(alm.Id, next.SupplierId);
        Assert.Equal(new[] { InvoiceLineMatch.SupplierCode, InvoiceLineMatch.SupplierCode, InvoiceLineMatch.SupplierCode, InvoiceLineMatch.Skipped },
            next.Lines.Select(l => l.MatchedBy));
        Assert.Equal(_coopersId, next.Lines[2].Product!.Id);
        Assert.Equal(10, next.Lines[1].UnitsPerSingle);
        Assert.False(next.AlreadyReceived);

        var again = await new StockReceivingService(check).MatchInvoiceAsync(Invoice("SI1", Poets));
        Assert.True(again.AlreadyReceived);
    }

    [Fact]
    public async Task Lines_with_nothing_supplied_need_no_action()
    {
        await using var db = new NepPosDbContext(_options);
        var match = await new StockReceivingService(db).MatchInvoiceAsync(Invoice("SI3", Line(1, "00069664", "9347937002587", 1, 0, 0)));

        Assert.Equal(InvoiceLineMatch.NotSupplied, Assert.Single(match.Lines).MatchedBy);
    }

    public void Dispose() => _connection.Dispose();
}
