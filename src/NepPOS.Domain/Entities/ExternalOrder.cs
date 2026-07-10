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
    public string SourcePlatform { get; private set; }   // e.g. "Shopify", "UberEats"
    public string ExternalOrderReference { get; private set; }  // the platform's own order ID
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    public string StoreId { get; private set; }

    private readonly List<ExternalOrderLine> _lines = new();
    public IReadOnlyCollection<ExternalOrderLine> Lines => _lines.AsReadOnly();

    private ExternalOrder() { }

    public ExternalOrder(string sourcePlatform, string externalOrderReference, string storeId)
    {
        if (string.IsNullOrWhiteSpace(sourcePlatform))
            throw new ArgumentException("Source platform is required.", nameof(sourcePlatform));
        if (string.IsNullOrWhiteSpace(externalOrderReference))
            throw new ArgumentException("External order reference is required.", nameof(externalOrderReference));

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
        Sku = sku;
        Quantity = quantity;
        UnitPriceIncGst = unitPriceIncGst;
    }
}