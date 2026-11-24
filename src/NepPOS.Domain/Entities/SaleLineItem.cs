namespace NepPOS.Domain.Entities;

/// <summary>
/// A single product line within a SaleTransaction.
/// Per architecture Section 5: GST is always calculated from the price the customer sees (tax-inclusive pricing).
/// </summary>
public sealed class SaleLineItem
{
    public Guid Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string ProductName { get; private set; } = null!;
    public int Quantity { get; private set; }
    public decimal UnitPriceIncGst { get; private set; }
    public decimal GstRate { get; private set; }

    /// <summary>
    /// The product's department/category when it was sold. A snapshot, so department reports
    /// stay correct after a product is moved to another category. Null for sales made before this was kept.
    /// </summary>
    public string? Category { get; private set; }

    /// <summary>The product sold (this register's id), so stock can be taken off. Null for lines that aren't a product.</summary>
    public int? ProductId { get; private set; }

    /// <summary>Singles in one of this line's items: 1, or 6 for a 6-pack, 24 for a case.</summary>
    public int PackUnits { get; private set; } = 1;

    /// <summary>Singles this line takes off stock (a refund line puts them back).</summary>
    public int StockUnits => Quantity * PackUnits;

    /// <summary>For a returned item: the line it came back from (on the original sale). Null for a normal line,
    /// and for a return without a receipt.</summary>
    public Guid? ReturnOfLineId { get; private set; }

    /// <summary>Why it came back ("Faulty", "Change of mind"…). Only on returned lines.</summary>
    public string? ReturnReason { get; private set; }

    /// <summary>A returned item goes back on the shelf (true) or is written off as wastage (faulty, damaged).</summary>
    public bool Restock { get; private set; } = true;

    /// <summary>A gift card sold or topped up by this line (GC-…). Not a product; GST-free.</summary>
    public string? GiftCardCode { get; private set; }

    public bool IsReturn => Quantity < 0;

    /// <summary>Taken off this line (GST inclusive, for the whole line): a promotion or a cashier's discount.</summary>
    public decimal Discount { get; private set; }

    /// <summary>Why: "2 for $10", "10% off", "Staff discount"… Printed under the line.</summary>
    public string? DiscountReason { get; private set; }

    // Derived from tax-inclusive unit price — never the other way around.
    public decimal LineTotalIncGst => UnitPriceIncGst * Quantity - Discount;
    public decimal GstAmount => LineTotalIncGst - LineTotalIncGst / (1 + GstRate);
    public decimal LineTotalExGst => LineTotalIncGst - GstAmount;

    // EF Core requires a parameterless constructor for materialization.
    private SaleLineItem() { }

    public SaleLineItem(string sku, string productName, int quantity, decimal unitPriceIncGst, decimal gstRate, string? category = null,
        int? productId = null, int packUnits = 1, Guid? returnOfLineId = null, string? returnReason = null, bool restock = true,
        string? giftCardCode = null, decimal discount = 0, string? discountReason = null)
    {
        if (discount < 0)
            throw new ArgumentOutOfRangeException(nameof(discount), "A discount can't be negative.");
        if (discount > 0 && (quantity < 0 || discount > unitPriceIncGst * quantity))
            throw new ArgumentOutOfRangeException(nameof(discount), "A discount can't be more than the line, or on a returned item.");
        if (returnOfLineId is not null && quantity > 0)
            throw new ArgumentException("A returned item has a negative quantity.", nameof(quantity));
        if (giftCardCode is not null && (quantity != 1 || gstRate != 0))
            throw new ArgumentException("A gift card is sold one at a time, GST-free.", nameof(giftCardCode));
        if (string.IsNullOrWhiteSpace(sku))
            throw new ArgumentException("SKU is required.", nameof(sku));
        if (quantity == 0)
            throw new ArgumentException("Quantity cannot be zero.", nameof(quantity));
        if (unitPriceIncGst < 0)
            throw new ArgumentException("Unit price cannot be negative.", nameof(unitPriceIncGst));
        if (gstRate < 0 || gstRate > 1)
            throw new ArgumentOutOfRangeException(nameof(gstRate), "GST rate must be between 0 and 1.");
        if (packUnits < 1)
            throw new ArgumentOutOfRangeException(nameof(packUnits), "A pack holds at least one single.");

        Id = Guid.NewGuid();
        Sku = sku;
        ProductName = productName;
        Quantity = quantity;
        UnitPriceIncGst = unitPriceIncGst;
        GstRate = gstRate;
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        ProductId = productId;
        PackUnits = packUnits;
        ReturnOfLineId = returnOfLineId;
        ReturnReason = quantity < 0 && !string.IsNullOrWhiteSpace(returnReason) ? returnReason.Trim() : null;
        Restock = quantity > 0 || restock;
        GiftCardCode = string.IsNullOrWhiteSpace(giftCardCode) ? null : giftCardCode.Trim().ToUpperInvariant();
        Discount = decimal.Round(discount, 2, MidpointRounding.AwayFromZero);
        DiscountReason = Discount > 0 && !string.IsNullOrWhiteSpace(discountReason) ? discountReason.Trim() : null;
    }
}