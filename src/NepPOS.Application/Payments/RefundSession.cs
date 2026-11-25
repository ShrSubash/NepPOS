using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Payments;

/// <summary>
/// Paying money back for a return (or an exchange where the customer gets money back). The sale's total is
/// zero or negative; each refund is a negative payment. Cash refunds round to 5c like cash sales.
/// No UI and no database, so every money rule here is unit tested.
/// </summary>
public sealed class RefundSession : IPaymentSession
{
    private readonly List<SalePayment> _payments = new();

    /// <param name="saleTotal">The return's total: zero (an even exchange) or negative.</param>
    public RefundSession(Guid transactionId, decimal saleTotal)
    {
        if (saleTotal > 0)
            throw new ArgumentOutOfRangeException(nameof(saleTotal), "A refund's total is zero or negative.");

        TransactionId = transactionId;
        RefundTotal = -decimal.Round(saleTotal, 2, MidpointRounding.AwayFromZero);
        IsComplete = RefundTotal == 0;
    }

    public Guid TransactionId { get; }

    /// <summary>What the customer gets back, exactly (positive).</summary>
    public decimal RefundTotal { get; }

    public IReadOnlyList<SalePayment> Payments => _payments;

    /// <summary>Cash rounding: negative when the customer got a few cents more back (as on the sale record).</summary>
    public decimal RoundingAdjustment { get; private set; }

    public bool IsComplete { get; private set; }

    /// <summary>Paid back so far (positive).</summary>
    public decimal Refunded => -_payments.Sum(p => p.Amount);

    /// <summary>Still to pay back, exactly.</summary>
    public decimal Remaining => Math.Max(0, RefundTotal - Refunded);

    /// <summary>What to hand over if the rest goes back in cash.</summary>
    public decimal RemainingCash => CashRounding.Round(Remaining);

    /// <summary>Cash back: the whole remainder (rounded to 5c) unless a smaller amount is given.</summary>
    public decimal RefundCash(decimal? amount = null)
    {
        EnsureOpen();
        var roundedRemaining = RemainingCash;
        var give = amount ?? roundedRemaining;
        if (give <= 0)
            throw new InvalidOperationException("Enter a cash amount.");
        if (!CashRounding.IsPayableInCash(give))
            throw new InvalidOperationException("Cash amounts must end in 0 or 5 cents.");
        if (give > roundedRemaining)
            throw new InvalidOperationException($"That's more than the {roundedRemaining:C} to give back.");

        if (give == roundedRemaining)
        {
            // Settles it: the few cents of rounding go on the record, as with a cash sale.
            RoundingAdjustment = Remaining - roundedRemaining;
            if (give > 0)
                _payments.Add(new SalePayment(TenderType.Cash, -give, -give, 0, tenderName: "Cash refund"));
            IsComplete = true;
        }
        else
        {
            _payments.Add(new SalePayment(TenderType.Cash, -give, -give, 0, tenderName: "Cash refund"));
        }
        return give;
    }

    /// <summary>A refund made on the EFTPOS terminal, recorded once the terminal approved it.</summary>
    /// <param name="terminal">The terminal's answer when the refund went through the EFTPOS integration: its card slips are kept with the refund.</param>
    public void RefundCard(decimal amount, string? authCode = null, PaymentResult? terminal = null)
    {
        Take(amount, "Card refund");
        _payments.Add(new SalePayment(TenderType.Eftpos, -amount, -amount, 0, authCode: string.IsNullOrWhiteSpace(authCode) ? null : authCode.Trim(),
            cardType: terminal?.CardType, maskedPan: terminal?.MaskedPan, responseText: terminal?.ResponseText,
            customerReceipt: terminal?.CustomerReceipt, tenderName: "Card refund",
            merchantReceipt: terminal?.MerchantReceipt, customerReceiptPrintedByTerminal: terminal?.CustomerReceiptPrinted ?? false));
        Settle();
    }

    /// <summary>Back onto a gift card, or as new store credit (the code is printed with a barcode).</summary>
    public void RefundToVoucher(TenderType type, string code, decimal amount)
    {
        if (type is not (TenderType.GiftCard or TenderType.StoreCredit))
            throw new ArgumentOutOfRangeException(nameof(type), "Only gift cards and store credit are vouchers.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The card needs a code.");
        Take(amount, type == TenderType.GiftCard ? "Gift card" : "Store credit");
        _payments.Add(new SalePayment(type, -amount, -amount, 0, reference: code.Trim().ToUpperInvariant(),
            tenderName: type == TenderType.GiftCard ? "Back to gift card" : "Store credit issued"));
        Settle();
    }

    /// <summary>Back onto the customer's account: takes it off what they owe.</summary>
    public void RefundToAccount(string customerCode, string customerName, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(customerCode))
            throw new InvalidOperationException("Choose the account customer first.");
        Take(amount, "Account");
        _payments.Add(new SalePayment(TenderType.Account, -amount, -amount, 0,
            reference: customerCode.Trim().ToUpperInvariant(), tenderName: customerName));
        Settle();
    }

    /// <summary>Back through a tender the owner added (Account…).</summary>
    public void RefundOther(string name, decimal amount, string? reference = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Choose the payment type.");
        Take(amount, name);
        _payments.Add(new SalePayment(TenderType.Other, -amount, -amount, 0, reference: reference, tenderName: name));
        Settle();
    }

    private void Take(decimal amount, string what)
    {
        EnsureOpen();
        if (amount <= 0)
            throw new InvalidOperationException("Enter an amount.");
        if (amount > Remaining)
            throw new InvalidOperationException($"{what}: that's more than the {Remaining:C} to give back.");
    }

    private void Settle()
    {
        if (Remaining == 0)
            IsComplete = true;
    }

    private void EnsureOpen()
    {
        if (IsComplete)
            throw new InvalidOperationException("This refund is already paid back.");
    }
}
