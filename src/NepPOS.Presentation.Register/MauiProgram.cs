using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Serilog;
using Serilog.Events;
using NepPOS.Application.Authentication;
using NepPOS.Application.Catalog;
using NepPOS.Application.Email;
using NepPOS.Application.Scanning;
using NepPOS.Application.Sync;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Infrastructure.Adapters.Email;
using NepPOS.Infrastructure.Adapters.Hardware;
using NepPOS.Infrastructure.Adapters.Payments;
using NepPOS.Infrastructure.Adapters.Sync;
using NepPOS.Infrastructure.Adapters.Reports;
using NepPOS.Infrastructure.Database;
using NepPOS.Infrastructure.Database.Authentication;
using NepPOS.Infrastructure.Database.Catalog;
using NepPOS.Infrastructure.Database.Scanning;
using NepPOS.Infrastructure.Database.Seeding;
using NepPOS.Infrastructure.Integration.HeadOffice;
using NepPOS.Presentation.Register.Services;

namespace NepPOS.Presentation.Register;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureLifecycleEvents(events => events.AddWindows(windows =>
                windows.OnWindowCreated(ScreenMode.Apply)))
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
        builder.Services.AddScoped<IScanResolver, ReceiptScanResolver>();
        builder.Services.AddScoped<IScanResolver, CustomerScanResolver>();
        builder.Services.AddScoped<ScanResolutionService>();
        builder.Services.AddScoped<IProductManagementService, ProductManagementService>();
        builder.Services.AddScoped<NepPOS.Application.Stock.IStockReceivingService, NepPOS.Infrastructure.Database.Stock.StockReceivingService>();

        // Checkout: this register's identity, saving paid sales, and the EFTPOS terminal.
        builder.Services.AddSingleton(new RegisterTerminal(StoreId: "STORE-001", RegisterId: "REG-001"));
        builder.Services.AddScoped<ICheckoutService, CheckoutService>();

        // Returns (scan the receipt) and gift cards / store credit (balances kept on this register).
        builder.Services.AddScoped<NepPOS.Application.Sales.IReturnsService, NepPOS.Infrastructure.Database.Sales.ReturnsService>();
        builder.Services.AddScoped<NepPOS.Application.Sales.IGiftCardService, NepPOS.Infrastructure.Database.Sales.GiftCardService>();
        builder.Services.AddScoped<NepPOS.Application.Customers.ICustomerService, NepPOS.Infrastructure.Database.Customers.CustomerService>();
        builder.Services.AddScoped<NepPOS.Application.Suppliers.ISupplierAccountService, NepPOS.Infrastructure.Database.Suppliers.SupplierAccountService>();
        builder.Services.AddScoped<NepPOS.Application.Stock.IWastageService, NepPOS.Infrastructure.Database.Stock.WastageService>();
        builder.Services.AddScoped<NepPOS.Application.Stock.IStocktakeService, NepPOS.Infrastructure.Database.Stock.StocktakeService>();
        builder.Services.AddScoped<NepPOS.Application.Stock.IPurchaseOrderService, NepPOS.Infrastructure.Database.Stock.PurchaseOrderService>();
        builder.Services.AddScoped<NepPOS.Application.Promotions.IPromotionService, NepPOS.Infrastructure.Database.Promotions.PromotionService>();
        builder.Services.AddScoped<NepPOS.Application.Sales.IHeldSaleService, NepPOS.Infrastructure.Database.Sales.HeldSaleService>();
        builder.Services.AddScoped<IStockPublisher, NepPOS.Infrastructure.Database.Sync.StockPublisher>();
        builder.Services.AddScoped<NepPOS.Application.Orders.IWebOrderService, NepPOS.Infrastructure.Database.Orders.WebOrderService>();
        builder.Services.AddScoped<NepPOS.Application.Orders.IStockTransferService, NepPOS.Infrastructure.Database.Orders.StockTransferService>();
        // EFTPOS: mx51 Spice running on this PC.
        // No terminal handy? Swap SpicePaymentGateway for SimulatedPaymentGateway on the line below.
        builder.Services.AddSingleton(new SpiceOptions(Host: "localhost", Port: 8282, PosId: ""));
        builder.Services.AddSingleton<IPaymentGateway, SpicePaymentGateway>();

        // Receipt & printer settings (per register) and the installed-printer list.
        builder.Services.AddSingleton<IRegisterSettingsStore, JsonRegisterSettingsStore>();
        builder.Services.AddSingleton<IPrinterDirectory, WindowsPrinterDirectory>();
        builder.Services.AddSingleton<ILocalHardwareService, LocalHardwareService>();

        // Saved report files (PDF / CSV) - Manage → Reports & End of Day.
        builder.Services.AddSingleton<IReportFileStore, LocalReportFileStore>();

        // Email (Manage → Email): settings with the password encrypted, and an outbox that sends in the background.
        builder.Services.AddSingleton<IEmailSettingsStore, JsonEmailSettingsStore>();
        builder.Services.AddSingleton<FileEmailOutbox>();
        builder.Services.AddSingleton<IEmailOutbox>(sp => sp.GetRequiredService<FileEmailOutbox>());

        // Head Office (Manage → Head Office): the connection with its key encrypted, the HTTP client,
        // and the background loop that sends the outbox and a heartbeat.
        builder.Services.AddSingleton<IHeadOfficeConnectionStore, JsonHeadOfficeConnectionStore>();
        builder.Services.AddSingleton<IHeadOfficeClient>(_ => new HeadOfficeClient(HeadOfficeClient.CreateDefaultHttpClient()));
        builder.Services.AddScoped<ISharedDataStore, NepPOS.Infrastructure.Database.Sync.SharedDataStore>();
        builder.Services.AddScoped<SyncService>();
        builder.Services.AddSingleton<SyncWorker>();

        // Licence: the signed copy from Head Office, checked offline. Stops new sales when it lapses.
        builder.Services.AddSingleton<NepPOS.Application.Licensing.ILicenceStore, NepPOS.Infrastructure.Adapters.Licensing.JsonLicenceStore>();
        builder.Services.AddSingleton<NepPOS.Application.Licensing.LicenceGuard>();

        var app = builder.Build();

        // Sends anything left in the email outbox (e.g. a Z emailed while the internet was down).
        app.Services.GetRequiredService<FileEmailOutbox>().Start();

        // Sends anything waiting in the outbox once this register is connected to Head Office.
        app.Services.GetRequiredService<SyncWorker>().Start();

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