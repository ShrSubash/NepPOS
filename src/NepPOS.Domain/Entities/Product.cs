using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

public sealed class Product
{
    public int Id { get; set; }

    /// <summary>
    /// The product's id outside this register (Head Office, the website). Never changes once set;
    /// <see cref="Id"/> is only this register's own number.
    /// </summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? CostPrice { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? Barcode { get; set; }
    public string? Plu { get; set; }
    public decimal StockOnHand { get; set; }
    public string? ProductUrl { get; set; }
    public int PointsPerUnit { get; set; }
    public bool EnableOnWebsite { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Cost of one single on the latest delivery, ex GST. <see cref="CostPrice"/> is the average cost.</summary>
    public decimal? LastCost { get; set; }

    /// <summary>GST-free (fresh food, some basics). False = 10% GST included in the price.</summary>
    public bool IsGstFree { get; set; }

    /// <summary>The usual supplier.</summary>
    public int? SupplierId { get; set; }

    /// <summary>
    /// Changes stock on hand and returns the movement that explains it, to be saved in the same
    /// transaction. <paramref name="quantity"/> is in singles: positive in, negative out.
    /// </summary>
    public StockMovement MoveStock(StockMovementType type, decimal quantity, string? reference, DateTime atUtc,
        decimal? unitCost = null, string? employeeName = null)
    {
        StockOnHand += quantity;
        return new StockMovement
        {
            ProductId = Id,
            Product = this,
            Type = type,
            Quantity = quantity,
            UnitCost = unitCost,
            Reference = reference,
            EmployeeName = employeeName,
            AtUtc = atUtc
        };
    }
}