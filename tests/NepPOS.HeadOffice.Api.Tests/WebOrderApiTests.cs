using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Orders;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>Website orders: the website sends them, the store's registers get them and report back, the website reads where they're up to.</summary>
public sealed class WebOrderApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-orders-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private long _sequence;

    public WebOrderApiTests()
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

    private SyncEvent StatusEvent(WebOrderStatusV1 status) =>
        new(Guid.NewGuid(), SyncEventTypes.OrderStatus, WebOrderStatusV1.Version, "STORE-001", "REG-001", ++_sequence, status.AtUtc,
            JsonSerializer.SerializeToElement(status, SyncJson.Options));

    private static async Task<SyncEventsResponse> SendAsync(HttpClient register, params SyncEvent[] events) =>
        (await (await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events), SyncJson.Options))
            .Content.ReadFromJsonAsync<SyncEventsResponse>(SyncJson.Options))!;

    private static PlaceWebOrderRequest Order(Guid storeId, Guid? orderId = null, decimal total = 37.50m, decimal onAccount = 0m, Guid? customerId = null) =>
        new(orderId ?? Guid.NewGuid(), storeId, "ORD-1001", "Sita Sharma", "0400 000 111", "sita@example.com", customerId, Fulfilment.Pickup, null, null, null,
            new List<WebOrderLineV1> { new(Guid.NewGuid(), "Basmati Rice 5kg", 2, 15m), new(null, "Delivery fee", 1, 7.50m) },
            total, total - onAccount, onAccount);

    private static Task<HttpResponseMessage> PlaceAsync(HttpClient website, PlaceWebOrderRequest order) =>
        website.PostAsJsonAsync("/api/v1/orders", order, SyncJson.Options);

    private static async Task<WebOrderChanges> ChangesAsync(HttpClient website, long after = 0) =>
        (await website.GetFromJsonAsync<WebOrderChanges>($"/api/v1/orders/changes?after={after}", SyncJson.Options))!;

    [Fact]
    public async Task PlacedOrder_ReachesTheRegisters_AndAResendIsHarmless()
    {
        var (storeId, register, website) = await BusinessAsync("Hamro Grocery");
        var order = Order(storeId);

        var first = await PlaceAsync(website, order);
        var again = await PlaceAsync(website, order);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, again.StatusCode));

        var shared = await register.GetFromJsonAsync<SharedEventsResponse>("/api/v1/sync/shared?after=0&afterWeb=0", SyncJson.Options);
        var placed = Assert.Single(shared!.Events, e => e.Event.Type == SyncEventTypes.OrderPlaced);
        Assert.True(placed.FromWebsite);
        var payload = placed.Event.Payload.Deserialize<WebOrderPlacedV1>(SyncJson.Options)!;
        Assert.Equal((order.OrderId, "STORE-001", 37.50m), (payload.OrderId, payload.StoreCode, payload.Prepaid));

        var view = await website.GetFromJsonAsync<WebOrderView>($"/api/v1/orders/{order.OrderId}", SyncJson.Options);
        Assert.Equal(WebOrderStatuses.Placed, view!.Status);
    }

    [Fact]
    public async Task StoreProgress_ShowsOnTheWebsite_AndAFinishedOrderStaysFinished()
    {
        var (storeId, register, website) = await BusinessAsync("Hamro Grocery");
        var order = Order(storeId);
        await PlaceAsync(website, order);
        var start = (await ChangesAsync(website)).Next;

        await SendAsync(register, StatusEvent(new WebOrderStatusV1(order.OrderId, WebOrderStatuses.Picking, DateTime.UtcNow, By: "Sam")));
        var picking = Assert.Single((await ChangesAsync(website, start)).Orders);
        Assert.Equal(WebOrderStatuses.Picking, picking.Status);

        // Too late for the website to cancel.
        Assert.Equal(HttpStatusCode.Conflict, (await website.PostAsJsonAsync($"/api/v1/orders/{order.OrderId}/cancel", new CancelWebOrderRequest("Changed my mind"))).StatusCode);

        await SendAsync(register, StatusEvent(new WebOrderStatusV1(order.OrderId, WebOrderStatuses.Completed, DateTime.UtcNow,
            new[] { new WebOrderPickedV1(0, 1), new WebOrderPickedV1(1, 1) }, 22.50m, 15m, 42)));
        await SendAsync(register, StatusEvent(new WebOrderStatusV1(order.OrderId, WebOrderStatuses.Cancelled, DateTime.UtcNow)));

        var done = (await ChangesAsync(website, picking.ChangeNumber)).Orders.Single();
        Assert.Equal((WebOrderStatuses.Completed, 22.50m, 15m, 42L), (done.Status, done.FinalTotal, done.RefundDue, done.InvoiceNumber));
        Assert.Equal(1, done.Picked![0].Quantity);
    }

    [Fact]
    public async Task WebsiteCancel_BeforePicking_GoesToTheRegisters()
    {
        var (storeId, register, website) = await BusinessAsync("Hamro Grocery");
        var order = Order(storeId);
        await PlaceAsync(website, order);

        var cancelled = await (await website.PostAsJsonAsync($"/api/v1/orders/{order.OrderId}/cancel", new CancelWebOrderRequest("Changed my mind")))
            .Content.ReadFromJsonAsync<WebOrderView>(SyncJson.Options);
        Assert.Equal(WebOrderStatuses.Cancelled, cancelled!.Status);

        var shared = await register.GetFromJsonAsync<SharedEventsResponse>("/api/v1/sync/shared?after=0&afterWeb=0", SyncJson.Options);
        var status = Assert.Single(shared!.Events, e => e.Event.Type == SyncEventTypes.OrderStatus).Event.Payload.Deserialize<WebOrderStatusV1>(SyncJson.Options)!;
        Assert.Equal((WebOrderStatuses.Cancelled, "Changed my mind"), (status.Status, status.Note));
    }

    [Fact]
    public async Task BadOrders_AreRefused()
    {
        var (storeId, _, website) = await BusinessAsync("Hamro Grocery");
        var (otherStore, _, _) = await BusinessAsync("Kathmandu Mart");

        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(website, Order(storeId, total: 30m))).StatusCode);       // lines add up to 37.50
        Assert.Equal(HttpStatusCode.NotFound, (await PlaceAsync(website, Order(otherStore))).StatusCode);                 // another business's store
        Assert.Equal(HttpStatusCode.Conflict, (await PlaceAsync(website, Order(storeId, onAccount: 20m, customerId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PlaceAsync(website, Order(storeId, onAccount: 20m))).StatusCode);   // on account needs the customer
    }

    [Fact]
    public async Task RegisterListsTheOwnersStores()
    {
        var (_, register, _) = await BusinessAsync("Hamro Grocery");
        var stores = await register.GetFromJsonAsync<List<StoreRef>>("/api/v1/sync/stores", SyncJson.Options);
        Assert.Equal("STORE-001", Assert.Single(stores!).StoreCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
