using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Sync;

namespace NepPOS.Infrastructure.Adapters.Sync;

/// <summary>
/// The Head Office connection in %LOCALAPPDATA%\NepPOS\head-office.json. The API key is encrypted
/// with Windows DPAPI for the signed-in Windows user: copied to another PC, the file can't be used
/// (the register then simply shows "not connected").
/// </summary>
public sealed class JsonHeadOfficeConnectionStore : IHeadOfficeConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NepPOS.HeadOffice.v1");

    private readonly string _path;
    private readonly ILogger<JsonHeadOfficeConnectionStore> _logger;
    private readonly object _lock = new();

    public JsonHeadOfficeConnectionStore(ILogger<JsonHeadOfficeConnectionStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "head-office.json");
    }

    public HeadOfficeConnection? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return null;

            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), JsonOptions);
                if (stored is null)
                    return null;

                var key = Unprotect(stored.ProtectedApiKey);
                return string.IsNullOrEmpty(key) ? null : stored.Connection with { ApiKey = key };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Head Office connection file {Path} could not be read", _path);
                return null;
            }
        }
    }

    public void Save(HeadOfficeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stored = new Stored(connection with { ApiKey = "" }, Protect(connection.ApiKey));
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }

    private static string Protect(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        return OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser))
            : Convert.ToBase64String(bytes); // tests on other systems only; registers are Windows
    }

    private string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return "";
        try
        {
            var bytes = Convert.FromBase64String(protectedSecret);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogWarning(ex, "Head Office key could not be decrypted on this PC; the register must be connected again");
            return "";
        }
    }

    /// <summary>The file layout: the connection without its key, plus the encrypted key.</summary>
    private sealed record Stored(HeadOfficeConnection Connection, string ProtectedApiKey);
}
