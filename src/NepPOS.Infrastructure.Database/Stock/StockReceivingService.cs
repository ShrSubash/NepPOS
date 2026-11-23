using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Stock;

/// <summary>
/// Posts supplier deliveries. Everything for one delivery (the receipt, its lines, the stock movements and
/// the products' costs) is saved by one SaveChanges, so it all happens or none of it does.
/// </summary>
public sealed class StockReceivingService : IStockReceivingService
{
    private readonly NepPosDbContext _dbContext;

    public StockReceivingService(NepPosDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<Supplier>> GetSuppliersAsync() =>
        await _dbContext.Suppliers.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

    public async Task<Supplier> AddSupplierAsync(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("Enter the supplier's name.", nameof(name));

        var existing = await _dbContext.Suppliers.FirstOrDefaultAsync(s => s.IsActive && s.Name.ToLower() == name.ToLower());
        if (existing is not null)
            return existing;

        var supplier = new Supplier { Name = name };
        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync();
        return supplier;
    }

    public async Task<IReadOnlyList<ProductVariant>> GetPacksAsync(int productId) =>
        await _dbContext.ProductVariants.AsNoTracking()
            .Where(v => v.ProductId == productId && v.IsActive && v.UnitQuantity > 1)
            .OrderBy(v => v.UnitQuantity)
            .ToListAsync();

    public async Task<bool> InvoiceExistsAsync(int? supplierId, string supplierName, string invoiceNumber)
    {
        var invoice = invoiceNumber.Trim();
        return supplierId is { } id
            ? await _dbContext.StockReceipts.AnyAsync(r => r.SupplierId == id && r.InvoiceNumber == invoice)
            : await _dbContext.StockReceipts.AnyAsync(r => r.SupplierId == null && r.SupplierName == supplierName.Trim() && r.InvoiceNumber == invoice);
    }

    public async Task<ReceiptPosted> PostAsync(ReceiptInput input, string? employeeName)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines.Where(l => l.Quantity != 0).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Add at least one product to the delivery.");
        if (string.IsNullOrWhiteSpace(input.InvoiceNumber))
            throw new InvalidOperationException("Enter the invoice number.");
        if (lines.Any(l => l.PackUnits < 1 || l.CostPerPack < 0))
            throw new InvalidOperationException("Every line needs a pack size and a cost of $0 or more.");
        if (await InvoiceExistsAsync(input.SupplierId, input.SupplierName, input.InvoiceNumber))
            throw new InvalidOperationException($"Invoice {input.InvoiceNumber.Trim()} from {input.SupplierName} has already been received.");

        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _dbContext.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var missing = ids.Where(id => !products.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("A product on this delivery has been deleted. Remove it and post again.");

        var now = DateTime.UtcNow;
        var receipt = new StockReceipt
        {
            SupplierId = input.SupplierId,
            SupplierName = input.SupplierName.Trim(),
            InvoiceNumber = input.InvoiceNumber.Trim(),
            InvoiceDate = input.InvoiceDate,
            PostedUtc = now,
            EmployeeName = employeeName
        };
        var reference = $"Invoice {receipt.InvoiceNumber} ({receipt.SupplierName})";
        var before = products.Values.ToDictionary(p => p.Id, p => (LastCost: p.LastCost ?? p.CostPrice, Average: p.CostPrice));

        foreach (var line in lines)
        {
            var product = products[line.ProductId];
            var singles = line.Quantity * line.PackUnits;
            var unitCost = Math.Round(line.CostPerPack / line.PackUnits, 4);

            receipt.Lines.Add(new StockReceiptLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                PackName = line.PackName,
                PackUnits = line.PackUnits,
                Quantity = line.Quantity,
                CostPerPack = line.CostPerPack,
                PreviousUnitCost = product.LastCost ?? product.CostPrice
            });

            // Average first (it needs the stock before this line), then the stock itself.
            product.CostPrice = StockMath.AverageCost(product.StockOnHand, product.CostPrice, singles, unitCost);
            product.LastCost = unitCost;
            if (product.SupplierId is null && input.SupplierId is not null)
                product.SupplierId = input.SupplierId;

            _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Receipt, singles, reference, now, unitCost, employeeName));
        }

        _dbContext.StockReceipts.Add(receipt);
        await _dbContext.SaveChangesAsync();

        // Cost per single moved (by half a cent or more) since the last delivery. A product's first cost isn't a change.
        var changes = new List<CostChange>();
        foreach (var product in products.Values)
        {
            var (oldCost, oldAverage) = before[product.Id];
            if (oldCost is null || Math.Abs(oldCost.Value - product.LastCost!.Value) < 0.005m)
                continue;
            changes.Add(new CostChange(product.Id, product.Name, oldCost, product.LastCost.Value, product.Price,
                StockMath.GrossProfitPercent(product.Price, 1, oldAverage, product.IsGstFree),
                StockMath.GrossProfitPercent(product.Price, 1, product.CostPrice, product.IsGstFree)));
        }
        changes = changes.OrderByDescending(c => c.NewCost - c.OldCost!.Value).ToList();

        return new ReceiptPosted(receipt.Id, receipt.Lines.Sum(l => l.Singles), receipt.Lines.Sum(l => l.LineCost), changes);
    }

    public async Task<IReadOnlyList<ReceiptSummary>> GetRecentAsync(int take = 50)
    {
        var receipts = await _dbContext.StockReceipts.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(take)
            .Select(r => new
            {
                r.Id, r.PostedUtc, r.SupplierName, r.InvoiceNumber, r.InvoiceDate, r.EmployeeName,
                Lines = r.Lines.Count,
                Costs = r.Lines.Select(l => new { l.Quantity, l.CostPerPack }).ToList()
            })
            .ToListAsync();

        // Totals added up here: SQLite can't sum decimals in a query.
        return receipts.Select(r => new ReceiptSummary(r.Id, r.PostedUtc, r.SupplierName, r.InvoiceNumber, r.InvoiceDate, r.Lines,
            r.Costs.Sum(c => c.Quantity * c.CostPerPack), r.EmployeeName)).ToList();
    }

    public async Task<StockReceipt?> GetAsync(int id) =>
        await _dbContext.StockReceipts.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
}
