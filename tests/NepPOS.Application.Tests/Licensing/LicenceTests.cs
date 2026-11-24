using System.Security.Cryptography;
using NepPOS.Application.Licensing;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Licensing;
using Xunit;

namespace NepPOS.Application.Tests.Licensing;

public class LicencePolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);

    private static LicenceClaims Licence(LicenceStatus status = LicenceStatus.Active, double daysLeft = 100, double heardDaysAgo = 0, string? message = null) =>
        new(1, Guid.NewGuid(), "Hamro Grocery", "standard", status, Now.AddDays(daysLeft), 1, 3, LicenceFeatures.All, message, Now.AddDays(-heardDaysAgo));

    [Fact]
    public void Paid_and_recently_online_is_fine() =>
        Assert.Equal(LicenceLevel.Ok, LicencePolicy.Evaluate(Licence(), Now).Level);

    [Theory]
    [InlineData(7, LicenceLevel.Warning)]     // a notice in the last week
    [InlineData(0.5, LicenceLevel.Warning)]
    [InlineData(-1, LicenceLevel.Grace)]      // ended: 14 days' grace
    [InlineData(-13.9, LicenceLevel.Grace)]
    [InlineData(-14, LicenceLevel.Locked)]    // then new sales stop
    public void The_end_date_gives_a_notice_then_grace_then_a_stop(double daysLeft, LicenceLevel expected) =>
        Assert.Equal(expected, LicencePolicy.Evaluate(Licence(daysLeft: daysLeft), Now).Level);

    [Fact]
    public void Suspended_stops_at_once_and_shows_the_message()
    {
        var state = LicencePolicy.Evaluate(Licence(LicenceStatus.Suspended, message: "Unpaid since October."), Now);

        Assert.True(state.IsLocked);
        Assert.StartsWith("Unpaid since October.", state.Detail);
    }

    [Theory]
    [InlineData(22, LicenceLevel.Ok)]
    [InlineData(24, LicenceLevel.Warning)]    // connect within a week
    [InlineData(30, LicenceLevel.Locked)]     // 30 days without Head Office
    public void A_register_must_reach_Head_Office_every_30_days(double heardDaysAgo, LicenceLevel expected) =>
        Assert.Equal(expected, LicencePolicy.Evaluate(Licence(heardDaysAgo: heardDaysAgo), Now).Level);

    [Fact]
    public void Head_Office_ignores_the_offline_rule() =>
        Assert.Equal(LicenceLevel.Ok, LicencePolicy.Evaluate(Licence(heardDaysAgo: 60), Now, checkOffline: false).Level);

    [Fact]
    public void Signatures_check_out_only_with_the_right_key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = LicenceSignature.Sign(Licence(), key);

        Assert.Equal("Hamro Grocery", LicenceSignature.Verify(signed, LicenceSignature.PublicKeyPem(key))!.BusinessName);
        Assert.Null(LicenceSignature.Verify(signed, LicenceSignature.PublicKeyPem(other)));
        Assert.Null(LicenceSignature.Verify(signed with { Signature = Convert.ToBase64String(new byte[64]) }, LicenceSignature.PublicKeyPem(key)));
        Assert.Null(LicenceSignature.Verify(signed with { Payload = "not base64!" }, LicenceSignature.PublicKeyPem(key)));
    }
}

