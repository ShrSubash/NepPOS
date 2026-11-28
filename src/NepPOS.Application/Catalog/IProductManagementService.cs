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

    /// <summary>
    /// Deletes deactivated products that were never used: never sold, no stock changes but their opening stock, never on a delivery,
    /// order, count, transfer or web order. Anything with history is kept (deactivated) so reports and GST
    /// records stay right. Returns how many were deleted and how many were kept.
    /// </summary>
    Task<(int Deleted, int Kept)> DeleteUnusedAsync(IReadOnlyCollection<int> ids);

    /// <summary>
    /// Saves a confirmed CSV import (<see cref="ProductCsv.Plan"/>) in one go: every new and changed product,
    /// each with its event for Head Office. All or nothing.
    /// </summary>
    Task<(int Created, int Updated)> ImportAsync(ProductImportPlan plan);

    /// <summary>Every barcode in use: products', packs' and extra barcodes. For checking an import before it's saved.</summary>
    Task<IReadOnlyList<string>> GetAllBarcodesAsync();

    /// <summary>
    /// Saves a confirmed import from the old POS (<see cref="LegacyPluImport.Plan"/>) in one go: products,
    /// packs, barcodes, suppliers and their item codes, and each product's stock as an opening movement.
    /// All or nothing.
    /// </summary>
    Task<int> ImportLegacyAsync(LegacyImportPlan plan, string? employeeName);

    /// <summary>Queues every product for Head Office (first connection). Returns how many.</summary>
    Task<int> SendAllToHeadOfficeAsync();
}