namespace NepPOS.Domain.Enums;

/// <summary>Why stock moved. Stored as text, so new kinds can be added without renumbering.</summary>
public enum StockMovementType
{
    /// <summary>Stock on hand when the product was imported or first counted.</summary>
    Opening,

    /// <summary>Sold (negative), or refunded (positive).</summary>
    Sale,

    /// <summary>Goods received from a supplier.</summary>
    Receipt,

    /// <summary>Stock count result: the difference between counted and expected.</summary>
    Stocktake,

    /// <summary>Broken, spoiled or written off (ullage).</summary>
    Wastage,

    /// <summary>Changed by hand on the product screen.</summary>
    Adjustment,

    /// <summary>A posted delivery taken back out because it was entered wrong (negative).</summary>
    ReceiptReversal,

    TransferOut,
    TransferIn
}
