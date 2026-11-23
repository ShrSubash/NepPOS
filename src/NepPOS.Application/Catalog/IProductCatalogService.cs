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

    /// <summary>Departments with active products, A–Z, with how many each has. "" = products with no department.</summary>
    Task<IReadOnlyList<DepartmentSummary>> GetDepartmentsAsync();

    /// <summary>
    /// The products sold most (in singles) over the register's most recent sales, best first: the
    /// automatic Favourites page. Empty for a new shop.
    /// </summary>
    Task<IReadOnlyList<Product>> GetBestSellersAsync(int take = 24);

    /// <summary>Every active product, A–Z: for shops without departments.</summary>
    Task<IReadOnlyList<Product>> GetAllActiveAsync();
}

public sealed record DepartmentSummary(string Name, int ProductCount);
