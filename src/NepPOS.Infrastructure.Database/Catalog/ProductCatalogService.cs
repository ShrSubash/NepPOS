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
    private const string LikeEscape = "\\";

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

    public async Task<IReadOnlyList<Product>> SearchAsync(string term, int maxResults = 60)
    {
        term = term?.Trim() ?? string.Empty;
        if (term.Length == 0 || maxResults <= 0)
        {
            return Array.Empty<Product>();
        }

        // Escape LIKE wildcards so "50%" or "6_PK" match literally.
        var escaped = term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var contains = $"%{escaped}%";
        var startsWith = $"{escaped}%";

        // SQLite LIKE is case-insensitive for ASCII, so "hein" finds "Heineken".
        return await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive &&
                        (EF.Functions.Like(p.Name, contains, LikeEscape) ||
                         (p.Plu != null && EF.Functions.Like(p.Plu, startsWith, LikeEscape)) ||
                         (p.Barcode != null && EF.Functions.Like(p.Barcode, startsWith, LikeEscape))))
            .OrderBy(p => EF.Functions.Like(p.Name, startsWith, LikeEscape) ? 0 : 1)
            .ThenBy(p => p.Name)
            .Take(maxResults)
            .ToListAsync();
    }
}
