using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Catalog;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

public class ProductCatalogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NepPosDbContext _dbContext;
    private readonly ProductCatalogService _catalog;

    public ProductCatalogServiceTests()
    {
        // In-memory SQLite lives only while the connection stays open.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NepPosDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new NepPosDbContext(options);
        _dbContext.Database.EnsureCreated();

        _dbContext.Products.AddRange(
            new Product { Name = "Heineken 6-Pack", Price = 18.99m, Category = "Beer", IsActive = true },
            new Product { Name = "Corona Extra 6-Pack", Price = 19.50m, Category = "Beer", IsActive = true },
            new Product { Name = "Old Lager", Price = 10.00m, Category = "Beer", IsActive = false },
            new Product { Name = "Discontinued Port", Price = 20.00m, Category = "Fortified", IsActive = false },
            new Product { Name = "Yellow Tail Shiraz", Price = 12.50m, Category = "Wine", IsActive = true });
        _dbContext.SaveChanges();
        _dbContext.ChangeTracker.Clear();

        _catalog = new ProductCatalogService(_dbContext);
    }

    [Fact]
    public async Task GetCategoriesAsync_ReturnsDistinctSortedCategoriesOfActiveProductsOnly()
    {
        var categories = await _catalog.GetCategoriesAsync();

        // "Fortified" has only an inactive product, so it must not appear as a tab.
        Assert.Equal(new[] { "Beer", "Wine" }, categories);
    }

    [Fact]
    public async Task GetProductsByCategoryAsync_ReturnsActiveProductsSortedByName()
    {
        var beers = await _catalog.GetProductsByCategoryAsync("Beer");

        Assert.Equal(new[] { "Corona Extra 6-Pack", "Heineken 6-Pack" }, beers.Select(p => p.Name));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}
