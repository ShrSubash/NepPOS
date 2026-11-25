using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using NepPOS.Contracts.Admin;
using NepPOS.Portal.Components;
using NepPOS.Portal.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// ---- Head Office: everything the Portal shows or changes goes through its admin API ----
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.AddHttpClient<HeadOfficeAdmin>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<PortalOptions>>().Value;
    if (Uri.TryCreate(options.HeadOfficeUrl.TrimEnd('/') + "/", UriKind.Absolute, out var url))
        client.BaseAddress = url;
    client.DefaultRequestHeaders.Add("X-Admin-Key", options.AdminKey);
    client.Timeout = TimeSpan.FromSeconds(20);
});

// ---- Sign-in: a cookie for NepPOS staff only, 8 hours, renewed while in use ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "neppos-portal";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/account/sign-in";
        options.AccessDeniedPath = "/account/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorizationBuilder()
    // Every page needs a signed-in person who has finished two-step setup...
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !context.User.HasClaim(PortalClaims.TwoFactorSetup, "required"))
        .Build())
    // ...except the two-step setup page itself.
    .AddPolicy(PortalPolicies.SignedIn, policy => policy.RequireAuthenticatedUser())
    .AddPolicy(PortalPolicies.Owner, policy => policy
        .RequireRole(StaffRoles.Owner)
        .RequireAssertion(context => !context.User.HasClaim(PortalClaims.TwoFactorSetup, "required")));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Behind the web server (Caddy, deploy/portal/README.md): trust its X-Forwarded-* headers so HTTPS is seen as
// HTTPS (no redirect loop, secure cookies). Kestrel only listens on localhost in production.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// A sign-in (or two-step) form left open while the Portal restarted, or brought back from the browser's
// history, carries a stale security token. Instead of a bare "antiforgery token" error page, send the
// person back to a fresh form with a short note. Any other form keeps the normal protection.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.StartsWithSegments("/account")
        && context.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>() is { IsValid: false })
    {
        var returnUrl = context.Request.Query["ReturnUrl"].ToString();
        var target = context.Request.Path.Value + "?expired=true"
            + (returnUrl.Length > 0 ? "&ReturnUrl=" + Uri.EscapeDataString(returnUrl) : "");
        context.Response.Redirect(target);
        return;
    }
    await next();
});

app.MapPost("/account/sign-out", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/sign-in");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
