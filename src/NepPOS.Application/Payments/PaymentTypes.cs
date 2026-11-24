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
    public IReadOnlyList<OtherTender> Others { get; init; } = Array.Empty<OtherTender>();
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
