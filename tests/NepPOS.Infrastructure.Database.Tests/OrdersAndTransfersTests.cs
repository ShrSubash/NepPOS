using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Customers;
using NepPOS.Application.Orders;
using NepPOS.Application.Payments;
using NepPOS.Contracts.Orders;
using NepPOS.Contracts.Sync;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Customers;
using NepPOS.Infrastructure.Database.Orders;
using NepPOS.Infrastructure.Database.Sync;
using Xunit;

namespace NepPOS.Infrastructure.Database.Tests;

/// <summary>Website orders picked and rung up at the store, and stock sent between two stores, against real in-memory databases.</summary>
public class OrdersAndTransfersTests : IDisposable
{
    private static readonly RegisterTerminal StoreA = new("STORE-001", "REG-001");
    private static readonly RegisterTerminal StoreB = new("STORE-002", "REG-001");

    private readonly SqliteConnection _a;
    private readonly SqliteConnection _b;
    private readonly DbContextOptions<NepPosDbContext> _optionsA;
    private readonly DbContextOptions<NepPosDbContext> _optionsB;
    private readonly Guid _riceSyncId = Guid.NewGuid();
    private long _sharedId;

    public OrdersAndTransfersTests()
    {
        (_a, _optionsA) = Open();
        (_b, _optionsB) = Open();
        using (var db = new NepPosDbContext(_optionsA))
        {
            db.Products.Add(new Product { Name = "Basmati Rice 5kg", Price = 16m, CostPrice = 9m, StockOnHand = 20, Barcode = "9300000000011", Plu = "2290", SyncId = _riceSyncId, Category = "Rice" });
            db.SaveChanges();
        }
        using (var db = new NepPosDbContext(_optionsB))
        {
            // The same rice at the other store, added there by hand: a different id, the same barcode.
            db.Products.Add(new Product { Name = "Basmati Rice 5 kg", Price = 16m, CostPrice = 9m, StockOnHand = 3, Barcode = "9300000000011", Category = "Rice" });
            db.SaveChanges();
        }
    }

