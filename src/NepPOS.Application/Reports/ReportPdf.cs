using System.Globalization;
using NepPOS.Application.Hardware;
using NepPOS.Domain.Enums;
using static NepPOS.Application.Reports.SimplePdf;

namespace NepPOS.Application.Reports;

/// <summary>
/// The report as an A4 PDF for the owner: headline figures in boxes, then a table per section.
/// Everything is included (the "Print includes" ticks only shorten the receipt-printer version).
/// </summary>
public static class ReportPdf
{
    private const float Margin = 40f;
    private const float Right = PageWidth - Margin;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float Bottom = PageHeight - 50f;
    private const float RowHeight = 17f;

    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    public static byte[] Build(EndOfDayReport report, StoreProfile store)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(store);

        var pdf = new SimplePdf();
        var layout = new Layout(pdf);
        var title = ReportTitles.Title(report);

        // ---- header ----
        var y = 48f;
        if (!string.IsNullOrWhiteSpace(store.BusinessName))
            pdf.Text(Margin, y, store.BusinessName.Trim(), 11, bold: true, Colors.Muted);
        pdf.TextRight(Right, y, $"Register {report.RegisterId}", 9, color: Colors.Muted);
        y += 24;
        pdf.Text(Margin, y, title, 20, bold: true);
        pdf.TextRight(Right, y - 10, $"Printed {DateTime.Now.ToString("dd/MM/yyyy HH:mm", Au)}", 9, color: Colors.Muted);
        if (!string.IsNullOrWhiteSpace(report.PrintedBy))
            pdf.TextRight(Right, y + 2, $"by {report.PrintedBy}", 9, color: Colors.Muted);
        y += 18;
        var period = ReportTitles.Period(report);
        if (report.Title is not null)
            period += report.Days.Count == 0 ? "   (no closed days)" : $"   (Z #{report.Days[0].ZNumber} - #{report.Days[^1].ZNumber}, {report.Days.Count} days)";
        pdf.Text(Margin, y, period, 10, color: Colors.Muted);
        y += 18;

        // ---- headline figures ----
        var eftpos = report.Tenders.Where(t => t.Tender == TenderType.Eftpos).Sum(t => t.Amount);
        var problems = report.VoidCount + report.NoSales + report.CardOutcomeUnknown + report.SaveFailures + report.LinesRemoved;
        var average = report.SaleCount == 0 ? 0 : report.GrossSales / report.SaleCount;

        y = layout.Boxes(y, new[]
        {
            ("SALES (INC GST)", Money(report.GrossSales), $"GST {Money(report.Gst)}", true, false),
            ("TRANSACTIONS", report.SaleCount.ToString(Au), $"{report.ItemCount} items", false, false),
            ("AVERAGE SALE", Money(average), $"Net (ex GST) {Money(report.NetSales)}", false, false),
            ("CASH IN DRAWER", Money(report.CashExpected), "plus your opening float", false, false)
        });
        y = layout.Boxes(y, new[]
        {
            ("EFTPOS", Money(eftpos), $"{Percent(eftpos, report.TotalTaken)} of money taken", false, false),
            ("TOTAL TAKEN", Money(report.TotalTaken), report.CashRounding == 0 ? "" : $"cash rounding {report.CashRounding.ToString("+0.00;-0.00", Au)}", false, false),
            ("INVOICES", report.FirstInvoice is null ? "-" : $"#{report.FirstInvoice}", report.FirstInvoice is null ? "" : $"to #{report.LastInvoice}", false, false),
            ("NEEDS A LOOK", problems.ToString(Au), "voids, no sales, card issues", false, problems > 0)
        });
        y += 6;

        // ---- sections ----
        var tenderRows = report.Tenders
            .Select(t => Row(t.Amount, report.TotalTaken, t.Label, t.Count.ToString(Au), Money(t.Amount)))
            .ToList();
        y = layout.Table(y, "How customers paid", new[] { ("Tender", 0.46f, false), ("Count", 0.14f, true), ("Amount", 0.2f, true) }, tenderRows);

        y = layout.Table(y, "Departments", new[] { ("Department", 0.46f, false), ("Qty", 0.14f, true), ("Sales", 0.2f, true) },
            report.Departments.Select(d => Row(d.Amount, report.GrossSales, d.Department, d.Quantity.ToString(Au), Money(d.Amount))).ToList());

