using NepPOS.Domain.Entities;

namespace NepPOS.Application.Catalog;

/// <summary>
/// CRUD surface for product/PLU management — separate from
/// IProductCatalogService, which is the read-only register-facing
/// lookup. Management operations require Manager+ role, enforced
/// at the Presentation layer route.
/// </summary>
public interface IProductManagementService
{
    Task<IReadOnlyList<Product>> GetAllAsync();
    Task<Product?> GetByIdAsync(int id);
    Task<Product> CreateAsync(Product product);
    Task UpdateAsync(Product product);
    Task DeactivateAsync(int id);
}