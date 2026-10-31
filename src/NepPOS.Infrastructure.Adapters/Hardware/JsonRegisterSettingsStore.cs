using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>
/// This register's store details and printer choice, in
/// %LOCALAPPDATA%\NepPOS\register-settings.json (per PC, like the legacy registry settings,
/// because each register has its own printer).
/// A missing or damaged file falls back to defaults so the register still starts.
/// </summary>
public sealed class JsonRegisterSettingsStore : IRegisterSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<JsonRegisterSettingsStore> _logger;
    private readonly object _lock = new();

    public JsonRegisterSettingsStore(ILogger<JsonRegisterSettingsStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "register-settings.json");
    }

    public RegisterSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new RegisterSettings();

            try
            {
                return JsonSerializer.Deserialize<RegisterSettings>(File.ReadAllText(_path), JsonOptions)
                       ?? new RegisterSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Register settings file {Path} could not be read - using defaults", _path);
                return new RegisterSettings();
            }
        }
    }

    public void Save(RegisterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write to a temp file first, then swap, so a crash mid-save can't leave a half-written file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }

        _logger.LogInformation("Register settings saved: printer {Mode} '{Printer}', drawer {Drawer}",
            settings.Printer.Mode, settings.Printer.PrinterName, settings.Printer.DrawerMode);
    }
}
