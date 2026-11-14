using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Endpoints;
using NepPOS.Infrastructure.Integration.HeadOffice;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>
/// Integration build step 3, end to end: the register's real HTTP client and sync rules talking to
/// the real Head Office (in memory). Only the register's outbox and connection file are stand-ins.
/// </summary>
public sealed class RegisterSyncEndToEndTests : IDisposable
{
    private const string AdminKey = "test-admin-key-0123456789abcdef";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"headoffice-e2e-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _admin;
    private readonly HeadOfficeClient _client;

    public RegisterSyncEndToEndTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:HeadOffice", $"Data Source={_dbPath}");
            host.UseSetting("HeadOffice:DatabaseProvider", "Sqlite");
            host.UseSetting("HeadOffice:AdminKey", AdminKey);
        });
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        _client = new HeadOfficeClient(_factory.CreateClient());   // the register's client, pointed at Head Office in memory
    }

    private string BaseUrl => _factory.Server.BaseAddress.ToString();

    private async Task<(Guid StoreId, string Code)> StoreWithCodeAsync()
    {
        var tenant = await (await _admin.PostAsJsonAsync("/api/v1/admin/tenants", new { name = "Nep Liquor" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.TenantResponse>();
        var store = await (await _admin.PostAsJsonAsync($"/api/v1/admin/tenants/{tenant!.Id}/stores", new { storeCode = "STORE-001", name = "Parramatta" }))
            .Content.ReadFromJsonAsync<AdminEndpoints.StoreResponse>();
        var code = await (await _admin.PostAsJsonAsync($"/api/v1/admin/stores/{store!.Id}/activation-codes", new { validDays = 7 }))
            .Content.ReadFromJsonAsync<AdminEndpoints.ActivationCodeResponse>();
        return (store.Id, code!.Code);
    }

    private async Task<HeadOfficeConnection> ConnectAsync(string code)
    {
        var answer = await _client.ActivateAsync(BaseUrl, new ActivateRequest(code, "REG-001", "Test PC", "STORE-001"));
        return new HeadOfficeConnection(BaseUrl, answer.TenantId, answer.TenantName, answer.StoreId, answer.StoreCode,
            answer.StoreName, answer.RegisterCode, answer.ApiKey, DateTime.UtcNow);
    }

    private static SyncEvent Event(long sequence, string store = "STORE-001") => new(Guid.NewGuid(), SyncEventTypes.JournalRecorded, 1,
        store, "REG-001", sequence, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { eventType = "NoSale" }));

    [Fact]
    public async Task ConnectedRegister_SendsItsOutbox_AndRepeatsAreHarmless()
    {
        var (_, code) = await StoreWithCodeAsync();
        var connection = await ConnectAsync(code);
        Assert.Equal(("Nep Liquor", "Parramatta"), (connection.TenantName, connection.StoreName));

        var outbox = new MemoryOutbox(Event(1), Event(2), Event(3, store: "STORE-002"));
        var sync = new SyncService(new OneConnection(connection), _client, outbox);

        var first = await sync.SendOutboxAsync();
        Assert.Equal((SyncOutcome.UpToDate, 2, 1), (first.Outcome, first.Sent, first.Rejected));
        Assert.Empty(outbox.Unsent);
        Assert.Contains("STORE-002", Assert.Single(outbox.Rejected).Reason);

        // The register crashed before marking them sent: the resend counts as done, nothing doubles.
        outbox.Requeue();
        var again = await sync.SendOutboxAsync();
        Assert.Equal(2, again.Sent);

        Assert.Equal(SyncOutcome.UpToDate, (await sync.HeartbeatAsync()).Outcome);
    }

    [Fact]
    public async Task CodeForAnotherStore_IsRefused_WithoutUsingTheCodeUp()
    {
        var (_, code) = await StoreWithCodeAsync();

        var wrongStore = await Assert.ThrowsAsync<HeadOfficeException>(() =>
            _client.ActivateAsync(BaseUrl, new ActivateRequest(code, "REG-001", null, "STORE-009")));
        Assert.Equal(HeadOfficeProblem.Refused, wrongStore.Problem);
        Assert.Contains("STORE-001", wrongStore.Message);

        var connection = await ConnectAsync(code);       // the same code still works for the right store
        Assert.Equal("REG-001", connection.RegisterCode);

        var reused = await Assert.ThrowsAsync<HeadOfficeException>(() => ConnectAsync(code));
        Assert.Contains("already used", reused.Message);
    }

    [Fact]
    public async Task RevokedRegister_IsToldItIsNoLongerAccepted()
    {
        var (storeId, code) = await StoreWithCodeAsync();
        var connection = await ConnectAsync(code);
        var registers = await _admin.GetFromJsonAsync<List<AdminEndpoints.RegisterResponse>>($"/api/v1/admin/stores/{storeId}/registers");
        await _admin.PostAsync($"/api/v1/admin/registers/{registers!.Single().Id}/revoke", null);

        var result = await new SyncService(new OneConnection(connection), _client, new MemoryOutbox(Event(1))).SendOutboxAsync();

        Assert.Equal(SyncOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task UnreachableHeadOffice_IsAFailureToRetry_NotAnError()
    {
        var offline = new HeadOfficeClient(new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
        var connection = new HeadOfficeConnection("http://127.0.0.1:9", Guid.NewGuid(), "x", Guid.NewGuid(), "STORE-001", "x",
            "REG-001", "npk_x", DateTime.UtcNow);
        var outbox = new MemoryOutbox(Event(1));

        var result = await new SyncService(new OneConnection(connection), offline, outbox).SendOutboxAsync();

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Single(outbox.Unsent);
    }

    [Fact]
    public async Task Times_ComeBackMarkedAsUtc()
    {
        var (storeId, code) = await StoreWithCodeAsync();
        var connection = await ConnectAsync(code);
        await _client.HeartbeatAsync(connection);

        var json = await _admin.GetStringAsync($"/api/v1/admin/stores/{storeId}/registers");

        using var doc = JsonDocument.Parse(json);
        var register = doc.RootElement[0];
        Assert.EndsWith("Z", register.GetProperty("activatedUtc").GetString());
        Assert.EndsWith("Z", register.GetProperty("lastSeenUtc").GetString());
    }

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    private sealed class OneConnection(HeadOfficeConnection connection) : IHeadOfficeConnectionStore
    {
        public HeadOfficeConnection? Load() => connection;
        public void Save(HeadOfficeConnection c) { }
        public void Clear() { }
    }

    private sealed class MemoryOutbox(params SyncEvent[] events) : IOutboxStore
    {
        private readonly List<SyncEvent> _all = events.ToList();
        public List<SyncEvent> Unsent { get; } = events.ToList();
        public List<RejectedEvent> Rejected { get; } = new();

        /// <summary>As if the register never got the answer: everything not rejected is offered again.</summary>
        public void Requeue()
        {
            Unsent.Clear();
            Unsent.AddRange(_all.Where(e => Rejected.All(r => r.EventId != e.EventId)));
        }

        public Task<IReadOnlyList<SyncEvent>> GetUnsentAsync(int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncEvent>>(Unsent.Take(max).ToList());

        public Task MarkSentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            Unsent.RemoveAll(e => ids.Contains(e.EventId));
            return Task.CompletedTask;
        }

        public Task MarkRejectedAsync(IReadOnlyCollection<RejectedEvent> rejected, CancellationToken ct = default)
        {
            Rejected.AddRange(rejected);
            Unsent.RemoveAll(e => rejected.Any(r => r.EventId == e.EventId));
            return Task.CompletedTask;
        }

        public Task<OutboxCounts> GetCountsAsync(CancellationToken ct = default) =>
            Task.FromResult(new OutboxCounts(Unsent.Count, Rejected.Count, null, null));
    }
}
