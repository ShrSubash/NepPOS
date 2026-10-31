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

    /// <summary>X reading printed: running totals since the last Z, nothing reset.</summary>
    XReport,

    /// <summary>Day closed. InvoiceNumber = last invoice included, so the next report starts after it.</summary>
    ZReport
}
