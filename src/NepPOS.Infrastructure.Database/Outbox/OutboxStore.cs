using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Sync;

namespace NepPOS.Infrastructure.Database.Outbox;

/// <summary>
/// The outbox as the Head Office sync uses it. Each call has its own short-lived DbContext,
/// like the journal, so it never saves changes another screen is still making.
/// </summary>
public sealed class OutboxStore : IOutboxStore
{
    private readonly DbContextOptions<NepPosDbContext> _options;

    public OutboxStore(DbContextOptions<NepPosDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<SyncEvent>> GetUnsentAsync(int max, CancellationToken cancellationToken = default)
    {
        await using var db = new NepPosDbContext(_options);
        var rows = await db.OutboxMessages.AsNoTracking()
            .Where(o => o.SentAtUtc == null && o.RejectedAtUtc == null)
            .OrderBy(o => o.Id)
            .Take(Math.Clamp(max, 1, SyncEventsRequest.MaxEvents))
            .ToListAsync(cancellationToken);

        return rows.Select(o => o.ToSyncEvent()).ToList();
    }

    public async Task MarkSentAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        if (eventIds.Count == 0) return;

        var now = DateTime.UtcNow;
        await using var db = new NepPosDbContext(_options);
        await db.OutboxMessages
            .Where(o => eventIds.Contains(o.EventId) && o.SentAtUtc == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.SentAtUtc, now)
                .SetProperty(o => o.Attempts, o => o.Attempts + 1)
                .SetProperty(o => o.LastError, (string?)null), cancellationToken);
    }

    public async Task MarkRejectedAsync(IReadOnlyCollection<RejectedEvent> rejected, CancellationToken cancellationToken = default)
    {
        if (rejected.Count == 0) return;

        var now = DateTime.UtcNow;
        await using var db = new NepPosDbContext(_options);
        foreach (var item in rejected)
        {
            var reason = item.Reason.Length > 500 ? item.Reason[..500] : item.Reason;
            await db.OutboxMessages
                .Where(o => o.EventId == item.EventId && o.SentAtUtc == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(o => o.RejectedAtUtc, now)
                    .SetProperty(o => o.Attempts, o => o.Attempts + 1)
                    .SetProperty(o => o.LastError, reason), cancellationToken);
        }
    }

    public async Task<OutboxCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new NepPosDbContext(_options);
        var waiting = await db.OutboxMessages.CountAsync(o => o.SentAtUtc == null && o.RejectedAtUtc == null, cancellationToken);
        var rejected = await db.OutboxMessages.CountAsync(o => o.RejectedAtUtc != null, cancellationToken);
        var lastSent = await db.OutboxMessages.MaxAsync(o => o.SentAtUtc, cancellationToken);
        var lastReason = await db.OutboxMessages
            .Where(o => o.RejectedAtUtc != null)
            .OrderByDescending(o => o.Id)
            .Select(o => o.LastError)
            .FirstOrDefaultAsync(cancellationToken);

        return new OutboxCounts(waiting, rejected,
            lastSent is { } sent ? DateTime.SpecifyKind(sent, DateTimeKind.Utc) : null,
            lastReason);
    }
}
