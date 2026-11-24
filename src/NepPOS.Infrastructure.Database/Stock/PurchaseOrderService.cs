using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Stock;

/// <summary>Purchase orders, suggested from reorder points and received as normal deliveries.</summary>
public sealed class PurchaseOrderService : IPurchaseOrderService
{
    private readonly NepPosDbContext _db;

    public PurchaseOrderService(NepPosDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<PurchaseOrder>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        var query = _db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (!includeClosed)
            query = query.Where(o => o.Status == PurchaseOrderStatus.Draft || o.Status == PurchaseOrderStatus.Sent);
        return await query.OrderByDescending(o => o.Id).Take(200).ToListAsync(cancellationToken);
    }

    public Task<PurchaseOrder?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        _db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ReorderSuggestion>> SuggestAsync(int supplierId, CancellationToken cancellationToken = default)
    {
        var products = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.ReorderPoint != null && (p.SupplierId == supplierId || p.SupplierId == null))
            .ToListAsync(cancellationToken);
        var low = products.Where(p => p.StockOnHand <= p.ReorderPoint!.Value).ToList();
        var ids = low.Select(p => p.Id).ToList();
        var cases = (await _db.ProductVariants.AsNoTracking()
                .Where(v => ids.Contains(v.ProductId) && v.IsActive && v.UnitQuantity > 1)
                .ToListAsync(cancellationToken))
            .GroupBy(v => v.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.UnitQuantity).First());

        return low.Select(p =>
            {
                // Enough to reach twice the reorder point, unless the product says how much to order.
                var singles = p.ReorderQuantity ?? Math.Max(1, p.ReorderPoint!.Value * 2 - p.StockOnHand);
                var (name, units) = cases.TryGetValue(p.Id, out var v) ? (v.DisplayName, v.UnitQuantity) : ("Single", 1);
                var packs = Math.Ceiling(singles / units);
                var cost = (p.LastCost ?? p.CostPrice ?? 0m) * units;
                return new ReorderSuggestion(p.Id, p.Name, p.StockOnHand, p.ReorderPoint!.Value, name, units, packs, decimal.Round(cost, 4));
            })
            .OrderBy(s => s.ProductName)
            .ToList();
    }

    public async Task<PurchaseOrder> SaveDraftAsync(PurchaseOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var lines = order.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Add at least one product to the order.");
        if (lines.Any(l => l.PackUnits < 1 || l.CostPerPack < 0))
            throw new InvalidOperationException("Every line needs a pack size and a cost of $0 or more.");
        var supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == order.SupplierId, cancellationToken)
            ?? throw new InvalidOperationException("Choose the supplier.");

        PurchaseOrder target;
        if (order.Id == 0)
        {
            var last = await _db.PurchaseOrders.MaxAsync(o => (int?)o.Id, cancellationToken) ?? 0;
            target = new PurchaseOrder { Number = $"PO-{last + 1:D5}", CreatedUtc = DateTime.UtcNow, EmployeeName = order.EmployeeName };
            _db.PurchaseOrders.Add(target);
        }
        else
        {
            target = await _db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == order.Id, cancellationToken)
                ?? throw new InvalidOperationException("That order no longer exists.");
            if (target.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Sent))
                throw new InvalidOperationException($"{target.Number} is {target.Status.ToString().ToLowerInvariant()} and can't be changed.");
            _db.PurchaseOrderLines.RemoveRange(target.Lines);
            target.Lines.Clear();
        }

        target.SupplierId = supplier.Id;
        target.SupplierName = supplier.Name;
        target.Note = string.IsNullOrWhiteSpace(order.Note) ? null : order.Note.Trim()[..Math.Min(500, order.Note.Trim().Length)];
        foreach (var line in lines)
            target.Lines.Add(new PurchaseOrderLine
            {
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                PackName = string.IsNullOrWhiteSpace(line.PackName) ? "Single" : line.PackName,
                PackUnits = line.PackUnits,
                Quantity = line.Quantity,
                CostPerPack = line.CostPerPack
            });

        await _db.SaveChangesAsync(cancellationToken);
        return target;
    }

    public async Task MarkSentAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await FindOpenAsync(id, cancellationToken);
        order.Status = PurchaseOrderStatus.Sent;
        order.SentUtc ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await FindOpenAsync(id, cancellationToken);
        order.Status = PurchaseOrderStatus.Cancelled;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReceiptPosted> ReceiveAsync(int id, string invoiceNumber, DateOnly invoiceDate, IReadOnlyDictionary<int, decimal>? arrived,
        string? employeeName, CancellationToken cancellationToken = default)
    {
        var order = await FindOpenAsync(id, cancellationToken);
        await _db.Entry(order).Collection(o => o.Lines).LoadAsync(cancellationToken);

        var lines = order.Lines
            .Select(l => new ReceiptLineInput(l.ProductId, l.PackUnits, l.PackName,
                arrived is not null && arrived.TryGetValue(l.Id, out var qty) ? qty : l.Quantity, l.CostPerPack))
            .Where(l => l.Quantity > 0)
            .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Nothing arrived? Cancel the order instead.");

        // A normal delivery: stock, costs, remembered supplier codes and the supplier's bill.
        var posted = await new StockReceivingService(_db).PostAsync(
            new ReceiptInput(order.SupplierId, order.SupplierName, invoiceNumber, invoiceDate, lines), employeeName);

        order.Status = PurchaseOrderStatus.Received;
        order.ReceivedUtc = DateTime.UtcNow;
        order.StockReceiptId = posted.ReceiptId;
        await _db.SaveChangesAsync(cancellationToken);
        return posted;
    }

    public async Task<IReadOnlyList<ReorderLevel>> ReorderLevelsAsync(int supplierId, CancellationToken cancellationToken = default) =>
        await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.SupplierId == supplierId)
            .OrderBy(p => p.Name)
            .Select(p => new ReorderLevel(p.Id, p.Name, p.StockOnHand, p.ReorderPoint, p.ReorderQuantity))
            .ToListAsync(cancellationToken);

    public async Task SetReorderLevelAsync(int productId, decimal? reorderPoint, decimal? reorderQuantity, CancellationToken cancellationToken = default)
    {
        if (reorderPoint < 0 || reorderQuantity <= 0)
            throw new InvalidOperationException("Reorder levels must be 0 or more.");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new InvalidOperationException("That product no longer exists.");
        product.ReorderPoint = reorderPoint;
        product.ReorderQuantity = reorderQuantity;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<PurchaseOrder> FindOpenAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That order no longer exists.");
        if (order.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Sent))
            throw new InvalidOperationException($"{order.Number} is already {order.Status.ToString().ToLowerInvariant()}.");
        return order;
    }
}