public class LicenceGuardTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Clock _clock = new(Start);
    private readonly MemoryLicenceStore _store = new(Start);
    private readonly Connections _connections = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private string PublicKey => LicenceSignature.PublicKeyPem(_key);

    private LicenceGuard Guard() => new(_store, _connections, _clock);

    private SignedLicence Signed(LicenceStatus status = LicenceStatus.Active, double daysLeft = 30, Guid? tenant = null) =>
        LicenceSignature.Sign(new LicenceClaims(1, tenant ?? _tenant, "Hamro Grocery", "standard", status, _clock.Now.AddDays(daysLeft), 1, 3,
            LicenceFeatures.All, null, _clock.Now), _key);

    private void Connect() => _connections.Connection = new HeadOfficeConnection("https://ho.example", _tenant, "Hamro Grocery",
        Guid.NewGuid(), "STORE-001", "Main", "REG-001", "npk_x", _clock.Now);

    [Fact]
    public void An_unactivated_register_has_14_days_then_new_sales_stop()
    {
        var guard = Guard();
        Assert.Equal(LicenceLevel.Warning, guard.Current.Level);
        Assert.True(guard.CanStartSale);

        _clock.Now = Start.AddDays(14);
        Assert.False(guard.CanStartSale);
        Assert.StartsWith("Activate this register", guard.Current.Headline);
    }

    [Fact]
    public void A_licence_from_Head_Office_is_kept_and_obeyed()
    {
        Connect();
        var guard = Guard();
        guard.Accept(Signed(), PublicKey, _clock.Now);
        Assert.Equal(LicenceLevel.Ok, guard.Current.Level);

        // Next heartbeat: suspended.
        guard.Accept(Signed(LicenceStatus.Suspended), null, _clock.Now);
        Assert.False(guard.CanStartSale);

        // A restart reads the same from the file.
        Assert.False(Guard().CanStartSale);
    }

    [Fact]
    public void A_register_activated_before_licences_gets_its_key_with_the_heartbeat()
    {
        Connect();
        var guard = Guard();

        guard.Accept(Signed(), null, _clock.Now);         // no key stored and none sent: can't be checked
        Assert.Null(guard.Claims);

        guard.Accept(Signed(), PublicKey, _clock.Now);    // the heartbeat now sends the key
        Assert.Equal("standard", guard.Claims!.Plan);
    }

    [Fact]
    public void A_licence_that_does_not_verify_is_ignored()
    {
        Connect();
        var guard = Guard();
        guard.Accept(Signed(), PublicKey, _clock.Now);

        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = LicenceSignature.Sign(new LicenceClaims(1, _tenant, "Hamro Grocery", "multistore", LicenceStatus.Active,
            _clock.Now.AddYears(10), 5, 50, LicenceFeatures.All, null, _clock.Now), forger);
        guard.Accept(forged, null, _clock.Now);

        Assert.Equal("standard", guard.Claims!.Plan);
    }

    [Fact]
    public void Putting_the_clock_back_stops_new_sales()
    {
        Connect();
        var guard = Guard();
        guard.Accept(Signed(daysLeft: 2), PublicKey, _clock.Now);

        _clock.Now = Start.AddDays(-20);
        Assert.False(guard.CanStartSale);
        Assert.Contains("date and time are wrong", guard.Current.Headline);
    }

    [Fact]
    public void Thirty_days_without_Head_Office_stops_new_sales_until_it_connects()
    {
        Connect();
        var guard = Guard();
        guard.Accept(Signed(daysLeft: 365), PublicKey, _clock.Now);

        _clock.Now = Start.AddDays(31);
        Assert.False(guard.CanStartSale);

        guard.Accept(Signed(daysLeft: 365), null, _clock.Now);
        Assert.True(guard.CanStartSale);
    }

    [Fact]
    public void Another_business_licence_does_not_count()
    {
        Connect();
        var guard = Guard();
        guard.Accept(Signed(tenant: Guid.NewGuid()), PublicKey, _clock.Now);

        Assert.Null(guard.Claims);
    }

    public void Dispose() => _key.Dispose();

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class MemoryLicenceStore(DateTime firstRun) : ILicenceStore
    {
        private StoredLicence _stored = new(null, null, null, firstRun);
        public StoredLicence Load() => _stored;
        public void Save(StoredLicence licence) => _stored = licence;
    }

    private sealed class Connections : IHeadOfficeConnectionStore
    {
        public HeadOfficeConnection? Connection { get; set; }
        public HeadOfficeConnection? Load() => Connection;
        public void Save(HeadOfficeConnection connection) => Connection = connection;
        public void Clear() => Connection = null;
    }
}
