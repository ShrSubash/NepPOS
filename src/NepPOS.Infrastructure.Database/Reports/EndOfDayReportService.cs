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

    public async Task<EndOfDayReport> GetPeriodAsync(DateOnly from, DateOnly to, string title, CancellationToken cancellationToken = default)
    {
        if (to < from)
            (from, to) = (to, from);

        await using var db = new NepPosDbContext(_options);

        var included = (await ClosedDaysAsync(db, cancellationToken))
            .Where(d => d.TradingDay >= from && d.TradingDay <= to)
            .ToList();

        var report = included.Count == 0
            ? EndOfDayCalculator.Calculate(Array.Empty<SaleTransaction>(), Array.Empty<JournalEntry>()) with
            {
                StoreId = _terminal.StoreId,
                RegisterId = _terminal.RegisterId,
                PrintedBy = _operator.EmployeeName
            }
            : await AddUpAsync(db, included, cancellationToken);

        return report with { Title = title, RangeFrom = from, RangeTo = to };
    }

    public async Task<EndOfDayReport?> GetLastZAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new NepPosDbContext(_options);

        var closedDays = await ClosedDaysAsync(db, cancellationToken);
        if (closedDays.Count == 0)
            return null;

        var last = closedDays[^1];
        var report = await AddUpAsync(db, new[] { last }, cancellationToken);

        return report with { IsZ = true, IsReprint = true, ZNumber = last.ZNumber, Days = Array.Empty<DayTotal>() };
    }

    /// <summary>
    /// Every Z this register has done, oldest first, with the Z before it. Its position is its
    /// Z number (1, 2, 3…). A few hundred rows a year, so the date filtering is done in memory.
    /// </summary>
    private async Task<List<ClosedDay>> ClosedDaysAsync(NepPosDbContext db, CancellationToken cancellationToken)
    {
        var allZ = await db.JournalEntries
            .AsNoTracking()
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .Where(j => j.EventType == JournalEventType.ZReport)
            .OrderBy(j => j.Sequence)
            .ToListAsync(cancellationToken);

        return allZ
            .Select((z, index) => new ClosedDay(
                z,
                index == 0 ? null : allZ[index - 1],
                index + 1,
                ReportPeriod.TradingDay(z.OccurredAtUtc)))
            .ToList();
    }

    /// <summary>Totals for a run of consecutive closed days, with one line per day.</summary>
    private async Task<EndOfDayReport> AddUpAsync(NepPosDbContext db, IReadOnlyList<ClosedDay> included, CancellationToken cancellationToken)
    {
        // The Zs are consecutive, so the period is one block: after the Z before the first one,
        // up to and including the last one.
        var first = included[0];
        var last = included[^1];
        var afterInvoice = first.Previous?.InvoiceNumber ?? 0;
        var throughInvoice = last.Z.InvoiceNumber ?? afterInvoice;
        var afterSequence = first.Previous?.Sequence ?? 0;
        var throughSequence = last.Z.Sequence;

        var sales = await db.SaleTransactions
            .AsNoTracking()
            .Include(s => s.LineItems)
            .Include(s => s.Payments)
            .Where(s => s.StoreId == _terminal.StoreId && s.RegisterId == _terminal.RegisterId)
            .Where(s => s.Status == SaleTransactionStatus.Completed
                        && s.InvoiceNumber > afterInvoice && s.InvoiceNumber <= throughInvoice)
            .ToListAsync(cancellationToken);

        var journal = await db.JournalEntries
            .AsNoTracking()
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .Where(j => j.Sequence > afterSequence && j.Sequence <= throughSequence)
            .ToListAsync(cancellationToken);

        // One line per closed day: the sales between the previous Z's invoice and this Z's.
        var days = included
            .Select(d =>
            {
                var after = d.Previous?.InvoiceNumber ?? 0;
                var through = d.Z.InvoiceNumber ?? after;
                var daySales = sales.Where(s => s.InvoiceNumber > after && s.InvoiceNumber <= through).ToList();
                return new DayTotal(
                    d.TradingDay,
                    d.ZNumber,
                    daySales.Count,
                    daySales.Sum(s => decimal.Round(s.GrandTotal, 2, MidpointRounding.AwayFromZero)));
            })
            .ToList();

        return EndOfDayCalculator.Calculate(sales, journal) with
        {
            StoreId = _terminal.StoreId,
            RegisterId = _terminal.RegisterId,
            PrintedBy = _operator.EmployeeName,
            PeriodStartUtc = first.Previous?.OccurredAtUtc
                ?? sales.Select(s => s.CreatedAtUtc.UtcDateTime).DefaultIfEmpty(first.Z.OccurredAtUtc).Min(),
            PeriodEndUtc = last.Z.OccurredAtUtc,
            Days = days
        };
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

    /// <summary>A daily Z, the Z before it (null for the first ever), its number and its trading day.</summary>
    private sealed record ClosedDay(JournalEntry Z, JournalEntry? Previous, int ZNumber, DateOnly TradingDay);
}
