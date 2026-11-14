using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Payments;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Journal;
using NepPOS.Infrastructure.Database.Outbox;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Integration build step 1: every sale and journal line leaves exactly one event for Head Office.</summary>
public class OutboxTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;

    public OutboxTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
    }

    private static SaleTransaction PaidSale(long invoiceNumber)
    {
        var sale = new SaleTransaction("STORE-001", "REG-001", 1, "Owner");
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 2, 22.00m, 0.10m, "Beer"));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddEftpos(44.00m, new PaymentResult(true, "00", "APPROVED", "851898", "Mastercard", "....1025"));
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(invoiceNumber);
        return sale;
    }

    [Fact]
    public async Task CompletedSale_WritesOneSaleCompletedEvent()
    {
        await using (var db = new NepPosDbContext(_options))
            await new SaleTransactionRepository(db).AddAsync(PaidSale(1001));

        await using var check = new NepPosDbContext(_options);
        var message = Assert.Single(await check.OutboxMessages.ToListAsync());
        Assert.Equal(SyncEventTypes.SaleCompleted, message.Type);
        Assert.Null(message.SentAtUtc);

        var payload = JsonSerializer.Deserialize<SaleCompletedV1>(message.Payload, SyncJson.Options)!;
        Assert.Equal(1001, payload.InvoiceNumber);
        Assert.Equal(44.00m, payload.Total);
        Assert.Equal("Beer", Assert.Single(payload.Lines).Category);
        var card = Assert.Single(payload.Payments);
        Assert.Equal(("Eftpos", "....1025", "851898"), (card.Tender, card.MaskedPan, card.AuthCode));
        Assert.Contains("\"invoiceNumber\":1001", message.Payload);   // camelCase JSON, as documented
    }

    [Fact]
    public async Task FailedSave_WritesNeitherTheSaleNorItsEvent()
    {
        await using (var db = new NepPosDbContext(_options))
            await new SaleTransactionRepository(db).AddAsync(PaidSale(1001));

        // Same invoice number again: the unique index rejects the sale, and its event goes with it.
        await using (var db = new NepPosDbContext(_options))
            await Assert.ThrowsAsync<DbUpdateException>(() => new SaleTransactionRepository(db).AddAsync(PaidSale(1001)));

        await using var check = new NepPosDbContext(_options);
        Assert.Equal(1, await check.SaleTransactions.CountAsync());
        Assert.Equal(1, await check.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task JournalLine_WritesOneJournalRecordedEvent()
    {
        var journal = new JournalService(_options, new RegisterTerminal("STORE-001", "REG-001"),
            new SignedInOperator(), NullLogger<JournalService>.Instance);

        await journal.WriteAsync(JournalEventType.ZReport, "Z #1: 3 sales", invoiceNumber: 1003, amount: 120.50m);

        await using var check = new NepPosDbContext(_options);
        var message = Assert.Single(await check.OutboxMessages.ToListAsync());
        Assert.Equal(SyncEventTypes.JournalRecorded, message.Type);

        var payload = JsonSerializer.Deserialize<JournalRecordedV1>(message.Payload, SyncJson.Options)!;
        Assert.Equal(("ZReport", 1003L, 120.50m, 1L), (payload.EventType, payload.InvoiceNumber!.Value, payload.Amount!.Value, payload.JournalSequence));
        Assert.Equal((await check.JournalEntries.SingleAsync()).Id, payload.JournalId);
    }

    [Fact]
    public async Task Events_CountUpWithNoGaps_AndBecomeSyncEnvelopes()
    {
        var journal = new JournalService(_options, new RegisterTerminal("STORE-001", "REG-001"),
            new SignedInOperator(), NullLogger<JournalService>.Instance);

        await journal.WriteAsync(JournalEventType.SignIn, "Owner signed in");
        await using (var db = new NepPosDbContext(_options))
            await new SaleTransactionRepository(db).AddAsync(PaidSale(1001));
        await using (var db = new NepPosDbContext(_options))
            await Assert.ThrowsAnyAsync<Exception>(() => new SaleTransactionRepository(db).AddAsync(PaidSale(1001)));
        await journal.WriteAsync(JournalEventType.NoSale, "Drawer opened");

        await using var check = new NepPosDbContext(_options);
        var events = (await check.OutboxMessages.OrderBy(o => o.Id).ToListAsync()).Select(o => o.ToSyncEvent()).ToList();

        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Sequence));      // the failed sale left no gap
        Assert.Equal(3, events.Select(e => e.EventId).Distinct().Count());
        Assert.All(events, e => Assert.Equal(("STORE-001", "REG-001", 1), (e.StoreId, e.RegisterId, e.Version)));
        Assert.Equal(1001, events[1].Payload.GetProperty("invoiceNumber").GetInt64());
    }

    public void Dispose() => _connection.Dispose();

    private sealed class SignedInOperator : ICurrentOperator
    {
        public int? EmployeeId => 1;
        public string? EmployeeName => "Owner";
    }
}
