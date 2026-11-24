using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Customers;
using NepPOS.Application.Payments;
using NepPOS.Application.Stock;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Customers;
using NepPOS.Infrastructure.Database.Stock;
using NepPOS.Infrastructure.Database.Sync;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>
/// Customer accounts written with the sale, supplier bills from deliveries, and what other stores share
/// (customers, account lines, gift card movements) applied once each — against a real in-memory database.
/// </summary>
public class AccountsAndSharingTests : IDisposable
{
    private static readonly RegisterTerminal Terminal = new("STORE-001", "REG-001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NepPosDbContext> _options;
    private long _invoice;

    public AccountsAndSharingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(_connection).Options;

        using var db = new NepPosDbContext(_options);
        db.Database.EnsureCreated();
    }

    private NepPosDbContext Db() => new(_options);

    private async Task<Customer> AddCustomerAsync(bool account, decimal limit = 0)
    {
        await using var db = Db();
        return await new CustomerService(db, Terminal).SaveAsync(
            new CustomerInput(null, "Corner Café", "0412 555 019", null, null, null, null, null, account, limit, 14));
    }

    private async Task SaveAsync(SaleTransaction sale, IPaymentSession payments)
    {
        sale.RecordPayments(payments.Payments, payments.RoundingAdjustment);
        sale.Complete(++_invoice);
        await using var db = Db();
        await new SaleTransactionRepository(db).AddAsync(sale);
    }

    private async Task<decimal> BalanceAsync(Guid customerId)
    {
        await using var db = Db();
        return (await db.Customers.SingleAsync(c => c.Id == customerId)).Balance;
    }

    [Fact]
    public async Task A_sale_on_account_adds_a_charge_due_after_their_terms()
    {
        var customer = await AddCustomerAsync(account: true);

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("1", "Milk 2L", 10, 3.50m, 0m));
        sale.SetCustomer(customer.Id, customer.Name);
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddAccount(customer.Code, customer.Name, 0m);
        await SaveAsync(sale, tender);

