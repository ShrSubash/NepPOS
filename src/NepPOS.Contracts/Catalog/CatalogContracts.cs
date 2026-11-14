using NepPOS.Contracts.Sync;

namespace NepPOS.Contracts.Catalog;

/// <summary>
/// One product as Head Office holds it for a store: the latest <c>product.changed</c> from its registers.
/// The website keeps its own photos, descriptions and "show on website" choice; these fields are the POS's.
/// </summary>
/// <param name="ProductId">The product's id in every system. The website stores it to match its own product.</param>
/// <param name="Name">Name on the register.</param>
/// <param name="Category">Department, e.g. Beer.</param>
/// <param name="Price">Shelf price, AUD inc GST.</param>
/// <param name="Barcode">Main barcode, if any.</param>
/// <param name="Plu">Register PLU or SKU, if any.</param>
/// <param name="EnableOnWebsite">The register's "Enable on website" tick, as a hint only. The website decides what it shows.</param>
/// <param name="IsActive">False when the product was deactivated at the register: stop selling it online too.</param>
/// <param name="Variants">Other pack sizes with their own barcode and price.</param>
/// <param name="UpdatedAtUtc">When the register changed it.</param>
/// <param name="ChangeNumber">Counts up across the store's catalog. Ask for changes after the highest one you have.</param>
public sealed record CatalogProduct(
    Guid ProductId,
    string Name,
    string Category,
    decimal Price,
    string? Barcode,
    string? Plu,
    bool EnableOnWebsite,
    bool IsActive,
    IReadOnlyList<ProductVariantV1> Variants,
    DateTime UpdatedAtUtc,
    long ChangeNumber);

/// <summary>
/// GET /api/v1/catalog/changes?storeId=…&amp;after=…: products changed after a change number, oldest first.
/// Keep <paramref name="Next"/> and send it as <c>after</c> next time; while <paramref name="More"/> is true,
/// ask again straight away. <c>after=0</c> returns the whole catalog.
/// </summary>
public sealed record CatalogChanges(IReadOnlyList<CatalogProduct> Products, long Next, bool More)
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 1000;
}
