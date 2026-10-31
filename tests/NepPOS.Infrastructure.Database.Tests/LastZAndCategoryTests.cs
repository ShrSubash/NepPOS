using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Journal;
using NepPOS.Infrastructure.Database.Reports;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>"Last Z (reprint)" rebuilds the closed day exactly; the department is kept with each sold line.</summary>
public class LastZAndCategoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly EndOfDayReportService _reports;

    public LastZAndCategoryTests()
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

    private async Task SellAsync(decimal price, string category)
    {
        await using var db = new NepPosDbContext(_options);
        var sale = new SaleTransaction("STORE-001", "REG-001", 1, "Owner");
        sale.AddLineItem(new SaleLineItem("101", "Test item", 1, price, 0.10m, category));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(price);
        await new CheckoutService(new SaleTransactionRepository(db)).CompleteSaleAsync(sale, tender);
    }

    [Fact]
    public async Task GetLastZAsync_IsNull_BeforeAnyDayIsClosed()
    {
        Assert.Null(await _reports.GetLastZAsync());
    }

    [Fact]
    public async Task GetLastZAsync_RebuildsTheLastClosedDayOnly()
    {
        await SellAsync(10.00m, "Beer");
        await _reports.CloseDayAsync();                 // Z #1

        await SellAsync(20.00m, "Wine");
        await SellAsync(5.00m, "Beer");
        var closed = await _reports.CloseDayAsync();    // Z #2

        await SellAsync(99.00m, "Spirits");             // today, still open

        var reprint = await _reports.GetLastZAsync();

        Assert.NotNull(reprint);
        Assert.True(reprint!.IsZ);
        Assert.True(reprint.IsReprint);
        Assert.Equal(2, reprint.ZNumber);
        Assert.Equal(closed.GrossSales, reprint.GrossSales);
        Assert.Equal(25.00m, reprint.GrossSales);
        Assert.Equal(new[] { "Wine", "Beer" }, reprint.Departments.Select(d => d.Department));
    }

    [Fact]
    public async Task CategoryIsSavedWithTheSoldLine()
    {
        await SellAsync(10.00m, "Beer");

        await using var db = new NepPosDbContext(_options);
        var line = await db.SaleLineItems.SingleAsync();

        Assert.Equal("Beer", line.Category);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class SignedInOperator : ICurrentOperator
    {
        public int? EmployeeId => 1;
        public string? EmployeeName => "Owner";
    }
}
