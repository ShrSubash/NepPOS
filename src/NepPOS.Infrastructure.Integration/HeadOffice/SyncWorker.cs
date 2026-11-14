using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Sync;

namespace NepPOS.Infrastructure.Integration.HeadOffice;

/// <summary>
/// The background loop on the register: sends the outbox every 10 seconds and a heartbeat every
/// minute. After a failure it waits longer each time (up to 5 minutes), so a shop with no internet
/// isn't flooded with retries. The till never waits for any of this.
/// </summary>
public sealed class SyncWorker : IDisposable
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SyncWorker> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _statusLock = new();

    private SyncStatus _status = new(SyncOutcome.NotConnected, null, null, 0);
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    public SyncWorker(IServiceScopeFactory scopes, ILogger<SyncWorker> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The latest result, for the Manage → Head Office screen.</summary>
    public SyncStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>Starts the loop. Called once when the app starts.</summary>
    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>Runs a sync straight away (the "Send now" button, or right after connecting).</summary>
    public void SendNow()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already woken: one run is enough */ }
    }

    private async Task LoopAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

                var result = await sync.SendOutboxAsync(_stop.Token);

                if (result.Outcome is SyncOutcome.UpToDate && DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatEvery)
                {
                    var heartbeat = await sync.HeartbeatAsync(_stop.Token);
                    if (heartbeat.Outcome == SyncOutcome.UpToDate)
                        _lastHeartbeatUtc = DateTime.UtcNow;
                    else
                        result = result with { Outcome = heartbeat.Outcome, Problem = heartbeat.Problem };
                }

                failures = result.Outcome is SyncOutcome.Failed or SyncOutcome.Unauthorized ? failures + 1 : 0;
                Record(result);

                if (result.Sent + result.Rejected > 0)
                    _logger.LogInformation("Head Office sync: {Sent} sent, {Rejected} rejected", result.Sent, result.Rejected);
                if (result.Problem is not null)
                    _logger.LogWarning("Head Office sync problem ({Outcome}): {Problem}", result.Outcome, result.Problem);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A database or programming error: log it, keep the loop alive, back off.
                failures++;
                Record(new SyncRunResult(SyncOutcome.Failed, 0, 0, ex.Message));
                _logger.LogError(ex, "Head Office sync failed");
            }

            try
            {
                // Sleep until the next run, or until someone presses "Send now".
                await _wake.WaitAsync(SyncBackoff.After(failures), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Record(SyncRunResult result)
    {
        lock (_statusLock)
        {
            _status = new SyncStatus(
                result.Outcome,
                result.Outcome == SyncOutcome.UpToDate ? DateTime.UtcNow : _status.LastSuccessUtc,
                result.Problem,
                _status.SentSinceStart + result.Sent);
        }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>What the Head Office screen shows about the background sync.</summary>
public sealed record SyncStatus(SyncOutcome Outcome, DateTime? LastSuccessUtc, string? Problem, int SentSinceStart);
