using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

/// <summary>
/// One tender applied to a SaleTransaction. A split sale (part card, part cash) has several.
/// <see cref="Amount"/> is the money actually kept for the sale: for cash it is the
/// 5c-rounded amount (Tendered minus Change), so cash payments always reconcile with the drawer.
/// A refund is a negative amount (money back to the customer), with no change.
/// </summary>
public sealed class SalePayment
{
    public Guid Id { get; private set; }
    public Guid SaleTransactionId { get; private set; }
    public TenderType TenderType { get; private set; }

    public decimal Amount { get; private set; }
    public decimal Tendered { get; private set; }
    public decimal Change { get; private set; }

    // EFTPOS response details. Never a full card number — masked PAN only (PCI DSS).
    public string? AuthCode { get; private set; }
    public string? CardType { get; private set; }
    public string? MaskedPan { get; private set; }
    public string? ResponseText { get; private set; }

    /// <summary>The bank's card slip text (customer copy), kept so it can be printed or reprinted later.</summary>
    public string? CustomerReceipt { get; private set; }

    /// <summary>The bank's card slip text (merchant copy), kept so it can be printed later (disputes, signatures).</summary>
    public string? MerchantReceipt { get; private set; }

    /// <summary>The terminal printed the customer slip itself, so the register doesn't print it automatically.</summary>
    public bool CustomerReceiptPrintedByTerminal { get; private set; }

    /// <summary>Gift card or store credit code (GC-…, SC-…), or the reference typed for an "other" tender.</summary>
    public string? Reference { get; private set; }

    /// <summary>The name shown and printed: an "other" tender's name ("Account"), or e.g. "Card refund".</summary>
    public string? TenderName { get; private set; }

    public bool IsRefund => Amount < 0;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    // EF Core requires a parameterless constructor for materialization.
    private SalePayment() { }

    public SalePayment(
        TenderType tenderType,
        decimal amount,
        decimal tendered,
        decimal change,
        string? authCode = null,
        string? cardType = null,
        string? maskedPan = null,
        string? responseText = null,
        string? customerReceipt = null,
        string? reference = null,
        string? tenderName = null,
        string? merchantReceipt = null,
        bool customerReceiptPrintedByTerminal = false)
    {
        if (amount == 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payment amount can't be zero.");
        if (change < 0)
            throw new ArgumentOutOfRangeException(nameof(change), "Change cannot be negative.");
        if (amount < 0 && change != 0)
            throw new ArgumentException("A refund has no change.", nameof(change));
        if (tendered - change != amount)
            throw new ArgumentException("Tendered minus change must equal the payment amount.", nameof(tendered));

        Id = Guid.NewGuid();
        TenderType = tenderType;
        Amount = amount;
        Tendered = tendered;
        Change = change;
        AuthCode = authCode;
        CardType = cardType;
        MaskedPan = maskedPan;
        ResponseText = responseText;
        CustomerReceipt = string.IsNullOrWhiteSpace(customerReceipt) ? null : customerReceipt;
        MerchantReceipt = string.IsNullOrWhiteSpace(merchantReceipt) ? null : merchantReceipt;
        CustomerReceiptPrintedByTerminal = customerReceiptPrintedByTerminal && CustomerReceipt is not null;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        TenderName = string.IsNullOrWhiteSpace(tenderName) ? null : tenderName.Trim();
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}
