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

    public string StoreId { get; private set; }
    public string RegisterId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public SaleTransactionStatus Status { get; private set; }

    private readonly List<SaleLineItem> _lineItems = new();
    public IReadOnlyCollection<SaleLineItem> LineItems => _lineItems.AsReadOnly();

    public decimal SubtotalExGst => _lineItems.Sum(li => li.LineTotalExGst);
    public decimal GstTotal => _lineItems.Sum(li => li.GstAmount);
    public decimal GrandTotal => SubtotalExGst + GstTotal;

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