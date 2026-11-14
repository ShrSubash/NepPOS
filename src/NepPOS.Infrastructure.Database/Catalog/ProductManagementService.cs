using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;
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

        _dbContext.Products.Add(product);
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
        _dbContext.Entry(saved).CurrentValues.SetValues(product);
        saved.SyncId = syncId;

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
