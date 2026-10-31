namespace NepPOS.Application.Hardware;

/// <summary>
/// Register hardware (receipt printer, cash drawer). Implemented in Infrastructure.Adapters;
/// which printer and how is decided by <see cref="RegisterSettings"/>, not by callers.
/// Methods never throw for hardware faults — they return <see cref="HardwareResult.Failed"/>
/// so a jammed printer can never fail or undo a completed sale.
/// </summary>
public interface ILocalHardwareService
{
    Task<HardwareResult> PrintReceiptAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default);

    Task<HardwareResult> OpenCashDrawerAsync(CancellationToken cancellationToken = default);
}

/// <summary>Loads and saves this register's settings (store details + printer).</summary>
public interface IRegisterSettingsStore
{
    RegisterSettings Load();

    void Save(RegisterSettings settings);
}

/// <summary>Printers installed on this PC, for the settings screen's printer list.</summary>
public interface IPrinterDirectory
{
    IReadOnlyList<string> GetInstalledPrinters();
}
