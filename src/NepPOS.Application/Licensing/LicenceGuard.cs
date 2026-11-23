using NepPOS.Application.Sync;
using NepPOS.Contracts.Licensing;

namespace NepPOS.Application.Licensing;

/// <summary>What the register keeps about its licence, in one small file.</summary>
/// <param name="PublicKeyPem">Head Office's key from activation (ignored when the build pins one).</param>
/// <param name="TrustedTimeUtc">The latest time the register trusts: Head Office's clock at the last contact,
/// or the PC's clock since then. A PC clock well behind it has been put back.</param>
/// <param name="FirstRunUtc">When this register first ran, for the activation deadline.</param>
public sealed record StoredLicence(SignedLicence? Licence, string? PublicKeyPem, DateTime? TrustedTimeUtc, DateTime FirstRunUtc);

public interface ILicenceStore
{
    /// <summary>The stored licence, or a new record (first run = now) when there's none yet.</summary>
    StoredLicence Load();

    void Save(StoredLicence licence);
}

/// <summary>
/// The key the register trusts. Empty = trust the key Head Office gave at activation. Before shipping to
/// customers, paste Head Office's public key here so a register can't be pointed at a fake Head Office.
/// </summary>
public static class LicenceTrust
{
    public const string PinnedPublicKeyPem = "";
}

/// <summary>
/// The register's licence, checked offline from the signed copy Head Office sends with every heartbeat.
/// New sales stop when it's locked (<see cref="CanStartSale"/>); a sale already being paid always finishes,
/// and reports, end of day and exports never stop.
/// </summary>
public sealed class LicenceGuard
{
    /// <summary>How long a register can run before it's activated with Head Office.</summary>
    public static readonly TimeSpan ActivateWithin = TimeSpan.FromDays(14);

    /// <summary>How long a connected register can wait for its first licence (older versions had none).</summary>
    public static readonly TimeSpan FirstLicenceWithin = TimeSpan.FromDays(7);

    // The PC's own clock moves the trusted time forward at most this often (fewer disk writes).
    private static readonly TimeSpan SaveTimeEvery = TimeSpan.FromHours(1);

    private readonly ILicenceStore _store;
    private readonly IHeadOfficeConnectionStore _connections;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private StoredLicence? _stored;

    public LicenceGuard(ILicenceStore store, IHeadOfficeConnectionStore connections, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised when a new licence arrives, so banners can refresh.</summary>
    public event Action? Changed;

    public LicenceState Current => Evaluate().State;

    /// <summary>The verified licence, or null when there's none (or it doesn't check out).</summary>
    public LicenceClaims? Claims => Evaluate().Claims;

    public bool CanStartSale => !Current.IsLocked;

    /// <summary>
    /// Keeps a licence from Head Office (activation or heartbeat) and takes its clock as the trusted time.
    /// A licence that doesn't verify is ignored; the one already held stays.
    /// </summary>
    /// <param name="publicKeyPem">Given at activation; null on heartbeats (the stored key is used).</param>
    public void Accept(SignedLicence? licence, string? publicKeyPem, DateTime serverTimeUtc)
    {
        lock (_lock)
        {
            var stored = Stored();
            var key = TrustedKey(publicKeyPem ?? stored.PublicKeyPem);
            var verified = LicenceSignature.Verify(licence, key) is not null;

            _stored = stored with
            {
                Licence = verified ? licence : stored.Licence,
                PublicKeyPem = publicKeyPem ?? stored.PublicKeyPem,
                TrustedTimeUtc = DateTime.SpecifyKind(serverTimeUtc, DateTimeKind.Utc)
            };
            _store.Save(_stored);
        }
        Changed?.Invoke();
    }

    private (LicenceState State, LicenceClaims? Claims) Evaluate()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var stored = Stored();

            // A PC clock behind a time already trusted has been put back: don't let it extend anything.
            var clockWentBack = stored.TrustedTimeUtc is { } trusted && now < trusted - LicencePolicy.ClockTolerance;
            if (!clockWentBack && (stored.TrustedTimeUtc is null || now - stored.TrustedTimeUtc > SaveTimeEvery))
            {
                _stored = stored = stored with { TrustedTimeUtc = now };
                _store.Save(stored);
            }

            HeadOfficeConnection? connection;
            try
            {
                connection = _connections.Load();
            }
            catch (Exception)
            {
                connection = null;
            }

            var claims = LicenceSignature.Verify(stored.Licence, TrustedKey(stored.PublicKeyPem));
            // A licence copied from another business's register doesn't count here.
            if (claims is not null && connection is not null && claims.TenantId != connection.TenantId)
                claims = null;

            if (claims is not null)
                return (LicencePolicy.Evaluate(claims, now, checkOffline: true, clockWentBack), claims);

            if (clockWentBack)
                return (LicencePolicy.Evaluate(Placeholder(now), now, clockWentBack: true), null);

            return (WithoutLicence(stored, connection, now), null);
        }
    }

    /// <summary>Not activated yet, or activated before licences existed: a deadline, then new sales stop.</summary>
    private static LicenceState WithoutLicence(StoredLicence stored, HeadOfficeConnection? connection, DateTime now)
    {
        if (stored.Licence is not null && connection is not null)
            return new(LicenceLevel.Locked, "This register's licence isn't valid",
                "Connect to the internet so Head Office can send a new one. Reports, end of day and exports still work.", null);

        if (connection is null)
        {
            var deadline = stored.FirstRunUtc + ActivateWithin;
            return now >= deadline
                ? new(LicenceLevel.Locked, "Activate this register to keep selling",
                    "Connect it in Manage → Head Office with an activation code from NepPOS. Reports, end of day and exports still work.", null)
                : new(LicenceLevel.Warning, $"Activate this register within {Days(now, deadline)}",
                    "Connect it in Manage → Head Office with an activation code from NepPOS.", deadline);
        }

        var waitUntil = connection.ConnectedUtc + FirstLicenceWithin;
        return now >= waitUntil
            ? new(LicenceLevel.Locked, "This register hasn't received its licence",
                "Connect to the internet so Head Office can send it. Reports, end of day and exports still work.", null)
            : new(LicenceLevel.Ok, "Waiting for the licence from Head Office", null, waitUntil);
    }

    private static LicenceClaims Placeholder(DateTime now) =>
        new(LicenceClaims.CurrentVersion, Guid.Empty, "", "", LicenceStatus.Active, now, 0, 0, Array.Empty<string>(), null, now);

    private static string? TrustedKey(string? fromHeadOffice) =>
        string.IsNullOrWhiteSpace(LicenceTrust.PinnedPublicKeyPem) ? fromHeadOffice : LicenceTrust.PinnedPublicKeyPem;

    private StoredLicence Stored() => _stored ??= _store.Load();

    private static string Days(DateTime now, DateTime then)
    {
        var days = Math.Max(1, (int)Math.Ceiling((then - now).TotalDays));
        return days == 1 ? "1 day" : $"{days} days";
    }
}
