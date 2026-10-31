using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Reports;

/// <summary>
/// End-of-day totals for one register since its last Z (day close).
/// X reading = these totals right now, nothing reset. Z report = the same totals, then the
/// period is closed so the next report starts from zero.
/// </summary>
public sealed record EndOfDayReport
{
    public bool IsZ { get; init; }

    /// <summary>1, 2, 3… per register. Set for a Z report; for an X it is the number the next Z will get.</summary>
    public int ZNumber { get; init; }

    public string StoreId { get; init; } = "";
    public string RegisterId { get; init; } = "";
    public string? PrintedBy { get; init; }

    /// <summary>When the period started: the last Z, or the first activity if there has never been a Z.</summary>
    public DateTime PeriodStartUtc { get; init; }
    public DateTime PeriodEndUtc { get; init; }

    // ---- sales ----
    public int SaleCount { get; init; }
    public long? FirstInvoice { get; init; }
    public long? LastInvoice { get; init; }
    public int ItemCount { get; init; }

    /// <summary>Sum of the receipt totals (GST inclusive), before cash rounding.</summary>
    public decimal GrossSales { get; init; }
    public decimal Gst { get; init; }
    public decimal NetSales => GrossSales - Gst;
    public decimal CashRounding { get; init; }

    // ---- tenders ----
    public IReadOnlyList<TenderTotal> Tenders { get; init; } = Array.Empty<TenderTotal>();

    /// <summary>All money kept: equals GrossSales + CashRounding.</summary>
    public decimal TotalTaken => Tenders.Sum(t => t.Amount);

    /// <summary>Cash that should be in the drawer from sales (the opening float is not included).</summary>
    public decimal CashExpected => Tenders.Where(t => t.Tender == TenderType.Cash).Sum(t => t.Amount);

    // ---- activity a manager should check ----
    public int VoidCount { get; init; }
    public decimal VoidAmount { get; init; }
    public int LinesRemoved { get; init; }
    public decimal LinesRemovedAmount { get; init; }
    public int NoSales { get; init; }
    public int CardDeclines { get; init; }
    public int CardOutcomeUnknown { get; init; }
    public int SaveFailures { get; init; }
    public int Reprints { get; init; }

    public IReadOnlyList<EmployeeTotal> ByEmployee { get; init; } = Array.Empty<EmployeeTotal>();
}

public sealed record TenderTotal(TenderType Tender, int Count, decimal Amount);

public sealed record EmployeeTotal(string Employee, int SaleCount, decimal Amount);

/// <summary>Builds and closes end-of-day reports for this register.</summary>
public interface IEndOfDayReportService
{
    /// <summary>X reading: totals since the last Z. Closes nothing.</summary>
    Task<EndOfDayReport> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Z report: the same totals, then the day is closed (recorded in the journal) so the next
    /// report starts from zero. Throws if the close could not be recorded.
    /// </summary>
    Task<EndOfDayReport> CloseDayAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The money rules of the end-of-day report. No database, so every total is unit tested.
/// </summary>
public static class EndOfDayCalculator
{
    /// <param name="sales">Sales in the period. Only completed ones are counted.</param>
    /// <param name="journal">Journal entries in the period (voids, removed lines, no sales…).</param>
    public static EndOfDayReport Calculate(IEnumerable<SaleTransaction> sales, IEnumerable<JournalEntry> journal)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(journal);

        var completed = sales.Where(s => s.Status == SaleTransactionStatus.Completed).ToList();
        var entries = journal.ToList();

        // Per sale, the same rounded figures printed on its receipt, so the report matches the receipts.
        static decimal Cents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

        var payments = completed.SelectMany(s => s.Payments).ToList();

        // Cash and EFTPOS are always listed, even at $0.00, so the cashier sees every tender.
        var tenders = Enum.GetValues<TenderType>()
            .Select(type =>
            {
                var ofType = payments.Where(p => p.TenderType == type).ToList();
                return new TenderTotal(type, ofType.Count, ofType.Sum(p => p.Amount));
            })
            .ToList();

        var byEmployee = completed
            .GroupBy(s => string.IsNullOrWhiteSpace(s.EmployeeName) ? "Unknown" : s.EmployeeName!)
            .Select(g => new EmployeeTotal(g.Key, g.Count(), g.Sum(s => Cents(s.GrandTotal))))
            .OrderByDescending(e => e.Amount)
            .ToList();

        int Count(JournalEventType type) => entries.Count(e => e.EventType == type);

        var invoices = completed.Where(s => s.InvoiceNumber is not null).Select(s => s.InvoiceNumber!.Value).ToList();

        return new EndOfDayReport
        {
            SaleCount = completed.Count,
            FirstInvoice = invoices.Count == 0 ? null : invoices.Min(),
            LastInvoice = invoices.Count == 0 ? null : invoices.Max(),
            ItemCount = completed.Sum(s => s.LineItems.Sum(li => li.Quantity)),
            GrossSales = completed.Sum(s => Cents(s.GrandTotal)),
            Gst = completed.Sum(s => Cents(s.GstTotal)),
            CashRounding = completed.Sum(s => s.CashRounding),
            Tenders = tenders,
            VoidCount = Count(JournalEventType.SaleVoided),
            VoidAmount = entries.Where(e => e.EventType == JournalEventType.SaleVoided).Sum(e => e.Amount ?? 0),
            LinesRemoved = Count(JournalEventType.LineRemoved),
            LinesRemovedAmount = entries.Where(e => e.EventType == JournalEventType.LineRemoved).Sum(e => e.Amount ?? 0),
            NoSales = Count(JournalEventType.NoSale),
            CardDeclines = Count(JournalEventType.CardDeclined),
            CardOutcomeUnknown = Count(JournalEventType.CardOutcomeUnknown),
            SaveFailures = Count(JournalEventType.SaleSaveFailed),
            Reprints = Count(JournalEventType.ReceiptReprinted),
            ByEmployee = byEmployee
        };
    }
}
