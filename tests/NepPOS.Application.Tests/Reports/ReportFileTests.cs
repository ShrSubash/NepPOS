using System.Text;
using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Reports;

/// <summary>Saved report files: CSV for Excel, PDF for the owner, and their file names.</summary>
public class ReportFileTests
{
    private static EndOfDayReport Sample(int products = 3) => new()
    {
        IsZ = true,
        ZNumber = 5,
        RegisterId = "REG-001",
        PrintedBy = "Store Owner",
        PeriodStartUtc = new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc),
        PeriodEndUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
        SaleCount = 3,
        ItemCount = 5,
        GrossSales = 100.00m,
        Gst = 9.09m,
        Tenders = new[] { new TenderTotal(TenderType.Cash, 1, 25.00m), new TenderTotal(TenderType.Eftpos, 2, 75.00m) },
        Departments = new[] { new DepartmentTotal("Beer, cider & RTD", 4, 80.00m), new DepartmentTotal("Wine", 1, 20.00m) },
        Products = Enumerable.Range(1, products)
            .Select(i => new ProductTotal($"{100 + i}", i == 1 ? "Heineken \"Classic\" 6-Pack" : $"Product {i}", 1, 1.00m))
            .ToArray(),
        Hours = new[] { new HourTotal(17, 3, 100.00m) },
        ByEmployee = new[] { new EmployeeTotal("Store Owner", 3, 100.00m) },
        VoidCount = 1,
        VoidAmount = 12.50m
    };

    [Fact]
    public void Csv_HasEverySectionWithTheSameColumns()
    {
        var lines = ReportCsv.Build(Sample()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Section,Item,Quantity,Amount,% of sales", lines[0]);
        Assert.Contains("Sales,Gross sales (inc GST),,100.00,", lines);
        Assert.Contains("Tenders,EFTPOS,2,75.00,", lines);
        Assert.Contains("Tenders,Cash in drawer (excludes float),,25.00,", lines);
        Assert.Contains("Hourly,17:00-18:00,3,100.00,100.0", lines);
        Assert.Contains("Activity,Voided carts,1,12.50,", lines);
    }

    [Fact]
    public void Csv_QuotesCommasAndQuotes()
    {
        var csv = ReportCsv.Build(Sample());

        Assert.Contains("Departments,\"Beer, cider & RTD\",4,80.00,80.0", csv);
        Assert.Contains("PLU sales,\"101 Heineken \"\"Classic\"\" 6-Pack\",1,1.00,1.0", csv);
    }

    [Fact]
    public void Pdf_IsAValidPdfWithOnePagePerScreenful()
    {
        var shortPdf = Encoding.Latin1.GetString(ReportPdf.Build(Sample(), new StoreProfile { BusinessName = "Nep Liquor" }));
        var longPdf = Encoding.Latin1.GetString(ReportPdf.Build(Sample(products: 120), new StoreProfile()));

        Assert.StartsWith("%PDF-1.4", shortPdf);
        Assert.EndsWith("%%EOF\n", shortPdf);
        Assert.Contains("/Count 1 ", shortPdf);
        Assert.Contains("(Z Report #5) Tj", shortPdf);
        Assert.Contains("(Page 1 of 1) Tj", shortPdf);

        // 120 products flow onto extra pages, and every page says "Page n of N" with the right N.
        var pages = int.Parse(longPdf[(longPdf.IndexOf("/Count ", StringComparison.Ordinal) + 7)..].Split(' ')[0]);
        Assert.True(pages >= 3, $"Expected at least 3 pages, got {pages}");
        Assert.Contains($"(Page {pages} of {pages}) Tj", longPdf);
        Assert.Contains("(Products sold \\(PLU\\) \\(continued\\)) Tj", longPdf);
    }

    [Fact]
    public void Pdf_XrefPointsAtEveryObject()
    {
        var bytes = ReportPdf.Build(Sample(), new StoreProfile());
        var text = Encoding.Latin1.GetString(bytes);

        var xrefStart = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        var entries = text[xrefStart..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ")).ToList();

        Assert.NotEmpty(entries);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = int.Parse(entries[i][..10]);
            Assert.StartsWith($"{i + 1} 0 obj", text[offset..]);
        }
    }

    [Fact]
    public void FileName_SortsByDateAndSaysWhatItIs()
    {
        var z = Sample();
        var weekly = z with
        {
            IsZ = false,
            Title = "WEEKLY REPORT",
            RangeFrom = new DateOnly(2026, 10, 26),
            RangeTo = new DateOnly(2026, 11, 1)
        };

        Assert.Equal($"{ReportPeriod.TradingDay(z.PeriodEndUtc):yyyy-MM-dd} Z0005", ReportTitles.FileName(z));
        Assert.Equal("2026-10-26 to 2026-11-01 Weekly", ReportTitles.FileName(weekly));
        Assert.Equal("Weekly Report", ReportTitles.Title(weekly));
        Assert.Equal("Z Report #5 (reprint)", ReportTitles.Title(z with { IsReprint = true }));
    }
}
