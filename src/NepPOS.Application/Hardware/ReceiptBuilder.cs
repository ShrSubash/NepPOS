using System.Globalization;
using System.Text.RegularExpressions;
using NepPOS.Domain.Entities;
using NepPOS.Application.Reports;
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

    public static ReceiptDocument Build(SaleTransaction sale, StoreProfile store, int lineWidth, bool reprint = false)
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
        lines.Add(new ReceiptLine(reprint ? "TAX INVOICE (REPRINT)" : "TAX INVOICE", ReceiptAlign.Center, Bold: true));
        lines.Add(new ReceiptLine(""));

        // ---- invoice details ----
        var local = sale.CreatedAtUtc.ToLocalTime();
        lines.Add(TwoColumn($"Invoice #{sale.InvoiceNumber}", local.ToString("dd/MM/yyyy HH:mm", Au), lineWidth));
        var registerLine = string.IsNullOrWhiteSpace(sale.EmployeeName)
            ? $"Register {sale.RegisterId}"
            : $"Register {sale.RegisterId} - {sale.EmployeeName}";
        lines.Add(new ReceiptLine(registerLine.Length > lineWidth ? registerLine[..lineWidth] : registerLine));
        lines.Add(separator);

        // ---- items ----
        var hasGstFree = false;
        foreach (var item in sale.LineItems)
        {
            var gstFree = item.GstRate == 0;
            hasGstFree |= gstFree;
            var name = (gstFree ? "*" : "") + item.ProductName;
            var lineTotal = Money(item.LineTotalIncGst);

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
        }

        lines.Add(separator);

        // ---- totals ----
        var total = decimal.Round(sale.GrandTotal, 2, MidpointRounding.AwayFromZero);
        var gst = decimal.Round(sale.GstTotal, 2, MidpointRounding.AwayFromZero);

        lines.Add(TwoColumn("TOTAL", Money(total), lineWidth, bold: true));
        lines.Add(TwoColumn("GST included", Money(gst), lineWidth));
        if (sale.CashRounding != 0)
            lines.Add(TwoColumn("Cash rounding", sale.CashRounding.ToString("+0.00;-0.00", Au), lineWidth));

        // ---- payments ----
        if (sale.Payments.Count > 0)
        {
            lines.Add(new ReceiptLine(""));
            foreach (var payment in sale.Payments)
            {
                if (payment.TenderType == TenderType.Cash)
                {
                    lines.Add(TwoColumn("Cash", Money(payment.Tendered), lineWidth));
                    if (payment.Change > 0)
                        lines.Add(TwoColumn("Change", Money(payment.Change), lineWidth));
                }
                else
                {
                    var card = string.Join(" ", new[] { payment.CardType, LastFour(payment.MaskedPan) }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    lines.Add(TwoColumn(string.IsNullOrEmpty(card) ? "EFTPOS" : $"EFTPOS {card}", Money(payment.Amount), lineWidth));
                }
            }
        }

        // ---- GST statement (ATO) ----
        lines.Add(new ReceiptLine(""));
        lines.Add(new ReceiptLine("Total price includes GST", ReceiptAlign.Center));
        if (hasGstFree)
            lines.Add(new ReceiptLine("* GST-free item", ReceiptAlign.Center));

        if (!string.IsNullOrWhiteSpace(store.Footer))
        {
            lines.Add(new ReceiptLine(""));
            foreach (var part in Wrap(store.Footer.Trim(), lineWidth))
                lines.Add(new ReceiptLine(part, ReceiptAlign.Center));
        }

        return new ReceiptDocument(lineWidth, lines);
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
        Row("Gross sales (inc GST)", Money(report.GrossSales));
        Row("GST", Money(report.Gst));
        Row("Net sales (ex GST)", Money(report.NetSales));
        if (report.CashRounding != 0)
            Row("Cash rounding", report.CashRounding.ToString("+0.00;-0.00", Au));

        // ---- tenders and cash (always) ----
        Section("TENDERS");
        foreach (var tender in report.Tenders)
            Row($"{(tender.Tender == TenderType.Eftpos ? "EFTPOS" : tender.Tender.ToString())} ({tender.Count})", Money(tender.Amount));
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
