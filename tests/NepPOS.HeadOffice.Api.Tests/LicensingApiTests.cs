using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Admin;
using NepPOS.Contracts.Licensing;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>Licences: trials, signed licences for registers, plan limits, suspending and the website.</summary>
public sealed class LicensingApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-licence-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public LicensingApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:HeadOffice", $"Data Source={_dbPath}");
            host.UseSetting("HeadOffice:DatabaseProvider", "Sqlite");
            host.UseSetting("HeadOffice:AdminKey", AdminKey);
        });
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        _admin.DefaultRequestHeaders.Add(PortalHeaders.User, "owner@neppos.example");
    }

    private async Task<(Guid TenantId, Guid StoreId)> NewBusinessAsync(string name = "Hamro Grocery")
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode = "STORE-001", name = "Main" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();
        return (tenant.Id, store!.Id);
    }

    private async Task<HttpResponseMessage> ActivateAsync(Guid storeId, string registerCode)
    {
        var code = (await (await _admin.PostAsJsonAsync($"/api/v1/admin/stores/{storeId}/activation-codes", new { validDays = 7 }))
            .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>())!.Code;
        return await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code, registerCode, null));
    }

    private Task<LicenceDetail?> LicenceAsync(Guid tenantId) =>
        _admin.GetFromJsonAsync<LicenceDetail>($"/api/v1/admin/tenants/{tenantId}/licence", SyncJson.Options);

    private static UpdateLicenceRequest Change(LicenceDetail l, LicenceStatus? status = null, DateTime? until = null, int? registers = null,
        IReadOnlyList<string>? features = null, string? message = null) =>
        new(l.Plan, status ?? l.Status, until ?? l.ValidUntilUtc, l.MaxStores, registers ?? l.MaxRegisters, features ?? l.Features, message ?? l.Message, "test");

    private Task<HttpResponseMessage> UpdateAsync(Guid tenantId, UpdateLicenceRequest request) =>
        _admin.PutAsJsonAsync($"/api/v1/admin/tenants/{tenantId}/licence", request, SyncJson.Options);

    [Fact]
    public async Task A_new_business_starts_a_30_day_trial()
    {
        var (tenantId, _) = await NewBusinessAsync();

        var licence = (await LicenceAsync(tenantId))!;
        Assert.Equal(LicenceStatus.Trial, licence.Status);
        Assert.Equal(LicencePlans.TrialPlan.Code, licence.Plan);
        Assert.InRange(licence.ValidUntilUtc, DateTime.UtcNow.AddDays(29.9), DateTime.UtcNow.AddDays(30.1));
        Assert.Equal((1, 0), (licence.StoresUsed, licence.RegistersUsed));
        Assert.Equal(LicenceLevel.Ok, licence.State.Level);
    }

    [Fact]
    public async Task Registers_get_a_licence_signed_with_the_key_they_were_given()
    {
        var (_, storeId) = await NewBusinessAsync();

        var activation = (await (await ActivateAsync(storeId, "REG-001")).Content.ReadFromJsonAsync<ActivateResponse>(SyncJson.Options))!;
        var claims = LicenceSignature.Verify(activation.Licence, activation.LicencePublicKey);
        Assert.NotNull(claims);
        Assert.Equal(("Hamro Grocery", LicenceStatus.Trial), (claims.BusinessName, claims.Status));

        // The heartbeat brings a fresh one, signed with the same key.
        var register = _factory.CreateClient();
        register.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activation.ApiKey);
        var beat = await (await register.PostAsync("/api/v1/heartbeat", null)).Content.ReadFromJsonAsync<HeartbeatResponse>(SyncJson.Options);
        Assert.NotNull(LicenceSignature.Verify(beat!.Licence, activation.LicencePublicKey));
        Assert.Equal(activation.LicencePublicKey, beat.LicencePublicKey);

        // An edited licence doesn't pass.
        var edited = beat.Licence! with { Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"status\":\"Active\"}")) };
        Assert.Null(LicenceSignature.Verify(edited, activation.LicencePublicKey));
    }

    [Fact]
    public async Task Activation_stops_at_the_plans_register_limit_without_using_the_code()
    {
        var (tenantId, storeId) = await NewBusinessAsync();
        await UpdateAsync(tenantId, Change((await LicenceAsync(tenantId))!, registers: 1));

        Assert.True((await ActivateAsync(storeId, "REG-001")).IsSuccessStatusCode);
        var second = await ActivateAsync(storeId, "REG-002");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("allows 1 register", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_second_store_needs_a_plan_that_allows_it()
    {
        var (tenantId, _) = await NewBusinessAsync();

        var refused = await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenantId}/stores", new { storeCode = "STORE-002", name = "Second" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var licence = (await LicenceAsync(tenantId))!;
        var multiStore = LicencePlans.MultiStore;
        await UpdateAsync(tenantId, new UpdateLicenceRequest(multiStore.Code, licence.Status, licence.ValidUntilUtc,
            multiStore.MaxStores, multiStore.MaxRegisters, multiStore.Features, null, "Upgraded"));
        Assert.True((await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenantId}/stores", new { storeCode = "STORE-002", name = "Second" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Suspending_needs_a_message_locks_registers_and_the_website_and_is_audited()
    {
        var (tenantId, storeId) = await NewBusinessAsync();
        var activation = (await (await ActivateAsync(storeId, "REG-001")).Content.ReadFromJsonAsync<ActivateResponse>(SyncJson.Options))!;
        var websiteKey = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenantId}/website-keys", new { name = "Shop" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.WebsiteKeyResponse>();
        var website = _factory.CreateClient();
        website.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", websiteKey!.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await website.GetAsync("/api/v1/stores")).StatusCode);

        var licence = (await LicenceAsync(tenantId))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await UpdateAsync(tenantId, Change(licence, LicenceStatus.Suspended))).StatusCode);

        var suspended = await UpdateAsync(tenantId, Change(licence, LicenceStatus.Suspended, message: "Unpaid since October. Call 0400 000 000."));
        Assert.True(suspended.IsSuccessStatusCode);

        // The register's next heartbeat carries the suspension.
        var register = _factory.CreateClient();
        register.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activation.ApiKey);
        var beat = await (await register.PostAsync("/api/v1/heartbeat", null)).Content.ReadFromJsonAsync<HeartbeatResponse>(SyncJson.Options);
        var claims = LicenceSignature.Verify(beat!.Licence, activation.LicencePublicKey)!;
        Assert.True(LicencePolicy.Evaluate(claims, DateTime.UtcNow).IsLocked);

        // Sales still reach Head Office (the owner's data), but the website stops and new registers can't start.
        Assert.Equal(HttpStatusCode.Forbidden, (await website.GetAsync("/api/v1/stores")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActivateAsync(storeId, "REG-002")).StatusCode);

        var audit = await _admin.GetFromJsonAsync<List<AuditEntryRow>>($"/api/v1/admin/audit?tenantId={tenantId}", SyncJson.Options);
        Assert.Contains(audit!, a => a.Action == "licence.changed" && a.Summary.Contains("status Trial → Suspended"));
    }

    [Fact]
    public async Task A_plan_without_the_website_refuses_website_keys()
    {
        var (tenantId, _) = await NewBusinessAsync();
        var websiteKey = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenantId}/website-keys", new { name = "Shop" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.WebsiteKeyResponse>();
        await UpdateAsync(tenantId, Change((await LicenceAsync(tenantId))!, features: Array.Empty<string>()));

        var website = _factory.CreateClient();
        website.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", websiteKey!.ApiKey);
        var response = await website.GetAsync("/api/v1/stores");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("doesn't include the website", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Businesses_list_and_overview_show_licences_to_chase()
    {
        var (tenantId, _) = await NewBusinessAsync("Ending Soon Liquor");
        await UpdateAsync(tenantId, Change((await LicenceAsync(tenantId))!, LicenceStatus.Active, until: DateTime.UtcNow.AddDays(3)));

        var rows = await _admin.GetFromJsonAsync<List<BusinessRow>>("/api/v1/admin/tenants", SyncJson.Options);
        Assert.Equal(LicenceLevel.Warning, Assert.Single(rows!).Licence!.Level);

        var overview = await _admin.GetFromJsonAsync<AdminOverview>("/api/v1/admin/overview", SyncJson.Options);
        Assert.Equal("Ending Soon Liquor", Assert.Single(overview!.LicencesNeedingAttention!).Name);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
