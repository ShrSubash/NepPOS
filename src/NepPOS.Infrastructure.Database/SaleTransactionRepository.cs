using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

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
            _dbContext.OutboxMessages.Add(OutboxMessage.SaleCompleted(saleTransaction));
            await TakeStockAsync(saleTransaction, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Takes each product line off stock in singles (a case of 24 is −24; a refund line puts stock back).
    /// Products deleted since they were rung up are skipped: the sale must still save.
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

        foreach (var product in products)
        {
            var units = byProduct[product.Id];
            if (units != 0)
                _dbContext.StockMovements.Add(product.MoveStock(StockMovementType.Sale, -units, reference, now, employeeName: sale.EmployeeName));
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