        Assert.Equal(35m, await BalanceAsync(customer.Id));
        await using var db = Db();
        var entry = await db.CustomerAccountEntries.SingleAsync();
        Assert.Equal((AccountEntryKind.Charge, 35m, sale.Id), (entry.Kind, entry.Amount, entry.SaleId!.Value));
        Assert.Equal(14, Math.Round((entry.DueUtc!.Value - entry.AtUtc).TotalDays));
        Assert.True(await db.OutboxMessages.AnyAsync(o => o.Type == SyncEventTypes.AccountEntry));
        Assert.True(await db.OutboxMessages.AnyAsync(o => o.Type == SyncEventTypes.CustomerChanged));
    }

    [Fact]
    public async Task A_payment_on_account_takes_it_off()
    {
        var customer = await AddCustomerAsync(account: true);
        await using (var db = Db())
            await new CustomerService(db, Terminal).AdjustAsync(customer.Id, 120m, "Opening balance", "Owner");

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem(AccountPayment.Sku, AccountPayment.Name, 1, 100m, 0m, AccountPayment.Category));
        sale.SetCustomer(customer.Id, customer.Name);
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(100m);
        await SaveAsync(sale, tender);

        Assert.Equal(20m, await BalanceAsync(customer.Id));
    }

    [Fact]
    public async Task Only_account_customers_can_charge()
    {
        var customer = await AddCustomerAsync(account: false);

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("1", "Milk 2L", 1, 3.50m, 0m));
        sale.SetCustomer(customer.Id, customer.Name);
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddAccount(customer.Code, customer.Name, 0m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(sale, tender));
    }

    [Fact]
    public async Task Debtors_lists_who_owes_with_ageing()
    {
        var customer = await AddCustomerAsync(account: true);
        await AddCustomerAsync(account: true);   // owes nothing: not listed
        await using (var db = Db())
            await new CustomerService(db, Terminal).AdjustAsync(customer.Id, 75m, "Opening balance", null);

        await using var check = Db();
        var row = Assert.Single(await new CustomerService(check, Terminal).DebtorsAsync());
        Assert.Equal((customer.Id, 75m), (row.Customer.Id, row.Ageing.Total));
    }

    [Fact]
    public async Task A_delivery_bills_the_supplier_and_reversing_it_credits_them()
    {
        int supplierId, productId;
        await using (var db = Db())
        {
            var supplier = new Supplier { Name = "CUB", TermsDays = 30 };
            var product = new Product { Name = "Asahi Super Dry", Price = 5.50m, CostPrice = 2.42m, StockOnHand = 10, Category = "Beer" };
            db.Suppliers.Add(supplier);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            (supplierId, productId) = (supplier.Id, product.Id);
        }

        ReceiptPosted posted;
        await using (var db = Db())
            posted = await new StockReceivingService(db).PostAsync(new ReceiptInput(supplierId, "CUB", "INV-9", new DateOnly(2026, 11, 1),
                new[] { new ReceiptLineInput(productId, 24, "Case of 24", 2, 50m) }), "Sam");

        await using (var db = Db())
        {
            var bill = await db.SupplierAccountEntries.SingleAsync();
            Assert.Equal((SupplierEntryKind.Bill, 110m, "INV-9", new DateOnly(2026, 12, 1)), (bill.Kind, bill.Amount, bill.Reference, bill.DueDate!.Value));
            Assert.Equal(110m, (await db.Suppliers.SingleAsync(s => s.Id == supplierId)).Balance);
        }

        await using (var db = Db())
            await new StockReceivingService(db).ReverseAsync(posted.ReceiptId, "Wrong delivery", "Sam");

        await using var check = Db();
        Assert.Equal(0m, (await check.Suppliers.SingleAsync(s => s.Id == supplierId)).Balance);
        Assert.Equal(2, await check.SupplierAccountEntries.CountAsync());
    }

    // ---- shared by other stores ----

    private static SharedEvent Shared<T>(long id, string type, T payload, Guid? eventId = null) =>
        new(id, new SyncEvent(eventId ?? Guid.NewGuid(), type, 1, "STORE-002", "REG-001", id, DateTime.UtcNow,
            JsonSerializer.SerializeToElement(payload, SyncJson.Options)));

    [Fact]
    public async Task Shared_customers_accounts_and_gift_cards_are_applied_once()
    {
        var customerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var customer = Shared(1, SyncEventTypes.CustomerChanged, new CustomerChangedV1(customerId, "CUST-55550001", "Bowling Club",
            null, null, null, null, null, null, true, 5000m, 30, true, now, now));
        var charge = Shared(2, SyncEventTypes.AccountEntry, new AccountEntryV1(Guid.NewGuid(), customerId, "Charge", 240m,
            Guid.NewGuid(), 81, now, now.AddDays(30), null, "Sale on account"));
        var giftEventId = Guid.NewGuid();
        var sold = Shared(3, SyncEventTypes.GiftCardMoved, new GiftCardMovedV1("GC-1234567890", "GiftCard", 50m, Guid.NewGuid(), 82, now, "Sold"), giftEventId);

        await using (var db = Db())
            Assert.Equal(3, await new SharedDataStore(db).ApplyAsync(new[] { customer, charge, sold }, 3));

        // The same page again (e.g. after a crash before the cursor was saved): nothing changes twice.
        await using (var db = Db())
            Assert.Equal(0, await new SharedDataStore(db).ApplyAsync(new[] { customer, charge, sold }, 3));

        await using var check = Db();
        Assert.Equal(3, await new SharedDataStore(check).GetCursorAsync());
        var club = await check.Customers.SingleAsync(c => c.Id == customerId);
        Assert.Equal(("Bowling Club", 240m), (club.Name, club.Balance));
        var card = await check.GiftCards.SingleAsync(g => g.Code == "GC-1234567890");
        Assert.Equal(50m, card.Balance);
        Assert.Equal("STORE-002 / REG-001", (await check.GiftCardMovements.SingleAsync()).Where);
    }

    [Fact]
    public async Task An_older_customer_edit_never_replaces_a_newer_one()
    {
        var customer = await AddCustomerAsync(account: true);
        var older = Shared(1, SyncEventTypes.CustomerChanged, new CustomerChangedV1(customer.Id, customer.Code, "Old name",
            null, null, null, null, null, null, true, 0m, 14, true, customer.CreatedUtc, customer.UpdatedUtc.AddMinutes(-5)));

        await using (var db = Db())
            await new SharedDataStore(db).ApplyAsync(new[] { older }, 1);

        await using var check = Db();
        Assert.Equal("Corner Café", (await check.Customers.SingleAsync(c => c.Id == customer.Id)).Name);
    }

    [Fact]
    public async Task A_gift_card_sold_at_another_store_can_be_spent_here()
    {
        var sold = Shared(1, SyncEventTypes.GiftCardMoved, new GiftCardMovedV1("GC-9988776655", "GiftCard", 50m, null, 5, DateTime.UtcNow, "Sold"));
        await using (var db = Db())
            await new SharedDataStore(db).ApplyAsync(new[] { sold }, 1);

        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("1", "Wine", 1, 30m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddVoucher(TenderType.GiftCard, "GC-9988776655", 0m, 50m);
        await SaveAsync(sale, tender);

        await using var check = Db();
        Assert.Equal(20m, (await check.GiftCards.SingleAsync()).Balance);
        Assert.True(await check.OutboxMessages.AnyAsync(o => o.Type == SyncEventTypes.GiftCardMoved));
    }

    public void Dispose() => _connection.Dispose();
}
