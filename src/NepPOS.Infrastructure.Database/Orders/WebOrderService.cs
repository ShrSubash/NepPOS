using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Application.Orders;
using NepPOS.Application.Payments;
using NepPOS.Contracts.Orders;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Orders;

/// <summary>
/// Website orders at this store. Every change goes to Head Office as <c>order.status</c> (so the website and the
/// store's other registers see it) in the same save as the change itself.
/// </summary>
public sealed class WebOrderService : IWebOrderService
{
    /// <summary>The SKU on a line the store doesn't stock as a product (a delivery fee).</summary>
    public const string NonStockSku = "WEB";

    private const decimal GstRate = 0.10m;

    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal _terminal;
    private readonly IJournal? _journal;
    private bool _completionQueued;

    public WebOrderService(NepPosDbContext db, RegisterTerminal terminal, IJournal? journal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _journal = journal;
    }

    public async Task<IReadOnlyList<WebOrder>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        var open = await _db.WebOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.Status == WebOrderStatus.Placed || o.Status == WebOrderStatus.Picking || o.Status == WebOrderStatus.Ready)
            .ToListAsync(cancellationToken);
        var list = open.OrderBy(o => o.WantedUtc ?? o.PlacedUtc).ToList();
        if (includeClosed)
            list.AddRange((await _db.WebOrders.AsNoTracking().Include(o => o.Lines)
                    .Where(o => o.Status == WebOrderStatus.Completed || o.Status == WebOrderStatus.Cancelled)
                    .ToListAsync(cancellationToken))
                .OrderByDescending(o => o.UpdatedUtc)
                .Take(50));
        return list;
    }

    public Task<WebOrder?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.WebOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<int> WaitingCountAsync(CancellationToken cancellationToken = default) =>
        _db.WebOrders.CountAsync(o => o.Status == WebOrderStatus.Placed || o.Status == WebOrderStatus.Picking, cancellationToken);

    public async Task StartPickingAsync(Guid id, string? employeeName, CancellationToken cancellationToken = default)
    {
        var order = await OpenAsync(id, cancellationToken);
        if (order.Status != WebOrderStatus.Placed)
            return;   // already being picked (or ready): nothing to tell anyone
        order.Status = WebOrderStatus.Picking;
        order.PickedBy = employeeName;
        await SaveAsync(order, new WebOrderStatusV1(order.Id, WebOrderStatuses.Picking, DateTime.UtcNow, By: employeeName), cancellationToken);
    }

    public async Task SavePickedAsync(Guid id, IReadOnlyDictionary<int, int> picked, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picked);
        var order = await OpenAsync(id, cancellationToken);
        foreach (var line in order.Lines)
            if (picked.TryGetValue(line.Id, out var quantity))
            {
                if (quantity < 0 || quantity > line.Quantity)
                    throw new InvalidOperationException($"{line.Name}: between 0 and {line.Quantity}.");
                line.Picked = quantity;
            }
        order.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkReadyAsync(Guid id, string? employeeName, CancellationToken cancellationToken = default)
    {
        var order = await OpenAsync(id, cancellationToken);
        foreach (var line in order.Lines)
            line.Picked ??= line.Quantity;
        if (order.Lines.All(l => l.Picked == 0))
            throw new InvalidOperationException("Nothing was found. Cancel the order instead.");
        order.Status = WebOrderStatus.Ready;
        order.PickedBy ??= employeeName;
        await SaveAsync(order, new WebOrderStatusV1(order.Id, WebOrderStatuses.Ready, DateTime.UtcNow, Picked(order),
            FinalTotal: Total(order), RefundDue: Refund(order), By: employeeName), cancellationToken);
    }

    public async Task<WebOrderSale> PrepareSaleAsync(Guid id, int? employeeId, string? employeeName, CancellationToken cancellationToken = default)
    {
        var order = await _db.WebOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That order no longer exists.");
        if (!order.IsOpen)
            throw new InvalidOperationException($"Order {order.Reference} is already {order.Status.ToString().ToLowerInvariant()}.");
        if (order.Lines.All(l => l.Supplied == 0))
            throw new InvalidOperationException("Nothing was found. Cancel the order instead.");

        var syncIds = order.Lines.Where(l => l.ProductSyncId is not null).Select(l => l.ProductSyncId!.Value).ToList();
        var products = await _db.Products.AsNoTracking().Where(p => syncIds.Contains(p.SyncId)).ToDictionaryAsync(p => p.SyncId, cancellationToken);

        var sale = new SaleTransaction(_terminal.StoreId, _terminal.RegisterId, employeeId, employeeName);
        foreach (var line in order.Lines.OrderBy(l => l.LineNumber).Where(l => l.Supplied > 0))
        {
            // The website's price is what the customer agreed to pay; stock and GST follow the store's product.
            if (line.ProductSyncId is { } syncId && products.TryGetValue(syncId, out var product))
                sale.AddLineItem(new SaleLineItem(product.Plu ?? product.Barcode ?? line.Sku ?? NonStockSku, product.Name, line.Supplied,
                    line.UnitPrice, product.IsGstFree ? 0m : GstRate, product.Category, product.Id));
            else
                sale.AddLineItem(new SaleLineItem(line.Sku ?? NonStockSku, line.Name, line.Supplied, line.UnitPrice, GstRate, "Website"));
        }

        Customer? customer = null;
        if (order.CustomerId is { } customerId)
            customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        sale.SetCustomer(customer?.Id, customer?.Name ?? order.CustomerName);

        var total = sale.GrandTotal;
        var paidOnline = Math.Min(order.Prepaid, total);
        var onAccount = customer is { IsAccount: true } ? Math.Min(order.OnAccount, total - paidOnline) : 0m;
        if (order.OnAccount > 0 && onAccount == 0 && total > paidOnline)
            throw new InvalidOperationException($"{order.CustomerName} isn't set up as an account customer here yet. Wait for the customer to sync, or take payment instead.");
        return new WebOrderSale(sale, paidOnline, onAccount, customer, total - paidOnline - onAccount, Math.Max(0, order.Prepaid - total));
    }

    public async Task<long> CompleteAsync(Guid id, WebOrderSale prepared, IPaymentSession tender, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(tender);
        var sale = prepared.Sale;
        if (!tender.IsComplete)
            throw new InvalidOperationException("The order is not fully paid yet.");
        if (tender.TransactionId != sale.Id)
            throw new InvalidOperationException("These payments belong to a different sale.");

        var repository = new SaleTransactionRepository(_db);
        if (sale.Status == SaleTransactionStatus.InProgress)
        {
            if (sale.Payments.Count == 0)
                sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
            sale.Complete(await repository.GetNextInvoiceNumberAsync(sale.StoreId, cancellationToken));
        }

        // The order, its status event and the sale (with its stock and payments) go in one save.
        if (!_completionQueued)
        {
            var order = await OpenAsync(id, cancellationToken);
            foreach (var line in order.Lines)
                line.Picked ??= line.Quantity;
            order.Status = WebOrderStatus.Completed;
            order.SaleId = sale.Id;
            order.InvoiceNumber = sale.InvoiceNumber;
            order.FinalTotal = sale.GrandTotal;
            order.RefundDue = prepared.RefundDue;
            order.UpdatedUtc = DateTime.UtcNow;
            _db.OutboxMessages.Add(OutboxMessage.WebOrderStatus(new WebOrderStatusV1(order.Id, WebOrderStatuses.Completed, DateTime.UtcNow,
                Picked(order), sale.GrandTotal, prepared.RefundDue, sale.InvoiceNumber, By: sale.EmployeeName), _terminal.StoreId, _terminal.RegisterId));
            _completionQueued = true;
        }

        await repository.AddAsync(sale, cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.WebOrderCompleted,
                $"Website order {prepared.Sale.CustomerName}: invoice {sale.InvoiceNumber}" + (prepared.RefundDue > 0 ? $", {prepared.RefundDue:C} to refund online" : ""),
                saleId: sale.Id, amount: sale.GrandTotal, cancellationToken: cancellationToken);
        return sale.InvoiceNumber!.Value;
    }

    public async Task CancelAsync(Guid id, string reason, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Say why, so the website can tell the customer.");
        var order = await OpenAsync(id, cancellationToken);
        order.Status = WebOrderStatus.Cancelled;
        order.CancelReason = reason.Trim().Length > 500 ? reason.Trim()[..500] : reason.Trim();
        order.RefundDue = order.Prepaid;
        await SaveAsync(order, new WebOrderStatusV1(order.Id, WebOrderStatuses.Cancelled, DateTime.UtcNow,
            RefundDue: order.Prepaid, Note: order.CancelReason, By: employeeName), cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.WebOrderCancelled,
                $"Website order {order.Reference} ({order.CustomerName}) cancelled by {employeeName ?? "unknown"}: {order.CancelReason}",
                amount: order.Total, cancellationToken: cancellationToken);
    }

    private async Task<WebOrder> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await _db.WebOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That order no longer exists.");
        if (!order.IsOpen)
            throw new InvalidOperationException($"Order {order.Reference} is already {order.Status.ToString().ToLowerInvariant()}.");
        return order;
    }

    private async Task SaveAsync(WebOrder order, WebOrderStatusV1 status, CancellationToken cancellationToken)
    {
        order.UpdatedUtc = DateTime.UtcNow;
        _db.OutboxMessages.Add(OutboxMessage.WebOrderStatus(status, _terminal.StoreId, _terminal.RegisterId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<WebOrderPickedV1> Picked(WebOrder order) =>
        order.Lines.OrderBy(l => l.LineNumber).Select(l => new WebOrderPickedV1(l.LineNumber, l.Supplied)).ToList();

    private static decimal Total(WebOrder order) => order.Lines.Sum(l => l.UnitPrice * l.Supplied);

    private static decimal Refund(WebOrder order) => Math.Max(0, order.Prepaid - Total(order));
}
