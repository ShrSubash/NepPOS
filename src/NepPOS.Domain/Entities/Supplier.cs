namespace NepPOS.Domain.Entities;

/// <summary>Who the shop buys from. Any business: a brewery, a wholesaler, a bakery.</summary>
public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The number the supplier had in the shop's previous POS, kept so imports can find it again.</summary>
    public string? LegacyNumber { get; set; }

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
}
