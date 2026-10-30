using NepPOS.Domain.Entities;

namespace NepPOS.Application.Catalog;

/// <summary>
/// Abstraction over "where products come from" — in-memory today,
/// SQLite-backed once inventory scoping is finalized. Presentation
/// depends only on this interface.
/// </summary>
public interface IProductCatalogService
{
    Task<IReadOnlyList<string>> GetCategoriesAsync();
    Task<IReadOnlyList<Product>> GetProductsByCategoryAsync(string category);
}