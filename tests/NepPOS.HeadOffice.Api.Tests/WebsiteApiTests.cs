using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Reports;
using NepPOS.Contracts.Sync;
using Microsoft.Extensions.DependencyInjection;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>A business's website reads its stores and sales with a website key, and nothing else.</summary>
public sealed class WebsiteApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-web-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public WebsiteApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:HeadOffice", $"Data Source={_dbPath}");
            host.UseSetting("HeadOffice:DatabaseProvider", "Sqlite");
            host.UseSetting("HeadOffice:AdminKey", AdminKey);
        });
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
    }

    /// <summary>A business with one store, one activated register and a website key.</summary>
    private async Task<(Guid StoreId, HttpClient Register, HttpClient Website)> BusinessAsync(string name)
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores",
                new { storeCode = "STORE-001", name = "Main", timeZoneId = "Australia/Sydney" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();
        var code = await (await _admin.PostAsJsonAsync($"/api/v1/admin/stores/{store!.Id}/activation-codes", new { }))
            .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>();
        var activation = await (await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code!.Code, "REG-001", null)))
            .Content.ReadFromJsonAsync<ActivateResponse>();
        var websiteKey = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant.Id}/website-keys", new { name = "Online shop" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.WebsiteKeyResponse>();

        var register = _factory.CreateClient();
        register.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activation!.ApiKey);
        var website = _factory.CreateClient();
        website.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", websiteKey!.ApiKey);
        return (store.Id, register, website);
    }

    private static SyncEvent SaleEvent(long sequence, DateTime occurredUtc, decimal total, string category) =>
        new(Guid.NewGuid(), SyncEventTypes.SaleCompleted, 1, "STORE-001", "REG-001", sequence, occurredUtc,
            JsonSerializer.SerializeToElement(new SaleCompletedV1(Guid.NewGuid(), 1000 + sequence, occurredUtc, 1, "Owner",
                total, decimal.Round(total / 11, 2), 0,
                new[] { new SaleLineV1("101", "Heineken 6-Pack", category, 1, total, 0.10m, total) },
                new[] { new SalePaymentV1("Eftpos", total, total, 0, "123456", "Visa", "....1234") }), SyncJson.Options));

    [Fact]
    public async Task Website_SeesItsStoreOnline_AndTheSalesTheRegisterSent()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var noon = new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc);     // 12:00 in Sydney
        await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(new[]
        {
            SaleEvent(1, noon, 44.00m, "Beer"),
            SaleEvent(2, noon.AddHours(2), 12.00m, "Wine")
        }));
        await register.PostAsync("/api/v1/heartbeat", null);

        var stores = await website.GetFromJsonAsync<List<StoreSummary>>("/api/v1/stores", SyncJson.Options);
        var store = Assert.Single(stores!);
        Assert.True(store.Online);
        Assert.Equal(noon.AddHours(2), store.LastSaleUtc);

        var report = await website.GetFromJsonAsync<SalesReport>($"/api/v1/reports/sales?storeId={storeId}&from=2026-11-14&to=2026-11-14", SyncJson.Options);
        Assert.Equal((2, 56.00m), (report!.SaleCount, report.GrossSales));
        Assert.Equal(new[] { "Beer", "Wine" }, report.Departments.Select(d => d.Name));
        Assert.Equal(new[] { 12, 14 }, report.Hours.Select(h => h.Hour));      // Sydney time
    }

    [Fact]
    public async Task Website_CannotSeeAnotherBusiness()
    {
        var (_, _, websiteA) = await BusinessAsync("Nep Liquor");
        var (storeB, _, _) = await BusinessAsync("Kathmandu Mart");

        var stores = await websiteA.GetFromJsonAsync<List<StoreSummary>>("/api/v1/stores", SyncJson.Options);
        var otherStore = await websiteA.GetAsync($"/api/v1/reports/sales?storeId={storeB}&from=2026-11-14&to=2026-11-14");

        Assert.DoesNotContain(stores!, s => s.StoreId == storeB);
        Assert.Equal(HttpStatusCode.NotFound, otherStore.StatusCode);
    }

    [Fact]
    public async Task Keys_OnlyOpenTheirOwnDoors()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");

        // A register key can't read reports; a website key can't send events or heartbeats.
        Assert.Equal(HttpStatusCode.Unauthorized, (await register.GetAsync("/api/v1/stores")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await website.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(Array.Empty<SyncEvent>()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await website.PostAsync("/api/v1/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/stores")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await website.GetAsync($"/api/v1/reports/sales?storeId={storeId}&from=2026-11-01&to=2026-11-30")).StatusCode);
    }

    [Fact]
    public async Task RevokedWebsiteKey_StopsWorking()
    {
        var (_, _, website) = await BusinessAsync("Nep Liquor");
        Assert.Equal(HttpStatusCode.OK, (await website.GetAsync("/api/v1/stores")).StatusCode);

        // The key's id isn't returned to the website, so find it the way an admin would: it's the only one.
        Guid keyId;
        using (var scope = _factory.Services.CreateScope())
            keyId = scope.ServiceProvider.GetRequiredService<NepPOS.HeadOffice.Api.Data.HeadOfficeDbContext>().WebsiteKeys.Single().Id;
        await _admin.PostAsync($"/api/v1/admin/website-keys/{keyId}/revoke", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await website.GetAsync("/api/v1/stores")).StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
