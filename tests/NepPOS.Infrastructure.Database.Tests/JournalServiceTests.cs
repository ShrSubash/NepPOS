using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Journal;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

public class JournalServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private readonly FakeOperator _operator = new();
    private readonly JournalService _journal;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public JournalServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NepPosDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var db = new NepPosDbContext(_options))
            db.Database.EnsureCreated();

        _journal = new JournalService(
            _options,
            new RegisterTerminal("STORE-001", "REG-001"),
            _operator,
            NullLogger<JournalService>.Instance);
    }

    [Fact]
    public async Task WriteAsync_StampsRegisterEmployeeAndNumbersEntriesInOrder()
    {
        _operator.SignIn(7, "Test Cashier");

        await _journal.WriteAsync(JournalEventType.SignIn, "Signed in");
        await _journal.WriteAsync(JournalEventType.SaleCompleted, "Invoice #1", Guid.NewGuid(), 1, 19.50m);

        var entries = await _journal.QueryAsync(new JournalQuery(Today, Today));

        Assert.Equal(2, entries.Count);
        Assert.Equal(new long[] { 2, 1 }, entries.Select(e => e.Sequence)); // newest first

        var sale = entries[0];
        Assert.Equal(JournalEventType.SaleCompleted, sale.EventType);
        Assert.Equal("STORE-001", sale.StoreId);
        Assert.Equal("REG-001", sale.RegisterId);
        Assert.Equal(7, sale.EmployeeId);
        Assert.Equal("Test Cashier", sale.EmployeeName);
        Assert.Equal(1, sale.InvoiceNumber);
        Assert.Equal(19.50m, sale.Amount);
        Assert.Equal(DateTimeKind.Utc, sale.OccurredAtUtc.Kind);
    }

    [Fact]
    public async Task QueryAsync_FiltersByEmployeeAndEventType()
    {
        _operator.SignIn(1, "Store Owner");
        await _journal.WriteAsync(JournalEventType.SaleVoided, "Cart voided", amount: 38.49m);

        _operator.SignIn(2, "Test Cashier");
        await _journal.WriteAsync(JournalEventType.SaleVoided, "Cart voided", amount: 10.00m);
        await _journal.WriteAsync(JournalEventType.SaleCompleted, "Invoice #1", invoiceNumber: 1, amount: 19.50m);

        var cashierVoids = await _journal.QueryAsync(
            new JournalQuery(Today, Today, EmployeeId: 2, EventType: JournalEventType.SaleVoided));

        var entry = Assert.Single(cashierVoids);
        Assert.Equal("Test Cashier", entry.EmployeeName);
        Assert.Equal(10.00m, entry.Amount);
    }

    [Fact]
    public async Task QueryAsync_OnlyReturnsTheRequestedDays()
    {
        await _journal.WriteAsync(JournalEventType.NoSale, "Drawer opened");

        var yesterday = Today.AddDays(-1);

        Assert.Empty(await _journal.QueryAsync(new JournalQuery(yesterday, yesterday)));
        Assert.Single(await _journal.QueryAsync(new JournalQuery(yesterday, Today)));
    }

    [Fact]
    public async Task WriteAsync_NeverThrows_WhenTheDatabaseIsBroken()
    {
        // A different, empty in-memory database: the JournalEntries table does not exist.
        using var emptyConnection = new SqliteConnection("DataSource=:memory:");
        emptyConnection.Open();
        var brokenOptions = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(emptyConnection).Options;

        var journal = new JournalService(
            brokenOptions, new RegisterTerminal("STORE-001", "REG-001"), _operator, NullLogger<JournalService>.Instance);

        var error = await Record.ExceptionAsync(() => journal.WriteAsync(JournalEventType.SignIn, "Signed in"));

        Assert.Null(error); // logged, not thrown — a journal fault must never block the register
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FakeOperator : ICurrentOperator
    {
        public int? EmployeeId { get; private set; }
        public string? EmployeeName { get; private set; }

        public void SignIn(int id, string name)
        {
            EmployeeId = id;
            EmployeeName = name;
        }
    }
}
