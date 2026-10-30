using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Catalog;

/// <summary>
/// SQLite-backed register catalog. Reads the same Products table that
/// PLU management writes and ProductScanResolver searches, so the grid,
/// scanning and the back office always agree.
/// </summary>
public sealed class ProductCatalogService : IProductCatalogService
{
    private readonly NepPosDbContext _dbContext;

    public ProductCatalogService(NepPosDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        // Read-only lookups: no tracking, so they never clash with PLU edits
        // made through the same app-lifetime DbContext.
        return await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive && p.Category != "")
            .Select(p => p.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Product>> GetProductsByCategoryAsync(string category)
    {
        ArgumentNullException.ThrowIfNull(category);

        return await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive && p.Category == category)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }
}
