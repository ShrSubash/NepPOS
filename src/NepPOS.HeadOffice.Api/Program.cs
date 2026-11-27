using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Endpoints;
using NepPOS.HeadOffice.Api.Licensing;
using NepPOS.HeadOffice.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// ---- Database: a SQLite file on a developer PC, SQL Server (Azure SQL) in the cloud ----
// HeadOffice:DatabaseProvider = "Sqlite" | "SqlServer"; ConnectionStrings:HeadOffice = where.
// Read when the database is first used, so tests and Azure settings always apply.
builder.Services.AddDbContext<HeadOfficeDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("HeadOffice")
        ?? throw new InvalidOperationException("Set ConnectionStrings:HeadOffice.");

    if (string.Equals(configuration["HeadOffice:DatabaseProvider"], "SqlServer", StringComparison.OrdinalIgnoreCase))
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    else
        options.UseSqlite(connectionString);
});

// ---- Sign-in: registers and websites use their own kinds of key; admin endpoints check X-Admin-Key ----
builder.Services.AddAuthentication(RegisterApiKeyHandler.Scheme)
    .AddScheme<AuthenticationSchemeOptions, RegisterApiKeyHandler>(RegisterApiKeyHandler.Scheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, WebsiteApiKeyHandler>(WebsiteApiKeyHandler.Scheme, _ => { });

// Register endpoints use the default (register key); website endpoints accept only a website key.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(WebsiteApiKeyHandler.Policy, policy => policy
        .AddAuthenticationSchemes(WebsiteApiKeyHandler.Scheme)
        .RequireClaim(WebsiteApiKeyHandler.WebsiteKeyIdClaim));
builder.Services.AddScoped<AdminKeyFilter>();

// ---- Licences: signed with HeadOffice:LicenceSigningKey (required in production) ----
builder.Services.AddSingleton<LicenceKeyRing>();
builder.Services.AddScoped<LicenceService>();

// Same JSON as the events themselves (camelCase, enums as text).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in SyncJson.Options.Converters)
        options.SerializerOptions.Converters.Add(converter);
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

// A SQLite file is created on first run, and gets any tables added since. SQL Server uses migrations (added when Head Office is deployed).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HeadOfficeDbContext>();
    if (db.Database.IsSqlite() && SqliteDevSchema.IsOldDeveloperDatabase(db))
    {
        // A headoffice.db made on a developer PC before migrations existed: keep it working as before.
        db.Database.EnsureCreated();
        SqliteDevSchema.AddMissingTables(db, app.Logger);
    }
    else
    {
        // Every new database (the server, tests, a fresh developer PC) is built and upgraded by migrations
        // (Data/Migrations), so a release that changes a table updates the live database without losing data.
        db.Database.Migrate();
    }

    // Head Office's copy of customers, accounts and gift cards, built once from events stored before it existed.
    NepPOS.HeadOffice.Api.Accounts.SharedAccounts.RebuildIfEmptyAsync(db).GetAwaiter().GetResult();

    // The first NepPOS Portal owner, from settings, when there are no staff yet.
    PortalEndpoints.CreateFirstOwner(db, app.Configuration, app.Logger);
}

// Behind the web server (Caddy, deploy/headoffice/README.md): trust its X-Forwarded-* headers so HTTPS is seen
// as HTTPS. Kestrel only listens on localhost in production, so only the web server can set them.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// The API description, for docs/api and for whoever builds against it: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", timeUtc = DateTime.UtcNow })).WithTags("Health");
app.MapAdminEndpoints();
app.MapPortalEndpoints();
app.MapRegisterEndpoints();
app.MapWebsiteEndpoints();

app.Run();

/// <summary>Lets the tests start the whole API in memory (WebApplicationFactory).</summary>
public partial class Program;
