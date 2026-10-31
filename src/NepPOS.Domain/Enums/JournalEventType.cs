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
    ReceiptReprinted
}
