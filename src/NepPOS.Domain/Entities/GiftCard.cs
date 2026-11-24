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
/// sale that caused it, so the balance can always be explained. Shared by every store of the same owner: each
/// movement goes to Head Office and is copied to the other registers, so a card works at any of them.
/// </summary>
public sealed class GiftCard
{
    public int Id { get; set; }

    /// <summary>"GC-1234567890" or "SC-1234567890" (older cards: 6 digits), as printed in the barcode.</summary>
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

    /// <summary>The same at every register (it is the Head Office event's id), so a movement is never copied twice.</summary>
    public Guid EventId { get; set; }

    public int GiftCardId { get; set; }
    public decimal Amount { get; set; }
    public Guid? SaleId { get; set; }
    public long? InvoiceNumber { get; set; }
    public DateTime AtUtc { get; set; }
    public string Note { get; set; } = "";

    /// <summary>Where it happened ("STORE-002 / REG-001"); null when it happened on this register.</summary>
    public string? Where { get; set; }
}
