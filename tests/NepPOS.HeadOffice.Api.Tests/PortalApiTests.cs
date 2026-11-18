using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Admin;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>NepPOS Portal: what it reads, the audit log, and how its staff sign in.</summary>
public sealed class PortalApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";
    private const string OwnerEmail = "owner@neppos.example";
    private const string OwnerPassword = "correct horse battery staple";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-portal-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _portal;

    public PortalApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:HeadOffice", $"Data Source={_dbPath}");
            host.UseSetting("HeadOffice:DatabaseProvider", "Sqlite");
            host.UseSetting("HeadOffice:AdminKey", AdminKey);
            host.UseSetting("HeadOffice:Owner:Email", OwnerEmail);
            host.UseSetting("HeadOffice:Owner:Name", "Subash");
            host.UseSetting("HeadOffice:Owner:Password", OwnerPassword);
        });
        _portal = _factory.CreateClient();
        _portal.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        _portal.DefaultRequestHeaders.Add(PortalHeaders.User, OwnerEmail);
    }

    private async Task<(Guid TenantId, Guid StoreId)> BusinessWithOneRegisterAsync(string name)
    {
        var tenant = await (await _portal.PostAsJsonAsync("/api/v1/admin/tenants", new { name }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _portal.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode = "STORE-001", name = "Main" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();
        var code = await (await _portal.PostAsJsonAsync($"/api/v1/admin/stores/{store!.Id}/activation-codes", new { }))
            .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>();
        var activation = await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code!.Code, "REG-001", "Front counter"));
        activation.EnsureSuccessStatusCode();
        return (tenant.Id, store.Id);
    }

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    [Fact]
    public async Task Portal_SeesTheBusiness_ItsRegister_AndWhoSetItUp()
    {
        var (tenantId, storeId) = await BusinessWithOneRegisterAsync("Kathmandu Cafe");

        var overview = await _portal.GetFromJsonAsync<AdminOverview>("/api/v1/admin/overview", SyncJson.Options);
        Assert.Equal((1, 1, 1, 0, 1), (overview!.Businesses, overview.Stores, overview.Registers, overview.OnlineRegisters, overview.OfflineRegisters));

        var list = await _portal.GetFromJsonAsync<List<BusinessRow>>("/api/v1/admin/tenants?search=kathmandu", SyncJson.Options);
        Assert.Equal(tenantId, Assert.Single(list!).Id);

        var detail = await _portal.GetFromJsonAsync<BusinessDetail>($"/api/v1/admin/tenants/{tenantId}", SyncJson.Options);
        var register = Assert.Single(Assert.Single(detail!.Stores).Registers);
        Assert.Equal(("REG-001", "Front counter", storeId), (register.RegisterCode, register.DeviceName, register.StoreId));

        var audit = await _portal.GetFromJsonAsync<List<AuditEntryRow>>($"/api/v1/admin/audit?tenantId={tenantId}", SyncJson.Options);
        Assert.Equal(new[] { "activation-code.issued", "store.created", "business.created" }, audit!.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal(OwnerEmail, a.Actor));
    }

    [Fact]
    public async Task RenameAndRevoke_AreLogged_AndRevokingTwiceLogsOnce()
    {
        var (tenantId, _) = await BusinessWithOneRegisterAsync("Old Name");
        var registerId = (await _portal.GetFromJsonAsync<List<RegisterRow>>("/api/v1/admin/registers", SyncJson.Options))!.Single().Id;

        Assert.Equal(HttpStatusCode.NoContent, (await _portal.PutAsJsonAsync($"/api/v1/admin/tenants/{tenantId}", new RenameBusinessRequest("New Name"))).StatusCode);
        await _portal.PostAsync($"/api/v1/admin/registers/{registerId}/revoke", null);
        await _portal.PostAsync($"/api/v1/admin/registers/{registerId}/revoke", null);

        var audit = await _portal.GetFromJsonAsync<List<AuditEntryRow>>($"/api/v1/admin/audit?tenantId={tenantId}", SyncJson.Options);
        Assert.Equal(1, audit!.Count(a => a.Action == "register.revoked"));
        Assert.Contains(audit, a => a.Action == "business.renamed" && a.BusinessName == "New Name");
        Assert.Empty(await _portal.GetFromJsonAsync<List<RegisterRow>>("/api/v1/admin/registers?status=offline", SyncJson.Options) ?? new());
    }

    [Fact]
    public async Task FirstOwner_SetsUpTwoStepSignIn_ThenNeedsTheCode_AndACodeWorksOnce()
    {
        var first = await (await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, null)))
            .Content.ReadFromJsonAsync<StaffSignInResponse>();
        Assert.True(first!.NeedsTwoFactorSetup);
        Assert.Equal(StaffRoles.Owner, first.Role);

        var setup = await (await _portal.PostAsync($"/api/v1/admin/staff/{first.Id}/two-factor/start", null)).Content.ReadFromJsonAsync<TwoFactorSetup>();
        var key = Totp.FromBase32(setup!.Secret);
        var now = Totp.StepAt(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _portal.PostAsJsonAsync($"/api/v1/admin/staff/{first.Id}/two-factor/confirm", new TwoFactorConfirmRequest(Totp.CodeAt(key, now)))).StatusCode);

        var noCode = await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, null));
        Assert.Equal((HttpStatusCode.Unauthorized, SignInProblems.CodeRequired), (noCode.StatusCode, await ProblemTypeAsync(noCode)));

        // The code used to confirm can't be used again; the next one works, once.
        var nextCode = Totp.CodeAt(key, now + 1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, Totp.CodeAt(key, now)))).StatusCode);
        var signedIn = await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, nextCode));
        Assert.False((await signedIn.Content.ReadFromJsonAsync<StaffSignInResponse>())!.NeedsTwoFactorSetup);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, nextCode))).StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswords_LockTheAccount_EvenForTheRightPassword()
    {
        for (var i = 0; i < 5; i++)
            await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, "wrong password " + i, null));

        var right = await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, null));
        Assert.Equal(SignInProblems.Locked, await ProblemTypeAsync(right));

        var unknown = await _portal.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest("nobody@example.com", OwnerPassword, null));
        Assert.Equal(SignInProblems.Invalid, await ProblemTypeAsync(unknown));
    }

    [Fact]
    public async Task Staff_NeedLongPasswords_AndTheLastOwnerCantBeDisabled()
    {
        var weak = await _portal.PostAsJsonAsync("/api/v1/admin/staff", new CreateStaffRequest("help@neppos.example", "Help Desk", StaffRoles.Support, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var created = await _portal.PostAsJsonAsync("/api/v1/admin/staff", new CreateStaffRequest("help@neppos.example", "Help Desk", StaffRoles.Support, "a long support password"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var staff = await _portal.GetFromJsonAsync<List<StaffRow>>("/api/v1/admin/staff", SyncJson.Options);
        var owner = staff!.Single(s => s.Role == StaffRoles.Owner);
        Assert.Equal(HttpStatusCode.Conflict, (await _portal.PostAsync($"/api/v1/admin/staff/{owner.Id}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task WithoutTheAdminKey_NothingIsReadable()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/admin/staff/sign-in", new StaffSignInRequest(OwnerEmail, OwnerPassword, null))).StatusCode);
    }

    public void Dispose()
    {
        _portal.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
