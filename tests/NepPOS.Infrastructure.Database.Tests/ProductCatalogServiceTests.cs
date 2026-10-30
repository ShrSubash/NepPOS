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
            new Product { Name = "Heineken 6-Pack", Price = 18.99m, Category = "Beer", Plu = "101", Barcode = "2000000001012", IsActive = true },
            new Product { Name = "Corona Extra 6-Pack", Price = 19.50m, Category = "Beer", IsActive = true },
            new Product { Name = "Old Lager", Price = 10.00m, Category = "Beer", IsActive = false },
            new Product { Name = "Discontinued Port", Price = 20.00m, Category = "Fortified", IsActive = false },
            new Product { Name = "Yellow Tail Shiraz", Price = 12.50m, Category = "Wine", IsActive = true },
            new Product { Name = "Extra Dry Riesling", Price = 15.00m, Category = "Wine", IsActive = true });
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

    [Fact]
    public async Task SearchAsync_IsCaseInsensitiveAndMatchesPartOfName()
    {
        var results = await _catalog.SearchAsync("hein");

        Assert.Equal(new[] { "Heineken 6-Pack" }, results.Select(p => p.Name));
    }

    [Fact]
    public async Task SearchAsync_MatchesPluAndBarcodePrefix()
    {
        Assert.Equal("Heineken 6-Pack", Assert.Single(await _catalog.SearchAsync("10")).Name);
        Assert.Equal("Heineken 6-Pack", Assert.Single(await _catalog.SearchAsync("2000000")).Name);
    }

    [Fact]
    public async Task SearchAsync_ExcludesInactiveProducts()
    {
        Assert.Empty(await _catalog.SearchAsync("lager"));
    }

    [Fact]
    public async Task SearchAsync_ListsNamesStartingWithTermFirst()
    {
        // Alphabetically "Corona Extra" comes before "Extra Dry", but "Extra Dry"
        // starts with the term, so it must be listed first.
        var results = await _catalog.SearchAsync("extra");

        Assert.Equal(new[] { "Extra Dry Riesling", "Corona Extra 6-Pack" }, results.Select(p => p.Name));
    }

    [Fact]
    public async Task SearchAsync_TreatsWildcardCharactersLiterally()
    {
        Assert.Empty(await _catalog.SearchAsync("%"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}
