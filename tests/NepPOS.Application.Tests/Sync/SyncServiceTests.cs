using System.Text.Json;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Sync;
using Xunit;

namespace NepPOS.Application.Tests.Sync;

/// <summary>Integration build step 3: what the register does with its outbox, whatever Head Office answers.</summary>
public class SyncServiceTests
{
    private static readonly HeadOfficeConnection Connected = new("https://ho.example.com", Guid.NewGuid(), "Nep Liquor",
        Guid.NewGuid(), "STORE-001", "Parramatta", "REG-001", "npk_test", DateTime.UtcNow);

    private static SyncEvent Event(long sequence) => new(Guid.NewGuid(), SyncEventTypes.JournalRecorded, 1, "STORE-001", "REG-001",
        sequence, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { detail = "x" }));

    [Fact]
    public async Task NotConnected_SendsNothing()
    {
        var outbox = new FakeOutbox(Event(1));
        var client = new FakeClient();
        var result = await new SyncService(new FakeConnections(null), client, outbox).SendOutboxAsync();

        Assert.Equal(SyncOutcome.NotConnected, result.Outcome);
        Assert.Equal(0, client.Calls);
        Assert.Single(outbox.Unsent);
    }

    [Fact]
    public async Task AcceptedAndDuplicates_AreMarkedSent_RejectedArePutAside()
    {
        var (a, b, c) = (Event(1), Event(2), Event(3));
        var outbox = new FakeOutbox(a, b, c);
        var client = new FakeClient
        {
            Answer = events => new SyncEventsResponse(new[] { a.EventId }, new[] { b.EventId },
                new[] { new RejectedEvent(c.EventId, "Event is for store STORE-002") }, 2)
        };

        var result = await new SyncService(new FakeConnections(Connected), client, outbox).SendOutboxAsync();

        Assert.Equal((SyncOutcome.UpToDate, 2, 1), (result.Outcome, result.Sent, result.Rejected));
        Assert.Empty(outbox.Unsent);
        Assert.Equal(new[] { a.EventId, b.EventId }, outbox.Sent);
        Assert.Equal("Event is for store STORE-002", Assert.Single(outbox.Rejected).Reason);
    }

    [Fact]
    public async Task HeadOfficeUnreachable_MarksNothing_SoTheSameEventsGoNextTime()
    {
        var outbox = new FakeOutbox(Event(1), Event(2));
        var client = new FakeClient { Throw = new HeadOfficeException(HeadOfficeProblem.Unreachable, "Can't reach Head Office.") };

        var result = await new SyncService(new FakeConnections(Connected), client, outbox).SendOutboxAsync();

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Equal("Can't reach Head Office.", result.Problem);
        Assert.Equal(2, outbox.Unsent.Count);
        Assert.Empty(outbox.Sent);
    }

    [Fact]
    public async Task RevokedKey_IsReportedAsUnauthorized()
    {
        var client = new FakeClient { Throw = new HeadOfficeException(HeadOfficeProblem.Unauthorized, "No longer accepted.") };

        var result = await new SyncService(new FakeConnections(Connected), client, new FakeOutbox(Event(1))).SendOutboxAsync();

        Assert.Equal(SyncOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task LongBacklog_GoesInBatchesOfFiveHundred_OldestFirst()
    {
        var outbox = new FakeOutbox(Enumerable.Range(1, 1203).Select(i => Event(i)).ToArray());
        var client = new FakeClient();

        var result = await new SyncService(new FakeConnections(Connected), client, outbox).SendOutboxAsync();

        Assert.Equal(1203, result.Sent);
        Assert.Equal(new[] { 500, 500, 203 }, client.BatchSizes);
        Assert.Equal(1, client.FirstSequences[0]);
        Assert.Equal(501, client.FirstSequences[1]);
        Assert.Empty(outbox.Unsent);
    }

    [Fact]
    public async Task NothingWaiting_DoesNotCallHeadOffice()
    {
        var client = new FakeClient();

        var result = await new SyncService(new FakeConnections(Connected), client, new FakeOutbox()).SendOutboxAsync();

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(4, 160)]
    [InlineData(5, 300)]
    [InlineData(100, 300)]
    public void Backoff_DoublesUpToFiveMinutes(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncBackoff.After(failures));
    }

    // ---- shared data (customers, accounts, gift cards from the other stores) ----

    private static SharedEvent Shared(long id) => new(id, new SyncEvent(Guid.NewGuid(), SyncEventTypes.CustomerChanged, 1,
        "STORE-002", "REG-001", id, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { name = "x" })));

    [Fact]
    public async Task PullShared_AppliesPagesUntilNoMore_AndMovesTheCursor()
    {
        var store = new FakeShared();
        var client = new FakeClient
        {
            Shared = after => after.Registers switch
            {
                0 => new SharedEventsResponse(new[] { Shared(3), Shared(5) }, 5, More: true),
                5 => new SharedEventsResponse(new[] { Shared(9), new SharedEvent(2, Shared(2).Event, FromWebsite: true) }, 12, More: false, LastWebId: 2),
                _ => throw new InvalidOperationException("asked again")
            }
        };

        var result = await new SyncService(new FakeConnections(Connected), client, new FakeOutbox(), shared: store).PullSharedAsync();

        Assert.Equal(SyncOutcome.UpToDate, result.Outcome);
        Assert.Equal(4, result.Received);
        Assert.Equal(new SharedCursor(12, 2), store.Cursor);
        Assert.Equal(new long[] { 3, 5, 9, 2 }, store.Applied);
    }

    [Fact]
    public async Task PullShared_NothingNew_StillMovesPastOtherEvents()
    {
        var store = new FakeShared { Cursor = new SharedCursor(40, 3) };
        var client = new FakeClient { Shared = after => new SharedEventsResponse(Array.Empty<SharedEvent>(), 57, false, after.Website) };

        await new SyncService(new FakeConnections(Connected), client, new FakeOutbox(), shared: store).PullSharedAsync();

        Assert.Equal(new SharedCursor(57, 3), store.Cursor);
    }

    [Fact]
    public async Task PullShared_Unreachable_KeepsTheCursor()
    {
        var store = new FakeShared { Cursor = new SharedCursor(8, 1) };
        var client = new FakeClient { Throw = new HeadOfficeException(HeadOfficeProblem.Unreachable, "offline") };

        var result = await new SyncService(new FakeConnections(Connected), client, new FakeOutbox(), shared: store).PullSharedAsync();

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Equal(new SharedCursor(8, 1), store.Cursor);
    }

    [Fact]
    public async Task PullShared_NotConnected_DoesNothing()
    {
        var store = new FakeShared();
        var client = new FakeClient { Shared = _ => throw new InvalidOperationException("should not be called") };

        var result = await new SyncService(new FakeConnections(null), client, new FakeOutbox(), shared: store).PullSharedAsync();

        Assert.Equal(SyncOutcome.NotConnected, result.Outcome);
    }

    // ---- fakes ----

    private sealed class FakeShared : ISharedDataStore
    {
        public SharedCursor Cursor { get; set; }
        public List<long> Applied { get; } = new();

        public Task<SharedCursor> GetCursorAsync(CancellationToken ct = default) => Task.FromResult(Cursor);

        public Task<int> ApplyAsync(IReadOnlyList<SharedEvent> events, SharedCursor next, CancellationToken ct = default)
        {
            Applied.AddRange(events.Select(e => e.Id));
            Cursor = next;
            return Task.FromResult(events.Count);
        }
    }

    private sealed class FakeConnections(HeadOfficeConnection? connection) : IHeadOfficeConnectionStore
    {
        public HeadOfficeConnection? Load() => connection;
        public void Save(HeadOfficeConnection c) { }
        public void Clear() { }
    }

    private sealed class FakeOutbox(params SyncEvent[] events) : IOutboxStore
    {
        public List<SyncEvent> Unsent { get; } = events.ToList();
        public List<Guid> Sent { get; } = new();
        public List<RejectedEvent> Rejected { get; } = new();

        public Task<IReadOnlyList<SyncEvent>> GetUnsentAsync(int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncEvent>>(Unsent.OrderBy(e => e.Sequence).Take(max).ToList());

        public Task MarkSentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            Sent.AddRange(ids);
            Unsent.RemoveAll(e => ids.Contains(e.EventId));
            return Task.CompletedTask;
        }

        public Task MarkRejectedAsync(IReadOnlyCollection<RejectedEvent> rejected, CancellationToken ct = default)
        {
            Rejected.AddRange(rejected);
            Unsent.RemoveAll(e => rejected.Any(r => r.EventId == e.EventId));
            return Task.CompletedTask;
        }

        public Task<OutboxCounts> GetCountsAsync(CancellationToken ct = default) =>
            Task.FromResult(new OutboxCounts(Unsent.Count, Rejected.Count, null, null));
    }

    private sealed class FakeClient : IHeadOfficeClient
    {
        public Func<IReadOnlyList<SyncEvent>, SyncEventsResponse>? Answer { get; init; }
        public Exception? Throw { get; init; }
        public int Calls { get; private set; }
        public List<int> BatchSizes { get; } = new();
        public List<long> FirstSequences { get; } = new();

        public Task<SyncEventsResponse> SendEventsAsync(HeadOfficeConnection c, IReadOnlyList<SyncEvent> events, CancellationToken ct = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            BatchSizes.Add(events.Count);
            FirstSequences.Add(events[0].Sequence);
            return Task.FromResult(Answer?.Invoke(events)
                ?? new SyncEventsResponse(events.Select(e => e.EventId).ToList(), Array.Empty<Guid>(), Array.Empty<RejectedEvent>(), events[^1].Sequence));
        }

        public Func<SharedCursor, SharedEventsResponse>? Shared { get; init; }

        public Task<SharedEventsResponse> GetSharedAsync(HeadOfficeConnection c, SharedCursor after, CancellationToken ct = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Shared?.Invoke(after) ?? new SharedEventsResponse(Array.Empty<SharedEvent>(), after.Registers, false, after.Website));
        }

        public Task<ActivateResponse> ActivateAsync(string baseUrl, ActivateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HeartbeatResponse> HeartbeatAsync(HeadOfficeConnection c, CancellationToken ct = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new HeartbeatResponse(DateTime.UtcNow));
        }
    }
}
