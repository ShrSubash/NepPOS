using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database;
using Xunit;
using Microsoft.Data.Sqlite;

namespace NepPOS.Infrastructure.Database.Tests;

public class SaleTransactionRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NepPosDbContext _dbContext;
    private readonly SaleTransactionRepository _repository;

    public SaleTransactionRepositoryTests()
    {
        // In-memory SQLite requires the connection to stay open for the DB's lifetime —
        // closing it deletes the database, so we own the connection here explicitly.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NepPosDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new NepPosDbContext(options);
        _dbContext.Database.EnsureCreated();

        _repository = new SaleTransactionRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_PersistsSaleTransactionWithLineItems()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("SKU-001", "Test Product", 2, 11.00m, 0.10m));
        sale.Complete(1);

        await _repository.AddAsync(sale);

        var saved = await _dbContext.SaleTransactions
            .Include(st => st.LineItems)
            .FirstOrDefaultAsync(st => st.Id == sale.Id);

        Assert.NotNull(saved);
        Assert.Single(saved!.LineItems);
        Assert.Equal(1, saved.InvoiceNumber);
    }

    [Fact]
    public async Task GetNextInvoiceNumberAsync_WithNoExistingSales_ReturnsOne()
    {
        var next = await _repository.GetNextInvoiceNumberAsync("STORE-001");
        Assert.Equal(1, next);
    }

    [Fact]
    public async Task GetNextInvoiceNumberAsync_AfterOneSale_ReturnsTwo()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("SKU-001", "Test Product", 1, 11.00m, 0.10m));
        sale.Complete(1);
        await _repository.AddAsync(sale);

        var next = await _repository.GetNextInvoiceNumberAsync("STORE-001");
        Assert.Equal(2, next);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}