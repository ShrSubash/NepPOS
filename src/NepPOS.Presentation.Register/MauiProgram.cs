using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using NepPOS.Application.Authentication;
using NepPOS.Application.Catalog;
using NepPOS.Application.Scanning;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Infrastructure.Adapters.Hardware;
using NepPOS.Infrastructure.Adapters.Payments;
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

        // ---- Logging: daily files in %LOCALAPPDATA%\NepPOS\Logs, kept 30 days ----
        // Every ILogger call in the app (and Blazor/EF errors) lands here, so problems
        // in a store can be diagnosed after the fact.
        var logFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "Logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)   // framework noise: warnings and errors only
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logFolder, "neppos-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: 20_000_000,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.AddSerilog(dispose: true);
        GlobalExceptionLogging.Register();

        Log.Information("NepPOS register starting. Version {Version}",
            typeof(MauiProgram).Assembly.GetName().Version);

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
        builder.Services.AddSingleton<ICurrentOperator>(sp => sp.GetRequiredService<CurrentUserSession>());
        builder.Services.AddScoped<IAuthenticationService, PinAuthenticationService>();
        builder.Services.AddScoped<IProductCatalogService, ProductCatalogService>();
        builder.Services.AddSingleton<OnScreenKeyboardService>();
        builder.Services.AddScoped<IScanResolver, ProductScanResolver>();
        builder.Services.AddScoped<IScanResolver, VoucherScanResolver>();
        builder.Services.AddScoped<IScanResolver, CustomerScanResolver>();
        builder.Services.AddScoped<ScanResolutionService>();
        builder.Services.AddScoped<IProductManagementService, ProductManagementService>();

        // Checkout: this register's identity, saving paid sales, and the EFTPOS terminal.
        builder.Services.AddSingleton(new RegisterTerminal(StoreId: "STORE-001", RegisterId: "REG-001"));
        builder.Services.AddScoped<ICheckoutService, CheckoutService>();
        // EFTPOS: mx51 Spice running on this PC.
        // No terminal handy? Swap SpicePaymentGateway for SimulatedPaymentGateway on the line below.
        builder.Services.AddSingleton(new SpiceOptions(Host: "localhost", Port: 8282, PosId: ""));
        builder.Services.AddSingleton<IPaymentGateway, SpicePaymentGateway>();

        // Receipt & printer settings (per register) and the installed-printer list.
        builder.Services.AddSingleton<IRegisterSettingsStore, JsonRegisterSettingsStore>();
        builder.Services.AddSingleton<IPrinterDirectory, WindowsPrinterDirectory>();
        builder.Services.AddSingleton<ILocalHardwareService, LocalHardwareService>();

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