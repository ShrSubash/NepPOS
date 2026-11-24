using System.Globalization;
using System.Text.RegularExpressions;
using NepPOS.Domain.Entities;
using NepPOS.Application.Customers;
using NepPOS.Application.Reports;
using NepPOS.Application.Sales;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Hardware;

/// <summary>
/// Lays out a completed sale as an Australian tax invoice (ATO, sales under $1,000):
/// "TAX INVOICE", seller name + ABN, date, each item with quantity and price,
/// the GST amount, and "Total price includes GST". GST-free lines are marked with *.
/// Pure text — no printer code — so it is fully unit tested and shared by every printer.
/// </summary>
public static class ReceiptBuilder
{
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    /// <param name="receiptNote">Printed under the barcode (e.g. "Keep this receipt for returns and exchanges").</param>
    public static ReceiptDocument Build(SaleTransaction sale, StoreProfile store, int lineWidth, bool reprint = false, string? receiptNote = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(store);
        if (lineWidth < 24)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "Receipts need at least 24 characters per line.");
        if (sale.Status != SaleTransactionStatus.Completed || sale.InvoiceNumber is null)
            throw new InvalidOperationException("Only completed sales can be printed as tax invoices.");

        var lines = new List<ReceiptLine>();
        var separator = new ReceiptLine(new string('-', lineWidth));

