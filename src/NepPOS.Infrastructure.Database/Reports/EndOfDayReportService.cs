using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Application.Reports;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Reports;

/// <summary>
/// End-of-day reports for this register. The period is "everything since the last Z":
/// sales with an invoice number after the one stored on the last ZReport journal entry,
/// and journal entries after that entry's sequence number. No extra table is needed and
/// the journal keeps a permanent record of every Z.
/// </summary>
public sealed class EndOfDayReportService : IEndOfDayReportService
{
    // Two managers pressing "Close day" at once must not close the same day twice.
    private static readonly SemaphoreSlim CloseLock = new(1, 1);

    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly RegisterTerminal _terminal;
    private readonly IJournal _journal;
    private readonly ICurrentOperator _operator;

    public EndOfDayReportService(
        DbContextOptions<NepPosDbContext> options,
        RegisterTerminal terminal,
        IJournal journal,
        ICurrentOperator currentOperator)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _operator = currentOperator ?? throw new ArgumentNullException(nameof(currentOperator));
    }

    public async Task<EndOfDayReport> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var (report, _) = await BuildAsync(isZ: false, cancellationToken);
        return report;
    }

    public async Task<EndOfDayReport> CloseDayAsync(CancellationToken cancellationToken = default)
    {
        await CloseLock.WaitAsync(cancellationToken);
        try
        {
            var (report, period) = await BuildAsync(isZ: true, cancellationToken);

            // The Z entry stores the last invoice it covers; the next period starts after it.
            // With no sales today, carry the previous boundary forward.
            await _journal.WriteAsync(
                JournalEventType.ZReport,
                $"Z #{report.ZNumber}: {report.SaleCount} sales, {report.GrossSales:C} gross, {report.CashExpected:C} cash",
                invoiceNumber: report.LastInvoice ?? period.AfterInvoice,
                amount: report.GrossSales,
                cancellationToken: cancellationToken);

            // IJournal never throws, so check the Z really was recorded before telling anyone the day is closed.
            await using var db = new NepPosDbContext(_options);
            var recorded = await LastZAsync(db, cancellationToken);
            if (recorded is null || recorded.Sequence <= period.AfterSequence)
                throw new InvalidOperationException("The day could not be closed. Details are in the log file.");

            return report with { PeriodEndUtc = recorded.OccurredAtUtc };
        }
        finally
        {
            CloseLock.Release();
        }
    }

    private async Task<(EndOfDayReport Report, Period Period)> BuildAsync(bool isZ, CancellationToken cancellationToken)
    {
        await using var db = new NepPosDbContext(_options);

        var lastZ = await LastZAsync(db, cancellationToken);
        var zCount = await db.JournalEntries
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .CountAsync(j => j.EventType == JournalEventType.ZReport, cancellationToken);

        var period = new Period(lastZ?.InvoiceNumber ?? 0, lastZ?.Sequence ?? 0);

        var sales = await db.SaleTransactions
            .AsNoTracking()
            .Include(s => s.LineItems)
            .Include(s => s.Payments)
            .Where(s => s.StoreId == _terminal.StoreId && s.RegisterId == _terminal.RegisterId)
            .Where(s => s.Status == SaleTransactionStatus.Completed && s.InvoiceNumber > period.AfterInvoice)
            .ToListAsync(cancellationToken);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .Where(j => j.Sequence > period.AfterSequence)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;

        // Period start: the last Z; with no Z yet, the earliest sale or journal entry.
        var start = lastZ?.OccurredAtUtc
            ?? sales.Select(s => s.CreatedAtUtc.UtcDateTime)
                .Concat(journal.Select(j => j.OccurredAtUtc))
                .DefaultIfEmpty(now)
                .Min();

        var report = EndOfDayCalculator.Calculate(sales, journal) with
        {
            IsZ = isZ,
            ZNumber = zCount + 1,
            StoreId = _terminal.StoreId,
            RegisterId = _terminal.RegisterId,
            PrintedBy = _operator.EmployeeName,
            PeriodStartUtc = start,
            PeriodEndUtc = now
        };

        return (report, period);
    }

    private Task<JournalEntry?> LastZAsync(NepPosDbContext db, CancellationToken cancellationToken) =>
        db.JournalEntries
            .AsNoTracking()
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .Where(j => j.EventType == JournalEventType.ZReport)
            .OrderByDescending(j => j.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Where the open period starts: after this invoice number and this journal sequence.</summary>
    private sealed record Period(long AfterInvoice, long AfterSequence);
}
