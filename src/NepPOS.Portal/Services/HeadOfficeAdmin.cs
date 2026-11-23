using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using NepPOS.Contracts.Admin;
using NepPOS.Contracts.Sync;

namespace NepPOS.Portal.Services;

/// <summary>Where Head Office is and the admin key the Portal uses (Portal:HeadOfficeUrl, Portal:AdminKey).</summary>
public sealed class PortalOptions
{
    public const string Section = "Portal";

    public string HeadOfficeUrl { get; set; } = "";

    /// <summary>Same value as Head Office's HeadOffice:AdminKey. User secrets or App Service settings only.</summary>
    public string AdminKey { get; set; } = "";
}

/// <summary>An answer from Head Office, or why there isn't one, in words staff can act on.</summary>
public sealed record AdminResult<T>(T? Value, string? Problem, string? ProblemType = null)
{
    public bool Ok => Problem is null;
}

/// <summary>Codes and keys Head Office shows once; the Portal displays them and never stores them.</summary>
public sealed record IssuedActivationCode(string Code, Guid StoreId, string StoreCode, DateTime ExpiresUtc);
public sealed record IssuedWebsiteKey(Guid Id, Guid TenantId, string Name, string ApiKey);
public sealed record CreatedBusiness(Guid Id, string Name);
public sealed record CreatedStore(Guid Id, Guid TenantId, string StoreCode, string Name, string TimeZoneId);

/// <summary>
/// Everything the Portal does goes through here, to Head Office's admin API. Every call says who is
/// signed in (X-Portal-User) so Head Office writes it in the audit log. It never throws for network or
/// validation problems: pages show the reason instead.
/// </summary>
public sealed class HeadOfficeAdmin
{
    private static readonly JsonSerializerOptions Json = SyncJson.Options;

    private readonly HttpClient _http;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<HeadOfficeAdmin> _logger;

    public HeadOfficeAdmin(HttpClient http, AuthenticationStateProvider auth, ILogger<HeadOfficeAdmin> logger)
    {
        _http = http;
        _auth = auth;
        _logger = logger;
    }

    // ---- Reading ----

    public Task<AdminResult<AdminOverview>> OverviewAsync(CancellationToken ct = default) => GetAsync<AdminOverview>("overview", ct);

    public Task<AdminResult<List<BusinessRow>>> BusinessesAsync(string? search, CancellationToken ct = default) =>
        GetAsync<List<BusinessRow>>(string.IsNullOrWhiteSpace(search) ? "tenants" : $"tenants?search={Uri.EscapeDataString(search.Trim())}", ct);

    public Task<AdminResult<BusinessDetail>> BusinessAsync(Guid id, CancellationToken ct = default) => GetAsync<BusinessDetail>($"tenants/{id}", ct);

    public Task<AdminResult<List<RegisterRow>>> RegistersAsync(string? status, CancellationToken ct = default) =>
        GetAsync<List<RegisterRow>>(string.IsNullOrEmpty(status) ? "registers" : $"registers?status={status}", ct);

    public Task<AdminResult<List<AuditEntryRow>>> AuditAsync(Guid? businessId, int take = 100, CancellationToken ct = default) =>
        GetAsync<List<AuditEntryRow>>(businessId is { } id ? $"audit?tenantId={id}&take={take}" : $"audit?take={take}", ct);

    public Task<AdminResult<List<StaffRow>>> StaffAsync(CancellationToken ct = default) => GetAsync<List<StaffRow>>("staff", ct);

    // ---- Changing ----

    public Task<AdminResult<CreatedBusiness>> CreateBusinessAsync(string name, CancellationToken ct = default) =>
        SendAsync<CreatedBusiness>(HttpMethod.Post, "tenants", new { name }, ct);

    public Task<AdminResult<bool>> RenameBusinessAsync(Guid id, string name, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Put, $"tenants/{id}", new RenameBusinessRequest(name), ct);

    public Task<AdminResult<CreatedStore>> CreateStoreAsync(Guid businessId, string storeCode, string name, string timeZoneId, CancellationToken ct = default) =>
        SendAsync<CreatedStore>(HttpMethod.Post, $"tenants/{businessId}/stores", new { storeCode, name, timeZoneId }, ct);

