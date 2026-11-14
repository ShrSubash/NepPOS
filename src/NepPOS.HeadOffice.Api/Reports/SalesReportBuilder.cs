using NepPOS.Contracts.Reports;
using NepPOS.Contracts.Sync;

namespace NepPOS.HeadOffice.Api.Reports;

/// <summary>
/// Adds up <c>sale.completed</c> events into a <see cref="SalesReport"/>. Plain C# with no database,
/// so every total is unit tested, and it uses the same rules as the register's Reports screen.
/// </summary>
public static class SalesReportBuilder
{
    /// <summary>A sale before 5 am counts for the previous trading day (same as NepPOS ReportPeriod).</summary>
    public static readonly TimeSpan TradingDayCutoff = TimeSpan.FromHours(5);

    public static DateOnly TradingDay(DateTime occurredUtc, TimeZoneInfo storeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurredUtc, DateTimeKind.Utc), storeZone) - TradingDayCutoff);

    /// <summary>The UTC window that surely contains trading days <paramref name="from"/>..<paramref name="to"/>; filter exactly afterwards.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) RoughUtcWindow(DateOnly from, DateOnly to) =>
        (from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-1), to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(2));

    public static SalesReport Build(
        Guid storeId, string storeCode, string storeName, DateOnly from, DateOnly to,
        IEnumerable<(DateTime OccurredUtc, SaleCompletedV1 Sale)> sales, TimeZoneInfo storeZone)
    {
        var inRange = sales
            .Select(s => (s.Sale, Day: TradingDay(s.OccurredUtc, storeZone),
                Local: TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s.OccurredUtc, DateTimeKind.Utc), storeZone)))
            .Where(s => s.Day >= from && s.Day <= to)
            .ToList();

        var payments = inRange.SelectMany(s => s.Sale.Payments).ToList();
        var lines = inRange.SelectMany(s => s.Sale.Lines).ToList();
        var gross = inRange.Sum(s => s.Sale.Total);
        var gst = inRange.Sum(s => s.Sale.Gst);

        return new SalesReport(
            storeId, storeCode, storeName, from, to,
            SaleCount: inRange.Count,
            ItemCount: lines.Sum(l => l.Quantity),
            GrossSales: gross,
            Gst: gst,
            NetSales: gross - gst,
            CashRounding: inRange.Sum(s => s.Sale.CashRounding),
            TotalTaken: payments.Sum(p => p.Amount),
            Tenders: payments
                .GroupBy(p => p.Tender.Equals("Eftpos", StringComparison.OrdinalIgnoreCase) ? "EFTPOS" : p.Tender)
                .Select(g => new NamedTotal(g.Key, g.Count(), g.Sum(p => p.Amount)))
                .OrderByDescending(t => t.Amount)
                .ToList(),
            Days: inRange
                .GroupBy(s => s.Day)
                .Select(g => new DayTotal(g.Key, g.Count(), g.Sum(s => s.Sale.Total)))
                .OrderBy(d => d.Day)
                .ToList(),
            Departments: lines
                .GroupBy(l => string.IsNullOrWhiteSpace(l.Category) ? "No department" : l.Category!)
                .Select(g => new NamedTotal(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotalIncGst)))
                .OrderByDescending(d => d.Amount)
                .ToList(),
            Products: lines
                .GroupBy(l => new { l.Sku, l.Name })
                .Select(g => new ProductSales(g.Key.Sku, g.Key.Name, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotalIncGst)))
                .OrderByDescending(p => p.Amount)
                .ThenBy(p => p.Name)
                .ToList(),
            Hours: inRange
                .GroupBy(s => s.Local.Hour)
                .Select(g => new HourTotal(g.Key, g.Count(), g.Sum(s => s.Sale.Total)))
                .OrderBy(h => h.Hour)
                .ToList(),
            Employees: inRange
                .GroupBy(s => string.IsNullOrWhiteSpace(s.Sale.EmployeeName) ? "Unknown" : s.Sale.EmployeeName!)
                .Select(g => new NamedTotal(g.Key, g.Count(), g.Sum(s => s.Sale.Total)))
                .OrderByDescending(e => e.Amount)
                .ToList());
    }
}
