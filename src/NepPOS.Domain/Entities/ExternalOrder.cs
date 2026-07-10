namespace NepPOS.Domain.Entities;

/// <summary>
/// A canonical representation of an order received from any external platform
/// (Shopify, Uber Eats, Doshii, etc.) — per architecture Section 8:
/// "One shared design works for every platform."
/// This is what every IExternalOrderProvider adapter must map its platform-specific
/// payload into before it reaches the Application layer.
/// </summary>
public sealed class ExternalOrder
{
    public Guid Id { get; private set; }
    public string SourcePlatform { get; private set; } = null!;          // e.g. "Shopify", "UberEats"
    public string ExternalOrderReference { get; private set; } = null!;  // the platform's own order ID
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    public string StoreId { get; private set; } = null!;

    private readonly List<ExternalOrderLine> _lines = new();
    public IReadOnlyCollection<ExternalOrderLine> Lines => _lines.AsReadOnly();

    // EF Core requires a parameterless constructor for materialization.
    // The `= null!` above suppresses CS8618 for that path — the public constructor
    // below is what actually guarantees these are always set for code you write.
    private ExternalOrder() { }

    public ExternalOrder(string sourcePlatform, string externalOrderReference, string storeId)
    {
        if (string.IsNullOrWhiteSpace(sourcePlatform))
            throw new ArgumentException("Source platform is required.", nameof(sourcePlatform));
        if (string.IsNullOrWhiteSpace(externalOrderReference))
            throw new ArgumentException("External order reference is required.", nameof(externalOrderReference));
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("Store ID is required.", nameof(storeId));

        Id = Guid.NewGuid();
        SourcePlatform = sourcePlatform;
        ExternalOrderReference = externalOrderReference;
        StoreId = storeId;
        ReceivedAtUtc = DateTimeOffset.UtcNow;
    }

    public void AddLine(string sku, int quantity, decimal unitPriceIncGst)
    {
        _lines.Add(new ExternalOrderLine(sku, quantity, unitPriceIncGst));
    }
}

public sealed class ExternalOrderLine
{
    public string Sku { get; }
    public int Quantity { get; }
    public decimal UnitPriceIncGst { get; }

    public ExternalOrderLine(string sku, int quantity, decimal unitPriceIncGst)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("SKU is required.", nameof(sku));

        Sku = sku;
        Quantity = quantity;
        UnitPriceIncGst = unitPriceIncGst;
    }
}