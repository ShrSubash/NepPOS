namespace NepPOS.Application.Payments;

/// <summary>A tender the owner adds in Manage → Payment types (Account, Cheque, Afterpay…).</summary>
/// <param name="OpensDrawer">Open the cash drawer when it's used (e.g. Cheque).</param>
/// <param name="NeedsReference">Ask for a reference (account name, cheque number) when it's used.</param>
public sealed record OtherTender(string Name, bool OpensDrawer = false, bool NeedsReference = false);

/// <summary>Which tenders the payment screen offers. Cash and EFTPOS are always there.</summary>
public sealed record PaymentTypeSettings
{
    public bool GiftCards { get; init; } = true;
    public bool StoreCredit { get; init; } = true;

    /// <summary>Account customers can charge sales to their account (an ACCOUNT button on the payment screen).</summary>
    public bool Accounts { get; init; } = true;

    /// <summary>A charge to an account that has overdue money needs a manager.</summary>
    public bool OverdueAccountsNeedManager { get; init; } = true;
    public IReadOnlyList<OtherTender> Others { get; init; } = Array.Empty<OtherTender>();
}

/// <summary>Discounts by hand at the register (Manage → Promotions).</summary>
public sealed record DiscountSettings
{
    /// <summary>A cashier can take off up to this % of a line or sale; more needs a manager. 0 = only managers.</summary>
    public decimal CashierMaxPercent { get; init; } = 10m;

    /// <summary>Reasons offered as buttons.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = new[] { "Staff discount", "Damaged packaging", "Price match", "Loyal customer" };
}

/// <summary>Return rules (Manage → Payment types → Returns).</summary>
public sealed record ReturnSettings
{
    /// <summary>Returns with a receipt are accepted for this many days (a manager can still do older ones).</summary>
    public int ReturnDays { get; init; } = 30;

    /// <summary>Refunds above this need a manager (0 = no limit).</summary>
    public decimal CashierRefundLimit { get; init; } = 100m;

    /// <summary>Printed under the receipt barcode.</summary>
    public string ReceiptNote { get; init; } = "Keep this receipt for returns and exchanges.";
}
