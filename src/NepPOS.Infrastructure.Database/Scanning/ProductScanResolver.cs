using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Scanning;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>
/// Digits only: a PLU typed on the keypad, or a barcode (EAN-8, UPC-A, EAN-13, GTIN-14). Vouchers (GC-…) and
/// member cards (CUST-…) have letters, so they never reach here. Order: PLU, then the product's or a pack's
/// main barcode, then any extra barcode. Barcodes match with or without leading zeros (a UPC on the bottle
/// as 82184045954 scans as 082184045954).
/// </summary>
public sealed class ProductScanResolver : IScanResolver
{
    private static readonly Regex DigitsPattern = new(@"^\d{1,14}$", RegexOptions.Compiled);

    private readonly NepPosDbContext _dbContext;

    public ProductScanResolver(NepPosDbContext dbContext) => _dbContext = dbContext;

    public int Priority => 10;

    public bool CanHandle(string rawCode) => DigitsPattern.IsMatch(rawCode);

    public async Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        // 1. PLU, exactly as typed.
        var byPlu = await _dbContext.Products.FirstOrDefaultAsync(p => p.IsActive && p.Plu == rawCode, cancellationToken);
        if (byPlu is not null)
            return ScanResolution.Product(byPlu);

        var packByPlu = await _dbContext.ProductVariants.Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.IsActive && v.Product.IsActive && v.Plu == rawCode, cancellationToken);
        if (packByPlu is not null)
            return new ScanResolution(ScanResultType.Product, packByPlu, null);

        // 2. Main barcodes, in the forms the same code can arrive in.
        var forms = BarcodeForms(rawCode);
        var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.IsActive && p.Barcode != null && forms.Contains(p.Barcode), cancellationToken);
        if (product is not null)
            return ScanResolution.Product(product);

        var pack = await _dbContext.ProductVariants.Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.IsActive && v.Product.IsActive && v.Barcode != null && forms.Contains(v.Barcode), cancellationToken);
        if (pack is not null)
            return new ScanResolution(ScanResultType.Product, pack, null);

        // 3. Extra barcodes.
        var key = ProductBarcode.KeyFor(rawCode);
        var extra = await _dbContext.ProductBarcodes
            .Include(b => b.Product)
            .Include(b => b.Variant)
            .FirstOrDefaultAsync(b => b.LookupKey == key && b.Product.IsActive, cancellationToken);
        if (extra is not null)
        {
            if (extra.Variant is { IsActive: true } variant)
            {
                variant.Product = extra.Product;
                return new ScanResolution(ScanResultType.Product, variant, null);
            }
            if (extra.VariantId is null)
                return ScanResolution.Product(extra.Product);
        }

        return ScanResolution.NotFound(rawCode);
    }

    /// <summary>The code as scanned, without leading zeros, and zero-padded to UPC-A, EAN-13 and GTIN-14 lengths.</summary>
    private static List<string> BarcodeForms(string rawCode)
    {
        var digits = rawCode.TrimStart('0');
        var forms = new HashSet<string> { rawCode };
        if (digits.Length > 0)
        {
            forms.Add(digits);
            foreach (var length in new[] { 8, 12, 13, 14 })
                if (digits.Length <= length)
                    forms.Add(digits.PadLeft(length, '0'));
        }
        return forms.ToList();
    }
}
