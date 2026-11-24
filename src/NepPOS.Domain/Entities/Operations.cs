namespace NepPOS.Domain.Entities;

public enum PromotionKind
{
    /// <summary>Buy <see cref="Promotion.BuyQuantity"/> for <see cref="Promotion.Price"/> ("2 for $10"); any mix of the products in it.</summary>
    MultiBuy = 1,

    /// <summary><see cref="Promotion.Percent"/>% off each item.</summary>
    PercentOff = 2,

    /// <summary><see cref="Promotion.Amount"/> off each item.</summary>
    AmountOff = 3
}

/// <summary>
/// A promotion applied automatically at the register: multi-buy / mix and match, % off or $ off, on chosen products or a
/// whole department, between dates and (for a happy hour) on certain days and times.
/// </summary>
public sealed class Promotion
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public PromotionKind Kind { get; set; }
    public int BuyQuantity { get; set; } = 2;
    public decimal Price { get; set; }
    public decimal Percent { get; set; }
    public decimal Amount { get; set; }

    /// <summary>A whole department, or null to use <see cref="ProductIds"/>.</summary>
    public string? Category { get; set; }

    /// <summary>The products in it, as "12,40,41" (empty when it's a department).</summary>
    public string ProductIds { get; set; } = "";

    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>Days it runs, as "Mon,Fri,Sat" (empty = every day).</summary>
    public string Days { get; set; } = "";

    /// <summary>Happy hour: only between these times (both null = all day).</summary>
    public TimeOnly? FromTime { get; set; }
    public TimeOnly? ToTime { get; set; }

    public bool IsActive { get; set; } = true;

    public IReadOnlyList<int> ProductIdList =>
        ProductIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();

    /// <summary>Running at this local time?</summary>
    public bool RunsAt(DateTime local)
    {
        if (!IsActive) return false;
        var day = DateOnly.FromDateTime(local);
        if (StartDate is { } start && day < start) return false;
        if (EndDate is { } end && day > end) return false;
        var days = Days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (days.Length > 0 && !days.Contains(local.DayOfWeek.ToString()[..3], StringComparer.OrdinalIgnoreCase)) return false;
        var time = TimeOnly.FromDateTime(local);
        if (FromTime is { } from && ToTime is { } to)
            return from <= to ? time >= from && time < to : time >= from || time < to;   // overnight happy hour
        return true;
    }
}

/// <summary>A sale put on hold (the customer went to get something) to be recalled later. The cart is kept as JSON.</summary>
public sealed class HeldSale
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public decimal Total { get; set; }
    public int ItemCount { get; set; }
    public string CartJson { get; set; } = "";
    public string? EmployeeName { get; set; }
    public DateTime HeldUtc { get; set; }
}

public enum StocktakeStatus
{
    Counting = 1,
    Posted = 2,
    Cancelled = 3
}

/// <summary>A stock count: products are scanned and counted, then posting sets stock on hand to what was counted.</summary>
public sealed class Stocktake
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public StocktakeStatus Status { get; set; } = StocktakeStatus.Counting;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? EmployeeName { get; set; }
    public List<StocktakeLine> Lines { get; set; } = new();
}

/// <summary>What was counted for one product (in singles). <see cref="Expected"/> and the cost are filled in when posted.</summary>
public sealed class StocktakeLine
{
    public int Id { get; set; }
    public int StocktakeId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Counted { get; set; }
    public decimal? Expected { get; set; }
    public decimal? UnitCost { get; set; }
    public DateTime CountedUtc { get; set; }
}

public enum PurchaseOrderStatus
{
    Draft = 1,
    Sent = 2,
    Received = 3,
    Cancelled = 4
}

/// <summary>An order to a supplier. Received through Receive stock, so stock, costs and the supplier's bill follow.</summary>
public sealed class PurchaseOrder
{
    public int Id { get; set; }

    /// <summary>"PO-00012".</summary>
    public string Number { get; set; } = "";

    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public int? StockReceiptId { get; set; }
    public string? Note { get; set; }
    public string? EmployeeName { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = new();

    public decimal Total => Lines.Sum(l => l.Quantity * l.CostPerPack);
}

/// <summary>One product on a purchase order: <see cref="Quantity"/> packs of <see cref="PackUnits"/> singles at a cost per pack (ex GST).</summary>
public sealed class PurchaseOrderLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string PackName { get; set; } = "Single";
    public int PackUnits { get; set; } = 1;
    public decimal Quantity { get; set; }
    public decimal CostPerPack { get; set; }
}
