using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NepPOS.Contracts.Licensing;

/// <summary>Where a business's NepPOS subscription stands, as set in NepPOS Portal.</summary>
public enum LicenceStatus
{
    Trial,
    Active,
    /// <summary>Stopped by NepPOS (e.g. unpaid). Registers stop new sales at once; data stays.</summary>
    Suspended,
    /// <summary>The business has left NepPOS.</summary>
    Cancelled
}

/// <summary>Optional parts of NepPOS a plan can include.</summary>
public static class LicenceFeatures
{
    /// <summary>The online shop / app reading the catalogue and sending orders (website keys).</summary>
    public const string Website = "website";

    /// <summary>Importing supplier e-invoices into Receive stock.</summary>
    public const string EInvoices = "einvoices";

    public static readonly IReadOnlyList<string> All = new[] { Website, EInvoices };

    public static string Label(string feature) => feature switch
    {
        Website => "Website and app",
        EInvoices => "Supplier e-invoices",
        _ => feature
    };
}

/// <summary>A ready-made set of limits. The Portal can still change any limit for one business.</summary>
public sealed record LicencePlan(string Code, string Name, int MaxStores, int MaxRegisters, IReadOnlyList<string> Features);

public static class LicencePlans
{
    public static readonly LicencePlan Starter = new("starter", "Starter", 1, 1, Array.Empty<string>());
    public static readonly LicencePlan Standard = new("standard", "Standard", 1, 3, LicenceFeatures.All);
    public static readonly LicencePlan MultiStore = new("multistore", "Multi-store", 5, 15, LicenceFeatures.All);

    public static readonly IReadOnlyList<LicencePlan> All = new[] { Starter, Standard, MultiStore };

    /// <summary>New businesses get this plan's limits for their trial.</summary>
    public static LicencePlan TrialPlan => Standard;

    public const int TrialDays = 30;

    public static LicencePlan? Find(string? code) => All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

    public static string NameOf(string? code) => Find(code)?.Name ?? code ?? "";
}

