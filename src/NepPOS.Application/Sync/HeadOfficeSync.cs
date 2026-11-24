using NepPOS.Application.Licensing;
using NepPOS.Contracts.Sync;

namespace NepPOS.Application.Sync;

/// <summary>
/// This register's link to Head Office, made once with an activation code (Manage → Head Office).
/// The API key is kept encrypted on disk; it is plain only in memory.
/// </summary>
public sealed record HeadOfficeConnection(
    string BaseUrl,
    Guid TenantId,
    string TenantName,
    Guid StoreId,
    string StoreCode,
    string StoreName,
    string RegisterCode,
    string ApiKey,
    DateTime ConnectedUtc);

/// <summary>Loads, saves and forgets the connection. Null = not connected.</summary>
public interface IHeadOfficeConnectionStore
{
    HeadOfficeConnection? Load();
    void Save(HeadOfficeConnection connection);
    void Clear();
}

/// <summary>Why a call to Head Office didn't work, in terms a person can act on.</summary>
public enum HeadOfficeProblem
{
    /// <summary>No internet, wrong address, Head Office down, or no answer in time. Try again later.</summary>
    Unreachable,

    /// <summary>Head Office no longer accepts this register's key (revoked). Connect again with a new code.</summary>
    Unauthorized,

    /// <summary>Head Office answered but refused the request (e.g. a used activation code).</summary>
    Refused
}

public sealed class HeadOfficeException : Exception
{
    public HeadOfficeException(HeadOfficeProblem problem, string message, Exception? inner = null) : base(message, inner)
    {
        Problem = problem;
    }

    public HeadOfficeProblem Problem { get; }
}

/// <summary>The Head Office API as the register uses it. Throws <see cref="HeadOfficeException"/> when it can't.</summary>
public interface IHeadOfficeClient
{
    Task<ActivateResponse> ActivateAsync(string baseUrl, ActivateRequest request, CancellationToken cancellationToken = default);
    Task<SyncEventsResponse> SendEventsAsync(HeadOfficeConnection connection, IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default);
    Task<HeartbeatResponse> HeartbeatAsync(HeadOfficeConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>The register's outbox (integration step 1) as the sync sees it.</summary>
public interface IOutboxStore
{
    /// <summary>Oldest first: events not yet sent and not rejected.</summary>
    Task<IReadOnlyList<SyncEvent>> GetUnsentAsync(int max, CancellationToken cancellationToken = default);

    Task MarkSentAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);

    /// <summary>Put aside with Head Office's reason; not sent again until a person deals with it.</summary>
    Task MarkRejectedAsync(IReadOnlyCollection<RejectedEvent> rejected, CancellationToken cancellationToken = default);

    Task<OutboxCounts> GetCountsAsync(CancellationToken cancellationToken = default);
}

public sealed record OutboxCounts(int Waiting, int Rejected, DateTime? LastSentUtc, string? LastRejectReason);

public enum SyncOutcome
{
    NotConnected,
    UpToDate,
    Failed,
    Unauthorized
}

/// <summary>One run of the sync: what was sent and what (if anything) went wrong.</summary>
public sealed record SyncRunResult(SyncOutcome Outcome, int Sent, int Rejected, string? Problem);

/// <summary>
/// Sends the outbox to Head Office in batches, oldest first. Accepted and duplicate events are
/// marked sent; rejected ones are put aside with the reason. If Head Office can't be reached,
/// nothing is marked and the same events go next time, so nothing is lost or sent twice.
/// </summary>
public sealed class SyncService
{
    public const int BatchSize = SyncEventsRequest.MaxEvents;

    // Enough to catch up after a long outage in one run without holding the database for long.
    private const int MaxBatchesPerRun = 20;

    private readonly IHeadOfficeConnectionStore _connections;
    private readonly IHeadOfficeClient _client;
    private readonly IOutboxStore _outbox;
    private readonly LicenceGuard? _licence;

    /// <param name="licence">Receives the licence each heartbeat brings (null in tests that don't need it).</param>
    public SyncService(IHeadOfficeConnectionStore connections, IHeadOfficeClient client, IOutboxStore outbox, LicenceGuard? licence = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _licence = licence;
    }

    public async Task<SyncRunResult> SendOutboxAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Load();
        if (connection is null)
            return new SyncRunResult(SyncOutcome.NotConnected, 0, 0, null);

        var sent = 0;
        var rejected = 0;
        try
        {
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                var events = await _outbox.GetUnsentAsync(BatchSize, cancellationToken);
                if (events.Count == 0)
                    break;

                var answer = await _client.SendEventsAsync(connection, events, cancellationToken);

                var done = answer.Accepted.Concat(answer.Duplicates).ToList();
                if (done.Count > 0)
                    await _outbox.MarkSentAsync(done, cancellationToken);
                if (answer.Rejected.Count > 0)
                    await _outbox.MarkRejectedAsync(answer.Rejected, cancellationToken);

                sent += done.Count;
                rejected += answer.Rejected.Count;

                // Head Office answered for none of them: stop rather than loop on the same batch.
                if (done.Count + answer.Rejected.Count == 0 || events.Count < BatchSize)
                    break;
            }

            return new SyncRunResult(SyncOutcome.UpToDate, sent, rejected, null);
        }
        catch (HeadOfficeException ex) when (ex.Problem == HeadOfficeProblem.Unauthorized)
        {
            return new SyncRunResult(SyncOutcome.Unauthorized, sent, rejected, ex.Message);
        }
        catch (HeadOfficeException ex)
        {
            return new SyncRunResult(SyncOutcome.Failed, sent, rejected, ex.Message);
        }
    }

    /// <summary>Tells Head Office this register is online. Returns the problem, or null when it worked.</summary>
    public async Task<(SyncOutcome Outcome, string? Problem)> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connections.Load();
        if (connection is null)
            return (SyncOutcome.NotConnected, null);

        try
        {
            var answer = await _client.HeartbeatAsync(connection, cancellationToken);
            _licence?.Accept(answer.Licence, answer.LicencePublicKey, answer.ServerTimeUtc);
            return (SyncOutcome.UpToDate, null);
        }
        catch (HeadOfficeException ex)
        {
            return (ex.Problem == HeadOfficeProblem.Unauthorized ? SyncOutcome.Unauthorized : SyncOutcome.Failed, ex.Message);
        }
    }
}

/// <summary>How long to wait after failures: 10 s, 20 s, 40 s… never more than 5 minutes.</summary>
public static class SyncBackoff
{
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);

    public static TimeSpan After(int failuresInARow) =>
        failuresInARow <= 0
            ? Normal
            : TimeSpan.FromSeconds(Math.Min(Longest.TotalSeconds, Normal.TotalSeconds * Math.Pow(2, Math.Min(failuresInARow, 10))));
}
