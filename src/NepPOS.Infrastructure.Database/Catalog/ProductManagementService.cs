using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Catalog;

/// <summary>
/// Product changes, each saved together with its <c>product.changed</c> event for Head Office
/// (same SaveChanges, so the register and Head Office can't disagree about a product).
/// </summary>
public sealed class ProductManagementService : IProductManagementService
{
    private readonly NepPosDbContext _dbContext;
    private readonly RegisterTerminal _terminal;

    public ProductManagementService(NepPosDbContext dbContext, RegisterTerminal terminal)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    public async Task<IReadOnlyList<Product>> GetAllAsync() =>
        await _dbContext.Products.OrderBy(p => p.Name).ToListAsync();

    public async Task<Product?> GetByIdAsync(int id) =>
        await _dbContext.Products.FindAsync(id);

    public async Task<Product> CreateAsync(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.SyncId == Guid.Empty)
            product.SyncId = Guid.NewGuid();

        // Stock typed on a new product is its opening stock: through the ledger like every other change.
        var opening = product.StockOnHand;
        product.StockOnHand = 0;
        _dbContext.Products.Add(product);
        if (opening != 0)
            _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Opening, opening, "New product", DateTime.UtcNow, product.CostPrice));
        _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, Array.Empty<ProductVariant>(), _terminal.StoreId, _terminal.RegisterId));
        await _dbContext.SaveChangesAsync();
        return product;
    }

    /// <summary>
    /// Copies the edited values onto the saved product. The edit screen works on a copy that doesn't
    /// carry <see cref="Product.SyncId"/>, so the saved one is kept: a product never changes identity.
    /// </summary>
    public async Task UpdateAsync(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var saved = await _dbContext.Products.FindAsync(product.Id)
            ?? throw new InvalidOperationException($"Product {product.Id} no longer exists.");

        var syncId = saved.SyncId == Guid.Empty ? Guid.NewGuid() : saved.SyncId;
        var stockBefore = saved.StockOnHand;
        _dbContext.Entry(saved).CurrentValues.SetValues(product);
        saved.SyncId = syncId;

        // Stock changed by hand: keep the old figure and record the difference, so the ledger still adds up.
        var difference = product.StockOnHand - stockBefore;
        saved.StockOnHand = stockBefore;
        if (difference != 0)
            _dbContext.StockMovements.Add(saved.MoveStock(StockMovementType.Adjustment, difference, "Changed on Products & PLUs", DateTime.UtcNow));

        await AddChangedEventAsync(saved);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeactivateAsync(int id)
    {
        var product = await _dbContext.Products.FindAsync(id);
        if (product is null) return;
        product.IsActive = false;
        if (product.SyncId == Guid.Empty)
            product.SyncId = Guid.NewGuid();

        await AddChangedEventAsync(product);
        await _dbContext.SaveChangesAsync();
    }

    public async Task<(int Created, int Updated)> ImportAsync(ProductImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var now = DateTime.UtcNow;
        foreach (var product in plan.New)
        {
            product.Id = 0;
            if (product.SyncId == Guid.Empty)
                product.SyncId = Guid.NewGuid();
            var opening = product.StockOnHand;
            product.StockOnHand = 0;
            _dbContext.Products.Add(product);
            if (opening != 0)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Opening, opening, "CSV import", now, product.CostPrice));
            _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, Array.Empty<ProductVariant>(), _terminal.StoreId, _terminal.RegisterId));
        }

        var ids = plan.Updates.Select(u => u.Updated.Id).ToList();
        var saved = await _dbContext.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var variants = (await _dbContext.ProductVariants.AsNoTracking().Where(v => ids.Contains(v.ProductId)).ToListAsync()).ToLookup(v => v.ProductId);

        var updated = 0;
        foreach (var update in plan.Updates)
        {
            // Deleted since the preview: skip it rather than fail the whole import.
            if (!saved.TryGetValue(update.Updated.Id, out var product))
                continue;

            var values = update.Updated;
            product.Name = values.Name;
            product.Plu = values.Plu;
            product.Barcode = values.Barcode;
            product.Category = values.Category;
            product.Price = values.Price;
            product.CostPrice = values.CostPrice;
            if (values.StockOnHand != product.StockOnHand)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Adjustment, values.StockOnHand - product.StockOnHand, "CSV import", now));
            product.EnableOnWebsite = values.EnableOnWebsite;
            product.IsActive = values.IsActive;
            if (product.SyncId == Guid.Empty)
                product.SyncId = Guid.NewGuid();

            _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, variants[product.Id], _terminal.StoreId, _terminal.RegisterId));
            updated++;
        }

        await _dbContext.SaveChangesAsync();
        return (plan.New.Count, updated);
    }

    public async Task<IReadOnlyList<string>> GetAllBarcodesAsync()
    {
        var products = await _dbContext.Products.Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync();
        var packs = await _dbContext.ProductVariants.Where(v => v.Barcode != null).Select(v => v.Barcode!).ToListAsync();
        var extra = await _dbContext.ProductBarcodes.Select(b => b.Code).ToListAsync();
        return products.Concat(packs).Concat(extra).ToList();
    }

    public async Task<int> ImportLegacyAsync(LegacyImportPlan plan, string? employeeName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var now = DateTime.UtcNow;

        // Suppliers: reuse one already imported from the same old number, else make it.
        var suppliers = new Dictionary<string, Supplier>();
        var supplierLinks = new List<(Product Product, Supplier? Supplier, IReadOnlyList<string> Codes)>();
        foreach (var (number, name) in plan.Suppliers)
        {
            var supplier = await _dbContext.Suppliers.FirstOrDefaultAsync(s => s.LegacyNumber == number)
                ?? _dbContext.Suppliers.Add(new Supplier { Name = name, LegacyNumber = number }).Entity;
            suppliers[number] = supplier;
        }

        foreach (var item in plan.New)
        {
            var supplier = item.SupplierNumber is { } number && suppliers.TryGetValue(number, out var found) ? found : null;
            var product = new Product
            {
                SyncId = Guid.NewGuid(),
                Plu = item.Plu,
                Name = item.Name,
                Price = item.Price,
                CostPrice = item.Cost,
                Category = item.Department,
                Barcode = item.Barcodes.FirstOrDefault(),
                IsGstFree = item.IsGstFree,
                IsActive = item.IsActive,
                EnableOnWebsite = item.OnWebsite
            };
            _dbContext.Products.Add(product);

            foreach (var code in item.Barcodes.Skip(1))
                _dbContext.ProductBarcodes.Add(new ProductBarcode { Product = product, Code = code, LookupKey = ProductBarcode.KeyFor(code) });

            var variants = new List<ProductVariant>();
            foreach (var pack in item.Packs)
            {
                var variant = new ProductVariant
                {
                    Product = product,
                    PackType = pack.Type,
                    UnitQuantity = pack.Units,
                    Price = pack.Price,
                    Barcode = pack.Barcodes.FirstOrDefault(),
                    IsActive = true
                };
                variants.Add(variant);
                _dbContext.ProductVariants.Add(variant);
                foreach (var code in pack.Barcodes.Skip(1))
                    _dbContext.ProductBarcodes.Add(new ProductBarcode { Product = product, Variant = variant, Code = code, LookupKey = ProductBarcode.KeyFor(code) });
            }

            if (item.Stock != 0)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Opening, item.Stock, "Imported from old POS", now, item.Cost, employeeName));

            _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, variants, _terminal.StoreId, _terminal.RegisterId));

            if (supplier is not null || item.SupplierCodes.Count > 0)
                supplierLinks.Add((product, supplier, item.SupplierCodes));
        }

        // Products and suppliers need their ids before the links can point at them.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        await _dbContext.SaveChangesAsync();
        foreach (var (product, supplier, codes) in supplierLinks)
        {
            product.SupplierId = supplier?.Id;
            foreach (var code in codes)
                _dbContext.ProductSupplierCodes.Add(new ProductSupplierCode { ProductId = product.Id, SupplierId = supplier?.Id, Code = code });
        }
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return plan.New.Count;
    }

    /// <summary>
    /// Queues every product (active or not) for Head Office: the first link to Head Office, or to
    /// put things right after a restore. Resending is harmless; Head Office keeps the latest.
    /// Also gives products made before this version their <see cref="Product.SyncId"/>.
    /// </summary>
    public async Task<int> SendAllToHeadOfficeAsync()
    {
        var products = await _dbContext.Products.ToListAsync();
        var variants = (await _dbContext.ProductVariants.AsNoTracking().ToListAsync()).ToLookup(v => v.ProductId);

        foreach (var product in products)
        {
            if (product.SyncId == Guid.Empty)
                product.SyncId = Guid.NewGuid();
            _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, variants[product.Id], _terminal.StoreId, _terminal.RegisterId));
        }

        await _dbContext.SaveChangesAsync();
        return products.Count;
    }

    private async Task AddChangedEventAsync(Product product)
    {
        var variants = await _dbContext.ProductVariants.AsNoTracking().Where(v => v.ProductId == product.Id).ToListAsync();
        _dbContext.OutboxMessages.Add(OutboxMessage.ProductChanged(product, variants, _terminal.StoreId, _terminal.RegisterId));
    }
}
