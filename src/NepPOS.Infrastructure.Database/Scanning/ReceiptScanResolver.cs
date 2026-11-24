using NepPOS.Application.Sales;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>The barcode on a receipt (RT001-19): scanned on the sell screen, it opens that sale for a return.</summary>
public sealed class ReceiptScanResolver : IScanResolver
{
    public int Priority => 20;

    public bool CanHandle(string rawCode) => ReceiptCode.Looks(rawCode);

    public Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScanResolution(ScanResultType.Receipt, rawCode.Trim().ToUpperInvariant(), null));
}
