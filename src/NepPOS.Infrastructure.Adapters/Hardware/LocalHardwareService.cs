using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>
/// Receipt printer + cash drawer, routed by this register's settings (Manage → Receipt &amp; Printer).
/// Never throws: every problem comes back as <see cref="HardwareResult.Failed"/> and is logged,
/// because a jammed printer must never fail or undo a sale that is already saved.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalHardwareService : ILocalHardwareService
{
    // One job at a time: two receipts interleaving on the same printer would be unreadable.
    private static readonly SemaphoreSlim PrinterLock = new(1, 1);

    private readonly IRegisterSettingsStore _settings;
    private readonly ILogger<LocalHardwareService> _logger;

    public LocalHardwareService(IRegisterSettingsStore settings, ILogger<LocalHardwareService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HardwareResult> PrintReceiptAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var printer = _settings.Load().Printer;
        if (printer.Mode == PrinterMode.None)
            return HardwareResult.Failed("No receipt printer is set up (Manage → Receipt & Printer).");

        return await RunAsync("print receipt", printer.PrinterName, () =>
        {
            if (printer.Mode == PrinterMode.Thermal)
            {
                RawPrinterQueue.Send(printer.PrinterName, EscPosRenderer.Render(receipt), "NepPOS receipt");
                return null;
            }

            var file = WindowsReceiptPrinter.Print(printer.PrinterName, receipt, "NepPOS receipt");
            return file is null ? null : $"Saved to {file}";
        }, cancellationToken);
    }

    public async Task<HardwareResult> OpenCashDrawerAsync(CancellationToken cancellationToken = default)
    {
        var printer = _settings.Load().Printer;
        if (printer.DrawerMode == CashDrawerMode.None)
            return HardwareResult.Failed("No cash drawer is set up (Manage → Receipt & Printer).");

        return await RunAsync("open cash drawer", printer.PrinterName, () =>
        {
            RawPrinterQueue.Send(printer.PrinterName, EscPosRenderer.DrawerKick, "NepPOS drawer");
            return null;
        }, cancellationToken);
    }

    private async Task<HardwareResult> RunAsync(string action, string printerName, Func<string?> work, CancellationToken cancellationToken)
    {
        await PrinterLock.WaitAsync(cancellationToken);
        try
        {
            // Spooler calls block; keep them off the UI thread.
            var note = await Task.Run(work, cancellationToken);
            _logger.LogInformation("Hardware: {Action} on '{Printer}' OK {Note}", action, printerName, note);
            return new HardwareResult(true, note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardware: {Action} on '{Printer}' failed", action, printerName);
            return HardwareResult.Failed($"Could not {action}: {ex.Message}");
        }
        finally
        {
            PrinterLock.Release();
        }
    }
}