        // ---- seller ----
        if (!string.IsNullOrWhiteSpace(store.BusinessName))
            lines.Add(new ReceiptLine(store.BusinessName.Trim(), ReceiptAlign.Center, Bold: true, Large: true));
        foreach (var addressLine in store.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)))
            lines.Add(new ReceiptLine(addressLine.Trim(), ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(store.Phone))
            lines.Add(new ReceiptLine("Ph " + store.Phone.Trim(), ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(store.Abn))
            lines.Add(new ReceiptLine("ABN " + FormatAbn(store.Abn), ReceiptAlign.Center));

        lines.Add(new ReceiptLine(""));
        // A refund is an adjustment note (it reduces GST already paid); an exchange that's paid for stays a tax invoice.
        var heading = sale.IsRefund ? "REFUND - ADJUSTMENT NOTE" : sale.HasReturns ? "TAX INVOICE - EXCHANGE" : "TAX INVOICE";
        lines.Add(new ReceiptLine(reprint ? heading + " (REPRINT)" : heading, ReceiptAlign.Center, Bold: true));
        if (sale.ReturnOfInvoice is { } original)
            lines.Add(new ReceiptLine($"Return of invoice #{original}", ReceiptAlign.Center));
        lines.Add(new ReceiptLine(""));

        // ---- invoice details ----
        var local = sale.CreatedAtUtc.ToLocalTime();
        lines.Add(TwoColumn($"Invoice #{sale.InvoiceNumber}", local.ToString("dd/MM/yyyy HH:mm", Au), lineWidth));
        var registerLine = string.IsNullOrWhiteSpace(sale.EmployeeName)
            ? $"Register {sale.RegisterId}"
            : $"Register {sale.RegisterId} - {sale.EmployeeName}";
        lines.Add(new ReceiptLine(registerLine.Length > lineWidth ? registerLine[..lineWidth] : registerLine));
        if (!string.IsNullOrWhiteSpace(sale.CustomerName))
            foreach (var part in Wrap("Customer: " + sale.CustomerName.Trim(), lineWidth))
                lines.Add(new ReceiptLine(part));
        lines.Add(separator);

        // ---- items ----
        var hasGstFree = false;
        foreach (var item in sale.LineItems)
        {
            // Gift cards and account payments carry no GST but aren't "GST-free goods", so they get no star.
            var gstFree = item.GstRate == 0 && item.GiftCardCode is null && !AccountPayment.IsAccountPayment(item);
            hasGstFree |= gstFree;
            var name = (gstFree ? "*" : "") + (item.IsReturn ? "RETURN " : "") + item.ProductName;
            // Full price here; any discount gets its own line underneath.
            var lineTotal = Money(item.UnitPriceIncGst * item.Quantity);

            if (item.Quantity == 1)
            {
                AddWrapped(lines, name, lineTotal, lineWidth);
            }
            else
            {
                foreach (var part in Wrap(name, lineWidth))
                    lines.Add(new ReceiptLine(part));
                lines.Add(TwoColumn($"  {item.Quantity} x {Money(item.UnitPriceIncGst)}", lineTotal, lineWidth));
            }
            if (item.IsReturn && item.ReturnReason is { } reason)
                lines.Add(new ReceiptLine($"  Reason: {reason}"));
            if (item.Discount > 0)
                lines.Add(TwoColumn($"  {item.DiscountReason ?? "Discount"}", "-" + Money(item.Discount), lineWidth));
        }

        lines.Add(separator);

        // ---- totals ----
        var total = decimal.Round(sale.GrandTotal, 2, MidpointRounding.AwayFromZero);
        var gst = decimal.Round(sale.GstTotal, 2, MidpointRounding.AwayFromZero);

        lines.Add(TwoColumn(total < 0 ? "TOTAL REFUND" : "TOTAL", Money(Math.Abs(total)), lineWidth, bold: true));
        lines.Add(TwoColumn(total < 0 ? "GST refunded" : "GST included", Money(Math.Abs(gst)), lineWidth));
        if (sale.CashRounding != 0)
            lines.Add(TwoColumn("Cash rounding", sale.CashRounding.ToString("+0.00;-0.00", Au), lineWidth));
        var saved = sale.LineItems.Sum(li => li.Discount);
        if (saved > 0)
            lines.Add(TwoColumn("You saved", Money(saved), lineWidth, bold: true));

        // ---- payments ----
        if (sale.Payments.Count > 0)
        {
            lines.Add(new ReceiptLine(""));
            foreach (var payment in sale.Payments)
                lines.Add(TwoColumn(TenderLabel(payment), Money(payment.IsRefund ? -payment.Amount : payment.Tendered), lineWidth));
            foreach (var payment in sale.Payments.Where(p => p.TenderType == TenderType.Cash && p.Change > 0))
                lines.Add(TwoColumn("Change", Money(payment.Change), lineWidth));
        }

        // ---- charged to an account: the customer signs the store's copy ----
        if (sale.Payments.Any(p => p.TenderType == TenderType.Account && !p.IsRefund))
        {
            lines.Add(new ReceiptLine(""));
            lines.Add(new ReceiptLine("Charged to account. Pay within your terms.", ReceiptAlign.Center));
            lines.Add(new ReceiptLine(""));
            lines.Add(new ReceiptLine("Signature " + new string('_', Math.Max(10, lineWidth - 10))));
        }

        // ---- GST statement (ATO) ----
        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine("Total price includes GST", ReceiptAlign.Center));
        if (hasGstFree)
            lines.Add(new ReceiptLine("* GST-free item", ReceiptAlign.Center));

        // ---- store credit issued, gift cards sold: their codes, scannable ----
        foreach (var credit in sale.Payments.Where(p => p.TenderType == TenderType.StoreCredit && p.IsRefund && p.Reference is not null))
            AddVoucher(lines, "STORE CREDIT", credit.Reference!, -credit.Amount, lineWidth);
        foreach (var card in sale.LineItems.Where(li => li.GiftCardCode is not null && li.Quantity > 0))
            AddVoucher(lines, "GIFT CARD", card.GiftCardCode!, card.LineTotalIncGst, lineWidth);

        if (!string.IsNullOrWhiteSpace(store.Footer))
        {
            lines.Add(new ReceiptLine(""));
            foreach (var part in Wrap(store.Footer.Trim(), lineWidth))
                lines.Add(new ReceiptLine(part, ReceiptAlign.Center));
        }

        // ---- the receipt's own barcode: scanned to find this sale for a return ----
        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine(ReceiptCode.For(sale.RegisterId, sale.InvoiceNumber!.Value), ReceiptAlign.Center, Barcode: true));
        if (!string.IsNullOrWhiteSpace(receiptNote))
            foreach (var part in Wrap(receiptNote.Trim(), lineWidth))
                lines.Add(new ReceiptLine(part, ReceiptAlign.Center));

        return new ReceiptDocument(lineWidth, lines);
    }

    /// <summary>"Cash", "EFTPOS Visa ...1234", "Gift card GC-123456", "Card refund", "Account"…</summary>
    public static string TenderLabel(SalePayment payment)
    {
        switch (payment.TenderType)
        {
            case TenderType.Cash:
                return payment.IsRefund ? "Cash refund" : "Cash";
            case TenderType.Eftpos:
                var card = string.Join(" ", new[] { payment.CardType, LastFour(payment.MaskedPan) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var label = payment.IsRefund ? "Card refund" : "EFTPOS";
                return string.IsNullOrEmpty(card) ? label : $"{label} {card}";
            case TenderType.GiftCard:
                return $"{(payment.IsRefund ? "To gift card" : "Gift card")} {payment.Reference}".Trim();
            case TenderType.StoreCredit:
                return $"{(payment.IsRefund ? "Store credit issued" : "Store credit")} {payment.Reference}".Trim();
            case TenderType.Account:
                return $"{(payment.IsRefund ? "Credited to account" : "On account")} {payment.Reference}".Trim();
            default:
                var name = payment.TenderName ?? "Other";
                return payment.IsRefund ? $"{name} refund" : name;
        }
    }

    /// <summary>A gift card or store credit: its name, value and code as a barcode, to scan when it's spent.</summary>
    private static void AddVoucher(List<ReceiptLine> lines, string title, string code, decimal amount, int lineWidth)
    {
        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine(new string('=', lineWidth)));
        lines.Add(new ReceiptLine($"{title} {Money(amount)}", ReceiptAlign.Center, Bold: true, Large: true));
        lines.Add(new ReceiptLine(code, ReceiptAlign.Center, Barcode: true));
        lines.Add(new ReceiptLine("Present this to spend it", ReceiptAlign.Center));
        lines.Add(new ReceiptLine(new string('=', lineWidth)));
    }

    /// <summary>
    /// Lays out the bank's EFTPOS slip (already formatted by the terminal, ~20 characters wide)
    /// centred on the receipt paper. Lines wider than the paper are cut, never wrapped,
    /// so the terminal's own layout is kept.
    /// </summary>
    public static ReceiptDocument BuildCardSlip(string slipText, int lineWidth)
    {
        if (lineWidth < 24)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "Receipts need at least 24 characters per line.");

        var lines = (slipText ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        // Drop blank lines at the top and bottom; keep the ones inside the slip.
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var blockWidth = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
        var indent = new string(' ', Math.Max(0, (lineWidth - blockWidth) / 2));

        var receiptLines = lines
            .Select(l => indent + l)
            .Select(l => new ReceiptLine(l.Length > lineWidth ? l[..lineWidth] : l))
            .ToList();

        return new ReceiptDocument(lineWidth, receiptLines);
    }

    /// <summary>
    /// Lays out an X reading, Z report or weekly/monthly/yearly report for the receipt printer.
    /// Sales, tenders and cash in drawer always print; the other sections follow <paramref name="sections"/>
    /// (ticked on the Reports screen). All sections print when it is null.
    /// </summary>
    public static ReceiptDocument BuildEndOfDay(EndOfDayReport report, StoreProfile store, int lineWidth, ReportSections? sections = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(store);
        if (lineWidth < 24)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "Receipts need at least 24 characters per line.");

        sections ??= new ReportSections { Products = true };

        var lines = new List<ReceiptLine>();
        var separator = new ReceiptLine(new string('-', lineWidth));
        void Row(string left, string right, bool bold = false) => lines.Add(TwoColumn(left, right, lineWidth, bold));
        void Section(string heading)
        {
            lines.Add(separator);
            lines.Add(new ReceiptLine(heading, ReceiptAlign.Left, Bold: true));
        }

        // ---- header ----
        if (!string.IsNullOrWhiteSpace(store.BusinessName))
            lines.Add(new ReceiptLine(store.BusinessName.Trim(), ReceiptAlign.Center, Bold: true));
        var isPeriod = report.Title is not null;
        var title = isPeriod ? report.Title! : report.IsZ ? $"Z REPORT #{report.ZNumber}" : "X READING";
        var subtitle = isPeriod
            ? report.Days.Count == 0
                ? "No closed days in this period"
                : $"Z #{report.Days[0].ZNumber} - #{report.Days[^1].ZNumber} ({report.Days.Count} days)"
            : report.IsZ
                ? report.IsReprint ? "REPRINT - day already closed" : "Day closed - totals reset"
                : "Totals so far - nothing reset";

        lines.Add(new ReceiptLine(title, ReceiptAlign.Center, Bold: true, Large: true));
        lines.Add(new ReceiptLine(subtitle, ReceiptAlign.Center));
        lines.Add(new ReceiptLine(""));
        Row("Register", report.RegisterId);
        if (isPeriod && report.RangeFrom is { } rangeFrom && report.RangeTo is { } rangeTo)
        {
            // Trading days, e.g. a monthly report is 01/10/2026 to 31/10/2026.
            Row("From", rangeFrom.ToString("ddd dd/MM/yyyy", CultureInfo.InvariantCulture));
            Row("To", rangeTo.ToString("ddd dd/MM/yyyy", CultureInfo.InvariantCulture));
        }
        else
        {
            Row("From", report.PeriodStartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Au));
            Row("To", report.PeriodEndUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Au));
        }
        if (!string.IsNullOrWhiteSpace(report.PrintedBy))
            Row("Printed by", report.PrintedBy);

        // ---- sales (always) ----
        Section("SALES");
        Row("Sales", report.SaleCount.ToString(Au));
        if (report.FirstInvoice is not null)
            Row("Invoices", report.FirstInvoice == report.LastInvoice
                ? $"#{report.FirstInvoice}"
                : $"#{report.FirstInvoice} - #{report.LastInvoice}");
        Row("Items sold", report.ItemCount.ToString(Au));
        if (report.RefundCount > 0)
            Row($"Refunds ({report.RefundCount})", Money(-report.RefundAmount));
        Row("Gross sales (inc GST)", Money(report.GrossSales));
        Row("GST", Money(report.Gst));
        Row("Net sales (ex GST)", Money(report.NetSales));
        if (report.CashRounding != 0)
            Row("Cash rounding", report.CashRounding.ToString("+0.00;-0.00", Au));

        // ---- tenders and cash (always) ----
        Section("TENDERS");
        foreach (var tender in report.Tenders)
            Row($"{tender.Label} ({tender.Count})", Money(tender.Amount));
        Row("TOTAL TAKEN", Money(report.TotalTaken), bold: true);
        lines.Add(separator);
        Row("CASH IN DRAWER", Money(report.CashExpected), bold: true);
        lines.Add(new ReceiptLine("(excludes opening float)"));

        // ---- what was sold ----
        if (sections.Departments && report.Departments.Count > 0)
        {
            Section("DEPARTMENTS");
            foreach (var department in report.Departments)
                Row($"{department.Department} ({department.Quantity})", Money(department.Amount));
        }

        if (sections.Products && report.Products.Count > 0)
        {
            Section("PLU SALES");
            foreach (var product in report.Products)
                Row($"{product.Quantity} x {product.Name}", Money(product.Amount));
        }

        if (sections.Hourly && report.Hours.Count > 0)
        {
            Section("HOURLY");
            foreach (var hour in report.Hours)
                Row($"{hour.Hour:00}:00-{(hour.Hour + 1) % 24:00}:00 ({hour.SaleCount})", Money(hour.Amount));
        }

        if (sections.Employees && report.ByEmployee.Count > 0)
        {
            Section("BY EMPLOYEE");
            foreach (var employee in report.ByEmployee)
                Row($"{employee.Employee} ({employee.SaleCount})", Money(employee.Amount));
        }

        if (sections.Activity)
        {
            Section("ACTIVITY");
            Row($"Voided carts ({report.VoidCount})", Money(report.VoidAmount));
            Row($"Lines removed ({report.LinesRemoved})", Money(report.LinesRemovedAmount));
            Row("No sales", report.NoSales.ToString(Au));
            Row("Cards declined", report.CardDeclines.ToString(Au));
            Row("Card result unknown", report.CardOutcomeUnknown.ToString(Au));
            Row("Sales not saved", report.SaveFailures.ToString(Au));
            Row("Receipts reprinted", report.Reprints.ToString(Au));
            Row($"Wastage at cost ({report.WastageCount})", Money(report.WastageValue));
        }

        // ---- by day (weekly/monthly) or by month (longer periods, so a yearly report stays short) ----
        if (report.Days.Count > 0)
        {
            var byMonth = report.RangeFrom is { } from && report.RangeTo is { } to && to.DayNumber - from.DayNumber > 62;
            if (byMonth)
            {
                Section("BY MONTH");
                foreach (var month in report.Days.GroupBy(d => new { d.TradingDay.Year, d.TradingDay.Month }))
                    Row($"{new DateOnly(month.Key.Year, month.Key.Month, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture)} ({month.Sum(d => d.SaleCount)})",
                        Money(month.Sum(d => d.GrossSales)));
            }
            else
            {
                Section("BY DAY");
                foreach (var day in report.Days)
                    Row($"{day.TradingDay.ToString("ddd dd/MM", CultureInfo.InvariantCulture)} Z#{day.ZNumber} ({day.SaleCount})", Money(day.GrossSales));
            }
        }

        lines.Add(separator);
        var footer = isPeriod ? "*** END OF REPORT ***" : report.IsZ ? "*** END OF DAY ***" : "*** NOT A Z - DAY STILL OPEN ***";
        lines.Add(new ReceiptLine(footer, ReceiptAlign.Center, Bold: true));

        return new ReceiptDocument(lineWidth, lines);
    }

    /// <summary>A wastage (ullage) docket: what was written off, why, by whom, and its value at cost.</summary>
    public static ReceiptDocument BuildWastage(Stock.WastageRecorded recorded, IReadOnlyList<Stock.WastageLine> items, string reason,
        string? employeeName, StoreProfile store, int lineWidth)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(store);
        if (lineWidth < 24)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "Receipts need at least 24 characters per line.");

        var lines = new List<ReceiptLine>();
        var separator = new ReceiptLine(new string('-', lineWidth));
        if (!string.IsNullOrWhiteSpace(store.BusinessName))
            lines.Add(new ReceiptLine(store.BusinessName.Trim(), ReceiptAlign.Center, Bold: true));
        lines.Add(new ReceiptLine("WASTAGE", ReceiptAlign.Center, Bold: true, Large: true));
        lines.Add(new ReceiptLine(""));
        lines.Add(TwoColumn($"Docket {recorded.Docket}", recorded.AtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Au), lineWidth));
        if (!string.IsNullOrWhiteSpace(employeeName))
            lines.Add(new ReceiptLine("By " + employeeName.Trim()));
        foreach (var part in Wrap("Reason: " + reason, lineWidth))
            lines.Add(new ReceiptLine(part));
        lines.Add(separator);
        foreach (var item in items.Where(i => i.Quantity != 0))
        {
            foreach (var part in Wrap(item.ProductName, lineWidth))
                lines.Add(new ReceiptLine(part));
            var what = item.PackUnits == 1 ? $"  {item.Quantity:0.##} x single" : $"  {item.Quantity:0.##} x {item.PackName} ({item.PackUnits})";
            lines.Add(TwoColumn(what, Money(item.Cost), lineWidth));
        }
        lines.Add(separator);
        lines.Add(TwoColumn($"Total at cost ({recorded.Singles:0.##} items)", Money(recorded.Cost), lineWidth, bold: true));
        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine("Signed " + new string('_', Math.Max(10, lineWidth - 7))));
        return new ReceiptDocument(lineWidth, lines);
    }

    /// <summary>
    /// A customer's account statement for the receipt printer: the latest lines with a running balance, how much is
    /// overdue, and the amount due, with their customer code as a barcode.
    /// </summary>
    public static ReceiptDocument BuildStatement(CustomerStatement statement, StoreProfile store, int lineWidth, DateTime asOfLocal, int maxLines = 40)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(store);
        if (lineWidth < 24)
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "Receipts need at least 24 characters per line.");

        var customer = statement.Customer;
        var lines = new List<ReceiptLine>();
        var separator = new ReceiptLine(new string('-', lineWidth));

        if (!string.IsNullOrWhiteSpace(store.BusinessName))
            lines.Add(new ReceiptLine(store.BusinessName.Trim(), ReceiptAlign.Center, Bold: true, Large: true));
        foreach (var addressLine in store.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)))
            lines.Add(new ReceiptLine(addressLine.Trim(), ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(store.Phone))
            lines.Add(new ReceiptLine("Ph " + store.Phone.Trim(), ReceiptAlign.Center));
        if (!string.IsNullOrWhiteSpace(store.Abn))
            lines.Add(new ReceiptLine("ABN " + FormatAbn(store.Abn), ReceiptAlign.Center));

        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine("ACCOUNT STATEMENT", ReceiptAlign.Center, Bold: true));
        lines.Add(new ReceiptLine(""));
        foreach (var part in Wrap(customer.Name, lineWidth))
            lines.Add(new ReceiptLine(part, Bold: true));
        if (!string.IsNullOrWhiteSpace(customer.Company))
            lines.Add(new ReceiptLine(customer.Company.Trim()));
        lines.Add(TwoColumn(customer.Code, asOfLocal.ToString("dd/MM/yyyy", Au), lineWidth));
        lines.Add(new ReceiptLine($"Terms: {customer.TermsDays} days"));
        lines.Add(separator);

        // Oldest first with a running balance; older lines are summed into "Brought forward".
        var ordered = statement.Entries.OrderBy(e => e.AtUtc).ToList();
        var shown = ordered.Skip(Math.Max(0, ordered.Count - maxLines)).ToList();
        var running = ordered.Take(ordered.Count - shown.Count).Sum(e => e.Amount);
        if (shown.Count < ordered.Count)
            lines.Add(TwoColumn("Brought forward", Money(running), lineWidth));
        foreach (var entry in shown)
        {
            running += entry.Amount;
            var what = entry.Kind switch
            {
                AccountEntryKind.Charge => entry.InvoiceNumber is { } inv ? $"Invoice #{inv}" : "Charge",
                AccountEntryKind.Payment => "Payment",
                AccountEntryKind.Credit => entry.InvoiceNumber is { } inv ? $"Credit #{inv}" : "Credit",
                _ => "Adjustment"
            };
            lines.Add(TwoColumn($"{entry.AtUtc.ToLocalTime():dd/MM/yy} {what}", Money(entry.Amount), lineWidth));
            lines.Add(TwoColumn("", "bal " + Money(running), lineWidth));
        }
        if (shown.Count == 0)
            lines.Add(new ReceiptLine("No account activity yet."));
        lines.Add(separator);

        var ageing = statement.Ageing;
        lines.Add(TwoColumn("Not yet due", Money(ageing.Current), lineWidth));
        lines.Add(TwoColumn("1-30 days overdue", Money(ageing.Days1To30), lineWidth));
        lines.Add(TwoColumn("31-60 days overdue", Money(ageing.Days31To60), lineWidth));
        lines.Add(TwoColumn("61-90 days overdue", Money(ageing.Days61To90), lineWidth));
        lines.Add(TwoColumn("Over 90 days", Money(ageing.Over90), lineWidth));
        lines.Add(separator);
        lines.Add(TwoColumn(ageing.Total < 0 ? "IN CREDIT" : "AMOUNT DUE", Money(Math.Abs(ageing.Total)), lineWidth, bold: true));
        if (ageing.Overdue > 0)
            lines.Add(TwoColumn("Overdue now", Money(ageing.Overdue), lineWidth, bold: true));

        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine(customer.Code, ReceiptAlign.Center, Barcode: true));
        lines.Add(new ReceiptLine("Bring this to pay at the counter", ReceiptAlign.Center));
        return new ReceiptDocument(lineWidth, lines);
    }

    /// <summary>"12345678901" → "12 345 678 901". Anything that isn't 11 digits is printed as entered.</summary>
    public static string FormatAbn(string abn)
    {
        var digits = Regex.Replace(abn ?? "", @"\D", "");
        return digits.Length == 11
            ? $"{digits[..2]} {digits[2..5]} {digits[5..8]} {digits[8..]}"
            : (abn ?? "").Trim();
    }

    private static string Money(decimal amount) => amount.ToString("C", Au);

    private static string? LastFour(string? maskedPan)
    {
        if (string.IsNullOrWhiteSpace(maskedPan)) return null;
        var digits = Regex.Replace(maskedPan, @"\D", "");
        return digits.Length >= 4 ? "..." + digits[^4..] : null;
    }

    /// <summary>Text on the left, amount on the right, padded to the full width.</summary>
    private static ReceiptLine TwoColumn(string left, string right, int width, bool bold = false)
    {
        var space = width - right.Length - 1;
        if (left.Length > space) left = left[..Math.Max(0, space)];
        return new ReceiptLine(left.PadRight(width - right.Length) + right, ReceiptAlign.Left, bold);
    }

    /// <summary>Name with the price on its last line; long names wrap onto extra lines.</summary>
    private static void AddWrapped(List<ReceiptLine> lines, string name, string amount, int width)
    {
        var parts = Wrap(name, width - amount.Length - 1);
        for (var i = 0; i < parts.Count - 1; i++)
            lines.Add(new ReceiptLine(parts[i]));
        lines.Add(TwoColumn(parts[^1], amount, width));
    }

    private static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > width ? word[..width] : word;
            if (current.Length == 0) current = w;
            else if (current.Length + 1 + w.Length <= width) current += " " + w;
            else { result.Add(current); current = w; }
        }
        result.Add(current);
        return result;
    }
}