    private static (SqliteConnection, DbContextOptions<NepPosDbContext>) Open()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NepPosDbContext>().UseSqlite(connection).Options;
        using var db = new NepPosDbContext(options);
        db.Database.EnsureCreated();
        return (connection, options);
    }

    private SharedEvent Shared<T>(string type, T payload, bool fromWebsite = false, string store = "STORE-009") =>
        new(++_sharedId, new SyncEvent(Guid.NewGuid(), type, 1, fromWebsite ? "WEB" : store, fromWebsite ? "WEBSITE" : "REG-001", _sharedId,
            DateTime.UtcNow, JsonSerializer.SerializeToElement(payload, SyncJson.Options)), fromWebsite);

    private static WebOrderPlacedV1 Order(Guid id, string store = "STORE-001", decimal prepaid = 37.50m, decimal onAccount = 0m, Guid? customerId = null, Guid? product = null) =>
        new(id, store, "ORD-1001", "Sita Sharma", "0400 000 111", null, customerId, Fulfilment.Pickup, null, "Ripe bananas please",
            DateTime.UtcNow, null,
            new[] { new WebOrderLineV1(product, "Basmati Rice 5kg", 2, 15m), new WebOrderLineV1(null, "Delivery fee", 1, 7.50m) },
            37.50m, prepaid, onAccount);

    private async Task ReceiveAsync(DbContextOptions<NepPosDbContext> options, RegisterTerminal terminal, params SharedEvent[] events)
    {
        await using var db = new NepPosDbContext(options);
        await new SharedDataStore(db, terminal).ApplyAsync(events, new SharedCursor(_sharedId, _sharedId));
    }

    [Fact]
    public async Task An_order_reaches_only_its_store_once()
    {
        var id = Guid.NewGuid();
        var placed = Shared(SyncEventTypes.OrderPlaced, Order(id, product: _riceSyncId), fromWebsite: true);
        await ReceiveAsync(_optionsA, StoreA, placed, placed);
        await ReceiveAsync(_optionsB, StoreB, placed);

        await using var a = new NepPosDbContext(_optionsA);
        var order = await a.WebOrders.Include(o => o.Lines).SingleAsync();
        Assert.Equal((WebOrderStatus.Placed, 2, 37.50m), (order.Status, order.Lines.Count, order.Prepaid));
        await using var b = new NepPosDbContext(_optionsB);
        Assert.False(await b.WebOrders.AnyAsync());
    }

    [Fact]
    public async Task Short_picked_order_is_rung_up_for_what_was_found_and_the_rest_refunded()
    {
        var id = Guid.NewGuid();
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderPlaced, Order(id, product: _riceSyncId), fromWebsite: true));

        await using (var db = new NepPosDbContext(_optionsA))
        {
            var service = new WebOrderService(db, StoreA);
            Assert.Equal(1, await service.WaitingCountAsync());
            await service.StartPickingAsync(id, "Sam");
            var order = await service.GetAsync(id);
            var rice = order!.Lines.Single(l => l.ProductSyncId == _riceSyncId);
            await service.SavePickedAsync(id, new Dictionary<int, int> { [rice.Id] = 1 });
            await service.MarkReadyAsync(id, "Sam");

            // One bag found: $15 + $7.50 fee; $37.50 was paid online, so $15 goes back.
            var prepared = await service.PrepareSaleAsync(id, 1, "Sam");
            Assert.Equal((22.50m, 22.50m, 0m, 15m), (prepared.Sale.GrandTotal, prepared.PaidOnline, prepared.DueAtStore, prepared.RefundDue));

            var tender = new TenderSession(prepared.Sale.Id, prepared.Sale.GrandTotal);
            prepared.Prepay(tender, "ORD-1001");
            Assert.True(tender.IsComplete);
            var invoice = await service.CompleteAsync(id, prepared, tender);
            Assert.True(invoice > 0);
        }

        await using (var check = new NepPosDbContext(_optionsA))
        {
            var order = await check.WebOrders.SingleAsync();
            Assert.Equal((WebOrderStatus.Completed, 15m, 22.50m), (order.Status, order.RefundDue, order.FinalTotal));
            Assert.Equal(19m, (await check.Products.SingleAsync()).StockOnHand);
            var sale = await check.SaleTransactions.Include(s => s.Payments).SingleAsync();
            Assert.Equal(WebOrderSale.PaidOnlineTender, sale.Payments.Single().TenderName);

            var statuses = (await check.OutboxMessages.Where(o => o.Type == SyncEventTypes.OrderStatus).OrderBy(o => o.Id).ToListAsync())
                .Select(o => JsonSerializer.Deserialize<WebOrderStatusV1>(o.Payload, SyncJson.Options)!.Status).ToList();
            Assert.Equal(new[] { "Picking", "Ready", "Completed" }, statuses);
            Assert.True(await check.OutboxMessages.AnyAsync(o => o.Type == SyncEventTypes.SaleCompleted));
        }
    }

    [Fact]
    public async Task Pay_at_the_store_part_is_left_for_the_cashier_and_account_part_goes_on_the_account()
    {
        Customer customer;
        await using (var db = new NepPosDbContext(_optionsA))
            customer = await new CustomerService(db, StoreA).SaveAsync(
                new CustomerInput(null, "Corner Café", "0412 555 019", null, null, null, null, null, true, 500, 14));

        var id = Guid.NewGuid();
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderPlaced,
            Order(id, prepaid: 10m, onAccount: 20m, customerId: customer.Id, product: _riceSyncId), fromWebsite: true));

        await using var service = new NepPosDbContext(_optionsA);
        var orders = new WebOrderService(service, StoreA);
        await orders.MarkReadyAsync(id, "Sam");
        var prepared = await orders.PrepareSaleAsync(id, 1, "Sam");
        Assert.Equal((10m, 20m, 7.50m), (prepared.PaidOnline, prepared.OnAccount, prepared.DueAtStore));

        var tender = new TenderSession(prepared.Sale.Id, prepared.Sale.GrandTotal);
        prepared.Prepay(tender, "ORD-1001");
        Assert.False(tender.IsComplete);
        tender.AddCash(10m);
        await orders.CompleteAsync(id, prepared, tender);

        await using var check = new NepPosDbContext(_optionsA);
        Assert.Equal(20m, (await check.Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task Cancelled_on_the_website_before_picking_and_a_finished_order_never_reopens()
    {
        var id = Guid.NewGuid();
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderPlaced, Order(id), fromWebsite: true));
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderStatus,
            new WebOrderStatusV1(id, WebOrderStatuses.Cancelled, DateTime.UtcNow, Note: "Changed my mind", By: "Website"), fromWebsite: true));
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderStatus, new WebOrderStatusV1(id, WebOrderStatuses.Picking, DateTime.UtcNow)));

        await using var db = new NepPosDbContext(_optionsA);
        var order = await db.WebOrders.SingleAsync();
        Assert.Equal((WebOrderStatus.Cancelled, "Changed my mind"), (order.Status, order.CancelReason));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WebOrderService(db, StoreA).StartPickingAsync(id, "Sam"));
    }

    [Fact]
    public async Task Store_cancel_refunds_everything_prepaid_and_needs_a_reason()
    {
        var id = Guid.NewGuid();
        await ReceiveAsync(_optionsA, StoreA, Shared(SyncEventTypes.OrderPlaced, Order(id), fromWebsite: true));

        await using var db = new NepPosDbContext(_optionsA);
        var service = new WebOrderService(db, StoreA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(id, " ", "Sam"));
        await service.CancelAsync(id, "Out of rice", "Sam");

        var order = await service.GetAsync(id);
        Assert.Equal((WebOrderStatus.Cancelled, 37.50m), (order!.Status, order.RefundDue));
        Assert.Equal(0, await service.WaitingCountAsync());
    }

    [Fact]
    public async Task Transfer_moves_stock_out_at_one_store_and_in_at_the_other_matched_by_barcode()
    {
        SharedEvent sent;
        await using (var a = new NepPosDbContext(_optionsA))
        {
            var rice = await a.Products.SingleAsync();
            var transfer = await new StockTransferService(a, StoreA).SendAsync("store-002", new[] { new TransferItem(rice.Id, 6) }, "For the weekend", "Sam");
            Assert.Equal(("TR-STORE-001-00001", "STORE-002", 54m), (transfer.Number, transfer.ToStore, transfer.Value));
            Assert.Equal(14m, (await a.Products.AsNoTracking().SingleAsync()).StockOnHand);

            var message = await a.OutboxMessages.SingleAsync(o => o.Type == SyncEventTypes.TransferSent);
            sent = Shared(SyncEventTypes.TransferSent, JsonSerializer.Deserialize<TransferSentV1>(message.Payload, SyncJson.Options)!, store: "STORE-001");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new StockTransferService(a, StoreA).SendAsync("STORE-001", new[] { new TransferItem(rice.Id, 1) }, null, "Sam"));
        }

        await ReceiveAsync(_optionsA, StoreA, sent);   // the sender's own event is never "incoming"
        await ReceiveAsync(_optionsB, StoreB, sent);

        SharedEvent received;
        await using (var b = new NepPosDbContext(_optionsB))
        {
            var service = new StockTransferService(b, StoreB);
            Assert.Equal(1, await service.IncomingCountAsync());
            var incoming = (await service.ListAsync()).Single();
            var match = Assert.Single(await service.MatchAsync(incoming.Id));
            Assert.Equal("Basmati Rice 5 kg", match.MatchedName);

            var lineId = incoming.Lines.Single().Id;
            await service.ReceiveAsync(incoming.Id, new Dictionary<int, decimal> { [lineId] = 5 }, "Ram");
            Assert.Equal(8m, (await b.Products.AsNoTracking().SingleAsync()).StockOnHand);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveAsync(incoming.Id, null, "Ram"));

            var message = await b.OutboxMessages.SingleAsync(o => o.Type == SyncEventTypes.TransferReceived);
            received = Shared(SyncEventTypes.TransferReceived, JsonSerializer.Deserialize<TransferReceivedV1>(message.Payload, SyncJson.Options)!, store: "STORE-002");
        }

        await ReceiveAsync(_optionsA, StoreA, received);
        await using var check = new NepPosDbContext(_optionsA);
        var mine = await check.StockTransfers.Include(t => t.Lines).SingleAsync();
        Assert.Equal((StockTransferStatus.Received, 5m), (mine.Status, mine.Lines.Single().Received));
        Assert.Equal(14m, (await check.Products.SingleAsync()).StockOnHand);   // nothing more moves at the sender
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}
