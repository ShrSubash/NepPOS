namespace NepPOS.Domain.Entities;

/// <summary>Who the shop buys from. Any business: a brewery, a wholesaler, a bakery.</summary>
public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The number the supplier had in the shop's previous POS, kept so imports can find it again.</summary>
    public string? LegacyNumber { get; set; }

    /// <summary>The shop's customer number with this supplier (on their e-invoices), used to recognise them on import.</summary>
    public string? AccountNumber { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>A supplier's own item code for a product, as on their invoices and price files.</summary>
public sealed class ProductSupplierCode
{
    public int Id { get; set; }
    public int ProductId { get; set; }

    /// <summary>Null when the code's supplier isn't known (old systems often didn't say).</summary>
    public int? SupplierId { get; set; }

    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// How many of the supplier's invoiced units make one of this product's singles. Usually 1; 10 when the
    /// supplier invoices cans and the shop sells the 10-pack as one item.
    /// </summary>
    public int SupplierUnitsPerSingle { get; set; } = 1;
}

/// <summary>A supplier's item the shop never stocks (bags, can handles), so e-invoice import skips it without asking.</summary>
public sealed class SupplierSkippedItem
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
