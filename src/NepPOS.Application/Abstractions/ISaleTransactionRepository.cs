using NepPOS.Domain.Entities;

namespace NepPOS.Application.Abstractions;

/// <summary>
/// Persistence contract for SaleTransaction. Implemented in Infrastructure.Database
/// per store/register — SQLite at the Register, SQL Server at Master/Cloud.
/// The Application layer never knows which provider is behind this.
/// </summary>
public interface ISaleTransactionRepository
{
    Task AddAsync(SaleTransaction saleTransaction, CancellationToken cancellationToken = default);
    Task<long> GetNextInvoiceNumberAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>A saved sale with its lines and payments (read-only), or null if it was never saved.</summary>
    Task<SaleTransaction?> GetByIdAsync(Guid saleId, CancellationToken cancellationToken = default);
}