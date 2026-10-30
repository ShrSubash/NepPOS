namespace NepPOS.Domain.Enums;

/// <summary>
/// Maps directly to legacy frmOptions cmbControl setting —
/// controls what a Manager (not Owner) can touch on PLU/products.
/// Configurable per-site later via Tender/Settings module.
/// </summary>
public enum ManagerAccessLevel
{
    None = 0,
    ViewOnly = 1,
    SellPricesOnly = 2,
    Full = 3
}