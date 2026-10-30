using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Catalog;

/// <summary>
/// Temporary in-memory catalog so the register UI is testable
/// before the real Product table/migration exists. Replace with
/// a SQLite-backed implementation once inventory scoping lands —
/// no changes needed in Presentation when that swap happens.
/// </summary>
public sealed class InMemoryProductCatalogService : IProductCatalogService
{
    private static readonly List<Product> _products = new()
    {
        new Product { Id = 1, Name = "Heineken 6-Pack", Price = 18.99m, Category = "Beer", ImageUrl = "images/products/heineken.png" },
        new Product { Id = 2, Name = "Jack Daniel's 700ml", Price = 54.00m, Category = "Spirits", ImageUrl = "images/products/jackdaniels.png" },
        new Product { Id = 3, Name = "Yellow Tail Shiraz", Price = 12.50m, Category = "Wine", ImageUrl = "images/products/shiraz.png" },
        new Product { Id = 4, Name = "Coca-Cola 375ml", Price = 3.20m, Category = "Soft Drink", ImageUrl = "images/products/coke.png" },
        new Product { Id = 5, Name = "Smirnoff Ice 4-Pack", Price = 16.00m, Category = "RTD", ImageUrl = "images/products/smirnoffice.png" },
        new Product { Id = 6, Name = "Corona Extra 6-Pack", Price = 19.50m, Category = "Beer", ImageUrl = "images/products/corona.png" },
    };

    public Task<Product?> FindByCodeAsync(string code)
    {
        var match = _products.FirstOrDefault(p => p.Barcode == code || p.Plu == code);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        var categories = _products.Select(p => p.Category).Distinct().OrderBy(c => c).ToList();
        return Task.FromResult<IReadOnlyList<string>>(categories);
    }

    public Task<IReadOnlyList<Product>> GetProductsByCategoryAsync(string category)
    {
        var results = _products.Where(p => p.Category == category).ToList();
        return Task.FromResult<IReadOnlyList<Product>>(results);
    }
}