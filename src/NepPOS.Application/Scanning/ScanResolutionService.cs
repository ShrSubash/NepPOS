namespace NepPOS.Application.Scanning;

/// <summary>
/// Tries each registered IScanResolver in priority order and returns
/// the first match. Presentation calls only this — never a specific resolver.
/// </summary>
public sealed class ScanResolutionService
{
    private readonly IReadOnlyList<IScanResolver> _resolvers;

    public ScanResolutionService(IEnumerable<IScanResolver> resolvers)
    {
        _resolvers = resolvers.OrderBy(r => r.Priority).ToList();
    }

    public async Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default)
    {
        var trimmed = rawCode.Trim();

        foreach (var resolver in _resolvers)
        {
            if (resolver.CanHandle(trimmed))
            {
                return await resolver.ResolveAsync(trimmed, cancellationToken);
            }
        }

        return ScanResolution.NotFound(trimmed);
    }
}