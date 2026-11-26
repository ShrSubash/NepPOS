using NepPOS.Application.Maintenance;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Application.Sales;

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

/// <summary>
/// What happens to the customer's EFTPOS card slip after a card payment or card refund.
/// The terminal integration (Spice/SCI) hands both slips to the POS; the POS decides whether to print.
/// Both slips are always saved with the payment, so they can be printed later from the Journal.
/// </summary>
public enum CardSlipMode
{
    /// <summary>Show a "Print card slip" button on the finished screen; nothing prints unless the cashier taps it.</summary>
    Ask = 0,

    /// <summary>Print the customer's slip straight after every card payment.</summary>
    Print = 1,

    /// <summary>Never print at the register; slips stay in the sale history (Journal) to print when asked for.</summary>
    DontPrint = 2
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

    /// <summary>
    /// Default for the payment screen's 🧾 Receipt toggle: print after each sale or not.
    /// The cashier can flip it per sale; "Print receipt" stays available after the sale either way.
    /// </summary>
    public bool AutoPrint { get; init; } = true;

    /// <summary>Print the EFTPOS merchant copy as soon as a card is approved (legacy behaviour; off by default).</summary>
    public bool PrintMerchantCopy { get; init; }

    /// <summary>Customer EFTPOS slip: ask the cashier (default), always print, or keep it in the history only.</summary>
    public CardSlipMode CustomerCardSlip { get; init; } = CardSlipMode.Ask;
}

/// <summary>Everything this register needs to know about its store and hardware.</summary>
public sealed record RegisterSettings
{
    public StoreProfile Store { get; init; } = new();
    public PrinterSettings Printer { get; init; } = new();

    /// <summary>Daily backups: second folder and how long to keep them (Manage → Backups &amp; updates).</summary>
    public BackupSettings Backup { get; init; } = new();

    /// <summary>Which sections the Reports screen and the Z print (ticked on the Reports screen).</summary>
    public ReportSections Reports { get; init; } = new();

    /// <summary>Close-day outputs (print / PDF / CSV) and the report folder, set on the Reports screen.</summary>
    public ReportOutput ReportOutput { get; init; } = new();

    /// <summary>Gift cards, store credit and owner-added tenders (Manage → Payment types).</summary>
    public PaymentTypeSettings PaymentTypes { get; init; } = new();

    /// <summary>Return period, refund limit, receipt note (Manage → Payment types).</summary>
    public ReturnSettings Returns { get; init; } = new();

    /// <summary>Sunday and public holiday surcharges (Manage → Surcharges).</summary>
    public SurchargeSettings Surcharges { get; init; } = new();

    /// <summary>Discounts by hand (Manage → Promotions).</summary>
    public DiscountSettings Discounts { get; init; } = new();

    /// <summary>Deli and meat labels with the price in the barcode (Manage → Till options).</summary>
    public Scanning.PriceBarcodeSettings PriceBarcodes { get; init; } = new();

    /// <summary>The screen facing the customer (Manage → Till options).</summary>
    public CustomerDisplaySettings CustomerDisplay { get; init; } = new();
}

/// <summary>A second screen that faces the customer: what they're buying, the total and their change.</summary>
public sealed record CustomerDisplaySettings
{
    public bool Enabled { get; init; }

    /// <summary>Shown in large type while no sale is going, e.g. "Welcome to Hamro Grocery".</summary>
    public string Welcome { get; init; } = "Welcome";

    /// <summary>Lines shown in turn while idle, one per line, e.g. this week's specials.</summary>
    public string IdleMessages { get; init; } = "";
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
/// <remarks>With <c>Barcode</c> set, <c>Text</c> is printed as a Code 128 barcode (with the code in text under it)
/// on a thermal printer, and as plain text elsewhere.</remarks>
public sealed record ReceiptLine(string Text, ReceiptAlign Align = ReceiptAlign.Left, bool Bold = false, bool Large = false, bool Barcode = false);

/// <summary>A receipt laid out as lines of at most <see cref="LineWidth"/> characters — printer-independent.</summary>
public sealed record ReceiptDocument(int LineWidth, IReadOnlyList<ReceiptLine> Lines);

/// <summary>
/// Outcome of a hardware call. Hardware problems are reported, never thrown, so they can't break a sale.
/// <see cref="Message"/> is the error when it failed, or extra info when it worked (e.g. the PDF file saved).
/// </summary>
public sealed record HardwareResult(bool Succeeded, string? Message = null)
{
    public static HardwareResult Ok() => new(true);
    public static HardwareResult Failed(string error) => new(false, error);
}
