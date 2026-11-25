using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Hardware;
using NepPOS.Application.Maintenance;
using NepPOS.Infrastructure.Database.Maintenance;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>The daily register backup: a real SQLite file, backed up while open, zipped, copied and tidied.</summary>
public sealed class RegisterBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neppos-backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataFolder;
    private readonly string _connectionString;
    private readonly MemorySettings _settings = new();

    public RegisterBackupTests()
    {
        _dataFolder = Path.Combine(_root, "NepPOS");
        Directory.CreateDirectory(_dataFolder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dataFolder, "neppos-register.db"), Pooling = false }.ToString();

        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var create = db.CreateCommand();
        create.CommandText = "CREATE TABLE Sales (Id INTEGER PRIMARY KEY, Total TEXT); INSERT INTO Sales (Total) VALUES ('19.50'), ('44.00');";
        create.ExecuteNonQuery();

        File.WriteAllText(Path.Combine(_dataFolder, "register-settings.json"), "{\"Store\":{}}");
    }

    private SqliteRegisterBackupService Service() =>
        new(_connectionString, _dataFolder, _settings, NullLogger<SqliteRegisterBackupService>.Instance);

    [Fact]
    public async Task BackUp_ZipsTheDatabaseAndSettings_WhileTheDatabaseIsOpen()
    {
        using var open = new SqliteConnection(_connectionString);
        open.Open();   // the till keeps its connection open

        var result = await Service().BackUpNowAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Warning);
        using var zip = ZipFile.OpenRead(result.File!.Path);
        Assert.Contains(zip.Entries, e => e.Name == "register-settings.json");

        var restored = Path.Combine(_root, "restored.db");
        zip.GetEntry("neppos-register.db")!.ExtractToFile(restored);
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restored, Pooling = false }.ToString());
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Sales";
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task BackUp_CopiesToTheSecondFolder()
    {
        var usb = Path.Combine(_root, "usb", "NepPOS backups");
        _settings.Value = _settings.Value with { Backup = new BackupSettings { CopyFolder = usb } };

        var result = await Service().BackUpNowAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Warning);
        Assert.True(File.Exists(Path.Combine(usb, result.File!.Name)));
    }

    [Fact]
    public async Task BackUp_DriveMissing_StillSavesLocally_WithAWarning()
    {
        // A drive letter that won't exist on the build machine.
        _settings.Value = _settings.Value with { Backup = new BackupSettings { CopyFolder = OperatingSystem.IsWindows() ? @"Q:\NepPOS backups" : "/nonexistent-drive/NepPOS" } };

        var service = Service();
        var result = await service.BackUpNowAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Warning);
        Assert.Single(service.List());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class MemorySettings : IRegisterSettingsStore
    {
        public RegisterSettings Value { get; set; } = new();
        public RegisterSettings Load() => Value;
        public void Save(RegisterSettings settings) => Value = settings;
    }
}
