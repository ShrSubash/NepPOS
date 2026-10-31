using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

/// <summary>
/// One tender applied to a SaleTransaction. A split sale (part card, part cash) has several.
/// <see cref="Amount"/> is the money actually kept for the sale: for cash it is the
/// 5c-rounded amount (Tendered minus Change), so cash payments always reconcile with the drawer.
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

    /// <summary>The bank's card slip text (customer copy), kept so it can be reprinted later.</summary>
    public string? CustomerReceipt { get; private set; }

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
        string? customerReceipt = null)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Payment amount must be positive.");
        if (change < 0)
            throw new ArgumentOutOfRangeException(nameof(change), "Change cannot be negative.");
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
        CustomerReceipt = customerReceipt;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}
