namespace NepPOS.Domain.Entities;

/// <summary>
/// A single product line within a SaleTransaction.
/// Per architecture Section 5: GST is always calculated from the price the customer sees (tax-inclusive pricing).
/// </summary>
public sealed class SaleLineItem
{
    public Guid Id { get; private set; }
    public string Sku { get; private set; }
    public string ProductName { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPriceIncGst { get; private set; }
    public decimal GstRate { get; private set; }

    // Derived from tax-inclusive unit price — never the other way around.
    public decimal LineTotalIncGst => UnitPriceIncGst * Quantity;
    public decimal GstAmount => LineTotalIncGst - LineTotalIncGst / (1 + GstRate);
    public decimal LineTotalExGst => LineTotalIncGst - GstAmount;

    private SaleLineItem() { }

    public SaleLineItem(string sku, string productName, int quantity, decimal unitPriceIncGst, decimal gstRate)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("SKU is required.", nameof(sku));
        if (quantity == 0)
            throw new ArgumentException("Quantity cannot be zero.", nameof(quantity));
        if (unitPriceIncGst < 0)
            throw new ArgumentException("Unit price cannot be negative.", nameof(unitPriceIncGst));
        if (gstRate < 0 || gstRate > 1)
            throw new ArgumentOutOfRangeException(nameof(gstRate), "GST rate must be between 0 and 1.");

        Id = Guid.NewGuid();
        Sku = sku;
        ProductName = productName;
        Quantity = quantity;
        UnitPriceIncGst = unitPriceIncGst;
        GstRate = gstRate;
    }
}