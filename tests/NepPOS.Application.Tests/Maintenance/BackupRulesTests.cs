using NepPOS.Application.Maintenance;
using Xunit;

namespace NepPOS.Application.Tests.Maintenance;

public class BackupRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 15, 0, 0);

    private static BackupFile Made(TimeSpan ago) => new($@"C:\b\{BackupRules.FileName(Now - ago)}", Now - ago, 1000);

    [Fact]
    public void NoBackups_IsDue() => Assert.True(BackupRules.IsDue(Array.Empty<BackupFile>(), Now));

    [Fact]
    public void BackupThisMorning_IsNotDue() => Assert.False(BackupRules.IsDue(new[] { Made(TimeSpan.FromHours(6)) }, Now));

    [Fact]
    public void BackupYesterday_IsDue() => Assert.True(BackupRules.IsDue(new[] { Made(TimeSpan.FromHours(21)) }, Now));

    [Fact]
    public void ToDelete_RemovesOnlyOlderThanKeepDays()
    {
        var files = new[] { Made(TimeSpan.FromDays(1)), Made(TimeSpan.FromDays(2)), Made(TimeSpan.FromDays(3)), Made(TimeSpan.FromDays(10)), Made(TimeSpan.FromDays(40)) };

        var delete = BackupRules.ToDelete(files, Now, keepDays: 30);

        Assert.Equal(files[4].Path, Assert.Single(delete).Path);
    }

    [Fact]
    public void ToDelete_AlwaysKeepsTheNewestFew_EvenIfOld()
    {
        // A register that was switched off for months keeps its last backups.
        var files = Enumerable.Range(0, 5).Select(i => Made(TimeSpan.FromDays(100 + i))).ToList();

        var delete = BackupRules.ToDelete(files, Now, keepDays: 30);

        Assert.Equal(files.Count - BackupRules.AlwaysKeep, delete.Count);
        Assert.DoesNotContain(delete, f => f.Path == files[0].Path);
    }

    [Fact]
    public void FileName_SortsByDate() =>
        Assert.Equal("neppos-backup-2026-11-25-1500.zip", BackupRules.FileName(Now));
}
