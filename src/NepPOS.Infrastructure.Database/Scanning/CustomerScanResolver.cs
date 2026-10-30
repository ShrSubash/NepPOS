using System.Text.RegularExpressions;
using NepPOS.Application.Scanning;

namespace NepPOS.Infrastructure.Database.Scanning;

/// <summary>
/// Handles loyalty/customer card scans — "CUST-" prefix distinguishes
/// these from every other code shape.
/// </summary>
public sealed class CustomerScanResolver : IScanResolver
{
    private static readonly Regex CustomerPattern = new(@"^CUST-\d{4,8}$", RegexOptions.Compiled);

    public int Priority => 20;

    public bool CanHandle(string rawCode) => CustomerPattern.IsMatch(rawCode);

    public Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        // TODO: real loyalty lookup once local-database-authoritative loyalty is wired.
        return Task.FromResult(new ScanResolution(ScanResultType.Customer, rawCode, null));
    }
}