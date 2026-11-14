using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;

namespace NepPOS.Infrastructure.Adapters.Reports;

/// <summary>
/// Report files on this PC: the folder set on the Reports screen, or %LOCALAPPDATA%\NepPOS\Reports,
/// with a sub-folder per year and month so years of Z reports stay easy to find.
/// </summary>
public sealed class LocalReportFileStore : IReportFileStore
{
    private readonly IRegisterSettingsStore _settings;
    private readonly ILogger<LocalReportFileStore> _logger;

    public LocalReportFileStore(IRegisterSettingsStore settings, ILogger<LocalReportFileStore> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "Reports");

    public string Folder
    {
        get
        {
            var configured = _settings.Load().ReportOutput.Folder;
            return string.IsNullOrWhiteSpace(configured) ? DefaultFolder : configured.Trim();
        }
    }

    public async Task<string> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var now = DateTime.Now;
        var folder = Path.Combine(Folder, now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(folder);

        // Never overwrite an earlier report: "2026-11-01 Z0005.pdf", then "... (2).pdf".
        var safeName = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        var path = Path.Combine(folder, safeName);
        for (var copy = 2; File.Exists(path); copy++)
            path = Path.Combine(folder, $"{name} ({copy}){extension}");

        await File.WriteAllBytesAsync(path, content, cancellationToken);
        _logger.LogInformation("Report saved to {Path}", path);
        return path;
    }

    public void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open report folder {Folder}", Folder);
        }
    }
}
