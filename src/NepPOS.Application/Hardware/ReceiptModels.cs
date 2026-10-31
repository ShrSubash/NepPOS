namespace NepPOS.Application.Hardware;

/// <summary>Business details printed at the top of every tax invoice.</summary>
public sealed record StoreProfile
{
    public string BusinessName { get; init; } = "";

    /// <summary>11-digit ABN. Printed as "ABN 12 345 678 901". Required for a valid tax invoice.</summary>
    public string Abn { get; init; } = "";

    public IReadOnlyList<string> AddressLines { get; init; } = Array.Empty<string>();
    public string Phone { get; init; } = "";

    /// <summary>Optional closing line, e.g. "Thank you - please drink responsibly".</summary>
    public string Footer { get; init; } = "";
}

/// <summary>How this register prints receipts.</summary>
public enum PrinterMode
{
    None = 0,

    /// <summary>Raw ESC/POS bytes to the Windows printer queue (Epson TM, Star, Bixolon, generic thermal).</summary>
    Thermal = 1,

    /// <summary>Normal Windows printing through the printer's driver (any printer).</summary>
    Windows = 2
}

/// <summary>How the cash drawer is opened.</summary>
public enum CashDrawerMode
{
    None = 0,

    /// <summary>Drawer is plugged into the receipt printer's RJ11 port; opened with the ESC/POS drawer kick.</summary>
    ViaPrinter = 1
}

/// <summary>Per-register hardware settings (each PC has its own printer).</summary>
public sealed record PrinterSettings
{
    public PrinterMode Mode { get; init; } = PrinterMode.None;

    /// <summary>Windows printer name exactly as shown in Settings → Printers &amp; scanners.</summary>
    public string PrinterName { get; init; } = "";

    /// <summary>Characters per line: 48 for 80 mm paper, 32 for 58 mm.</summary>
    public int LineWidth { get; init; } = 48;

    public CashDrawerMode DrawerMode { get; init; } = CashDrawerMode.None;

    /// <summary>Print a receipt automatically after every sale (otherwise only on Reprint).</summary>
    public bool AutoPrint { get; init; } = true;
}

/// <summary>Everything this register needs to know about its store and hardware.</summary>
public sealed record RegisterSettings
{
    public StoreProfile Store { get; init; } = new();
    public PrinterSettings Printer { get; init; } = new();
}

public enum ReceiptAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// One printed line. Large = double HEIGHT only (same width), so a Large line still fits
/// <see cref="ReceiptDocument.LineWidth"/> characters on every printer.
/// </summary>
public sealed record ReceiptLine(string Text, ReceiptAlign Align = ReceiptAlign.Left, bool Bold = false, bool Large = false);

/// <summary>A receipt laid out as lines of at most <see cref="LineWidth"/> characters — printer-independent.</summary>
public sealed record ReceiptDocument(int LineWidth, IReadOnlyList<ReceiptLine> Lines);

/// <summary>Outcome of a hardware call. Hardware problems are reported, never thrown, so they can't break a sale.</summary>
public sealed record HardwareResult(bool Succeeded, string? Error = null)
{
    public static HardwareResult Ok() => new(true);
    public static HardwareResult Failed(string error) => new(false, error);
}
