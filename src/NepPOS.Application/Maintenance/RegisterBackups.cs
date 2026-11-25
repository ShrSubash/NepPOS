namespace NepPOS.Application.Maintenance;

/// <summary>Manage → Backups &amp; updates. Saved with the register's other settings.</summary>
public sealed record BackupSettings
{
    /// <summary>
    /// A second place for every backup, e.g. a USB drive (E:\NepPOS backups) or a synced folder (OneDrive).
    /// Empty = backups stay on this PC only.
    /// </summary>
    public string CopyFolder { get; init; } = "";

    /// <summary>Backups older than this are deleted (the newest few are always kept).</summary>
    public int KeepDays { get; init; } = 30;
}

/// <summary>One backup file: the register database and its settings, zipped.</summary>
public sealed record BackupFile(string Path, DateTime CreatedLocal, long SizeBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <param name="Succeeded">False only when nothing was saved. A failed copy to the second folder still succeeds, with a warning.</param>
public sealed record BackupResult(bool Succeeded, string Message, BackupFile? File = null, bool Warning = false);

/// <summary>
/// The register's own backup: a consistent copy of the SQLite database taken while NepPOS keeps running.
/// Runs by itself once a day (<see cref="BackupRules.Every"/>), and on "Back up now".
/// </summary>
public interface IRegisterBackupService
{
    /// <summary>Where backups are saved on this PC.</summary>
    string LocalFolder { get; }

    /// <summary>Backups on this PC, newest first.</summary>
    IReadOnlyList<BackupFile> List();

    /// <summary>The last automatic or manual run this session (null before the first).</summary>
    BackupResult? LastResult { get; }

    Task<BackupResult> BackUpNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>When to back up and which old backups to delete. Pure, so it is tested without files.</summary>
public static class BackupRules
{
    /// <summary>A day's backup is due when the newest is older than this (a little under a day, so it doesn't drift later).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(20);

    /// <summary>Never delete below this many, however old (a register switched off for a month still has its last backups).</summary>
    public const int AlwaysKeep = 3;

    public const string FilePrefix = "neppos-backup-";

    public static bool IsDue(IEnumerable<BackupFile> existing, DateTime nowLocal)
    {
        var newest = existing.Select(f => (DateTime?)f.CreatedLocal).Max();
        return newest is null || nowLocal - newest.Value >= Every;
    }

    /// <summary>Backups to delete: older than <paramref name="keepDays"/>, apart from the newest <see cref="AlwaysKeep"/>.</summary>
    public static IReadOnlyList<BackupFile> ToDelete(IEnumerable<BackupFile> existing, DateTime nowLocal, int keepDays)
    {
        var days = Math.Clamp(keepDays, 1, 3650);
        return existing
            .OrderByDescending(f => f.CreatedLocal)
            .Skip(AlwaysKeep)
            .Where(f => nowLocal - f.CreatedLocal > TimeSpan.FromDays(days))
            .ToList();
    }

    /// <summary>neppos-backup-2026-11-25-0300.zip</summary>
    public static string FileName(DateTime nowLocal) => $"{FilePrefix}{nowLocal:yyyy-MM-dd-HHmm}.zip";
}
