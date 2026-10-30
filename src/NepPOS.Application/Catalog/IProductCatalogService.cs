using NepPOS.Domain.Entities;

namespace NepPOS.Application.Catalog;

/// <summary>
/// Read-only, register-facing product lookup (active products only).
/// Presentation depends only on this interface; the SQLite implementation
/// lives in Infrastructure.Database.
/// </summary>
public interface IProductCatalogService
{
    Task<IReadOnlyList<string>> GetCategoriesAsync();
    Task<IReadOnlyList<Product>> GetProductsByCategoryAsync(string category);

    /// <summary>
    /// Finds active products whose name contains the term, or whose PLU/barcode starts with it.
    /// Case-insensitive. Names that start with the term are listed first.
    /// </summary>
    Task<IReadOnlyList<Product>> SearchAsync(string term, int maxResults = 60);
}
