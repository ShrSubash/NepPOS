using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>
/// GET /api/v1/sync/shared: customers, account lines and gift card movements one register sends reach the owner's
/// other registers — never the sender itself, never another business, and never sales or journal lines.
/// </summary>
public sealed class SharedSyncApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-shared-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly Dictionary<string, long> _sequences = new();

    public SharedSyncApiTests()
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

    /// <summary>A business with one store and the given registers, each with its own key.</summary>
    private async Task<HttpClient[]> BusinessAsync(string name, params string[] registers)
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode = "STORE-001", name = "Main" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();

        var clients = new List<HttpClient>();
        foreach (var registerCode in registers)
        {
            var code = await (await _admin.PostAsJsonAsync($"/api/v1/admin/stores/{store!.Id}/activation-codes", new { }))
                .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>();
            var activation = await (await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code!.Code, registerCode, null)))
                .Content.ReadFromJsonAsync<ActivateResponse>();
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activation!.ApiKey);
            clients.Add(client);
        }
        return clients.ToArray();
    }

    private SyncEvent Event<T>(string register, string type, T payload)
    {
        _sequences[register] = _sequences.GetValueOrDefault(register) + 1;
        return new SyncEvent(Guid.NewGuid(), type, 1, "STORE-001", register, _sequences[register], DateTime.UtcNow,
            JsonSerializer.SerializeToElement(payload, SyncJson.Options));
    }

    private static async Task SendAsync(HttpClient register, params SyncEvent[] events)
    {
        var answer = await (await register.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events), SyncJson.Options))
            .Content.ReadFromJsonAsync<SyncEventsResponse>(SyncJson.Options);
        Assert.Equal(events.Length, answer!.Accepted.Count);
    }

    private static async Task<SharedEventsResponse> SharedAsync(HttpClient register, long after = 0) =>
        (await register.GetFromJsonAsync<SharedEventsResponse>($"/api/v1/sync/shared?after={after}", SyncJson.Options))!;

    [Fact]
    public async Task OtherRegisters_GetCustomersAccountsAndGiftCards_ButNotSalesOrTheirOwn()
    {
        var registers = await BusinessAsync("Nep Grocery", "REG-001", "REG-002");
        var (a, b) = (registers[0], registers[1]);
        var customerId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await SendAsync(a,
            Event("REG-001", SyncEventTypes.CustomerChanged, new CustomerChangedV1(customerId, "CUST-12345678", "Corner Café",
                null, null, null, null, null, null, true, 1000m, 14, true, now, now)),
            Event("REG-001", SyncEventTypes.JournalRecorded, new { detail = "Signed in" }),
            Event("REG-001", SyncEventTypes.AccountEntry, new AccountEntryV1(Guid.NewGuid(), customerId, "Charge", 35m,
                Guid.NewGuid(), 7, now, now.AddDays(14), null, null)),
            Event("REG-001", SyncEventTypes.GiftCardMoved, new GiftCardMovedV1("GC-1234567890", "GiftCard", 50m, null, 8, now, "Sold")));

        var forB = await SharedAsync(b);
        Assert.Equal(new[] { SyncEventTypes.CustomerChanged, SyncEventTypes.AccountEntry, SyncEventTypes.GiftCardMoved },
            forB.Events.Select(e => e.Event.Type).ToArray());
        Assert.All(forB.Events, e => Assert.Equal(("STORE-001", "REG-001"), (e.Event.StoreId, e.Event.RegisterId)));
        Assert.False(forB.More);
        Assert.Equal(forB.Events[^1].Id, forB.LastId);
        var customer = forB.Events[0].Event.Payload.Deserialize<CustomerChangedV1>(SyncJson.Options)!;
        Assert.Equal("Corner Café", customer.Name);

        // The sender doesn't get its own back, but its place still moves on.
        var forA = await SharedAsync(a);
        Assert.Empty(forA.Events);
        Assert.True(forA.LastId >= forB.LastId);

        // Asking again from where it got to: nothing new.
        Assert.Empty((await SharedAsync(b, forB.LastId)).Events);
    }

    [Fact]
    public async Task AnotherBusiness_NeverSeesThem()
    {
        var mine = await BusinessAsync("Nep Grocery", "REG-001");
        var theirs = await BusinessAsync("Other Shop", "REG-001");
        var now = DateTime.UtcNow;

        await SendAsync(mine[0], Event("REG-001", SyncEventTypes.GiftCardMoved,
            new GiftCardMovedV1("GC-1111111111", "GiftCard", 20m, null, 1, now, "Sold")));

        Assert.Empty((await SharedAsync(theirs[0])).Events);
    }

    [Fact]
    public async Task WithoutAKey_IsRefused()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/sync/shared?after=0");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
