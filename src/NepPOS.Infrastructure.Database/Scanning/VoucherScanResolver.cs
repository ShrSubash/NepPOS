using System.Text.RegularExpressions;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>
/// Gift card (GC-) and store credit (SC-) codes: letters first, so never mistaken for a PLU or barcode.
/// Payload: the code; the sell screen shows its balance.
/// </summary>
public sealed class VoucherScanResolver : IScanResolver
{
    private static readonly Regex VoucherPattern = new(@"^(GC|SC)-\d{6}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public int Priority => 20;

    public bool CanHandle(string rawCode) => VoucherPattern.IsMatch(rawCode);

    public Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ScanResolution(ScanResultType.Voucher, rawCode.Trim().ToUpperInvariant(), null));
    }
}