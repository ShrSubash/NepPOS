using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

public sealed class ProductScanResolver : IScanResolver
{
    private static readonly Regex BarcodePattern = new(@"^\d{12,13}$", RegexOptions.Compiled);
    private static readonly Regex PluPattern = new(@"^\d{1,5}$", RegexOptions.Compiled);

    private readonly NepPosDbContext _dbContext;

    public ProductScanResolver(NepPosDbContext dbContext) => _dbContext = dbContext;

    public int Priority => 10;

    public bool CanHandle(string rawCode) =>
        BarcodePattern.IsMatch(rawCode) || PluPattern.IsMatch(rawCode);

    public async Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products
            .FirstOrDefaultAsync(p => p.IsActive && (p.Barcode == rawCode || p.Plu == rawCode), cancellationToken);

        if (product is not null)
            return ScanResolution.Product(product);

        var variant = await _dbContext.ProductVariants
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.IsActive && (v.Barcode == rawCode || v.Plu == rawCode), cancellationToken);

        return variant is not null
            ? new ScanResolution(ScanResultType.Product, variant, null)
            : ScanResolution.NotFound(rawCode);
    }
}