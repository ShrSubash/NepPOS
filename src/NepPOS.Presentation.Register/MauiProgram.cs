using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Authentication;
using NepPOS.Application.Catalog;
using NepPOS.Application.Scanning;
using NepPOS.Infrastructure.Database;
using NepPOS.Infrastructure.Database.Authentication;
using NepPOS.Infrastructure.Database.Catalog;
using NepPOS.Infrastructure.Database.Scanning;
using NepPOS.Infrastructure.Database.Seeding;
using NepPOS.Presentation.Register.Services;

namespace NepPOS.Presentation.Register;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // SQLite file lives in the app's local data directory —
        // survives app updates, isolated per-device, matches the
        // Slave Register offline-first design.
        var appDataFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "NepPOS");

        Directory.CreateDirectory(appDataFolder);

        var dbPath = Path.Combine(appDataFolder, "neppos-register.db");
        var connectionString = $"Data Source={dbPath}";
        System.Diagnostics.Debug.WriteLine($">>> NEPPOS DB PATH: {dbPath}");

        builder.Services.AddRegisterDatabase(connectionString);

        // Session + auth wiring for the login flow
        builder.Services.AddSingleton<CurrentUserSession>();
        builder.Services.AddScoped<IAuthenticationService, PinAuthenticationService>();
        builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
        builder.Services.AddSingleton<OnScreenKeyboardService>();
        builder.Services.AddScoped<IScanResolver, ProductScanResolver>();
        builder.Services.AddScoped<IScanResolver, VoucherScanResolver>();
        builder.Services.AddScoped<IScanResolver, CustomerScanResolver>();
        builder.Services.AddScoped<ScanResolutionService>();
        builder.Services.AddScoped<IProductManagementService, ProductManagementService>();

        var app = builder.Build();

        // Apply pending migrations and seed test data on startup.
        // CreateMauiApp() is synchronous, so block on the async work here
        // rather than changing the method signature the MAUI host expects.
        using (var scope = app.Services.CreateScope())
        {
            var workforceDb = scope.ServiceProvider.GetRequiredService<WorkforceDbContext>();
            var registerDb = scope.ServiceProvider.GetRequiredService<NepPosDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<App>>();

            try
            {
                registerDb.Database.Migrate();
                workforceDb.Database.Migrate();
#if DEBUG
                // Dev-only test logins. Release builds must create the owner via first-run setup.
                WorkforceDbSeeder.SeedAsync(workforceDb, logger).GetAwaiter().GetResult();
                RegisterDbSeeder.SeedAsync(registerDb, logger).GetAwaiter().GetResult();
#endif
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Database migration/seed failed at startup.");
                // Deliberately not rethrowing — a register terminal should still
                // launch and show a clear error state rather than crash silently.
            }
        }

        return app;
    }
}