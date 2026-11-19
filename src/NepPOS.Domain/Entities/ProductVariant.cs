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

    /// <summary>What the cashier and the receipt see: "6-pack", "Case of 24". Null = from the pack type.</summary>
    public string? Name { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : PackType switch
        {
            PackType.Pack => $"{UnitQuantity}-pack",
            PackType.FullCase => $"Case of {UnitQuantity}",
            _ => $"Pack of {UnitQuantity}"
        };
    public string? Barcode { get; set; }
    public string? Plu { get; set; }
    public int UnitQuantity { get; set; } = 1;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}