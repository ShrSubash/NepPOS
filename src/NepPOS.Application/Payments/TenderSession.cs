using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Payments;

/// <summary>What the checkout saves from either session: the payments, the cash rounding, and whether it's settled.</summary>
public interface IPaymentSession
{
    Guid TransactionId { get; }
    IReadOnlyList<SalePayment> Payments { get; }
    decimal RoundingAdjustment { get; }
    bool IsComplete { get; }
}

/// <summary>
/// The state of one checkout while the customer pays: split tenders, cash rounding and change.
/// No UI and no database, so every money rule here is unit tested.
/// </summary>
public sealed class TenderSession : IPaymentSession
{
    private readonly List<SalePayment> _payments = new();

    public TenderSession(Guid transactionId, decimal saleTotal)
    {
        if (saleTotal <= 0)
            throw new ArgumentOutOfRangeException(nameof(saleTotal), "Sale total must be positive.");

        TransactionId = transactionId;
        SaleTotal = decimal.Round(saleTotal, 2, MidpointRounding.AwayFromZero);
    }

    public Guid TransactionId { get; }
    public decimal SaleTotal { get; }
    public IReadOnlyList<SalePayment> Payments => _payments;

    /// <summary>Cash rounding applied when cash settled the sale (e.g. +0.02 or -0.02).</summary>
    public decimal RoundingAdjustment { get; private set; }

    /// <summary>Change to hand back once the sale is complete.</summary>
    public decimal ChangeDue { get; private set; }

    public bool IsComplete { get; private set; }

    public decimal Paid => _payments.Sum(p => p.Amount);

    /// <summary>Exact amount still owing — what a card is charged.</summary>
    public decimal Remaining => Math.Max(0, SaleTotal + RoundingAdjustment - Paid);

    /// <summary>What the customer hands over if they pay the rest in cash.</summary>
    public decimal RemainingCash => CashRounding.Round(Remaining);

    /// <summary>
    /// Takes cash. If it covers the rounded remainder the sale completes and change is worked out;
    /// otherwise it is a part payment and the rest can be paid by card or more cash.
    /// </summary>
    public void AddCash(decimal tendered)
    {
        EnsureOpen();

        if (tendered <= 0)
            throw new InvalidOperationException("Enter a cash amount.");
        if (!CashRounding.IsPayableInCash(tendered))
            throw new InvalidOperationException("Cash amounts must end in 0 or 5 cents.");

        var remaining = Remaining;
        var roundedRemaining = CashRounding.Round(remaining);

        if (tendered >= roundedRemaining)
        {
            RoundingAdjustment = roundedRemaining - remaining;
            ChangeDue = tendered - roundedRemaining;

            // Only 1–2c left after a card part-payment rounds to nothing: no cash is kept.
            if (roundedRemaining > 0)
                _payments.Add(new SalePayment(TenderType.Cash, roundedRemaining, tendered, ChangeDue));

            IsComplete = true;
        }
        else
        {
            _payments.Add(new SalePayment(TenderType.Cash, tendered, tendered, 0));
        }
    }

    /// <summary>
    /// Takes part (or all) of what's due from a gift card or store credit. <paramref name="available"/> is the card's
    /// balance: the amount taken is the smaller of the two. Returns what was taken.
    /// </summary>
    public decimal AddVoucher(TenderType type, string code, decimal amount, decimal available)
    {
        EnsureOpen();
        if (type is not (TenderType.GiftCard or TenderType.StoreCredit))
            throw new ArgumentOutOfRangeException(nameof(type), "Only gift cards and store credit are vouchers.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Scan or type the card's code.");
        if (_payments.Any(p => p.TenderType == type && string.Equals(p.Reference, code.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{code.Trim()} is already used on this sale.");
        if (available <= 0)
            throw new InvalidOperationException($"{code.Trim()} has nothing left on it.");

        var take = Math.Min(Math.Min(amount <= 0 ? Remaining : amount, Remaining), available);
        if (take <= 0)
            throw new InvalidOperationException("Nothing is due.");

        _payments.Add(new SalePayment(type, take, take, 0, reference: code.Trim().ToUpperInvariant()));
        if (Remaining == 0)
            IsComplete = true;
        return take;
    }

    /// <summary>
    /// Charges part (or all) of what's due to a customer's account. The payment screen checks their credit limit
    /// (and asks for a manager) before calling this.
    /// </summary>
    public decimal AddAccount(string customerCode, string customerName, decimal amount)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(customerCode))
            throw new InvalidOperationException("Choose the account customer first.");
        if (_payments.Any(p => p.TenderType == TenderType.Account))
            throw new InvalidOperationException("This sale is already charged to an account.");

        var take = Math.Min(amount <= 0 ? Remaining : amount, Remaining);
        if (take <= 0)
            throw new InvalidOperationException("Nothing is due.");

        _payments.Add(new SalePayment(TenderType.Account, take, take, 0,
            reference: customerCode.Trim().ToUpperInvariant(), tenderName: customerName));
        if (Remaining == 0)
            IsComplete = true;
        return take;
    }

    /// <summary>A tender the owner added (Account, Cheque…): exact amount, no change.</summary>
    public void AddOther(string name, decimal amount, string? reference = null)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Choose the payment type.");
        if (amount <= 0)
            throw new InvalidOperationException("Enter an amount.");
        if (amount > Remaining)
            throw new InvalidOperationException($"{name} amount is more than the amount due.");

        _payments.Add(new SalePayment(TenderType.Other, amount, amount, 0, reference: reference, tenderName: name));
        if (Remaining == 0)
            IsComplete = true;
    }

    /// <summary>One share when the remainder is split evenly between <paramref name="ways"/> people; the last pays the cents left.</summary>
    public decimal EvenShare(int ways)
    {
        if (ways < 2)
            throw new ArgumentOutOfRangeException(nameof(ways), "Split between two or more.");
        return Math.Floor(Remaining / ways * 100m) / 100m;
    }

    /// <summary>Checks a card amount before it goes to the terminal, so a card is never charged for an invalid amount.</summary>
    public void ValidateCardAmount(decimal amount)
    {
        EnsureOpen();

        if (amount <= 0)
            throw new InvalidOperationException("Enter a card amount.");
        if (amount > Remaining)
            throw new InvalidOperationException("Card amount is more than the amount due. Cash out is not enabled.");
    }

    /// <summary>Records an approved EFTPOS payment. Card amounts are exact (no rounding).</summary>
    public void AddEftpos(decimal amount, PaymentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateCardAmount(amount);

        if (!result.Approved)
            throw new InvalidOperationException("A declined card payment cannot be recorded.");

        _payments.Add(new SalePayment(
            TenderType.Eftpos, amount, amount, 0,
            result.AuthCode, result.CardType, result.MaskedPan, result.ResponseText, result.CustomerReceipt));

        if (Remaining == 0)
            IsComplete = true;
    }

    private void EnsureOpen()
    {
        if (IsComplete)
            throw new InvalidOperationException("This sale is already fully paid.");
    }
}
