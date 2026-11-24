namespace NepPOS.Domain.Enums;

/// <summary>
/// How a sale (or part of a split sale) was paid.
/// Stored as text, so new tender types can be added without renumbering.
/// </summary>
public enum TenderType
{
    Cash = 1,
    Eftpos = 2,

    /// <summary>A NepPOS gift card (GC-…). <see cref="Entities.SalePayment.Reference"/> holds its code.</summary>
    GiftCard = 3,

    /// <summary>Store credit from an earlier return (SC-…). <see cref="Entities.SalePayment.Reference"/> holds its code.</summary>
    StoreCredit = 4,

    /// <summary>A tender the owner added (Account, Cheque…). <see cref="Entities.SalePayment.TenderName"/> holds its name.</summary>
    Other = 5,

    /// <summary>Charged to a customer's account (they pay later). <see cref="Entities.SalePayment.Reference"/> holds the customer code.</summary>
    Account = 6
}
