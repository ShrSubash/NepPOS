using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Journal;

/// <summary>
/// SQLite journal. Each write uses its own short-lived DbContext so it can never
/// accidentally save half-finished changes tracked by other screens (e.g. a PLU edit).
/// </summary>
public sealed class JournalService : IJournal
{
    // One register = one writer, but the UI can raise two events at once; serialise so
    // sequence numbers never collide. The unique index is the database-level guarantee.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly RegisterTerminal _terminal;
    private readonly ICurrentOperator _operator;
    private readonly ILogger<JournalService> _logger;

    public JournalService(
        DbContextOptions<NepPosDbContext> options,
        RegisterTerminal terminal,
        ICurrentOperator currentOperator,
        ILogger<JournalService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _operator = currentOperator ?? throw new ArgumentNullException(nameof(currentOperator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(
        JournalEventType eventType,
        string detail,
        Guid? saleId = null,
        long? invoiceNumber = null,
        decimal? amount = null,
        CancellationToken cancellationToken = default)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = new NepPosDbContext(_options);

            var lastSequence = await db.JournalEntries
                .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
                .MaxAsync(j => (long?)j.Sequence, cancellationToken) ?? 0;

            db.JournalEntries.Add(new JournalEntry(
                lastSequence + 1,
                _terminal.StoreId,
                _terminal.RegisterId,
                _operator.EmployeeId,
                _operator.EmployeeName,
                eventType,
                detail,
                saleId,
                invoiceNumber,
                amount));

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Never rethrow: the event itself (sale, sign-in…) has already happened.
            _logger.LogError(ex,
                "JOURNAL WRITE FAILED: {EventType} {Detail} sale {SaleId} invoice {InvoiceNumber} amount {Amount}",
                eventType, detail, saleId, invoiceNumber, amount);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Local calendar days -> UTC range [fromUtc, toUtc).
        var fromUtc = query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var toUtc = query.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

        await using var db = new NepPosDbContext(_options);

        var entries = db.JournalEntries
            .AsNoTracking()
            .Where(j => j.StoreId == _terminal.StoreId && j.RegisterId == _terminal.RegisterId)
            .Where(j => j.OccurredAtUtc >= fromUtc && j.OccurredAtUtc < toUtc);

        if (query.EmployeeId is not null)
            entries = entries.Where(j => j.EmployeeId == query.EmployeeId);

        if (query.EventType is { } eventType)
            entries = entries.Where(j => j.EventType == eventType);

        return await entries
            .OrderByDescending(j => j.Sequence)
            .Take(Math.Clamp(query.MaxResults, 1, 5000))
            .ToListAsync(cancellationToken);
    }
}
