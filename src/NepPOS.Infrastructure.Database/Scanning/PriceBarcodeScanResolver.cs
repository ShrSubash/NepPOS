using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Hardware;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>
/// Deli and meat labels with the price in the barcode (see <see cref="PriceBarcodes"/>). Runs before the normal
/// product lookup; a product that really has this exact barcode still wins, so in-store barcodes the shop made
/// itself keep working.
/// </summary>
public sealed class PriceBarcodeScanResolver : IScanResolver
{
    private readonly NepPosDbContext _dbContext;
    private readonly IRegisterSettingsStore _settings;

    public PriceBarcodeScanResolver(NepPosDbContext dbContext, IRegisterSettingsStore settings)
    {
        _dbContext = dbContext;
        _settings = settings;
    }

    public int Priority => 5;

    public bool CanHandle(string rawCode) =>
        rawCode.Length == PriceBarcodes.Length && rawCode[0] == '2' && _settings.Load().PriceBarcodes.Enabled;

    public async Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        var exact = await ProductScanResolver.FindByBarcodeAsync(_dbContext, rawCode, cancellationToken);
        if (exact is not null)
            return new ScanResolution(ScanResultType.Product, exact, null);

        if (!PriceBarcodes.TryRead(rawCode, _settings.Load().PriceBarcodes, out var itemCode, out var price))
            return ScanResolution.NotFound(rawCode);

        var product = await _dbContext.Products.FirstOrDefaultAsync(p => p.IsActive && p.Plu == itemCode, cancellationToken);
        return product is null
            ? new ScanResolution(ScanResultType.NotFound, null, $"Price label for item {itemCode}: no product has PLU {itemCode}.")
            : new ScanResolution(ScanResultType.Product, new PricedItem(product, price, rawCode), null);
    }
}
