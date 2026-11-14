using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Catalog;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Catalog link: every product add, edit or deactivate leaves one product.changed event for Head Office.</summary>
public class ProductSyncTests : IDisposable
{
    private static readonly RegisterTerminal Terminal = new("STORE-001", "REG-001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;

    public ProductSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
    }

    private async Task<List<ProductChangedV1>> EventsAsync()
    {
        await using var db = new NepPosDbContext(_options);
        var messages = await db.OutboxMessages.OrderBy(m => m.Id).ToListAsync();
        Assert.All(messages, m => Assert.Equal((SyncEventTypes.ProductChanged, "STORE-001", "REG-001"), (m.Type, m.StoreId, m.RegisterId)));
        return messages.Select(m => JsonSerializer.Deserialize<ProductChangedV1>(m.Payload, SyncJson.Options)!).ToList();
    }

    [Fact]
    public async Task Create_SendsTheWholeProduct_ButNotCostPrice()
    {
        await using (var db = new NepPosDbContext(_options))
            await new ProductManagementService(db, Terminal).CreateAsync(new Product
            {
                Name = "Heineken 6-Pack", Category = "Beer", Price = 22.00m, CostPrice = 15.50m,
                Barcode = "9300000000011", Plu = " ", EnableOnWebsite = true
            });

        var sent = Assert.Single(await EventsAsync());
        Assert.NotEqual(Guid.Empty, sent.ProductId);
        Assert.Equal(("Heineken 6-Pack", "Beer", 22.00m, "9300000000011", true, true),
            (sent.Name, sent.Category, sent.Price, sent.Barcode, sent.EnableOnWebsite, sent.IsActive));
        Assert.Null(sent.Plu);      // a blank PLU is sent as "none"

        await using var check = new NepPosDbContext(_options);
        Assert.DoesNotContain("15.5", (await check.OutboxMessages.SingleAsync()).Payload);
    }

    [Fact]
    public async Task Edit_KeepsTheProductsId_EvenThoughTheScreenSendsACopy()
    {
        Product created;
        await using (var db = new NepPosDbContext(_options))
            created = await new ProductManagementService(db, Terminal).CreateAsync(new Product { Name = "Coke 1.25L", Category = "Soft drink", Price = 4.00m });

        // As PluManagement does: a new Product with the same Id, which gets a fresh SyncId of its own.
        await using (var db = new NepPosDbContext(_options))
        {
            var service = new ProductManagementService(db, Terminal);
            await service.GetAllAsync();
            await service.UpdateAsync(new Product { Id = created.Id, Name = "Coke 1.25L", Category = "Soft drink", Price = 4.50m, IsActive = true });
            await service.DeactivateAsync(created.Id);
        }

        var events = await EventsAsync();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(created.SyncId, e.ProductId));
        Assert.Equal((4.50m, true), (events[1].Price, events[1].IsActive));
        Assert.False(events[2].IsActive);

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(created.SyncId, (await check.Products.SingleAsync()).SyncId);
    }

    [Fact]
    public async Task SendAll_GivesOldProductsAnId_AndQueuesEveryProductWithItsPackSizes()
    {
        await using (var db = new NepPosDbContext(_options))
        {
            // Products made before this version have no SyncId yet (the migration leaves it empty).
            var old = new Product { Name = "VB Stubby", Category = "Beer", Price = 4.00m, SyncId = Guid.Empty };
            db.Products.AddRange(old, new Product { Name = "Old Wine", Category = "Wine", Price = 15m, IsActive = false });
            await db.SaveChangesAsync();
            db.ProductVariants.Add(new ProductVariant { ProductId = old.Id, PackType = PackType.FullCase, Barcode = "9300000000099", UnitQuantity = 24, Price = 65m });
            await db.SaveChangesAsync();
        }

        int count;
        await using (var db = new NepPosDbContext(_options))
            count = await new ProductManagementService(db, Terminal).SendAllToHeadOfficeAsync();

        var events = await EventsAsync();
        Assert.Equal(2, count);
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.ProductId == Guid.Empty);
        var vb = events.Single(e => e.Name == "VB Stubby");
        Assert.Equal((24, 65m), (Assert.Single(vb.Variants).UnitQuantity, vb.Variants[0].Price));
        Assert.False(events.Single(e => e.Name == "Old Wine").IsActive);

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(vb.ProductId, (await check.Products.SingleAsync(p => p.Name == "VB Stubby")).SyncId);
    }

    public void Dispose() => _connection.Dispose();
}
