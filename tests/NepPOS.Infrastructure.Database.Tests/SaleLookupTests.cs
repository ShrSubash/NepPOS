using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>The journal's receipt view reloads a saved sale: lines, payments and who made it.</summary>
public class SaleLookupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NepPosDbContext _dbContext;
    private readonly SaleTransactionRepository _repository;

    public SaleLookupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;
        _dbContext = new NepPosDbContext(options);
        _dbContext.Database.EnsureCreated();

        _repository = new SaleTransactionRepository(_dbContext);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsSaleWithEmployeeLinesAndPayments()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001", employeeId: 2, employeeName: "Test Cashier");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", 1, 19.50m, 0.10m));

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddEftpos(19.50m, new PaymentResult(true, "000", "APPROVED", "851898", "Mastercard", "............1025"));

        await new CheckoutService(_repository).CompleteSaleAsync(sale, tender);
        _dbContext.ChangeTracker.Clear();

        var loaded = await _repository.GetByIdAsync(sale.Id);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.EmployeeId);
        Assert.Equal("Test Cashier", loaded.EmployeeName);
        Assert.Single(loaded.LineItems);
        var payment = Assert.Single(loaded.Payments);
        Assert.Equal(TenderType.Eftpos, payment.TenderType);
        Assert.Equal("851898", payment.AuthCode);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_ForASaleThatWasNeverSaved()
    {
        Assert.Null(await _repository.GetByIdAsync(Guid.NewGuid()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}
