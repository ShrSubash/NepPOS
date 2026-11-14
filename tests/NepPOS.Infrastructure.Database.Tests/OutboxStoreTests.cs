using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Journal;
using NepPOS.Infrastructure.Database.Outbox;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Integration build step 3: reading the outbox in order and marking what Head Office has.</summary>
public class OutboxStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly JournalService _journal;
    private readonly OutboxStore _store;

    public OutboxStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;
        using (var db = new NepPosDbContext(_options))
            db.Database.EnsureCreated();

        _journal = new JournalService(_options, new RegisterTerminal("STORE-001", "REG-001"),
            new SignedInOperator(), NullLogger<JournalService>.Instance);
        _store = new OutboxStore(_options);
    }

    private async Task<IReadOnlyList<SyncEvent>> ThreeEventsAsync()
    {
        await _journal.WriteAsync(JournalEventType.SignIn, "Owner signed in");
        await _journal.WriteAsync(JournalEventType.NoSale, "Drawer opened");
        await _journal.WriteAsync(JournalEventType.SignOut, "Owner signed out");
        return await _store.GetUnsentAsync(500);
    }

    [Fact]
    public async Task Unsent_ComeOldestFirst_WithTheirSequence()
    {
        var events = await ThreeEventsAsync();

        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(("STORE-001", "REG-001"), (e.StoreId, e.RegisterId)));
        Assert.Equal(2, (await _store.GetUnsentAsync(2)).Count);
    }

    [Fact]
    public async Task SentAndRejected_AreNotOfferedAgain_AndAreCounted()
    {
        var events = await ThreeEventsAsync();

        await _store.MarkSentAsync(new[] { events[0].EventId });
        await _store.MarkRejectedAsync(new[] { new RejectedEvent(events[1].EventId, "Event is for store STORE-002") });

        var left = await _store.GetUnsentAsync(500);
        Assert.Equal(events[2].EventId, Assert.Single(left).EventId);

        var counts = await _store.GetCountsAsync();
        Assert.Equal((1, 1, "Event is for store STORE-002"), (counts.Waiting, counts.Rejected, counts.LastRejectReason));
        Assert.NotNull(counts.LastSentUtc);
        Assert.Equal(DateTimeKind.Utc, counts.LastSentUtc!.Value.Kind);
    }

    [Fact]
    public async Task MarkingSentTwice_KeepsTheFirstTime()
    {
        var events = await ThreeEventsAsync();
        await _store.MarkSentAsync(new[] { events[0].EventId });
        DateTime? first;
        await using (var db = new NepPosDbContext(_options))
            first = (await db.OutboxMessages.SingleAsync(o => o.EventId == events[0].EventId)).SentAtUtc;

        await Task.Delay(20);
        await _store.MarkSentAsync(new[] { events[0].EventId });   // a duplicate answer after a retry

        await using var check = new NepPosDbContext(_options);
        var row = await check.OutboxMessages.SingleAsync(o => o.EventId == events[0].EventId);
        Assert.Equal(first, row.SentAtUtc);
        Assert.Equal(1, row.Attempts);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class SignedInOperator : ICurrentOperator
    {
        public int? EmployeeId => 1;
        public string? EmployeeName => "Owner";
    }
}
