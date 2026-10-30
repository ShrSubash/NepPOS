namespace NepPOS.Application.Scanning;

/// <summary>
/// Resolves a single scanned/typed code to a concrete action.
/// Implementations declare the code shape they own via CanHandle,
/// checked cheaply (regex/length) before any database hit.
/// </summary>
public interface IScanResolver
{
    /// <summary>Lower runs first. Most specific shapes (barcodes, prefixed vouchers) go first.</summary>
    int Priority { get; }

    bool CanHandle(string rawCode);

    Task<ScanResolution> ResolveAsync(string rawCode, CancellationToken cancellationToken = default);
}

public enum ScanResultType
{
    Product,
    Voucher,
    Customer,
    NotFound
}

public sealed record ScanResolution(ScanResultType Type, object? Payload, string? Message)
{
    public static ScanResolution Product(Domain.Entities.Product product) => new(ScanResultType.Product, product, null);
    public static ScanResolution NotFound(string rawCode) => new(ScanResultType.NotFound, null, $"No match for '{rawCode}'.");
}