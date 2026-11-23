using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Licensing;
using NepPOS.HeadOffice.Api.Data;

namespace NepPOS.HeadOffice.Api.Licensing;

/// <summary>
/// The licence signing key, loaded once. From HeadOffice:LicenceSigningKey (a PEM private key) when set;
/// otherwise, except in Production, one is made and kept in the database so a developer PC just works.
/// </summary>
public sealed class LicenceKeyRing : IDisposable
{
    public const string SettingName = "HeadOffice:LicenceSigningKey";

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ECDsa? _key;
    private string? _publicKeyPem;

    public LicenceKeyRing(IConfiguration configuration, IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<(ECDsa Key, string PublicKeyPem)> GetAsync(HeadOfficeDbContext db, CancellationToken ct)
    {
        if (_key is not null)
            return (_key, _publicKeyPem!);

        await _gate.WaitAsync(ct);
        try
        {
            if (_key is null)
            {
                var pem = _configuration[SettingName];
                if (string.IsNullOrWhiteSpace(pem))
                {
                    if (_environment.IsProduction())
                        throw new InvalidOperationException($"Set {SettingName} (a PEM EC private key) before running Head Office in production.");
                    pem = await DevelopmentKeyAsync(db, ct);
                }

                var key = ECDsa.Create();
                key.ImportFromPem(pem);
                _publicKeyPem = LicenceSignature.PublicKeyPem(key);
                _key = key;
            }
            return (_key, _publicKeyPem!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> DevelopmentKeyAsync(HeadOfficeDbContext db, CancellationToken ct)
    {
        var stored = await db.LicenceSigningKeys.AsNoTracking().OrderBy(k => k.Id).FirstOrDefaultAsync(ct);
        if (stored is not null)
            return stored.PrivateKeyPem;

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();
        db.LicenceSigningKeys.Add(new LicenceSigningKey { PrivateKeyPem = pem });
        await db.SaveChangesAsync(ct);
        return pem;
    }

    public void Dispose()
    {
        _key?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Reads, creates and signs business licences, and answers "is this allowed?".</summary>
public sealed class LicenceService
{
    private readonly HeadOfficeDbContext _db;
    private readonly LicenceKeyRing _keys;

    public LicenceService(HeadOfficeDbContext db, LicenceKeyRing keys)
    {
        _db = db;
        _keys = keys;
    }

    /// <summary>The business's licence; a 30-day trial is started for a business that has none yet.</summary>
    public async Task<TenantLicence> GetOrStartTrialAsync(Guid tenantId, CancellationToken ct)
    {
        var licence = await _db.TenantLicences.FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);
        if (licence is not null)
            return licence;

        licence = TenantLicence.NewTrial(tenantId, DateTime.UtcNow);
        _db.TenantLicences.Add(licence);
        await _db.SaveChangesAsync(ct);
        return licence;
    }

    public static LicenceClaims ClaimsOf(TenantLicence licence, string businessName, DateTime nowUtc) =>
        new(LicenceClaims.CurrentVersion, licence.TenantId, businessName, licence.Plan, licence.Status,
            licence.ValidUntilUtc, licence.MaxStores, licence.MaxRegisters, licence.FeatureList, licence.Message, nowUtc);

    /// <summary>Where the licence stands today (Head Office is online, so only dates and status count).</summary>
    public static LicenceState StateOf(TenantLicence licence, string businessName, DateTime nowUtc) =>
        LicencePolicy.Evaluate(ClaimsOf(licence, businessName, nowUtc), nowUtc, checkOffline: false);

    public async Task<(SignedLicence Licence, string PublicKeyPem)> SignAsync(TenantLicence licence, string businessName, CancellationToken ct)
    {
        var (key, publicKey) = await _keys.GetAsync(_db, ct);
        return (LicenceSignature.Sign(ClaimsOf(licence, businessName, DateTime.UtcNow), key), publicKey);
    }

    public async Task<string> PublicKeyPemAsync(CancellationToken ct) => (await _keys.GetAsync(_db, ct)).PublicKeyPem;
}
