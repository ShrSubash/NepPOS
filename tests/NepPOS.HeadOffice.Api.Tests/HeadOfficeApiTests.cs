using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Endpoints;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>
/// The whole API running in memory against its own SQLite file: setting up a customer,
/// activating a register, receiving events once only, and keeping businesses apart.
/// </summary>
public sealed class HeadOfficeApiTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;

    public HeadOfficeApiTests()
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

    // ---- helpers: what the platform admin and a register would do ----

    private async Task<(Guid TenantId, Guid StoreId)> NewStoreAsync(string business, string storeCode = "STORE-001")
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name = business }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode, name = "Main" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();
        return (tenant.Id, store!.Id);
    }

    private async Task<string> NewCodeAsync(Guid storeId) =>
        (await (await _admin.PostAsJsonAsync($"/api/v1/admin/stores/{storeId}/activation-codes", new { validDays = 7 }))
            .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>())!.Code;

    private async Task<(HttpClient Client, ActivateResponse Activation)> ActivatedRegisterAsync(Guid storeId, string registerCode = "REG-001")
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate",
            new ActivateRequest(await NewCodeAsync(storeId), registerCode, "Front counter"));
        response.EnsureSuccessStatusCode();
        var activation = (await response.Content.ReadFromJsonAsync<ActivateResponse>())!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activation.ApiKey);
        return (client, activation);
    }

    private static SyncEvent Event(long sequence, string store = "STORE-001", string register = "REG-001", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), SyncEventTypes.JournalRecorded, 1, store, register, sequence,
            new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc),
            JsonSerializer.SerializeToElement(new { eventType = "NoSale", detail = "Drawer opened" }));

    private static async Task<SyncEventsResponse> SendAsync(HttpClient client, params SyncEvent[] events)
    {
        var response = await client.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(events));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SyncEventsResponse>())!;
    }

    private async Task<T> InDatabaseAsync<T>(Func<HeadOfficeDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HeadOfficeDbContext>());
    }

    // ---- tests ----

    [Fact]
    public async Task AdminEndpoints_NeedTheAdminKey()
    {
        var anonymous = _factory.CreateClient();
        var wrongKey = _factory.CreateClient();
        wrongKey.DefaultRequestHeaders.Add("X-Admin-Key", "not-the-key-not-the-key-123");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/admin/tenants", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.PostAsJsonAsync("/api/v1/admin/tenants", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Activation_GivesAKeyForThatStore_AndTheCodeWorksOnlyOnce()
    {
        var (tenantId, storeId) = await NewStoreAsync("Nep Liquor");
        var code = await NewCodeAsync(storeId);
        var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code.ToLowerInvariant().Replace("-", " "), "reg-001", "Front counter"));
        var again = await client.PostAsJsonAsync("/api/v1/activate", new ActivateRequest(code, "REG-002", null));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var activation = (await first.Content.ReadFromJsonAsync<ActivateResponse>())!;
        Assert.Equal((tenantId, storeId, "STORE-001", "REG-001"), (activation.TenantId, activation.StoreId, activation.StoreCode, activation.RegisterCode));
        Assert.StartsWith("npk_", activation.ApiKey);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // Only a hash of the key is stored.
        var stored = await InDatabaseAsync(db => db.Registers.SingleAsync());
        Assert.DoesNotContain(activation.ApiKey, stored.ApiKeyHash);
        Assert.Equal(64, stored.ApiKeyHash.Length);
    }

    [Fact]
    public async Task ActiveRegisterCode_CannotBeActivatedTwice_UntilRevoked()
    {
        var (_, storeId) = await NewStoreAsync("Nep Liquor");
        var (oldClient, _) = await ActivatedRegisterAsync(storeId);

        var duplicate = await _factory.CreateClient().PostAsJsonAsync("/api/v1/activate", new ActivateRequest(await NewCodeAsync(storeId), "REG-001", null));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var registerId = await InDatabaseAsync(db => db.Registers.Select(r => r.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/api/v1/admin/registers/{registerId}/revoke", null)).StatusCode);

        // The old PC's key stops working; a new PC can take over REG-001.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.PostAsync("/api/v1/heartbeat", null)).StatusCode);
        var (newClient, _) = await ActivatedRegisterAsync(storeId);
        Assert.Equal(HttpStatusCode.OK, (await newClient.PostAsync("/api/v1/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task Events_AreStoredOnce_AndARepeatIsReportedAsDuplicate()
    {
        var (_, storeId) = await NewStoreAsync("Nep Liquor");
        var (register, _) = await ActivatedRegisterAsync(storeId);
        var first = Event(1);
        var second = Event(2);

        var sent = await SendAsync(register, first, second);
        var resent = await SendAsync(register, first, second, Event(3));   // a retry after a dropped connection, plus a new one

        Assert.Equal(new[] { first.EventId, second.EventId }, sent.Accepted);
        Assert.Equal(2, sent.LastSequence);
        Assert.Equal(new[] { first.EventId, second.EventId }, resent.Duplicates);
        Assert.Single(resent.Accepted);
        Assert.Equal(3, resent.LastSequence);
        Assert.Equal(3, await InDatabaseAsync(db => db.ReceivedEvents.CountAsync()));
    }

    [Fact]
    public async Task Events_ForAnotherStoreOrRegister_OrAReusedSequence_AreRejected()
    {
        var (_, storeId) = await NewStoreAsync("Nep Liquor");
        var (register, _) = await ActivatedRegisterAsync(storeId);
        await SendAsync(register, Event(1));

        var result = await SendAsync(register,
            Event(2, store: "STORE-002"),
            Event(3, register: "REG-009"),
            Event(1));                                   // sequence 1 already used by a different event

        Assert.Empty(result.Accepted);
        Assert.Equal(3, result.Rejected.Count);
        Assert.Contains("STORE-002", result.Rejected[0].Reason);
        Assert.Contains("REG-009", result.Rejected[1].Reason);
        Assert.Contains("Sequence 1", result.Rejected[2].Reason);
        Assert.Equal(1, await InDatabaseAsync(db => db.ReceivedEvents.CountAsync()));
    }

    [Fact]
    public async Task TwoBusinesses_WithTheSameStoreCode_StayApart()
    {
        var (tenantA, storeA) = await NewStoreAsync("Nep Liquor");
        var (tenantB, storeB) = await NewStoreAsync("Kathmandu Mart");     // also STORE-001
        var (registerA, _) = await ActivatedRegisterAsync(storeA);
        var (registerB, _) = await ActivatedRegisterAsync(storeB);

        await SendAsync(registerA, Event(1));
        await SendAsync(registerB, Event(1));                               // same codes, same sequence, other business

        // B resending A's event id is refused, not filed under A.
        var aEventId = await InDatabaseAsync(db => db.ReceivedEvents.Where(e => e.TenantId == tenantA).Select(e => e.EventId).SingleAsync());
        var stolen = await SendAsync(registerB, Event(2, id: aEventId));
        Assert.Single(stolen.Rejected);

        var perTenant = await InDatabaseAsync(db => db.ReceivedEvents.GroupBy(e => e.TenantId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync());
        Assert.Equal(1, perTenant.Single(t => t.Key == tenantA).Count);
        Assert.Equal(1, perTenant.Single(t => t.Key == tenantB).Count);
    }

    [Fact]
    public async Task RegisterEndpoints_NeedAValidKey()
    {
        var noKey = _factory.CreateClient();
        var badKey = _factory.CreateClient();
        badKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "npk_made-up-key");

        Assert.Equal(HttpStatusCode.Unauthorized, (await noKey.PostAsJsonAsync("/api/v1/sync/events", new SyncEventsRequest(new[] { Event(1) }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await badKey.PostAsync("/api/v1/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task Heartbeat_RecordsWhenTheRegisterWasLastSeen()
    {
        var (_, storeId) = await NewStoreAsync("Nep Liquor");
        var (register, _) = await ActivatedRegisterAsync(storeId);

        var before = DateTime.UtcNow.AddSeconds(-1);
        var response = await register.PostAsync("/api/v1/heartbeat", null);

        response.EnsureSuccessStatusCode();
        var lastSeen = await InDatabaseAsync(db => db.Registers.Select(r => r.LastSeenUtc).SingleAsync());
        Assert.NotNull(lastSeen);
        Assert.True(lastSeen >= before);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }
}
