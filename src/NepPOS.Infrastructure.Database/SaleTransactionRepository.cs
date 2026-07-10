using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Domain.Entities;

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
        await _dbContext.SaveChangesAsync(cancellationToken);
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