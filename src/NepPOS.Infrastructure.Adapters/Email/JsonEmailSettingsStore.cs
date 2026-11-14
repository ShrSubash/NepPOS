using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Email;

namespace NepPOS.Infrastructure.Adapters.Email;

/// <summary>
/// Email settings in %LOCALAPPDATA%\NepPOS\email-settings.json. The password is encrypted with
/// Windows DPAPI for the signed-in Windows user, so the file is useless if copied to another PC.
/// </summary>
public sealed class JsonEmailSettingsStore : IEmailSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NepPOS.Email.v1");

    private readonly string _path;
    private readonly ILogger<JsonEmailSettingsStore> _logger;
    private readonly object _lock = new();

    public JsonEmailSettingsStore(ILogger<JsonEmailSettingsStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "email-settings.json");
    }

    public EmailSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new EmailSettings();

            try
            {
                var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path), JsonOptions);
                if (stored is null)
                    return new EmailSettings();

                return stored.Settings with { Password = Unprotect(stored.ProtectedPassword) };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email settings file {Path} could not be read - using defaults", _path);
                return new EmailSettings();
            }
        }
    }

    public void Save(EmailSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stored = new StoredSettings(settings with { Password = "" }, Protect(settings.Password));
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        var bytes = Encoding.UTF8.GetBytes(password);
        return OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser))
            : Convert.ToBase64String(bytes); // tests on other systems only; registers are Windows
    }

    private string Unprotect(string protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return "";
        try
        {
            var bytes = Convert.FromBase64String(protectedPassword);
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Copied from another PC or Windows user: the password has to be typed again.
            _logger.LogWarning(ex, "Saved email password could not be decrypted on this PC");
            return "";
        }
    }

    /// <summary>The file layout: settings without the password, plus the encrypted password.</summary>
    private sealed record StoredSettings(EmailSettings Settings, string ProtectedPassword);
}