    public Task<AdminResult<IssuedActivationCode>> IssueActivationCodeAsync(Guid storeId, int validDays = 7, CancellationToken ct = default) =>
        SendAsync<IssuedActivationCode>(HttpMethod.Post, $"stores/{storeId}/activation-codes", new { validDays }, ct);

    public Task<AdminResult<bool>> RevokeRegisterAsync(Guid registerId, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"registers/{registerId}/revoke", null, ct);

    public Task<AdminResult<IssuedWebsiteKey>> CreateWebsiteKeyAsync(Guid businessId, string name, CancellationToken ct = default) =>
        SendAsync<IssuedWebsiteKey>(HttpMethod.Post, $"tenants/{businessId}/website-keys", new { name }, ct);

    public Task<AdminResult<bool>> RevokeWebsiteKeyAsync(Guid keyId, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"website-keys/{keyId}/revoke", null, ct);

    // ---- Licences ----

    public Task<AdminResult<LicenceDetail>> LicenceAsync(Guid businessId, CancellationToken ct = default) =>
        GetAsync<LicenceDetail>($"tenants/{businessId}/licence", ct);

    public Task<AdminResult<LicenceDetail>> UpdateLicenceAsync(Guid businessId, UpdateLicenceRequest request, CancellationToken ct = default) =>
        SendAsync<LicenceDetail>(HttpMethod.Put, $"tenants/{businessId}/licence", request, ct);

    // ---- Staff ----

    public Task<AdminResult<StaffSignInResponse>> SignInAsync(string email, string password, string? code, CancellationToken ct = default) =>
        SendAsync<StaffSignInResponse>(HttpMethod.Post, "staff/sign-in", new StaffSignInRequest(email, password, code), ct);

    public Task<AdminResult<StaffRow>> CreateStaffAsync(CreateStaffRequest request, CancellationToken ct = default) =>
        SendAsync<StaffRow>(HttpMethod.Post, "staff", request, ct);

    public Task<AdminResult<bool>> DisableStaffAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"staff/{id}/disable", null, ct);

    public Task<AdminResult<TwoFactorSetup>> StartTwoFactorAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TwoFactorSetup>(HttpMethod.Post, $"staff/{id}/two-factor/start", null, ct);

    public Task<AdminResult<bool>> ConfirmTwoFactorAsync(Guid id, string code, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"staff/{id}/two-factor/confirm", new TwoFactorConfirmRequest(code), ct);

    public Task<AdminResult<bool>> ChangePasswordAsync(Guid id, string current, string next, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"staff/{id}/password", new ChangePasswordRequest(current, next), ct);

    // ---- Plumbing ----

    private Task<AdminResult<T>> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<AdminResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "api/v1/admin/" + path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        var user = (await _auth.GetAuthenticationStateAsync()).User;
        if (user.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email)
            request.Headers.Add(PortalHeaders.User, email);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(bool))
                    return new AdminResult<T>((T)(object)true, null);
                var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
                return value is null ? new AdminResult<T>(default, "Head Office sent an empty answer.") : new AdminResult<T>(value, null);
            }

            var (problem, type) = await ProblemOf(response, ct);
            return new AdminResult<T>(default, problem, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Head Office admin call failed: {Method} {Path}", method, path);
            return new AdminResult<T>(default, "Can't reach Head Office. Check it's running and Portal:HeadOfficeUrl is right.");
        }
    }

    /// <summary>Turns Head Office's error into one sentence: validation messages, a conflict's text, or the status.</summary>
    private static async Task<(string Message, string? Type)> ProblemOf(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        string? type = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return (root.GetString()!, null);
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                return (string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray()).Select(v => v.GetString())), type);
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                return (detail.GetString()!, type);
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(text) && text.Length < 300)
                return (text.Trim('"'), null);
        }

        return (response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Head Office refused the Portal's admin key. Check Portal:AdminKey matches HeadOffice:AdminKey.",
            HttpStatusCode.Forbidden => "Head Office's admin API is switched off: set HeadOffice:AdminKey there.",
            HttpStatusCode.NotFound => "Not found. It may have been removed.",
            _ => $"Head Office answered {(int)response.StatusCode} {response.ReasonPhrase}."
        }, type);
    }
}
