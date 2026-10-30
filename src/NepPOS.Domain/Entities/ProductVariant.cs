using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

/// <summary>
/// Optional additional pack size for a Product beyond its base
/// single-unit Price/Barcode/Plu. A product with no variants sells
/// only as its base unit; each variant adds another scannable size.
/// </summary>
public sealed class ProductVariant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public PackType PackType { get; set; }
    public string? Barcode { get; set; }
    public string? Plu { get; set; }
    public int UnitQuantity { get; set; } = 1;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}