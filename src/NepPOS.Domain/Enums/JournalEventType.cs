namespace NepPOS.Domain.Enums;

/// <summary>
/// What happened at the register. Stored as text, so new event types can be added
/// at any time without renumbering or a data migration.
/// </summary>
public enum JournalEventType
{
    SignIn,
    SignOut,
    SaleCompleted,
    SaleVoided,
    LineRemoved,
    CardDeclined,
    CardOutcomeUnknown,
    SaleSaveFailed,
    NoSale,
    DrawerOpened,
    ReceiptReprinted,
    SettingsChanged,

    /// <summary>A refund or exchange saved (the money went back to the customer, or the difference was paid).</summary>
    RefundCompleted,

    /// <summary>The Sunday / public holiday surcharge was taken off a sale.</summary>
    SurchargeRemoved,

    /// <summary>Stock written off (ullages): Amount = its value at cost.</summary>
    WastageRecorded,

    /// <summary>A sale put on hold (Amount = its total), and one recalled.</summary>
    SaleHeld,
    SaleRecalled,

    /// <summary>A cashier gave a discount by hand (Amount = how much); promotions aren't journalled.</summary>
    DiscountGiven,

    /// <summary>A stocktake posted: stock set to what was counted (Amount = the difference at cost).</summary>
    StocktakePosted,

    /// <summary>A website order completed (Amount = what was rung up) or cancelled at the register.</summary>
    WebOrderCompleted,
    WebOrderCancelled,

    /// <summary>Stock sent to (Amount = its value at cost) or received from another store.</summary>
    TransferSent,
    TransferReceived,

    /// <summary>X reading printed: running totals since the last Z, nothing reset.</summary>
    XReport,

    /// <summary>Day closed. InvoiceNumber = last invoice included, so the next report starts after it.</summary>
    ZReport
}
