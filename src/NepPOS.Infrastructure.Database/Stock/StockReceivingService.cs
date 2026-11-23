using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Scanning;

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
            ? await _dbContext.StockReceipts.AnyAsync(r => r.SupplierId == id && r.InvoiceNumber == invoice && r.ReversedUtc == null)
            : await _dbContext.StockReceipts.AnyAsync(r => r.SupplierId == null && r.SupplierName == supplierName.Trim() && r.InvoiceNumber == invoice && r.ReversedUtc == null);
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
        if (input.SupplierId is null && input.AddSupplier && string.IsNullOrWhiteSpace(input.SupplierName))
            throw new InvalidOperationException("Choose the supplier.");

        // One transaction: a new supplier, the receipt, stock, costs and remembered codes all save or none do.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var supplierId = input.SupplierId;
        if (supplierId is null && input.AddSupplier)
        {
            var supplier = await FindSupplierAsync(input.SupplierAccountNumber, input.SupplierName)
                ?? _dbContext.Suppliers.Add(new Supplier { Name = input.SupplierName.Trim(), AccountNumber = input.SupplierAccountNumber?.Trim() }).Entity;
            await _dbContext.SaveChangesAsync();
            supplierId = supplier.Id;
        }
        if (supplierId is { } knownSupplier && !string.IsNullOrWhiteSpace(input.SupplierAccountNumber))
        {
            var supplier = await _dbContext.Suppliers.FindAsync(knownSupplier);
            if (supplier is not null && string.IsNullOrWhiteSpace(supplier.AccountNumber))
                supplier.AccountNumber = input.SupplierAccountNumber.Trim();
        }

        if (await InvoiceExistsAsync(supplierId, input.SupplierName, input.InvoiceNumber))
            throw new InvalidOperationException($"Invoice {input.InvoiceNumber.Trim()} from {input.SupplierName} has already been received.");

        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _dbContext.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var missing = ids.Where(id => !products.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("A product on this delivery has been deleted. Remove it and post again.");

        var now = DateTime.UtcNow;
        var receipt = new StockReceipt
        {
            SupplierId = supplierId,
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
                PreviousUnitCost = product.LastCost ?? product.CostPrice,
                PreviousLastCost = product.LastCost,
                PreviousAverageCost = product.CostPrice
            });

            // Average first (it needs the stock before this line), then the stock itself.
            product.CostPrice = StockMath.AverageCost(product.StockOnHand, product.CostPrice, singles, unitCost);
            product.LastCost = unitCost;
            if (product.SupplierId is null && supplierId is not null)
                product.SupplierId = supplierId;

            _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Receipt, singles, reference, now, unitCost, employeeName));
        }

        if (supplierId is { } id)
            await RememberSupplierCodesAsync(id, lines, input.SkipAlways);

        _dbContext.StockReceipts.Add(receipt);
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed: StockReceipts") == true)
        {
            // Another post of the same invoice got in first (the database rule caught it).
            throw new InvalidOperationException($"Invoice {receipt.InvoiceNumber} from {receipt.SupplierName} has already been received.");
        }
        await transaction.CommitAsync();

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
                r.Id, r.PostedUtc, r.SupplierName, r.InvoiceNumber, r.InvoiceDate, r.EmployeeName, r.ReversedUtc,
                Lines = r.Lines.Count,
                Costs = r.Lines.Select(l => new { l.Quantity, l.CostPerPack }).ToList()
            })
            .ToListAsync();

        // Totals added up here: SQLite can't sum decimals in a query.
        return receipts.Select(r => new ReceiptSummary(r.Id, r.PostedUtc, r.SupplierName, r.InvoiceNumber, r.InvoiceDate, r.Lines,
            r.Costs.Sum(c => c.Quantity * c.CostPerPack), r.EmployeeName) { Reversed = r.ReversedUtc != null }).ToList();
    }

    public async Task<ReceiptReversed> ReverseAsync(int receiptId, string reason, string? employeeName)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length == 0)
            throw new InvalidOperationException("Say why the delivery is being reversed.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        var receipt = await _dbContext.StockReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == receiptId)
            ?? throw new InvalidOperationException("That delivery no longer exists.");
        if (receipt.ReversedUtc is { } reversedAt)
            throw new InvalidOperationException($"Invoice {receipt.InvoiceNumber} from {receipt.SupplierName} was already reversed on {reversedAt.ToLocalTime():d MMM yyyy}.");

        var ids = receipt.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _dbContext.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        // Anything that happened to a product after this delivery (a sale, a count, another delivery) means
        // its costs can't simply be put back as they were; they're worked back instead.
        var postedUtc = receipt.PostedUtc;
        var movedSince = (await _dbContext.StockMovements
            .Where(m => ids.Contains(m.ProductId) && m.AtUtc > postedUtc)
            .Select(m => m.ProductId).Distinct().ToListAsync()).ToHashSet();
        var receivedSince = (await _dbContext.StockReceipts
            .Where(r => r.Id > receiptId && r.ReversedUtc == null)
            .SelectMany(r => r.Lines).Where(l => ids.Contains(l.ProductId))
            .Select(l => l.ProductId).Distinct().ToListAsync()).ToHashSet();

        var now = DateTime.UtcNow;
        var reference = $"Reversed invoice {receipt.InvoiceNumber} ({receipt.SupplierName})";
        if (reference.Length > 100)
            reference = reference[..100];

        var negative = new List<string>();
        foreach (var lines in receipt.Lines.GroupBy(l => l.ProductId))
        {
            if (!products.TryGetValue(lines.Key, out var product))
                continue;   // deleted since: nothing to take out

            var first = lines.OrderBy(l => l.Id).First();
            var singles = lines.Sum(l => l.Singles);
            var unitCost = singles == 0 ? 0 : Math.Round(lines.Sum(l => l.LineCost) / singles, 4);

            if (movedSince.Contains(product.Id))
                product.CostPrice = StockMath.ReverseAverageCost(product.StockOnHand, product.CostPrice, singles, unitCost);
            else
                product.CostPrice = first.PreviousAverageCost;

            if (!receivedSince.Contains(product.Id))
                product.LastCost = first.PreviousLastCost;

            _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.ReceiptReversal, -singles, reference, now, unitCost, employeeName));
            if (product.StockOnHand < 0)
                negative.Add(product.Name);
        }

        receipt.ReversedUtc = now;
        receipt.ReversedBy = employeeName;
        receipt.ReversalReason = reason.Length > 200 ? reason[..200] : reason;

        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return new ReceiptReversed(receipt.Lines.Sum(l => l.Singles), receipt.Lines.Sum(l => l.LineCost), negative);
    }

    public async Task<InvoiceMatchResult> MatchInvoiceAsync(SupplierInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var supplier = await FindSupplierAsync(invoice.AccountNumber, invoice.SupplierName);
        var codes = invoice.Lines.Select(l => l.ItemCode).Where(c => c.Length > 0).Distinct().ToList();

        // Codes linked for this supplier win; codes from the old POS (supplier unknown) are the fallback.
        var supplierId = supplier?.Id;
        var links = await _dbContext.ProductSupplierCodes.AsNoTracking()
            .Where(c => codes.Contains(c.Code) && (c.SupplierId == null || (supplierId != null && c.SupplierId == supplierId)))
            .ToListAsync();
        var skipped = supplier is null
            ? new HashSet<string>()
            : (await _dbContext.SupplierSkippedItems.AsNoTracking()
                .Where(i => i.SupplierId == supplier.Id && codes.Contains(i.Code)).Select(i => i.Code).ToListAsync()).ToHashSet();
        var linkedIds = links.Select(l => l.ProductId).Distinct().ToList();
        var linkedProducts = await _dbContext.Products.AsNoTracking()
            .Where(p => linkedIds.Contains(p.Id) && p.IsActive).ToDictionaryAsync(p => p.Id);

        var results = new List<InvoiceLineMatchResult>();
        foreach (var line in invoice.Lines)
        {
            if (line.Units <= 0)
            {
                results.Add(new(line, InvoiceLineMatch.NotSupplied, null, 1));
                continue;
            }
            if (skipped.Contains(line.ItemCode))
            {
                results.Add(new(line, InvoiceLineMatch.Skipped, null, 1));
                continue;
            }

            var link = links.Where(l => l.Code == line.ItemCode && linkedProducts.ContainsKey(l.ProductId))
                .OrderBy(l => l.SupplierId == null ? 1 : 0).FirstOrDefault();
            if (link is not null)
            {
                results.Add(new(line, InvoiceLineMatch.SupplierCode, linkedProducts[link.ProductId], Math.Max(1, link.SupplierUnitsPerSingle)));
                continue;
            }

            var byBarcode = string.IsNullOrEmpty(line.Barcode) ? null : await ProductScanResolver.FindByBarcodeAsync(_dbContext, line.Barcode);
            switch (byBarcode)
            {
                // The product's own barcode covering 10 invoiced units: the product is the 10-pack.
                case Product product:
                    results.Add(new(line, InvoiceLineMatch.Barcode, product, line.BarcodeMultiple));
                    break;
                // A pack's barcode: the product's single is the invoiced unit when the pack and the barcode agree.
                case ProductVariant pack:
                    results.Add(new(line, InvoiceLineMatch.Barcode, pack.Product,
                        pack.UnitQuantity > 0 && line.BarcodeMultiple % pack.UnitQuantity == 0 ? line.BarcodeMultiple / pack.UnitQuantity : 1));
                    break;
                default:
                    results.Add(new(line, InvoiceLineMatch.None, null, 1));
                    break;
            }
        }

        var already = await InvoiceExistsAsync(supplier?.Id, supplier?.Name ?? invoice.SupplierName, invoice.InvoiceNumber);
        return new InvoiceMatchResult(invoice, supplier?.Id, supplier?.Name ?? invoice.SupplierName, already, results);
    }

    private async Task<Supplier?> FindSupplierAsync(string? accountNumber, string name)
    {
        var account = accountNumber?.Trim();
        if (!string.IsNullOrEmpty(account))
        {
            var byAccount = await _dbContext.Suppliers.FirstOrDefaultAsync(s => s.IsActive && s.AccountNumber == account);
            if (byAccount is not null)
                return byAccount;
        }

        var lowered = name.Trim().ToLower();
        return await _dbContext.Suppliers.FirstOrDefaultAsync(s => s.IsActive && s.Name.ToLower() == lowered);
    }

    /// <summary>Links each line's supplier item code to its product, and records items to always skip.</summary>
    private async Task RememberSupplierCodesAsync(int supplierId, IReadOnlyList<ReceiptLineInput> lines, IReadOnlyList<SkipItem> skipAlways)
    {
        var coded = lines.Where(l => !string.IsNullOrWhiteSpace(l.SupplierItemCode)).ToList();
        var codes = coded.Select(l => l.SupplierItemCode!.Trim()).Concat(skipAlways.Select(s => s.Code.Trim())).Distinct().ToList();
        if (codes.Count == 0)
            return;

        var existing = await _dbContext.ProductSupplierCodes.Where(c => c.SupplierId == supplierId && codes.Contains(c.Code)).ToListAsync();
        foreach (var line in coded)
        {
            var code = line.SupplierItemCode!.Trim();
            var link = existing.FirstOrDefault(c => c.Code == code);
            if (link is null)
            {
                link = new ProductSupplierCode { SupplierId = supplierId, Code = code };
                _dbContext.ProductSupplierCodes.Add(link);
                existing.Add(link);
            }
            link.ProductId = line.ProductId;
            link.SupplierUnitsPerSingle = Math.Max(1, line.SupplierUnitsPerSingle);
        }

        // A code once skipped but now received is no longer skipped.
        var skipRows = await _dbContext.SupplierSkippedItems.Where(i => i.SupplierId == supplierId && codes.Contains(i.Code)).ToListAsync();
        var receivedCodes = coded.Select(l => l.SupplierItemCode!.Trim()).ToHashSet();
        _dbContext.SupplierSkippedItems.RemoveRange(skipRows.Where(i => receivedCodes.Contains(i.Code)));
        var skippedAlready = skipRows.Where(i => !receivedCodes.Contains(i.Code)).Select(i => i.Code).ToHashSet();
        foreach (var skip in skipAlways.Where(s => !string.IsNullOrWhiteSpace(s.Code)))
            if (skippedAlready.Add(skip.Code.Trim()))
                _dbContext.SupplierSkippedItems.Add(new SupplierSkippedItem
                {
                    SupplierId = supplierId,
                    Code = skip.Code.Trim(),
                    Description = skip.Description.Length > 200 ? skip.Description[..200] : skip.Description
                });
    }

    public async Task<StockReceipt?> GetAsync(int id) =>
        await _dbContext.StockReceipts.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
}
