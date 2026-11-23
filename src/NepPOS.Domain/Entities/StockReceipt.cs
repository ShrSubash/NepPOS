namespace NepPOS.Domain.Entities;

/// <summary>
/// A supplier delivery received into stock: one invoice, posted once. Its lines add stock (through the
/// ledger), set each product's last cost and move its average cost. Posted receipts are never edited:
/// a wrong one is reversed (stock back out, costs put back), which frees its invoice number to post again.
/// </summary>
public sealed class StockReceipt
{
    public int Id { get; set; }

    public int? SupplierId { get; set; }

    /// <summary>The supplier's name when received, so the receipt still reads right if the supplier is renamed.</summary>
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>As printed on the supplier's invoice. Unique per supplier among receipts that aren't reversed.</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    public DateOnly InvoiceDate { get; set; }

    public DateTime PostedUtc { get; set; }

    public string? EmployeeName { get; set; }

    /// <summary>When the receipt was reversed; null while it stands.</summary>
    public DateTime? ReversedUtc { get; set; }
    public string? ReversedBy { get; set; }
    public string? ReversalReason { get; set; }

    public bool IsReversed => ReversedUtc is not null;

    public List<StockReceiptLine> Lines { get; set; } = new();
}

/// <summary>One product on a delivery: how many of which pack, at what cost per pack (ex GST).</summary>
public sealed class StockReceiptLine
{
    public int Id { get; set; }
    public int StockReceiptId { get; set; }

    public int ProductId { get; set; }

    /// <summary>The product's name when received.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>"Single", "Case of 24"…</summary>
    public string PackName { get; set; } = string.Empty;

    /// <summary>Singles in one pack: 1, 6, 24.</summary>
    public int PackUnits { get; set; } = 1;

    /// <summary>Packs received.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cost of one pack, ex GST, as on the invoice.</summary>
    public decimal CostPerPack { get; set; }

    /// <summary>Cost of one single before this delivery (last cost), to show what changed.</summary>
    public decimal? PreviousUnitCost { get; set; }

    /// <summary>The product's last cost and average cost just before this delivery, put back if it's reversed.</summary>
    public decimal? PreviousLastCost { get; set; }
    public decimal? PreviousAverageCost { get; set; }

    public decimal Singles => Quantity * PackUnits;
    public decimal UnitCost => PackUnits == 0 ? 0 : CostPerPack / PackUnits;
    public decimal LineCost => Quantity * CostPerPack;
}
