using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NepPOS.HeadOffice.Api.Data;

namespace NepPOS.HeadOffice.Api.Security;

/// <summary>
/// Registers sign in with "Authorization: Bearer npk_…". The key decides which tenant, store and
/// register the caller is; nothing in the request body can change that (docs/adr/0002).
/// </summary>
public sealed class RegisterApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "RegisterApiKey";

    public RegisterApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer npk_", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var hash = Secrets.Hash(header["Bearer ".Length..].Trim());
        var db = Context.RequestServices.GetRequiredService<HeadOfficeDbContext>();

        var register = await db.Registers.AsNoTracking()
            .Where(r => r.ApiKeyHash == hash && !r.IsRevoked)
            .Join(db.Stores, r => r.StoreId, s => s.Id, (r, s) => new { r.Id, r.TenantId, r.StoreId, r.RegisterCode, s.StoreCode })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (register is null)
            return AuthenticateResult.Fail("Unknown or revoked API key.");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(RegisterIdentity.TenantIdClaim, register.TenantId.ToString()),
            new Claim(RegisterIdentity.StoreIdClaim, register.StoreId.ToString()),
            new Claim(RegisterIdentity.RegisterIdClaim, register.Id.ToString()),
            new Claim(RegisterIdentity.StoreCodeClaim, register.StoreCode),
            new Claim(RegisterIdentity.RegisterCodeClaim, register.RegisterCode)
        }, Scheme);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}

/// <summary>Who is calling, read from the API key's claims.</summary>
public sealed record RegisterIdentity(Guid TenantId, Guid StoreId, Guid RegisterId, string StoreCode, string RegisterCode)
{
    public const string TenantIdClaim = "tenant_id";
    public const string StoreIdClaim = "store_id";
    public const string RegisterIdClaim = "register_id";
    public const string StoreCodeClaim = "store_code";
    public const string RegisterCodeClaim = "register_code";

    public static RegisterIdentity From(ClaimsPrincipal user)
    {
        string Claim(string type) => user.FindFirstValue(type)
            ?? throw new InvalidOperationException($"Signed-in register has no '{type}' claim.");

        return new RegisterIdentity(
            Guid.Parse(Claim(TenantIdClaim)),
            Guid.Parse(Claim(StoreIdClaim)),
            Guid.Parse(Claim(RegisterIdClaim)),
            Claim(StoreCodeClaim),
            Claim(RegisterCodeClaim));
    }
}

/// <summary>
/// Platform admin endpoints (creating tenants, stores, activation codes) need the "X-Admin-Key"
/// header to match HeadOffice:AdminKey. With no key configured they are switched off.
/// This is the stand-in until the platform portal exists.
/// </summary>
public sealed class AdminKeyFilter : IEndpointFilter
{
    public const string Header = "X-Admin-Key";

    private readonly IConfiguration _configuration;

    public AdminKeyFilter(IConfiguration configuration) => _configuration = configuration;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = _configuration["HeadOffice:AdminKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 24)
            return Results.Problem("Admin endpoints are switched off: set HeadOffice:AdminKey (24+ characters).", statusCode: 403);

        var given = context.HttpContext.Request.Headers[Header].ToString();
        if (!Secrets.SameSecret(given, expected))
            return Results.Unauthorized();

        return await next(context);
    }
}

/// <summary>
/// Websites sign in with "Authorization: Bearer nwk_…". The key decides which business they can
/// read; it opens only the website endpoints (reports), never the register ones.
/// </summary>
public sealed class WebsiteApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "WebsiteApiKey";
    public const string Policy = "Website";
    public const string TenantIdClaim = "tenant_id";
    public const string WebsiteKeyIdClaim = "website_key_id";

    public WebsiteApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer nwk_", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var hash = Secrets.Hash(header["Bearer ".Length..].Trim());
        var db = Context.RequestServices.GetRequiredService<HeadOfficeDbContext>();

        var key = await db.WebsiteKeys.FirstOrDefaultAsync(k => k.ApiKeyHash == hash && !k.IsRevoked, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Unknown or revoked website key.");

        // Shown in admin so an unused key can be spotted and revoked; at most once a minute per key.
        if (key.LastUsedUtc is null || DateTime.UtcNow - key.LastUsedUtc > TimeSpan.FromMinutes(1))
        {
            key.LastUsedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(TenantIdClaim, key.TenantId.ToString()),
            new Claim(WebsiteKeyIdClaim, key.Id.ToString())
        }, Scheme);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    /// <summary>The business the signed-in website belongs to.</summary>
    public static Guid TenantOf(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(TenantIdClaim) ?? throw new InvalidOperationException("Signed-in website has no tenant."));
}
