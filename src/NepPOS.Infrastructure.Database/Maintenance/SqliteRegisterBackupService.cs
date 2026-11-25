using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Hardware;
using NepPOS.Application.Maintenance;

namespace NepPOS.Infrastructure.Database.Maintenance;

/// <summary>
/// Backs up the register: SQLite's online backup copies the database while sales carry on, then the copy and
/// register-settings.json are zipped into Backups\neppos-backup-yyyy-MM-dd-HHmm.zip, copied to the second folder
/// (USB drive or OneDrive) if one is set, and old backups are tidied away.
///
/// Not included on purpose: the Head Office key, licence and email password files. They're encrypted for this
/// Windows account and can't be used on another PC anyway; after a restore, reconnect on Manage → Head Office.
/// </summary>
public sealed class SqliteRegisterBackupService : IRegisterBackupService
{
    private const string DatabaseEntry = "neppos-register.db";
    private const string SettingsFile = "register-settings.json";

    private readonly string _connectionString;
    private readonly string _dataFolder;
    private readonly IRegisterSettingsStore _settings;
    private readonly ILogger<SqliteRegisterBackupService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="connectionString">The register database ("Data Source=…\neppos-register.db").</param>
    /// <param name="dataFolder">%LOCALAPPDATA%\NepPOS: where the database and settings live.</param>
    public SqliteRegisterBackupService(string connectionString, string dataFolder, IRegisterSettingsStore settings,
        ILogger<SqliteRegisterBackupService> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _dataFolder = dataFolder ?? throw new ArgumentNullException(nameof(dataFolder));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LocalFolder = Path.Combine(_dataFolder, "Backups");
    }

    public string LocalFolder { get; }

    public BackupResult? LastResult { get; private set; }

    public IReadOnlyList<BackupFile> List() => ListIn(LocalFolder);

    public async Task<BackupResult> BackUpNowAsync(CancellationToken cancellationToken = default)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            // File and SQLite work is synchronous: keep it off the UI thread.
            LastResult = await Task.Run(() => BackUp(DateTime.Now), cancellationToken);
            return LastResult;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private BackupResult BackUp(DateTime nowLocal)
    {
        var settings = _settings.Load().Backup;
        var zipPath = Path.Combine(LocalFolder, BackupRules.FileName(nowLocal));
        var tempDb = Path.Combine(LocalFolder, $"backup-{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(LocalFolder);

            // 1. A consistent copy of the live database (SQLite online backup; sales can continue meanwhile).
            using (var source = new SqliteConnection(_connectionString))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDb, Pooling = false }.ToString()))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }

            // 2. Zip it with the register's settings (printer, receipt, store details).
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, DatabaseEntry, CompressionLevel.Optimal);
                var settingsPath = Path.Combine(_dataFolder, SettingsFile);
                if (File.Exists(settingsPath))
                    zip.CreateEntryFromFile(settingsPath, SettingsFile, CompressionLevel.Optimal);
            }

            var file = ToBackupFile(new FileInfo(zipPath));
            Prune(LocalFolder, nowLocal, settings.KeepDays);
            _logger.LogInformation("Backup saved: {Path} ({Bytes} bytes)", zipPath, file.SizeBytes);

            // 3. The second copy. A missing USB drive mustn't lose the backup that was just made.
            if (string.IsNullOrWhiteSpace(settings.CopyFolder))
                return new BackupResult(true, $"Backed up at {nowLocal:h:mm tt}.", file);

            var copyFolder = settings.CopyFolder.Trim();
            try
            {
                if (!Directory.Exists(Path.GetPathRoot(copyFolder) ?? copyFolder))
                    return new BackupResult(true, $"Backed up on this PC, but {copyFolder} isn't available (USB drive unplugged?).", file, Warning: true);

                Directory.CreateDirectory(copyFolder);
                File.Copy(zipPath, Path.Combine(copyFolder, Path.GetFileName(zipPath)), overwrite: true);
                Prune(copyFolder, nowLocal, settings.KeepDays);
                return new BackupResult(true, $"Backed up at {nowLocal:h:mm tt}, with a copy in {copyFolder}.", file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Backup copy to {Folder} failed", copyFolder);
                return new BackupResult(true, $"Backed up on this PC, but copying to {copyFolder} failed: {ex.Message}", file, Warning: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            _logger.LogError(ex, "Backup failed");
            return new BackupResult(false, "Backup failed: " + ex.Message);
        }
        finally
        {
            TryDelete(tempDb);
        }
    }

    private void Prune(string folder, DateTime nowLocal, int keepDays)
    {
        foreach (var old in BackupRules.ToDelete(ListIn(folder), nowLocal, keepDays))
        {
            TryDelete(old.Path);
            _logger.LogInformation("Old backup deleted: {Path}", old.Path);
        }
    }

    private static IReadOnlyList<BackupFile> ListIn(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<BackupFile>();

        return new DirectoryInfo(folder)
            .GetFiles(BackupRules.FilePrefix + "*.zip")
            .Select(ToBackupFile)
            .OrderByDescending(f => f.CreatedLocal)
            .ToList();
    }

    private static BackupFile ToBackupFile(FileInfo file) => new(file.FullName, file.LastWriteTime, file.Length);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next tidy-up.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Backs up once a day while NepPOS is running: checks five minutes after start (so opening the till stays quick)
/// and then every 30 minutes, and backs up when the newest backup is more than <see cref="BackupRules.Every"/> old.
/// </summary>
public sealed class BackupWorker : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);

    private readonly IRegisterBackupService _backups;
    private readonly ILogger<BackupWorker> _logger;
    private readonly CancellationTokenSource _stop = new();

    public BackupWorker(IRegisterBackupService backups, ILogger<BackupWorker> logger)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        try
        {
            await Task.Delay(FirstCheck, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (BackupRules.IsDue(_backups.List(), DateTime.Now))
                        await _backups.BackUpNowAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Automatic backup check failed");
                }
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
