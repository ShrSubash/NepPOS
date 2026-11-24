namespace NepPOS.Domain.Entities;

public enum GiftCardKind
{
    /// <summary>Sold to a customer (GC-…).</summary>
    GiftCard = 1,

    /// <summary>Given instead of money back on a return (SC-…).</summary>
    StoreCredit = 2
}

/// <summary>
/// A gift card or store credit and what's left on it. Every change is a <see cref="GiftCardMovement"/>, saved with the
/// sale that caused it, so the balance can always be explained. Kept on this register (cards used at several
/// registers need Head Office; see docs).
/// </summary>
public sealed class GiftCard
{
    public int Id { get; set; }

    /// <summary>"GC-123456" or "SC-123456", as printed in the barcode.</summary>
    public string Code { get; set; } = "";

    public GiftCardKind Kind { get; set; }
    public decimal Balance { get; set; }
    public DateTime IssuedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>Cancelled cards (lost, stolen) can't be spent.</summary>
    public bool IsActive { get; set; } = true;

    public List<GiftCardMovement> Movements { get; set; } = new();

    public string KindName => Kind == GiftCardKind.StoreCredit ? "Store credit" : "Gift card";
}

/// <summary>One change to a card's balance: positive = loaded (sold, topped up, refunded to it), negative = spent.</summary>
public sealed class GiftCardMovement
{
    public long Id { get; set; }
    public int GiftCardId { get; set; }
    public decimal Amount { get; set; }
    public Guid? SaleId { get; set; }
    public long? InvoiceNumber { get; set; }
    public DateTime AtUtc { get; set; }
    public string Note { get; set; } = "";
}
