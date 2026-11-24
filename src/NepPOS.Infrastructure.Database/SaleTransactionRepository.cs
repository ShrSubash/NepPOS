using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;
using NepPOS.Infrastructure.Database.Sales;

namespace NepPOS.Infrastructure.Database;

/// <summary>
/// SQLite-backed implementation of ISaleTransactionRepository for the Register tier.
/// Master/Cloud tiers will get a separate implementation class registered against
/// the SQL Server provider — same interface, different DI registration per tier.
/// </summary>
public sealed class SaleTransactionRepository : ISaleTransactionRepository
{
    private readonly NepPosDbContext _dbContext;

    public SaleTransactionRepository(NepPosDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(SaleTransaction saleTransaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saleTransaction);

        await _dbContext.SaleTransactions.AddAsync(saleTransaction, cancellationToken);

        // Same SaveChanges = same transaction: the sale, its stock movements and its Head Office event
        // are saved together or not at all.
        if (saleTransaction.Status == SaleTransactionStatus.Completed)
        {
            await CheckReturnsAsync(saleTransaction, cancellationToken);
            _dbContext.OutboxMessages.Add(OutboxMessage.SaleCompleted(saleTransaction));
            await TakeStockAsync(saleTransaction, cancellationToken);
            await new GiftCardLedger(_dbContext).ApplyAsync(saleTransaction, cancellationToken);
            await new AccountLedger(_dbContext).ApplyAsync(saleTransaction, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A returned line can't bring back more than was sold on it, counting earlier returns. Checked again here
    /// (the return screen checks first) so two returns of the same receipt can't both go through.
    /// </summary>
    private async Task CheckReturnsAsync(SaleTransaction sale, CancellationToken cancellationToken)
    {
        var returning = sale.LineItems.Where(li => li.ReturnOfLineId is not null)
            .GroupBy(li => li.ReturnOfLineId!.Value)
            .ToDictionary(g => g.Key, g => -g.Sum(li => li.Quantity));
        if (returning.Count == 0)
            return;

        var ids = returning.Keys.ToList();
        var sold = await _dbContext.SaleLineItems.AsNoTracking().Where(li => ids.Contains(li.Id))
            .Select(li => new { li.Id, li.Quantity, li.ProductName }).ToListAsync(cancellationToken);
        var earlier = await _dbContext.SaleLineItems.AsNoTracking()
            .Where(li => li.ReturnOfLineId != null && ids.Contains(li.ReturnOfLineId.Value))
            .Select(li => new { Id = li.ReturnOfLineId!.Value, li.Quantity }).ToListAsync(cancellationToken);

        foreach (var (lineId, count) in returning)
        {
            var original = sold.FirstOrDefault(l => l.Id == lineId)
                ?? throw new InvalidOperationException("A returned item's original sale isn't on this register.");
            var alreadyBack = -earlier.Where(e => e.Id == lineId).Sum(e => e.Quantity);
            if (count > original.Quantity - alreadyBack)
                throw new InvalidOperationException(
                    $"{original.ProductName}: {original.Quantity} sold, {alreadyBack} already returned, so at most {Math.Max(0, original.Quantity - alreadyBack)} can come back.");
        }
    }

    /// <summary>
    /// Takes each product line off stock in singles (a case of 24 is −24; a refund line puts stock back).
    /// A returned item that's faulty or damaged comes back in and goes straight out again as wastage, so the
    /// stock history shows both. Products deleted since they were rung up are skipped: the sale must still save.
    /// </summary>
    private async Task TakeStockAsync(SaleTransaction sale, CancellationToken cancellationToken)
    {
        var byProduct = sale.LineItems
            .Where(li => li.ProductId is not null)
            .GroupBy(li => li.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(li => li.StockUnits));
        if (byProduct.Count == 0)
            return;

        var ids = byProduct.Keys.ToList();
        var products = await _dbContext.Products.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var reference = sale.InvoiceNumber is { } invoice ? $"Invoice {invoice}" : "Sale";
        var now = DateTime.UtcNow;

        var writtenOff = sale.LineItems
            .Where(li => li.ProductId is not null && li.IsReturn && !li.Restock)
            .GroupBy(li => li.ProductId!.Value)
            .ToDictionary(g => g.Key, g => -g.Sum(li => li.StockUnits));

        foreach (var product in products)
        {
            var units = byProduct[product.Id];
            if (units != 0)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Sale, -units, reference, now, employeeName: sale.EmployeeName));
            if (writtenOff.TryGetValue(product.Id, out var wasted) && wasted > 0)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Wastage, -wasted, $"{reference}: returned faulty", now, employeeName: sale.EmployeeName));
        }
    }

    public async Task<SaleTransaction?> GetByIdAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.SaleTransactions
            .AsNoTracking()
            .Include(st => st.LineItems)
            .Include(st => st.Payments)
            .FirstOrDefaultAsync(st => st.Id == saleId, cancellationToken);
    }

    public async Task<long> GetNextInvoiceNumberAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("Store ID is required.", nameof(storeId));

        // Gapless, per-store sequential numbering (Section 6). This query-then-increment
        // approach is correct for a single-writer Register but would need a proper
        // sequence/lock at Master where multiple registers could theoretically race —
        // flagged here deliberately rather than silently assumed safe everywhere.
        var maxInvoiceNumber = await _dbContext.SaleTransactions
            .Where(st => st.StoreId == storeId && st.InvoiceNumber != null)
            .Select(st => st.InvoiceNumber)
            .MaxAsync(cancellationToken);

        return (maxInvoiceNumber ?? 0) + 1;
    }
}