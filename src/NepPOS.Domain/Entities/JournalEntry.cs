using NepPOS.Domain.Enums;

namespace NepPOS.Domain.Entities;

/// <summary>
/// One line in the register journal: who did what, when, and for how much.
/// Append-only — entries are never edited or deleted, which is what makes the
/// journal trustworthy for audits, reports and the upward sync to Master/Cloud.
/// </summary>
public sealed class JournalEntry
{
    /// <summary>GUID assigned on the register, so entries sync without key clashes.</summary>
    public Guid Id { get; private set; }

    /// <summary>1, 2, 3… per register (like legacy JNL#). A gap means an entry is missing.</summary>
    public long Sequence { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }
    public string StoreId { get; private set; } = null!;
    public string RegisterId { get; private set; } = null!;

    // Snapshot of who was signed in. The name is copied so reports still read
    // correctly after an employee is renamed or deactivated.
    public int? EmployeeId { get; private set; }
    public string? EmployeeName { get; private set; }

    public JournalEventType EventType { get; private set; }

    public Guid? SaleId { get; private set; }
    public long? InvoiceNumber { get; private set; }
    public decimal? Amount { get; private set; }

    /// <summary>Human-readable detail, e.g. "Heineken 6-Pack x2 removed".</summary>
    public string Detail { get; private set; } = "";

    // EF Core requires a parameterless constructor for materialization.
    private JournalEntry() { }

    public JournalEntry(
        long sequence,
        string storeId,
        string registerId,
        int? employeeId,
        string? employeeName,
        JournalEventType eventType,
        string detail,
        Guid? saleId = null,
        long? invoiceNumber = null,
        decimal? amount = null)
    {
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence starts at 1.");
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("Store ID is required.", nameof(storeId));
        if (string.IsNullOrWhiteSpace(registerId))
            throw new ArgumentException("Register ID is required.", nameof(registerId));

        Id = Guid.NewGuid();
        Sequence = sequence;
        OccurredAtUtc = DateTime.UtcNow;
        StoreId = storeId;
        RegisterId = registerId;
        EmployeeId = employeeId;
        EmployeeName = employeeName;
        EventType = eventType;
        Detail = (detail ?? "").Length > 500 ? detail![..500] : detail ?? "";
        SaleId = saleId;
        InvoiceNumber = invoiceNumber;
        Amount = amount;
    }
}
