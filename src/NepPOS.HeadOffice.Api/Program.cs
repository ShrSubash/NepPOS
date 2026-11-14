using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Endpoints;
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
    if (db.Database.IsSqlite())
    {
        db.Database.EnsureCreated();
        SqliteDevSchema.AddMissingTables(db, app.Logger);
    }
    else
        db.Database.Migrate();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// The API description, for docs/api and for whoever builds against it: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", timeUtc = DateTime.UtcNow })).WithTags("Health");
app.MapAdminEndpoints();
app.MapRegisterEndpoints();
app.MapWebsiteEndpoints();

app.Run();

/// <summary>Lets the tests start the whole API in memory (WebApplicationFactory).</summary>
public partial class Program;
