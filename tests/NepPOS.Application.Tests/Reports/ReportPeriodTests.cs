using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Reports;

public class ReportPeriodTests
{
    [Theory]
    [InlineData("2026-10-26", "2026-10-26", "2026-11-01")] // Monday
    [InlineData("2026-10-29", "2026-10-26", "2026-11-01")] // Thursday
    [InlineData("2026-11-01", "2026-10-26", "2026-11-01")] // Sunday ends the week
    public void Week_RunsMondayToSunday(string day, string from, string to)
    {
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), ReportPeriod.Week(DateOnly.Parse(day)));
    }

    [Fact]
    public void Month_CoversTheWholeCalendarMonth()
    {
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), ReportPeriod.Month(new DateOnly(2028, 2, 15)));
    }

    [Theory]
    [InlineData("2026-11-01", "2026-07-01", "2027-06-30")]
    [InlineData("2027-06-30", "2026-07-01", "2027-06-30")]
    [InlineData("2027-07-01", "2027-07-01", "2028-06-30")]
    public void FinancialYear_RunsJulyToJune(string day, string from, string to)
    {
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), ReportPeriod.FinancialYear(DateOnly.Parse(day)));
    }

    [Fact]
    public void TradingDay_ZClosedAfterMidnightCountsForThePreviousDay()
    {
        var friday = new DateOnly(2026, 10, 30);
        DateTime LocalToUtc(int hour) =>
            new DateTime(2026, 10, 31, hour, 30, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.Equal(friday, ReportPeriod.TradingDay(LocalToUtc(1)));               // Sat 01:30 -> Friday
        Assert.Equal(friday.AddDays(1), ReportPeriod.TradingDay(LocalToUtc(6)));    // Sat 06:30 -> Saturday
    }

    [Theory]
    [InlineData(6, "BY DAY")]
    [InlineData(364, "BY MONTH")]
    public void BuildEndOfDay_PeriodReportShowsZRangeAndBreakdown(int spanDays, string breakdownHeading)
    {
        var from = new DateOnly(2026, 7, 1);
        var report = new EndOfDayReport
        {
            Title = "WEEKLY REPORT",
            RegisterId = "REG-001",
            RangeFrom = from,
            RangeTo = from.AddDays(spanDays),
            SaleCount = 3,
            GrossSales = 60m,
            Tenders = new[] { new TenderTotal(TenderType.Cash, 3, 60m), new TenderTotal(TenderType.Eftpos, 0, 0m) },
            Days = new[]
            {
                new DayTotal(from, 11, 1, 10m),
                new DayTotal(from.AddDays(1), 12, 2, 50m)
            }
        };

        var text = ReceiptBuilder.BuildEndOfDay(report, new StoreProfile(), 32).Lines.Select(l => l.Text).ToList();

        Assert.All(text, line => Assert.True(line.Length <= 32, $"Too wide: '{line}'"));
        Assert.Contains("WEEKLY REPORT", text);
        Assert.Contains("Z #11 - #12 (2 days)", text);
        Assert.Contains(breakdownHeading, text);
        Assert.Contains("*** END OF REPORT ***", text);
        if (breakdownHeading == "BY MONTH")
            Assert.Contains(text, l => l.StartsWith("Jul 2026 (3)") && l.EndsWith("$60.00"));
        else
            Assert.Contains(text, l => l.StartsWith("Thu 02/07 Z#12 (2)") && l.EndsWith("$50.00"));
    }
}
