using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Catalog;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>Stock link: stock levels a register sends become the store's stock, which its website reads.</summary>
public sealed class StockApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-stock-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private long _sequence;

    public StockApiTests()
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

    private async Task<(Guid StoreId, HttpClient Register, HttpClient Website)> BusinessAsync(string name)
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode = "STORE-001", name = "Main" }))
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

    private SyncEvent StockEvent(Guid productId, decimal stockOnHand, DateTime occurredUtc) =>
        new(Guid.NewGuid(), SyncEventTypes.StockChanged, StockChangedV1.Version, "STORE-001", "REG-001", ++_sequence, occurredUtc,
            JsonSerializer.SerializeToElement(new StockChangedV1(productId, stockOnHand), SyncJson.Options));

    private static async Task<SyncEventsResponse> SendAsync(HttpClient register, params SyncEvent[] events) =>
        (await (await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events), SyncJson.Options))
            .Content.ReadFromJsonAsync<SyncEventsResponse>(SyncJson.Options))!;

    private static async Task<StockChanges> ChangesAsync(HttpClient website, Guid storeId, long after = 0, int? limit = null) =>
        (await website.GetFromJsonAsync<StockChanges>(
            $"/api/v1/stock/changes?storeId={storeId}&after={after}" + (limit is { } l ? $"&limit={l}" : ""), SyncJson.Options))!;

    [Fact]
    public async Task LatestStockPerProduct_AndOnlyWhatChangedSince()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var (asahi, chips) = (Guid.NewGuid(), Guid.NewGuid());
        await SendAsync(register, StockEvent(asahi, 48, DateTime.UtcNow.AddMinutes(-2)), StockEvent(chips, 10, DateTime.UtcNow.AddMinutes(-2)));
        await SendAsync(register, StockEvent(asahi, 47, DateTime.UtcNow));

        var all = await ChangesAsync(website, storeId);
        Assert.Equal(2, all.Levels.Count);
        Assert.Equal(47m, all.Levels.Single(l => l.ProductId == asahi).StockOnHand);
        Assert.False(all.More);

        await SendAsync(register, StockEvent(chips, 9, DateTime.UtcNow));
        var since = await ChangesAsync(website, storeId, all.Next);
        Assert.Equal((chips, 9m), (Assert.Single(since.Levels).ProductId, since.Levels[0].StockOnHand));
    }

    [Fact]
    public async Task ALateOlderLevel_DoesNotUndoANewerOne()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var asahi = Guid.NewGuid();
        await SendAsync(register, StockEvent(asahi, 20, DateTime.UtcNow));
        await SendAsync(register, StockEvent(asahi, 30, DateTime.UtcNow.AddHours(-1)));

        Assert.Equal(20m, Assert.Single((await ChangesAsync(website, storeId)).Levels).StockOnHand);
    }

    [Fact]
    public async Task ManyProducts_ComeInPages_AndAnotherBusinessCantRead()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        await SendAsync(register, Enumerable.Range(1, 5).Select(i => StockEvent(Guid.NewGuid(), i, DateTime.UtcNow)).ToArray());

        var first = await ChangesAsync(website, storeId, limit: 3);
        var second = await ChangesAsync(website, storeId, first.Next, limit: 3);
        Assert.Equal((3, true, 2, false), (first.Levels.Count, first.More, second.Levels.Count, second.More));

        var (_, _, otherWebsite) = await BusinessAsync("Kathmandu Mart");
        Assert.Equal(HttpStatusCode.NotFound, (await otherWebsite.GetAsync($"/api/v1/stock/changes?storeId={storeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await register.GetAsync($"/api/v1/stock/changes?storeId={storeId}")).StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
