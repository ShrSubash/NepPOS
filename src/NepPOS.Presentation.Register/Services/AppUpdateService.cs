using System.Reflection;
using Microsoft.Extensions.Logging;
using Velopack;

namespace NepPOS.Presentation.Register.Services;

public enum UpdateState
{
    /// <summary>This build has no update address (built without -p:NepPosUpdateUrl=…).</summary>
    NotSetUp,

    /// <summary>Running from Visual Studio or a copied folder, not from the installer: updates don't apply.</summary>
    NotInstalled,

    Checking,
    UpToDate,

    /// <summary>Downloaded; installs the next time NepPOS starts (or now, from Manage).</summary>
    ReadyToInstall,

    Failed
}

public sealed record UpdateStatus(UpdateState State, string Message, string CurrentVersion, string? NewVersion = null, DateTime? CheckedAt = null);

/// <summary>
/// Keeps the register up to date (Velopack). Every 4 hours it looks for a newer release at the update address,
/// downloads it in the background, and Velopack installs it the next time NepPOS starts — usually the next
/// morning — so a sale is never interrupted. A manager can also install it straight away on Manage → Backups &amp; updates.
/// The register database lives in %LOCALAPPDATA%\NepPOS, outside the install folder, so updates never touch it.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    private readonly ILogger<AppUpdateService> _logger;
    private readonly UpdateManager? _manager;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private UpdateInfo? _ready;

    public AppUpdateService(ILogger<AppUpdateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var current = typeof(AppUpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";
        if (FeedUrl is null)
        {
            Status = new UpdateStatus(UpdateState.NotSetUp, "Automatic updates aren't set up in this build.", current);
            return;
        }

        try
        {
            _manager = new UpdateManager(FeedUrl);
            if (!_manager.IsInstalled)
            {
                Status = new UpdateStatus(UpdateState.NotInstalled, "Updates work once NepPOS is installed with its installer.", current);
                _manager = null;
                return;
            }

            Status = new UpdateStatus(UpdateState.UpToDate, "Not checked yet.", _manager.CurrentVersion?.ToString() ?? current);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update manager could not start");
            Status = new UpdateStatus(UpdateState.Failed, "Updates couldn't start. Details are in the log file.", current);
            _manager = null;
        }
    }

    /// <summary>Where releases are published, baked in at build time (AssemblyMetadata "UpdateFeedUrl").</summary>
    public static string? FeedUrl { get; } = typeof(AppUpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateFeedUrl")?.Value is { Length: > 0 } url ? url : null;

    public UpdateStatus Status { get; private set; }

    public bool CanInstallNow => _manager is not null && _ready is not null;

    /// <summary>Starts the background checks. Called once when the app starts.</summary>
    public void Start()
    {
        if (_manager is null)
            return;
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Looks for an update and downloads it. Never throws: problems end up in <see cref="Status"/>.</summary>
    public async Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null)
            return Status;

        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var current = _manager.CurrentVersion?.ToString() ?? Status.CurrentVersion;
            Status = Status with { State = UpdateState.Checking, Message = "Checking for updates…" };

            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = new UpdateStatus(_ready is null ? UpdateState.UpToDate : UpdateState.ReadyToInstall,
                    _ready is null ? "NepPOS is up to date." : Status.Message, current, Status.NewVersion, DateTime.Now);
                return Status;
            }

            var version = update.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Update {Version} found; downloading", version);
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;

            Status = new UpdateStatus(UpdateState.ReadyToInstall,
                $"Version {version} is ready. It installs the next time NepPOS starts.", current, version, DateTime.Now);
            return Status;
        }
        catch (Exception ex)
        {
            // No internet, server down… the register keeps working on the version it has.
            _logger.LogWarning(ex, "Update check failed");
            Status = Status with { State = UpdateState.Failed, Message = "Couldn't check for updates (no internet?). It tries again later.", CheckedAt = DateTime.Now };
            return Status;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Installs the downloaded update and restarts NepPOS (Manage button; only between sales).</summary>
    public void InstallNowAndRestart()
    {
        if (_manager is null || _ready is null)
            return;

        _logger.LogInformation("Installing update {Version} now", _ready.TargetFullRelease.Version);
        Serilog.Log.CloseAndFlush();
        _manager.ApplyUpdatesAndRestart(_ready);
    }

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(FirstCheck, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                await CheckNowAsync(_stop.Token);
                await Task.Delay(CheckEvery, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
