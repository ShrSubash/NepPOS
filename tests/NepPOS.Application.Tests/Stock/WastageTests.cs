using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Stock;

public class WastageTests
{
    [Fact]
    public void Line_CostsSinglesTimesCost()
    {
        var line = new WastageLine(1, "Corona 355ml", "6-pack", 6, 2, 1.85m);

        Assert.Equal(12m, line.Singles);
        Assert.Equal(22.20m, line.Cost);
    }

    [Fact]
    public void Docket_IsWAndLocalDateTime()
    {
        Assert.Equal("W261124-143005", Wastage.DocketFor(new DateTime(2026, 11, 24, 14, 30, 5)));
    }

    [Theory]
    [InlineData("Wastage W261124-143005: Broken", "Broken")]
    [InlineData("Wastage W261124-143005: Tasting: Saturday", "Tasting: Saturday")]
    [InlineData("Invoice #19: returned faulty", "Invoice #19: returned faulty")]
    [InlineData(null, "")]
    public void ReasonOf_ReadsTheReasonBack(string? reference, string expected)
    {
        Assert.Equal(expected, Wastage.ReasonOf(reference));
    }

    [Fact]
    public void Reference_IsCutTo100()
    {
        Assert.Equal(100, Wastage.Reference("W261124-143005", new string('x', 200)).Length);
    }

    [Fact]
    public void Docket_ListsItemsReasonAndTotal()
    {
        var items = new[]
        {
            new WastageLine(1, "Corona 355ml", "6-pack", 6, 1, 1.85m),
            new WastageLine(2, "Milk 2L", "Single", 1, 3, 2.10m)
        };
        var recorded = new WastageRecorded("W261124-143005", DateTime.UtcNow, 9, 17.40m, Array.Empty<string>());

        var text = ReceiptBuilder.BuildWastage(recorded, items, "Broken", "Sam", new StoreProfile { BusinessName = "Nep Grocery" }, 42)
            .Lines.Select(l => l.Text).ToList();

        Assert.Contains("WASTAGE", text);
        Assert.Contains("Reason: Broken", text);
        Assert.Contains(text, l => l.StartsWith("  1 x 6-pack (6)") && l.EndsWith("$11.10"));
        Assert.Contains(text, l => l.StartsWith("  3 x single") && l.EndsWith("$6.30"));
        Assert.Contains(text, l => l.StartsWith("Total at cost (9 items)") && l.EndsWith("$17.40"));
    }

    [Fact]
    public void EndOfDay_AddsUpWastageFromTheJournal()
    {
        var entries = new[]
        {
            new JournalEntry(1, "STORE-001", "REG-001", 1, "Sam", JournalEventType.WastageRecorded, "Wastage W1: Broken", amount: 12.50m),
            new JournalEntry(2, "STORE-001", "REG-001", 1, "Sam", JournalEventType.WastageRecorded, "Wastage W2: Expired", amount: 4.25m),
            new JournalEntry(3, "STORE-001", "REG-001", 1, "Sam", JournalEventType.NoSale, "No sale")
        };

        var report = EndOfDayCalculator.Calculate(Array.Empty<SaleTransaction>(), entries);

        Assert.Equal((2, 16.75m), (report.WastageCount, report.WastageValue));
        var text = ReceiptBuilder.BuildEndOfDay(report, new StoreProfile(), 42).Lines.Select(l => l.Text).ToList();
        Assert.Contains(text, l => l.StartsWith("Wastage at cost (2)") && l.EndsWith("$16.75"));
    }
}
