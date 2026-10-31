using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Journal;
using NepPOS.Infrastructure.Database.Reports;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>X shows the open day; Z closes it so the next report starts from zero.</summary>
public class EndOfDayReportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly JournalService _journal;
    private readonly EndOfDayReportService _reports;

    public EndOfDayReportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using (var db = new NepPosDbContext(_options))
            db.Database.EnsureCreated();

        var terminal = new RegisterTerminal("STORE-001", "REG-001");
        var signedIn = new SignedInOperator();
        _journal = new JournalService(_options, terminal, signedIn, NullLogger<JournalService>.Instance);
        _reports = new EndOfDayReportService(_options, terminal, _journal, signedIn);
    }

    private async Task<long> SellAsync(decimal price, string registerId = "REG-001")
    {
        await using var db = new NepPosDbContext(_options);
        var sale = new SaleTransaction("STORE-001", registerId, 1, "Owner");
        sale.AddLineItem(new SaleLineItem("101", "Test item", 1, price, 0.10m));

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(price);

        return await new CheckoutService(new SaleTransactionRepository(db)).CompleteSaleAsync(sale, tender);
    }

    [Fact]
    public async Task XReading_ShowsTheOpenDayWithoutClosingIt()
    {
        await SellAsync(10.00m);
        await SellAsync(5.00m);
        await _journal.WriteAsync(JournalEventType.NoSale, "Drawer opened");

        var first = await _reports.GetCurrentAsync();
        var again = await _reports.GetCurrentAsync();

        Assert.False(first.IsZ);
        Assert.Equal(2, first.SaleCount);
        Assert.Equal(15.00m, first.GrossSales);
        Assert.Equal(15.00m, first.CashExpected);
        Assert.Equal(1, first.NoSales);
        Assert.Equal(1, first.ZNumber);           // the number the first Z will get
        Assert.Equal(first.GrossSales, again.GrossSales); // an X never resets anything
        Assert.Equal("Owner", first.PrintedBy);
    }

    [Fact]
    public async Task ZReport_ClosesTheDaySoTheNextPeriodStartsFromZero()
    {
        await SellAsync(10.00m);
        await _journal.WriteAsync(JournalEventType.NoSale, "Drawer opened");

        var z1 = await _reports.CloseDayAsync();

        Assert.True(z1.IsZ);
        Assert.Equal(1, z1.ZNumber);
        Assert.Equal(1, z1.SaleCount);
        Assert.Equal(1, z1.NoSales);

        var afterClose = await _reports.GetCurrentAsync();
        Assert.Equal(0, afterClose.SaleCount);
        Assert.Equal(0, afterClose.NoSales);
        Assert.Equal(2, afterClose.ZNumber);

        var nextInvoice = await SellAsync(7.50m);
        var nextDay = await _reports.GetCurrentAsync();
        Assert.Equal(1, nextDay.SaleCount);
        Assert.Equal(7.50m, nextDay.GrossSales);
        Assert.Equal(nextInvoice, nextDay.FirstInvoice);
    }

    [Fact]
    public async Task ZReport_WithNoSales_StillClosesAndKeepsTheInvoiceBoundary()
    {
        await SellAsync(10.00m);
        await _reports.CloseDayAsync();

        var emptyZ = await _reports.CloseDayAsync();   // nothing sold since the last Z
        Assert.Equal(2, emptyZ.ZNumber);
        Assert.Equal(0, emptyZ.SaleCount);

        // The sale before the first Z must not reappear after an empty Z.
        Assert.Equal(0, (await _reports.GetCurrentAsync()).SaleCount);
    }

    [Fact]
    public async Task Reports_OnlyCountThisRegister()
    {
        await SellAsync(10.00m);
        await SellAsync(99.00m, registerId: "REG-002");

        var report = await _reports.GetCurrentAsync();

        Assert.Equal(1, report.SaleCount);
        Assert.Equal(10.00m, report.GrossSales);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class SignedInOperator : ICurrentOperator
    {
        public int? EmployeeId => 1;
        public string? EmployeeName => "Owner";
    }
}
