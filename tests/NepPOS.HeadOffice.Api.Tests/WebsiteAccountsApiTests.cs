using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Accounts;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>
/// The website's side of customers, store accounts and gift cards: it reads what the registers shared, and what it
/// changes (a sign-up, a gift card spent online) reaches every register.
/// </summary>
public sealed class WebsiteAccountsApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-webaccounts-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private long _sequence;

    public WebsiteAccountsApiTests()
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

    private async Task<(HttpClient Register, HttpClient Website)> BusinessAsync(string name)
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
        return (register, website);
    }

    private SyncEvent Event<T>(string type, T payload) =>
        new(Guid.NewGuid(), type, 1, "STORE-001", "REG-001", ++_sequence, DateTime.UtcNow, JsonSerializer.SerializeToElement(payload, SyncJson.Options));

    private static async Task SendAsync(HttpClient register, params SyncEvent[] events)
    {
        var answer = await (await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events), SyncJson.Options))
            .Content.ReadFromJsonAsync<SyncEventsResponse>(SyncJson.Options);
        Assert.Equal(events.Length, answer!.Accepted.Count);
    }

    [Fact]
    public async Task TheWebsite_SeesAnAccountCustomerFromTheRegister_WithBalanceAndStatement()
    {
        var (register, website) = await BusinessAsync("Nep Grocery");
        var customerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await SendAsync(register,
            Event(SyncEventTypes.CustomerChanged, new CustomerChangedV1(customerId, "CUST-12345678", "Corner Café", "0412 555 019",
                "Owner@CornerCafe.com.au", null, null, null, null, true, 1500m, 14, true, now, now)),
            Event(SyncEventTypes.AccountEntry, new AccountEntryV1(Guid.NewGuid(), customerId, "Charge", 300m, Guid.NewGuid(), 88,
                now.AddDays(-40), now.AddDays(-26), null, "Sale on account")),
            Event(SyncEventTypes.AccountEntry, new AccountEntryV1(Guid.NewGuid(), customerId, "Payment", -100m, Guid.NewGuid(), 97,
                now.AddDays(-20), null, null, null)));

        var found = await website.GetFromJsonAsync<CustomerAccount>("/api/v1/customers/find?email=owner%40cornercafe.com.au", SyncJson.Options);
        Assert.Equal(("CUST-12345678", 200m, 200m), (found!.Code, found.Balance, found.Overdue));

        var statement = await website.GetFromJsonAsync<CustomerStatementView>($"/api/v1/customers/{customerId}/account", SyncJson.Options);
        Assert.Equal(2, statement!.Lines.Count);
        Assert.Equal("Payment", statement.Lines[0].Kind);
        Assert.Equal(200m, statement.Ageing.Total);
    }

    [Fact]
    public async Task A_SignUp_LinksToTheSameEmail_OrBecomesANewCustomerForTheRegisters()
    {
        var (register, website) = await BusinessAsync("Nep Grocery");
        var now = DateTime.UtcNow;
        await SendAsync(register, Event(SyncEventTypes.CustomerChanged, new CustomerChangedV1(Guid.NewGuid(), "CUST-11112222", "Priya",
            null, "priya@example.com", null, null, null, null, false, 0, 30, true, now, now)));

        var linked = await (await website.PostAsJsonAsync("/api/v1/customers", new CustomerSignUp("PRIYA@example.com", "Priya S", null), SyncJson.Options))
            .Content.ReadFromJsonAsync<CustomerAccount>(SyncJson.Options);
        Assert.Equal("CUST-11112222", linked!.Code);

        var created = await (await website.PostAsJsonAsync("/api/v1/customers", new CustomerSignUp("tom@example.com", "Tom Nguyen", "0401 998 312"), SyncJson.Options))
            .Content.ReadFromJsonAsync<CustomerAccount>(SyncJson.Options);
        Assert.StartsWith("CUST-", created!.Code);
        Assert.False(created.IsAccount);

        var shared = await register.GetFromJsonAsync<SharedEventsResponse>("/api/v1/sync/shared?after=0&afterWeb=0", SyncJson.Options);
        var fromWeb = Assert.Single(shared!.Events, e => e.FromWebsite);
        Assert.Equal(SyncEventTypes.CustomerChanged, fromWeb.Event.Type);
        Assert.Equal("Tom Nguyen", fromWeb.Event.Payload.Deserialize<CustomerChangedV1>(SyncJson.Options)!.Name);
        Assert.Equal(fromWeb.Id, shared.LastWebId);
    }

    [Fact]
    public async Task A_GiftCardSoldInStore_CanBeSpentOnline_Once_AndNeverOverspent()
    {
        var (register, website) = await BusinessAsync("Nep Grocery");
        await SendAsync(register, Event(SyncEventTypes.GiftCardMoved,
            new GiftCardMovedV1("GC-1234567890", "GiftCard", 50m, Guid.NewGuid(), 12, DateTime.UtcNow, "Sold / topped up")));

        var card = await website.GetFromJsonAsync<GiftCardView>("/api/v1/giftcards/gc-1234567890", SyncJson.Options);
        Assert.Equal((50m, true), (card!.Balance, card.IsActive));

        var request = new GiftCardChangeRequest(Guid.NewGuid(), 20m, "Order HG-1001");
        var spent = await (await website.PostAsJsonAsync("/api/v1/giftcards/GC-1234567890/redeem", request, SyncJson.Options))
            .Content.ReadFromJsonAsync<GiftCardChangeResult>(SyncJson.Options);
        Assert.Equal(30m, spent!.Balance);

        // A retry of the same request changes nothing.
        var again = await (await website.PostAsJsonAsync("/api/v1/giftcards/GC-1234567890/redeem", request, SyncJson.Options))
            .Content.ReadFromJsonAsync<GiftCardChangeResult>(SyncJson.Options);
        Assert.Equal(30m, again!.Balance);

        var tooMuch = await website.PostAsJsonAsync("/api/v1/giftcards/GC-1234567890/redeem",
            new GiftCardChangeRequest(Guid.NewGuid(), 30.01m, "Order HG-1002"), SyncJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);

        var refunded = await (await website.PostAsJsonAsync("/api/v1/giftcards/GC-1234567890/refund",
                new GiftCardChangeRequest(Guid.NewGuid(), 20m, "Order HG-1001 cancelled"), SyncJson.Options))
            .Content.ReadFromJsonAsync<GiftCardChangeResult>(SyncJson.Options);
        Assert.Equal(50m, refunded!.Balance);

        // The register hears about both online changes.
        var shared = await register.GetFromJsonAsync<SharedEventsResponse>("/api/v1/sync/shared?after=0&afterWeb=0", SyncJson.Options);
        Assert.Equal(new[] { -20m, 20m }, shared!.Events.Where(e => e.FromWebsite)
            .Select(e => e.Event.Payload.Deserialize<GiftCardMovedV1>(SyncJson.Options)!.Amount).ToArray());
    }

    [Fact]
    public async Task AnotherBusinessesWebsite_SeesNone()
    {
        var (register, _) = await BusinessAsync("Nep Grocery");
        var (_, otherWebsite) = await BusinessAsync("Other Shop");
        await SendAsync(register, Event(SyncEventTypes.GiftCardMoved,
            new GiftCardMovedV1("GC-5555555555", "GiftCard", 50m, null, 1, DateTime.UtcNow, "Sold")));

        Assert.Equal(HttpStatusCode.NotFound, (await otherWebsite.GetAsync("/api/v1/giftcards/GC-5555555555")).StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
