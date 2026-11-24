using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Sync;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Sync;

/// <summary>
/// Queues a <c>stock.changed</c> event for each product whose stock on hand moved since it was last sent, and remembers
/// what was sent, in the same save (so each change goes once, and nothing is missed if the register restarts).
/// </summary>
public sealed class StockPublisher : IStockPublisher
{
    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal _terminal;

    public StockPublisher(NepPosDbContext db, RegisterTerminal terminal)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    public async Task<int> QueueChangesAsync(int max = 500, CancellationToken cancellationToken = default)
    {
        // Money and stock are kept as text in SQLite, so the comparison happens here, not in SQL.
        var products = await _db.Products.ToListAsync(cancellationToken);
        var changed = products.Where(p => p.StockSent != p.StockOnHand).Take(Math.Clamp(max, 1, 5000)).ToList();
        foreach (var product in changed)
        {
            _db.OutboxMessages.Add(OutboxMessage.StockChanged(product, _terminal.StoreId, _terminal.RegisterId));
            product.StockSent = product.StockOnHand;
        }
        if (changed.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return changed.Count;
    }
}
