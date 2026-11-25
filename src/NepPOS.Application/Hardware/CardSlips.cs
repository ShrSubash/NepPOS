using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Hardware;

/// <summary>
/// Which EFTPOS card slips a sale or refund has, and which ones the register should print by itself.
/// The terminal returns a customer and a merchant slip for every card transaction; they are saved with the
/// payment, and the register prints the customer's copy according to <see cref="PrinterSettings.CustomerCardSlip"/>.
/// </summary>
public static class CardSlips
{
    /// <summary>Customer slips saved with these payments (for "Print card slip" and Journal reprints).</summary>
    public static IReadOnlyList<string> CustomerCopies(IEnumerable<SalePayment> payments) =>
        CardPayments(payments).Select(p => p.CustomerReceipt).OfType<string>().ToList();

    /// <summary>Merchant slips saved with these payments (Journal reprint, e.g. for a dispute).</summary>
    public static IReadOnlyList<string> MerchantCopies(IEnumerable<SalePayment> payments) =>
        CardPayments(payments).Select(p => p.MerchantReceipt).OfType<string>().ToList();

    /// <summary>
    /// Customer slips to print straight away: only in <see cref="CardSlipMode.Print"/> mode, and never one the terminal
    /// already printed itself (the customer would get two).
    /// </summary>
    public static IReadOnlyList<string> CustomerCopiesToPrintNow(IEnumerable<SalePayment> payments, PrinterSettings printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (printer.Mode == PrinterMode.None || printer.CustomerCardSlip != CardSlipMode.Print)
            return Array.Empty<string>();

        return CardPayments(payments)
            .Where(p => p.CustomerReceipt is not null && !p.CustomerReceiptPrintedByTerminal)
            .Select(p => p.CustomerReceipt!)
            .ToList();
    }

    private static IEnumerable<SalePayment> CardPayments(IEnumerable<SalePayment> payments) =>
        (payments ?? throw new ArgumentNullException(nameof(payments))).Where(p => p.TenderType == TenderType.Eftpos);
}