/// <summary>
/// What Head Office signs and the register keeps. The register checks the signature itself, so it
/// can't be edited and works offline; Head Office sends a fresh one with every heartbeat.
/// </summary>
/// <param name="ValidUntilUtc">End of the trial or of the paid period.</param>
/// <param name="Message">Shown on the register when suspended (why, who to call).</param>
/// <param name="IssuedUtc">When Head Office signed it: the register's "last heard from Head Office".</param>
public sealed record LicenceClaims(
    int Version,
    Guid TenantId,
    string BusinessName,
    string Plan,
    LicenceStatus Status,
    DateTime ValidUntilUtc,
    int MaxStores,
    int MaxRegisters,
    IReadOnlyList<string> Features,
    string? Message,
    DateTime IssuedUtc)
{
    public const int CurrentVersion = 1;

    public bool Has(string feature) => Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The signed form: <paramref name="Payload"/> is the claims as JSON (base64), signed with ECDSA P-256 / SHA-256.</summary>
public sealed record SignedLicence(string Payload, string Signature);

/// <summary>How the register should behave right now.</summary>
public enum LicenceLevel
{
    /// <summary>Nothing to show.</summary>
    Ok,
    /// <summary>A notice: ending soon, or needs to reach Head Office soon.</summary>
    Warning,
    /// <summary>Ended, but everything still works for a few days.</summary>
    Grace,
    /// <summary>New sales stop. Reports, end of day and exports still work.</summary>
    Locked
}

/// <param name="Headline">One line for the banner.</param>
/// <param name="Detail">What happens next and what to do.</param>
/// <param name="LocksAtUtc">When new sales will stop, if nothing changes.</param>
public sealed record LicenceState(LicenceLevel Level, string Headline, string? Detail, DateTime? LocksAtUtc)
{
    public static readonly LicenceState Fine = new(LicenceLevel.Ok, "Licensed", null, null);
    public bool IsLocked => Level == LicenceLevel.Locked;
}

/// <summary>
/// The rules, in one place for Head Office, the Portal and the register:
/// a notice 7 days before the end; 14 days' grace after it; then new sales stop.
/// A register that hasn't reached Head Office for 30 days stops too (notice from day 23),
/// and so does one whose clock has been put back.
/// </summary>
public static class LicencePolicy
{
    public static readonly TimeSpan WarnBefore = TimeSpan.FromDays(7);
    public static readonly TimeSpan Grace = TimeSpan.FromDays(14);
    public static readonly TimeSpan OfflineLimit = TimeSpan.FromDays(30);
    public static readonly TimeSpan OfflineWarnBefore = TimeSpan.FromDays(7);

    /// <summary>A clock this far behind the latest time the register has seen has been put back.</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromDays(1);

    /// <param name="checkOffline">False on Head Office and in the Portal (they are online by definition).</param>
    /// <param name="clockWentBack">The register's clock is behind a time it has already seen.</param>
    public static LicenceState Evaluate(LicenceClaims claims, DateTime nowUtc, bool checkOffline = true, bool clockWentBack = false)
    {
        ArgumentNullException.ThrowIfNull(claims);

        if (claims.Status == LicenceStatus.Suspended)
            return new(LicenceLevel.Locked, $"NepPOS is suspended for {claims.BusinessName}",
                Join(claims.Message, "New sales are paused. Reports, end of day and exports still work. Contact NepPOS to resume."), null);
        if (claims.Status == LicenceStatus.Cancelled)
            return new(LicenceLevel.Locked, "This NepPOS subscription has ended",
                Join(claims.Message, "New sales are stopped. Reports, end of day and exports still work."), null);
        if (clockWentBack)
            return new(LicenceLevel.Locked, "This PC's date and time are wrong",
                "Set the right date and time in Windows, or connect to the internet, and sales start again.", null);

        var what = claims.Status == LicenceStatus.Trial ? "trial" : "subscription";
        var endsAt = claims.ValidUntilUtc;
        var stopsAt = endsAt + Grace;
        var offlineStopsAt = claims.IssuedUtc + OfflineLimit;

        if (nowUtc >= stopsAt)
            return new(LicenceLevel.Locked, $"Your NepPOS {what} has ended",
                "New sales are paused. Reports, end of day and exports still work. Contact NepPOS to renew.", null);
        if (checkOffline && nowUtc >= offlineStopsAt)
            return new(LicenceLevel.Locked, "This register needs to reach Head Office",
                $"It hasn't been online since {Day(claims.IssuedUtc)}. New sales start again as soon as it connects to the internet.", null);

        var locksAt = checkOffline && offlineStopsAt < stopsAt ? offlineStopsAt : stopsAt;

        if (nowUtc >= endsAt)
            return new(LicenceLevel.Grace, $"Your NepPOS {what} ended on {Day(endsAt)}",
                $"Everything still works until {Day(stopsAt)}. After that new sales stop. Contact NepPOS to renew.", locksAt);
        if (endsAt - nowUtc <= WarnBefore)
            return new(LicenceLevel.Warning, $"Your NepPOS {what} ends in {DaysUntil(nowUtc, endsAt)}",
                $"Renew before {Day(endsAt)}.", locksAt);
        if (checkOffline && offlineStopsAt - nowUtc <= OfflineWarnBefore)
            return new(LicenceLevel.Warning, $"Connect this register to the internet within {DaysUntil(nowUtc, offlineStopsAt)}",
                $"It hasn't reached Head Office since {Day(claims.IssuedUtc)}. If it can't by {Day(offlineStopsAt)}, new sales stop until it does.", locksAt);

        return LicenceState.Fine with { LocksAtUtc = locksAt };
    }

    private static string DaysUntil(DateTime now, DateTime then)
    {
        var days = Math.Max(1, (int)Math.Ceiling((then - now).TotalDays));
        return days == 1 ? "1 day" : $"{days} days";
    }

    private static string Day(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string Join(string? first, string second) => string.IsNullOrWhiteSpace(first) ? second : $"{first.Trim()} {second}";
}

/// <summary>Signs and checks licences. Head Office holds the private key; registers only the public one.</summary>
public static class LicenceSignature
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static SignedLicence Sign(LicenceClaims claims, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(privateKey);

        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, Json);
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256);
        return new SignedLicence(Convert.ToBase64String(payload), Convert.ToBase64String(signature));
    }

    /// <summary>The claims, or null when the signature doesn't match the key or the licence is damaged.</summary>
    public static LicenceClaims? Verify(SignedLicence? licence, string? publicKeyPem)
    {
        if (licence is null || string.IsNullOrWhiteSpace(publicKeyPem))
            return null;

        try
        {
            var payload = Convert.FromBase64String(licence.Payload);
            var signature = Convert.FromBase64String(licence.Signature);

            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                return null;

            var claims = JsonSerializer.Deserialize<LicenceClaims>(payload, Json);
            return claims is { Version: >= 1 } ? claims with { Features = claims.Features ?? Array.Empty<string>() } : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string PublicKeyPem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();
}
