using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Stock;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Ullages: stock written off at cost, found by code or name, and listed in the history.</summary>
public class WastageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly int _coronaId;

    public WastageServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
        var corona = new Product { Name = "Corona 355ml", Plu = "102", Barcode = "7501064191275", Price = 4.50m, CostPrice = 1.85m, StockOnHand = 48, Category = "Beer" };
        db.Products.Add(corona);
        db.SaveChanges();
        db.ProductVariants.Add(new ProductVariant { ProductId = corona.Id, PackType = PackType.Pack, UnitQuantity = 6, Price = 24m });
        db.SaveChanges();
        _coronaId = corona.Id;
    }

    [Fact]
    public async Task Finds_by_barcode_with_its_pack_sizes()
    {
        await using var db = new NepPosDbContext(_options);
        var found = Assert.Single(await new WastageService(db).FindProductsAsync("7501064191275"));

        Assert.Equal((_coronaId, 1.85m), (found.ProductId, found.CostPerSingle));
        Assert.Equal(new[] { 1, 6 }, found.Packs.Select(p => p.Units).ToArray());
    }

    [Fact]
    public async Task Finds_by_name()
    {
        await using var db = new NepPosDbContext(_options);
        Assert.Single(await new WastageService(db).FindProductsAsync("coro"));
    }

    [Fact]
    public async Task Writing_off_takes_stock_off_at_cost_and_shows_in_history()
    {
        WastageRecorded recorded;
        await using (var db = new NepPosDbContext(_options))
            recorded = await new WastageService(db).RecordAsync(new[]
            {
                new WastageLine(_coronaId, "Corona 355ml", "6-pack", 6, 1, 1.85m),
                new WastageLine(_coronaId, "Corona 355ml", "Single", 1, 2, 1.85m)
            }, "Broken", "Sam");

        Assert.Equal((8m, 14.80m), (recorded.Singles, recorded.Cost));
        Assert.StartsWith("W", recorded.Docket);

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(40m, (await check.Products.SingleAsync(p => p.Id == _coronaId)).StockOnHand);
        var movement = await check.StockMovements.SingleAsync(m => m.Type == StockMovementType.Wastage);
        Assert.Equal((-8m, 1.85m, "Sam"), (movement.Quantity, movement.UnitCost!.Value, movement.EmployeeName));

        var today = DateOnly.FromDateTime(DateTime.Today);
        var entry = Assert.Single(await new WastageService(check).HistoryAsync(today, today));
        Assert.Equal(("Corona 355ml", 8m, 14.80m, "Broken"), (entry.ProductName, entry.Singles, entry.Cost, entry.Reason));
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        await using var db = new NepPosDbContext(_options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WastageService(db).RecordAsync(
            new[] { new WastageLine(_coronaId, "Corona 355ml", "Single", 1, 1, 1.85m) }, " ", "Sam"));
    }

    public void Dispose() => _connection.Dispose();
}
