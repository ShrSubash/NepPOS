using System.Text.Json;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Licensing;

namespace NepPOS.Infrastructure.Adapters.Licensing;

/// <summary>
/// The licence in %LOCALAPPDATA%\NepPOS\licence.json. It's signed by Head Office, so editing the file only
/// makes it invalid; deleting it means the register waits for a new one at its next contact.
/// </summary>
public sealed class JsonLicenceStore : ILicenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<JsonLicenceStore> _logger;
    private readonly object _lock = new();

    public JsonLicenceStore(ILogger<JsonLicenceStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "licence.json"))
    {
    }

    /// <param name="path">Tests pass a temporary file.</param>
    public JsonLicenceStore(ILogger<JsonLicenceStore> logger, string path)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = path;
    }

    public StoredLicence Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && JsonSerializer.Deserialize<StoredLicence>(File.ReadAllText(_path), JsonOptions) is { } stored)
                    return stored;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Licence file {Path} could not be read", _path);
            }

            // First run (or an unreadable file): start the activation clock now and remember it.
            var fresh = new StoredLicence(null, null, null, DateTime.UtcNow);
            SaveLocked(fresh);
            return fresh;
        }
    }

    public void Save(StoredLicence licence)
    {
        ArgumentNullException.ThrowIfNull(licence);
        lock (_lock)
            SaveLocked(licence);
    }

    private void SaveLocked(StoredLicence licence)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(licence, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Licence file {Path} could not be saved", _path);
        }
    }
}
