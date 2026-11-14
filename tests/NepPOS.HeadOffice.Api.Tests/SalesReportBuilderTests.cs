using NepPOS.Contracts.Sync;
using NepPOS.HeadOffice.Api.Reports;

namespace NepPOS.HeadOffice.Api.Tests;

/// <summary>Head Office's sales totals follow the register's rules: AUD, GST inclusive, 5 am trading day, store time.</summary>
public class SalesReportBuilderTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>A sale at a Sydney local time (Sydney is UTC+11 in November).</summary>
    private static (DateTime, SaleCompletedV1) Sale(string sydneyTime, string employee, decimal cash, decimal card, params SaleLineV1[] lines)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(sydneyTime), Sydney);
        var payments = new List<SalePaymentV1>();
        if (cash > 0) payments.Add(new SalePaymentV1("Cash", cash, cash, 0, null, null, null));
        if (card > 0) payments.Add(new SalePaymentV1("Eftpos", card, card, 0, "123456", "Visa", "....1234"));
        var total = lines.Sum(l => l.LineTotalIncGst);
        return (utc, new SaleCompletedV1(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, 1, employee, total,
            decimal.Round(total / 11, 2), cash + card - total, lines, payments));
    }

    private static SaleLineV1 Line(string sku, string name, string? category, int qty, decimal price) =>
        new(sku, name, category, qty, price, 0.10m, qty * price);

    private static readonly (DateTime, SaleCompletedV1)[] Week =
    {
        Sale("2026-11-13 10:15", "Owner", 0, 44.00m, Line("101", "Heineken 6-Pack", "Beer", 2, 22.00m)),
        Sale("2026-11-13 17:40", "Cashier", 20.00m, 0, Line("201", "Yellowtail Shiraz", "Wine", 1, 12.00m), Line("901", "Ice", null, 1, 8.00m)),
        Sale("2026-11-14 01:30", "Cashier", 0, 22.00m, Line("101", "Heineken 6-Pack", "Beer", 1, 22.00m)),   // after midnight: still Friday's trading
        Sale("2026-11-14 11:00", "Owner", 0, 30.00m, Line("301", "Jim Beam", "Spirits", 1, 30.00m))
    };

    [Fact]
    public void FridaysTrading_IncludesTheSaleAfterMidnight_NotSaturdays()
    {
        var friday = new DateOnly(2026, 11, 13);

        var report = SalesReportBuilder.Build(Guid.NewGuid(), "STORE-001", "Parramatta", friday, friday, Week, Sydney);

        Assert.Equal(3, report.SaleCount);
        Assert.Equal(86.00m, report.GrossSales);
        Assert.Equal(report.GrossSales - report.Gst, report.NetSales);
        Assert.Equal(5, report.ItemCount);
        Assert.Equal(friday, Assert.Single(report.Days).Day);
    }

    [Fact]
    public void Totals_ByTenderDepartmentProductHourAndEmployee()
    {
        var report = SalesReportBuilder.Build(Guid.NewGuid(), "STORE-001", "Parramatta",
            new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14), Week, Sydney);

        Assert.Equal(4, report.SaleCount);
        Assert.Equal(116.00m, report.TotalTaken);
        Assert.Equal(("EFTPOS", 3, 96.00m), (report.Tenders[0].Name, report.Tenders[0].Count, report.Tenders[0].Amount));
        Assert.Equal(new[] { "Beer", "Spirits", "Wine", "No department" }, report.Departments.Select(d => d.Name));
        Assert.Equal((3, 66.00m), (report.Departments[0].Count, report.Departments[0].Amount));
        Assert.Equal(("101", 3, 66.00m), (report.Products[0].Sku, report.Products[0].Quantity, report.Products[0].Amount));
        Assert.Equal(new[] { 1, 10, 11, 17 }, report.Hours.Select(h => h.Hour));   // Sydney time, not UTC
        Assert.Equal(new[] { 86.00m, 30.00m }, report.Days.Select(d => d.Amount));
        Assert.Equal(("Owner", 2, 74.00m), (report.Employees[0].Name, report.Employees[0].Count, report.Employees[0].Amount));
    }

    [Fact]
    public void NoSales_GivesAnEmptyReport()
    {
        var day = new DateOnly(2026, 12, 25);
        var report = SalesReportBuilder.Build(Guid.NewGuid(), "STORE-001", "Parramatta", day, day, Week, Sydney);

        Assert.Equal(0, report.SaleCount);
        Assert.Empty(report.Days);
        Assert.Empty(report.Tenders);
    }
}
