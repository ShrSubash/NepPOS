using System.Linq;

namespace NepPOS.Domain.Entities;

/// <summary>
/// Represents a completed or in-progress sale at a register.
/// Per architecture Section 6: every record gets a unique ID at creation time,
/// even with no network connection. InvoiceNumber is assigned only on completion.
/// </summary>
public sealed class SaleTransaction
{
    // Assigned client-side at creation — works fully offline (Section 6, Data & Sync Principles).
    public Guid Id { get; private set; }

    // Null until the sale is finalized. Gapless, per-store sequential — required for tax compliance.
    public long? InvoiceNumber { get; private set; }

    public string StoreId { get; private set; } = null!;
    public string RegisterId { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public SaleTransactionStatus Status { get; private set; }

    private readonly List<SaleLineItem> _lineItems = new();

    public IReadOnlyCollection<SaleLineItem> LineItems => _lineItems.AsReadOnly();

    public decimal SubtotalExGst => _lineItems.Sum(li => li.LineTotalExGst);
    public decimal GstTotal => _lineItems.Sum(li => li.GstAmount);
    public decimal GrandTotal => SubtotalExGst + GstTotal;

    private readonly List<SalePayment> _payments = new();

    public IReadOnlyCollection<SalePayment> Payments => _payments.AsReadOnly();

    /// <summary>
    /// Australian 5c cash rounding applied when cash finalised the sale (e.g. +0.02 or -0.01).
    /// Payments always add up to GrandTotal (to the cent) plus this amount.
    /// </summary>
    public decimal CashRounding { get; private set; }

    // EF Core requires a parameterless constructor for materialization.
    private SaleTransaction() { }

    public SaleTransaction(string storeId, string registerId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("Store ID is required.", nameof(storeId));
        if (string.IsNullOrWhiteSpace(registerId))
            throw new ArgumentException("Register ID is required.", nameof(registerId));

        Id = Guid.NewGuid();
        StoreId = storeId;
        RegisterId = registerId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Status = SaleTransactionStatus.InProgress;
    }

    /// <summary>
    /// Adds a line item to the sale. Returns are modeled as normal transactions
    /// with negative quantity — not special reversal records (Section 5, Sales domain rule).
    /// </summary>
    public void AddLineItem(SaleLineItem lineItem)
    {
        if (Status != SaleTransactionStatus.InProgress)
            throw new InvalidOperationException(
                $"Cannot modify a sale in status '{Status}'. Only in-progress sales can be edited.");

        ArgumentNullException.ThrowIfNull(lineItem);
        _lineItems.Add(lineItem);
    }

    /// <summary>
    /// Records how the sale was paid. Called once, before Complete, with every tender
    /// (several for a split sale) and the cash rounding that settled it.
    /// </summary>
    public void RecordPayments(IEnumerable<SalePayment> payments, decimal cashRounding)
    {
        if (Status != SaleTransactionStatus.InProgress)
            throw new InvalidOperationException(
                $"Cannot record payments for a sale in status '{Status}'.");
        if (_payments.Count > 0)
            throw new InvalidOperationException("Payments have already been recorded for this sale.");

        ArgumentNullException.ThrowIfNull(payments);
        var paymentList = payments.ToList();

        var expected = decimal.Round(GrandTotal, 2, MidpointRounding.AwayFromZero) + cashRounding;
        var paid = paymentList.Sum(p => p.Amount);
        if (paid != expected)
            throw new InvalidOperationException(
                $"Payments total {paid:0.00} but the sale requires {expected:0.00}.");

        _payments.AddRange(paymentList);
        CashRounding = cashRounding;
    }

    /// <summary>
    /// Finalizes the sale and assigns its invoice number.
    /// Invoice number assignment is the caller's responsibility (Application layer),
    /// since gapless sequencing requires coordination the Domain entity cannot own alone.
    /// </summary>
    public void Complete(long invoiceNumber)
    {
        if (Status != SaleTransactionStatus.InProgress)
            throw new InvalidOperationException(
                $"Cannot complete a sale in status '{Status}'.");
        if (_lineItems.Count == 0)
            throw new InvalidOperationException("Cannot complete a sale with no line items.");

        InvoiceNumber = invoiceNumber;
        Status = SaleTransactionStatus.Completed;
    }
}

public enum SaleTransactionStatus
{
    InProgress,
    Completed,
    Voided
}
