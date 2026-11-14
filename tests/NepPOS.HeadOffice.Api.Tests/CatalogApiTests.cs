using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Catalog;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>Catalog link: products a register sends become the store's catalog, which its website reads.</summary>
public sealed class CatalogApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-catalog-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private long _sequence;

    public CatalogApiTests()
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

    private SyncEvent ProductEvent(Guid productId, string name, decimal price, DateTime occurredUtc, bool isActive = true) =>
        new(Guid.NewGuid(), SyncEventTypes.ProductChanged, 1, "STORE-001", "REG-001", ++_sequence, occurredUtc,
            JsonSerializer.SerializeToElement(new ProductChangedV1(productId, name, "Beer", price, "9300000000011", "101", false, isActive,
                new[] { new ProductVariantV1("FullCase", "9300000000099", null, 24, 65m, true) }), SyncJson.Options));

    private static async Task<SyncEventsResponse> SendAsync(HttpClient register, params SyncEvent[] events) =>
        (await (await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events), SyncJson.Options))
            .Content.ReadFromJsonAsync<SyncEventsResponse>(SyncJson.Options))!;

    private static async Task<CatalogChanges> ChangesAsync(HttpClient website, Guid storeId, long after = 0, int? limit = null) =>
        (await website.GetFromJsonAsync<CatalogChanges>(
            $"/api/v1/catalog/changes?storeId={storeId}&after={after}" + (limit is { } l ? $"&limit={l}" : ""), SyncJson.Options))!;

    [Fact]
    public async Task ProductsFromTheRegister_AreTheStoresCatalog_LatestVersionOnly()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var heineken = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var answer = await SendAsync(register,
            ProductEvent(heineken, "Heineken 6-Pack", 22.00m, now.AddMinutes(-2)),
            ProductEvent(heineken, "Heineken 6-Pack", 23.50m, now.AddMinutes(-1)),
            ProductEvent(Guid.NewGuid(), "Corona 6-Pack", 24.00m, now));
        Assert.Equal(3, answer.Accepted.Count);

        var catalog = await ChangesAsync(website, storeId);
        Assert.False(catalog.More);
        Assert.Equal(2, catalog.Products.Count);
        var product = catalog.Products.Single(p => p.ProductId == heineken);
        Assert.Equal((23.50m, "9300000000011", "101"), (product.Price, product.Barcode, product.Plu));
        Assert.Equal((24, 65m), (Assert.Single(product.Variants).UnitQuantity, product.Variants[0].Price));
        Assert.Equal(catalog.Products.Max(p => p.ChangeNumber), catalog.Next);
    }

    [Fact]
    public async Task AskingAfterTheLastChangeNumber_GivesOnlyWhatChangedSince()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var heineken = Guid.NewGuid();
        await SendAsync(register, ProductEvent(heineken, "Heineken 6-Pack", 22.00m, DateTime.UtcNow.AddMinutes(-5)),
                                  ProductEvent(Guid.NewGuid(), "Corona 6-Pack", 24.00m, DateTime.UtcNow.AddMinutes(-5)));
        var first = await ChangesAsync(website, storeId);

        Assert.Empty((await ChangesAsync(website, storeId, first.Next)).Products);

        await SendAsync(register, ProductEvent(heineken, "Heineken 6-Pack", 21.00m, DateTime.UtcNow, isActive: false));
        var next = await ChangesAsync(website, storeId, first.Next);
        var changed = Assert.Single(next.Products);
        Assert.Equal((heineken, 21.00m, false), (changed.ProductId, changed.Price, changed.IsActive));
    }

    [Fact]
    public async Task ALateOlderChange_DoesNotUndoANewerOne()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        var heineken = Guid.NewGuid();
        await SendAsync(register, ProductEvent(heineken, "Heineken 6-Pack", 25.00m, DateTime.UtcNow));
        await SendAsync(register, ProductEvent(heineken, "Heineken 6-Pack", 19.00m, DateTime.UtcNow.AddHours(-1)));

        Assert.Equal(25.00m, Assert.Single((await ChangesAsync(website, storeId)).Products).Price);
    }

    [Fact]
    public async Task LargeCatalog_ComesInPages()
    {
        var (storeId, register, website) = await BusinessAsync("Nep Liquor");
        await SendAsync(register, Enumerable.Range(1, 5).Select(i => ProductEvent(Guid.NewGuid(), $"Product {i}", i, DateTime.UtcNow)).ToArray());

        var page1 = await ChangesAsync(website, storeId, 0, limit: 2);
        var page2 = await ChangesAsync(website, storeId, page1.Next, limit: 2);
        var page3 = await ChangesAsync(website, storeId, page2.Next, limit: 2);

        Assert.Equal((true, true, false), (page1.More, page2.More, page3.More));
        Assert.Equal(5, page1.Products.Concat(page2.Products).Concat(page3.Products).Select(p => p.ProductId).Distinct().Count());
    }

    [Fact]
    public async Task ABrokenProduct_IsRefused_AndAnotherBusinessCantReadTheCatalog()
    {
        var (storeId, register, _) = await BusinessAsync("Nep Liquor");
        var (_, _, otherWebsite) = await BusinessAsync("Kathmandu Mart");

        var answer = await SendAsync(register, ProductEvent(Guid.Empty, "No id", 1m, DateTime.UtcNow));
        Assert.Contains("productId", Assert.Single(answer.Rejected).Reason);

        Assert.Equal(HttpStatusCode.NotFound, (await otherWebsite.GetAsync($"/api/v1/catalog/changes?storeId={storeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await register.GetAsync($"/api/v1/catalog/changes?storeId={storeId}")).StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
