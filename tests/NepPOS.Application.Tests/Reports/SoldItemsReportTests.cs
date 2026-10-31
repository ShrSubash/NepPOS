using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Domain.Entities;
using Xunit;

namespace NepPOS.Application.Tests.Reports;

/// <summary>Department, PLU and hourly sections, and choosing which sections print.</summary>
public class SoldItemsReportTests
{
    private static long _invoice = 1000;

    private static SaleTransaction Sale(params SaleLineItem[] lines)
    {
        var sale = new SaleTransaction("STORE-001", "REG-001", 1, "Owner");
        foreach (var line in lines)
            sale.AddLineItem(line);

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddEftpos(tender.Remaining, new PaymentResult(true, "00", "APPROVED"));
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(Interlocked.Increment(ref _invoice));
        return sale;
    }

    private static EndOfDayReport TwoSales() => EndOfDayCalculator.Calculate(
        new[]
        {
            Sale(new SaleLineItem("101", "Heineken 6-Pack", 2, 22.00m, 0.10m, "Beer"),
                 new SaleLineItem("201", "Yellowtail Shiraz", 1, 12.00m, 0.10m, "Wine")),
            Sale(new SaleLineItem("101", "Heineken 6-Pack", 1, 22.00m, 0.10m, "Beer"),
                 new SaleLineItem("999", "Old line", 1, 5.00m, 0.10m))
        },
        Array.Empty<JournalEntry>());

    [Fact]
    public void Calculate_TotalsDepartmentsBiggestFirst()
    {
        var report = TwoSales();

        Assert.Equal(new[] { "Beer", "Wine", "No department" }, report.Departments.Select(d => d.Department));
        var beer = report.Departments[0];
        Assert.Equal((3, 66.00m), (beer.Quantity, beer.Amount));
        Assert.Equal(report.GrossSales, report.Departments.Sum(d => d.Amount));
    }

    [Fact]
    public void Calculate_TotalsEachProductAcrossSales()
    {
        var report = TwoSales();

        var heineken = report.Products[0];
        Assert.Equal(("101", "Heineken 6-Pack", 3, 66.00m), (heineken.Sku, heineken.Name, heineken.Quantity, heineken.Amount));
        Assert.Equal(3, report.Products.Count);
    }

    [Fact]
    public void Calculate_GroupsSalesByLocalHour()
    {
        var report = TwoSales();

        // Both sales were just made, so they fall in this hour (or the next, right on the hour).
        Assert.InRange(report.Hours.Count, 1, 2);
        Assert.All(report.Hours, h => Assert.InRange(h.Hour, 0, 23));
        Assert.Equal(2, report.Hours.Sum(h => h.SaleCount));
        Assert.Equal(report.GrossSales, report.Hours.Sum(h => h.Amount));
    }

    [Fact]
    public void BuildEndOfDay_PrintsOnlyTheTickedSections()
    {
        var report = TwoSales() with { RegisterId = "REG-001" };
        var store = new StoreProfile();

        string Text(ReportSections sections) =>
            string.Join("\n", ReceiptBuilder.BuildEndOfDay(report, store, 48, sections).Lines.Select(l => l.Text));

        var defaults = Text(new ReportSections());
        Assert.Contains("DEPARTMENTS", defaults);
        Assert.Contains("HOURLY", defaults);
        Assert.Contains("ACTIVITY", defaults);
        Assert.DoesNotContain("PLU SALES", defaults);      // long list: off unless ticked

        var minimal = Text(new ReportSections { Departments = false, Hourly = false, Employees = false, Activity = false });
        Assert.Contains("TOTAL TAKEN", minimal);           // sales, tenders and cash always print
        Assert.Contains("CASH IN DRAWER", minimal);
        Assert.DoesNotContain("DEPARTMENTS", minimal);
        Assert.DoesNotContain("BY EMPLOYEE", minimal);

        var withProducts = ReceiptBuilder.BuildEndOfDay(report, store, 32, new ReportSections { Products = true })
            .Lines.Select(l => l.Text).ToList();
        Assert.Contains(withProducts, l => l.StartsWith("3 x Heineken 6-Pack") && l.EndsWith("$66.00"));
        Assert.All(withProducts, l => Assert.True(l.Length <= 32, $"Too wide: '{l}'"));
    }
}