        y = layout.Table(y, "Sales by hour", new[] { ("Hour", 0.46f, false), ("Sales", 0.14f, true), ("Amount", 0.2f, true) },
            report.Hours.Select(h => Row(h.Amount, report.GrossSales,
                $"{DateTime.Today.AddHours(h.Hour).ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant()} - " +
                $"{DateTime.Today.AddHours(h.Hour + 1).ToString("h tt", CultureInfo.InvariantCulture).ToLowerInvariant()}",
                h.SaleCount.ToString(Au), Money(h.Amount))).ToList());

        y = layout.Table(y, "Employees", new[] { ("Employee", 0.36f, false), ("Sales", 0.1f, true), ("Average", 0.14f, true), ("Amount", 0.2f, true) },
            report.ByEmployee.Select(e => Row(e.Amount, report.GrossSales, e.Employee, e.SaleCount.ToString(Au),
                Money(e.SaleCount == 0 ? 0 : e.Amount / e.SaleCount), Money(e.Amount))).ToList());

        if (report.Days.Count > 0)
        {
            var byMonth = report.RangeFrom is { } from && report.RangeTo is { } to && to.DayNumber - from.DayNumber > 62;
            var dayRows = byMonth
                ? report.Days.GroupBy(d => new DateOnly(d.TradingDay.Year, d.TradingDay.Month, 1))
                    .Select(m => Row(m.Sum(d => d.GrossSales), report.GrossSales, m.Key.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                        m.Sum(d => d.SaleCount).ToString(Au), Money(m.Sum(d => d.GrossSales))))
                    .ToList()
                : report.Days.Select(d => Row(d.GrossSales, report.GrossSales,
                        $"{d.TradingDay.ToString("ddd d MMM", CultureInfo.InvariantCulture)}  (Z #{d.ZNumber})",
                        d.SaleCount.ToString(Au), Money(d.GrossSales)))
                    .ToList();
            y = layout.Table(y, byMonth ? "By month" : "By day", new[] { (byMonth ? "Month" : "Day", 0.46f, false), ("Sales", 0.14f, true), ("Amount", 0.2f, true) }, dayRows);
        }

        var activity = new (string Label, int Count, decimal? Amount)[]
        {
            ("Voided carts", report.VoidCount, report.VoidAmount),
            ("Lines removed from a sale", report.LinesRemoved, report.LinesRemovedAmount),
            ("Drawer opened with no sale", report.NoSales, null),
            ("Cards declined", report.CardDeclines, null),
            ("Card result unknown (check terminal)", report.CardOutcomeUnknown, null),
            ("Paid but not saved", report.SaveFailures, null),
            ("Receipts reprinted", report.Reprints, null)
        };
        if (report.WastageCount > 0)
            activity = activity.Append(("Wastage written off (at cost)", report.WastageCount, (decimal?)report.WastageValue)).ToArray();
        y = layout.Table(y, "Activity to check", new[] { ("Event", 0.6f, false), ("Count", 0.16f, true), ("Amount", 0.24f, true) },
            activity.Select(a => new TableRow(new[] { a.Label, a.Count.ToString(Au), a.Amount is null ? "" : Money(a.Amount.Value) }, null, a.Count > 0)).ToList(),
            withShare: false);

        layout.Table(y, "Products sold (PLU)", new[] { ("PLU", 0.12f, false), ("Product", 0.38f, false), ("Qty", 0.1f, true), ("Sales", 0.2f, true) },
            report.Products.Select(p => Row(p.Amount, report.GrossSales, p.Sku, p.Name, p.Quantity.ToString(Au), Money(p.Amount))).ToList());

        // ---- footer on every page ----
        for (var i = 0; i < pdf.PageCount; i++)
        {
            var pageNumber = i + 1;
            pdf.OnPage(i, () =>
            {
                pdf.Line(Margin, PageHeight - 34, Right, Colors.Line);
                pdf.Text(Margin, PageHeight - 22, $"NepPOS - {title}", 8, color: Colors.Muted);
                pdf.TextRight(Right, PageHeight - 22, $"Page {pageNumber} of {pdf.PageCount}", 8, color: Colors.Muted);
            });
        }

