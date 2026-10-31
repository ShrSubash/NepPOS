using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Journal;
using NepPOS.Infrastructure.Database.Reports;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Weekly / monthly reports add up the closed days in the range; the open day is never included.</summary>
public class PeriodReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly EndOfDayReportService _reports;

    private static readonly DateOnly Today = ReportPeriod.TradingDay(DateTime.UtcNow);

    public PeriodReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using (var db = new NepPosDbContext(_options))
            db.Database.EnsureCreated();

        var terminal = new RegisterTerminal("STORE-001", "REG-001");
        var signedIn = new SignedInOperator();
        var journal = new JournalService(_options, terminal, signedIn, NullLogger<JournalService>.Instance);
        _reports = new EndOfDayReportService(_options, terminal, journal, signedIn);
    }

    private async Task SellAsync(decimal price)
    {
        await using var db = new NepPosDbContext(_options);
        var sale = new SaleTransaction("STORE-001", "REG-001", 1, "Owner");
        sale.AddLineItem(new SaleLineItem("101", "Test item", 1, price, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(price);
        await new CheckoutService(new SaleTransactionRepository(db)).CompleteSaleAsync(sale, tender);
    }

    /// <summary>Closes the day, then moves that Z to midday on the given trading day.</summary>
    private async Task CloseDayOnAsync(DateOnly day)
    {
        await _reports.CloseDayAsync();
        var noonUtc = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local).ToUniversalTime();

        await using var db = new NepPosDbContext(_options);
        var zSequence = await db.JournalEntries
            .Where(j => j.EventType == JournalEventType.ZReport)
            .MaxAsync(j => j.Sequence);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE JournalEntries SET OccurredAtUtc = {0} WHERE Sequence = {1}",
            noonUtc.ToString("yyyy-MM-dd HH:mm:ss"), zSequence);
    }

    private async Task ThreeClosedDaysAndAnOpenOneAsync()
    {
        await SellAsync(10.00m);
        await CloseDayOnAsync(Today.AddDays(-3));      // Z #1: $10

        await SellAsync(20.00m);
        await SellAsync(30.00m);
        await CloseDayOnAsync(Today.AddDays(-2));      // Z #2: $50

        await SellAsync(40.00m);
        await CloseDayOnAsync(Today.AddDays(-1));      // Z #3: $40

        await SellAsync(50.00m);                       // today, not closed yet
    }

    [Fact]
    public async Task GetPeriodAsync_AddsUpTheClosedDaysInTheRange()
    {
        await ThreeClosedDaysAndAnOpenOneAsync();

        var report = await _reports.GetPeriodAsync(Today.AddDays(-3), Today.AddDays(-2), "PERIOD REPORT");

        Assert.Equal(3, report.SaleCount);
        Assert.Equal(60.00m, report.GrossSales);
        Assert.Equal(new[] { 1, 2 }, report.Days.Select(d => d.ZNumber));
        Assert.Equal(new[] { 10.00m, 50.00m }, report.Days.Select(d => d.GrossSales));
        Assert.Equal("PERIOD REPORT", report.Title);
    }

    [Fact]
    public async Task GetPeriodAsync_SingleDayInTheMiddle_OnlyThatDay()
    {
        await ThreeClosedDaysAndAnOpenOneAsync();

        var report = await _reports.GetPeriodAsync(Today.AddDays(-2), Today.AddDays(-2), "PERIOD REPORT");

        Assert.Equal(2, report.SaleCount);
        Assert.Equal(50.00m, report.GrossSales);
        Assert.Equal(2, Assert.Single(report.Days).ZNumber);
    }

    [Fact]
    public async Task GetPeriodAsync_NeverIncludesTheDayStillOpen()
    {
        await ThreeClosedDaysAndAnOpenOneAsync();

        var report = await _reports.GetPeriodAsync(Today.AddDays(-7), Today, "WEEKLY REPORT");

        Assert.Equal(4, report.SaleCount);              // $10 + $20 + $30 + $40, not today's $50
        Assert.Equal(100.00m, report.GrossSales);
        Assert.Equal(3, report.Days.Count);
    }

    [Fact]
    public async Task GetPeriodAsync_NoClosedDays_ReturnsAnEmptyReport()
    {
        await ThreeClosedDaysAndAnOpenOneAsync();

        var report = await _reports.GetPeriodAsync(Today.AddDays(-30), Today.AddDays(-20), "MONTHLY REPORT");

        Assert.Equal(0, report.SaleCount);
        Assert.Empty(report.Days);
        Assert.Equal(2, report.Tenders.Count);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class SignedInOperator : ICurrentOperator
    {
        public int? EmployeeId => 1;
        public string? EmployeeName => "Owner";
    }
}
