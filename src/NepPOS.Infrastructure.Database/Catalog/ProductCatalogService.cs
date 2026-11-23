using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

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

    /// <summary>Favourites look at this many recent sales: recent enough to follow the season, cheap to add up.</summary>
    private const int RecentSales = 2000;

    public async Task<IReadOnlyList<DepartmentSummary>> GetDepartmentsAsync()
    {
        var counts = await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .GroupBy(p => p.Category)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts
            .OrderBy(d => d.Name.Length == 0 ? 1 : 0)   // "no department" last
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DepartmentSummary(d.Name, d.Count))
            .ToList();
    }

    public async Task<IReadOnlyList<Product>> GetBestSellersAsync(int take = 24)
    {
        if (take <= 0)
            return Array.Empty<Product>();

        // Invoice numbers only go up, so the highest ones are the latest sales (SQLite can't compare dates
        // stored as DateTimeOffset in a query).
        var top = await _dbContext.SaleTransactions
            .AsNoTracking()
            .Where(s => s.Status == SaleTransactionStatus.Completed && s.InvoiceNumber != null)
            .OrderByDescending(s => s.InvoiceNumber)
            .Take(RecentSales)
            .SelectMany(s => s.LineItems)
            .Where(li => li.ProductId != null)
            .GroupBy(li => li.ProductId!.Value)
            .Select(g => new { ProductId = g.Key, Units = g.Sum(li => li.Quantity * li.PackUnits) })
            .Where(x => x.Units > 0)
            .OrderByDescending(x => x.Units)
            .Take(take * 2)                                // room for products made inactive since
            .ToListAsync();

        if (top.Count == 0)
            return Array.Empty<Product>();

        var ids = top.Select(x => x.ProductId).ToList();
        var products = await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        return top.Where(x => products.ContainsKey(x.ProductId)).Select(x => products[x.ProductId]).Take(take).ToList();
    }

    public async Task<IReadOnlyList<Product>> GetAllActiveAsync() =>
        await _dbContext.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
}
