using System.Globalization;
using System.Text;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Reports;

/// <summary>
/// The report as one CSV file for Excel or the accountant: every section is a block of rows
/// with the same columns (Section, Item, Quantity, Amount, % of sales), so it can be filtered by Section.
/// Everything is included, whatever is ticked for printing.
/// </summary>
public static class ReportCsv
{
    public static string Build(EndOfDayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var csv = new StringBuilder();
        void Row(string section, string item, decimal? quantity, decimal? amount, bool share = false) =>
            csv.Append(Field(section)).Append(',')
               .Append(Field(item)).Append(',')
               .Append(quantity?.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
               .Append(amount?.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(share && amount is { } a && report.GrossSales != 0
                   ? (a / report.GrossSales * 100).ToString("0.0", CultureInfo.InvariantCulture)
                   : "")
               .Append("\r\n");

        csv.Append("Section,Item,Quantity,Amount,% of sales\r\n");

        Row("Report", ReportTitles.Title(report), null, null);
        Row("Report", ReportTitles.Period(report), null, null);
        Row("Report", "Register " + report.RegisterId, null, null);

        Row("Sales", "Transactions", report.SaleCount, null);
        Row("Sales", "Items sold", report.ItemCount, null);
        Row("Sales", "Gross sales (inc GST)", null, report.GrossSales);
        Row("Sales", "GST", null, report.Gst);
        Row("Sales", "Net sales (ex GST)", null, report.NetSales);
        Row("Sales", "Cash rounding", null, report.CashRounding);
        if (report.FirstInvoice is not null)
            Row("Sales", $"Invoices {report.FirstInvoice} to {report.LastInvoice}", null, null);

        foreach (var tender in report.Tenders)
            Row("Tenders", tender.Label, tender.Count, tender.Amount);
        Row("Tenders", "Total taken", null, report.TotalTaken);
        Row("Tenders", "Cash in drawer (excludes float)", null, report.CashExpected);

        foreach (var d in report.Departments)
            Row("Departments", d.Department, d.Quantity, d.Amount, share: true);
        foreach (var p in report.Products)
            Row("PLU sales", $"{p.Sku} {p.Name}", p.Quantity, p.Amount, share: true);
        foreach (var h in report.Hours)
            Row("Hourly", $"{h.Hour:00}:00-{(h.Hour + 1) % 24:00}:00", h.SaleCount, h.Amount, share: true);
        foreach (var e in report.ByEmployee)
            Row("Employees", e.Employee, e.SaleCount, e.Amount, share: true);
        foreach (var day in report.Days)
            Row("By day", $"{day.TradingDay:yyyy-MM-dd} Z#{day.ZNumber}", day.SaleCount, day.GrossSales, share: true);

        Row("Activity", "Voided carts", report.VoidCount, report.VoidAmount);
        Row("Activity", "Lines removed", report.LinesRemoved, report.LinesRemovedAmount);
        Row("Activity", "No sales", report.NoSales, null);
        Row("Activity", "Cards declined", report.CardDeclines, null);
        Row("Activity", "Card result unknown", report.CardOutcomeUnknown, null);
        Row("Activity", "Sales not saved", report.SaveFailures, null);
        Row("Activity", "Receipts reprinted", report.Reprints, null);
        Row("Activity", "Wastage at cost", report.WastageCount, report.WastageValue);

        return csv.ToString();
    }

    /// <summary>Quotes a field when it holds a comma, quote or line break (RFC 4180).</summary>
    private static string Field(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}

/// <summary>Titles and file names shared by the screen, the PDF and the CSV.</summary>
public static class ReportTitles
{
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    /// <summary>"Z Report #5", "Z Report #5 (reprint)", "X Reading", "Weekly Report".</summary>
    public static string Title(EndOfDayReport report) => report switch
    {
        { Title: { } title } => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant()),
        { IsZ: true, IsReprint: true } => $"Z Report #{report.ZNumber} (reprint)",
        { IsZ: true } => $"Z Report #{report.ZNumber}",
        _ => "X Reading (day still open)"
    };

    /// <summary>The dates the report covers, e.g. "Mon 26 Oct 2026 - Sun 1 Nov 2026".</summary>
    public static string Period(EndOfDayReport report) =>
        report.Title is not null && report.RangeFrom is { } from && report.RangeTo is { } to
            ? $"{from.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)} - {to.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}"
            : $"{report.PeriodStartUtc.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} - " +
              $"{report.PeriodEndUtc.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// File name without extension, sortable in Explorer:
    /// "2026-11-01 Z0005", "2026-11-01 1530 X", "2026-10-26 to 2026-11-01 Weekly".
    /// </summary>
    public static string FileName(EndOfDayReport report)
    {
        if (report.Title is not null && report.RangeFrom is { } from && report.RangeTo is { } to)
        {
            var kind = report.Title.Split(' ')[0];
            return $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.ToLowerInvariant())}";
        }

        var end = report.PeriodEndUtc.ToLocalTime();
        return report.IsZ
            ? $"{ReportPeriod.TradingDay(report.PeriodEndUtc):yyyy-MM-dd} Z{report.ZNumber:0000}"
            : $"{end.ToString("yyyy-MM-dd HHmm", Au)} X";
    }
}
