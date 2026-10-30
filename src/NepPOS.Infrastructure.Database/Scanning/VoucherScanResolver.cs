using System.Text.RegularExpressions;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>
/// Handles gift/voucher codes — distinct "GC-" prefix keeps these
/// unambiguous against numeric-only barcodes and PLUs.
/// </summary>
public sealed class VoucherScanResolver : IScanResolver
{
    private static readonly Regex VoucherPattern = new(@"^GC-\d{6}$", RegexOptions.Compiled);

    public int Priority => 20;

    public bool CanHandle(string rawCode) => VoucherPattern.IsMatch(rawCode);

    public Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        // TODO: real voucher balance lookup once VII Gift Card adapter is wired.
        return Task.FromResult(new ScanResolution(ScanResultType.Voucher, rawCode, null));
    }
}