        return pdf.ToBytes();
    }

    private static string Money(decimal amount) => amount.ToString("C", Au);

    private static string Percent(decimal part, decimal whole) =>
        whole == 0 ? "0%" : (part / whole * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A table row with a "% of" share shown as a bar.</summary>
    private static TableRow Row(decimal value, decimal whole, params string[] cells) =>
        new(cells, whole == 0 ? 0 : (float)Math.Clamp(value / whole, 0, 1), false);

    private sealed record TableRow(string[] Cells, float? Share, bool Highlight);

    /// <summary>Keeps track of where the next thing goes and starts new pages.</summary>
    private sealed class Layout
    {
        private readonly SimplePdf _pdf;

        public Layout(SimplePdf pdf) => _pdf = pdf;

        /// <summary>A row of four headline boxes. Returns the y below them.</summary>
        public float Boxes(float y, (string Label, string Value, string Note, bool Dark, bool Warn)[] boxes)
        {
            const float gap = 8f, height = 54f;
            var width = (ContentWidth - gap * (boxes.Length - 1)) / boxes.Length;
            for (var i = 0; i < boxes.Length; i++)
            {
                var (label, value, note, dark, warn) = boxes[i];
                var x = Margin + i * (width + gap);
                _pdf.Box(x, y, width, height, dark ? Colors.Ink : warn ? Colors.WarnFill : Colors.HeaderFill);
                var text = dark ? Colors.White : warn ? Colors.Warn : Colors.Ink;
                var sub = dark ? "0.72 0.74 0.86" : Colors.Muted;
                _pdf.Text(x + 9, y + 14, label, 7, bold: true, sub);
                _pdf.Text(x + 9, y + 32, Fit(value, width - 18, 15, bold: true), 15, bold: true, text);
                _pdf.Text(x + 9, y + 45, Fit(note, width - 18, 7.5f), 7.5f, color: sub);
            }
            return y + height + gap;
        }

        /// <summary>
        /// A section title and a table. Columns are (heading, share of width, right-aligned);
        /// the last 20% of the width is the "% of sales" bar. Starts a new page when needed.
        /// </summary>
        public float Table(float y, string title, (string Heading, float Width, bool RightAlign)[] columns,
                           IReadOnlyList<TableRow> rows, bool withShare = true)
        {
            // Keep the title with its header and at least one row.
            if (y + 30 + RowHeight * 2 > Bottom)
                y = NewPage();

            y += 16;
            _pdf.Text(Margin, y, title, 12, bold: true);
            y += 8;
            y = Header(y, columns, withShare);

            if (rows.Count == 0)
            {
                _pdf.Text(Margin + 6, y + 12, "Nothing in this period.", 9, color: Colors.Muted);
                return y + RowHeight + 4;
            }

            foreach (var row in rows)
            {
                if (y + RowHeight > Bottom)
                {
                    y = NewPage();
                    _pdf.Text(Margin, y + 10, title + " (continued)", 10, bold: true, Colors.Muted);
                    y = Header(y + 16, columns, withShare);
                }

                if (row.Highlight)
                    _pdf.Box(Margin, y, ContentWidth, RowHeight, Colors.WarnFill);

                var x = Margin;
                for (var c = 0; c < columns.Length; c++)
                {
                    var width = columns[c].Width * ContentWidth;
                    var cell = Fit(row.Cells[c], width - 12, 9);
                    if (columns[c].RightAlign)
                        _pdf.TextRight(x + width - 6, y + 12, cell, 9, color: row.Highlight ? Colors.Warn : Colors.Ink);
                    else
                        _pdf.Text(x + 6, y + 12, cell, 9, row.Highlight, row.Highlight ? Colors.Warn : Colors.Ink);
                    x += width;
                }

                if (withShare && row.Share is { } share)
                {
                    var barX = x + 10;
                    var barWidth = Right - barX - 34;
                    _pdf.Box(barX, y + 6, barWidth, 5, Colors.AccentLight);
                    _pdf.Box(barX, y + 6, barWidth * share, 5, Colors.Accent);
                    _pdf.TextRight(Right - 4, y + 12, (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%", 8, color: Colors.Muted);
                }

                _pdf.Line(Margin, y + RowHeight, Right, Colors.Line);
                y += RowHeight;
            }

            return y + 4;
        }

        private float Header(float y, (string Heading, float Width, bool RightAlign)[] columns, bool withShare)
        {
            _pdf.Box(Margin, y, ContentWidth, RowHeight, Colors.HeaderFill);
            var x = Margin;
            foreach (var (heading, share, rightAlign) in columns)
            {
                var width = share * ContentWidth;
                if (rightAlign)
                    _pdf.TextRight(x + width - 6, y + 12, heading, 8, bold: true, Colors.Muted);
                else
                    _pdf.Text(x + 6, y + 12, heading, 8, bold: true, Colors.Muted);
                x += width;
            }
            if (withShare)
                _pdf.Text(x + 10, y + 12, "% of total", 8, bold: true, Colors.Muted);
            return y + RowHeight;
        }

        private float NewPage()
        {
            _pdf.NewPage();
            return 40f;
        }
    }
}
