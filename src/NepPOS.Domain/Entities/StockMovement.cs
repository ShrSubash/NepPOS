using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

/// <summary>
/// One change to a product's stock, in singles (a case of 24 sold is −24). Rows are only ever added:
/// stock on hand is where the movements add up to, so every number can be explained. Saved in the
/// same transaction as whatever caused it (the sale, the receipt, the count).
/// </summary>
public sealed class StockMovement
{
    public long Id { get; set; }

    public int ProductId { get; set; }

    /// <summary>Set by <see cref="Product.MoveStock"/> so a movement for a product not saved yet gets its id on save.</summary>
    public Product? Product { get; set; }

    public StockMovementType Type { get; set; }

    /// <summary>Singles: positive in, negative out.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cost of one single, when known (receipts, imports).</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>What caused it, in words a person can find: "Invoice 1042", "Imported from old POS".</summary>
    public string? Reference { get; set; }

    public string? EmployeeName { get; set; }

    public DateTime AtUtc { get; set; }
}
