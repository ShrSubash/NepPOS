namespace NepPOS.Domain.Entities;

/// <summary>
/// An extra barcode for a product, or for one of its packs. A product (or pack) can have as many as it
/// needs: the same beer often has a different barcode for each brewery run or importer. The product's
/// own <see cref="Product.Barcode"/> and a pack's <see cref="ProductVariant.Barcode"/> stay the main ones.
/// </summary>
public sealed class ProductBarcode
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The pack this barcode sells. Null = the product's single unit.</summary>
    public int? VariantId { get; set; }
    public ProductVariant? Variant { get; set; }

    /// <summary>As printed on the item.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>What scans are matched on: see <see cref="KeyFor"/>. Unique.</summary>
    public string LookupKey { get; set; } = string.Empty;

    /// <summary>
    /// Digits without leading zeros, so a UPC printed as 82184045954 is found when the scanner sends
    /// 082184045954 or 0082184045954. Codes with letters are compared in upper case.
    /// </summary>
    public static string KeyFor(string code)
    {
        var trimmed = code.Trim();
        if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit))
        {
            var digits = trimmed.TrimStart('0');
            return digits.Length == 0 ? "0" : digits;
        }

        return trimmed.ToUpperInvariant();
    }
}
