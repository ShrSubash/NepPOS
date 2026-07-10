using NepPOS.Domain.Entities;

namespace NepPOS.Application.Abstractions;

/// <summary>
/// Maps a canonical ExternalOrder into a SaleTransaction and persists it,
/// exactly like a normal in-store sale (architecture Section 7.2).
/// Implemented in the Application layer since it coordinates Domain rules
/// with persistence (via ISaleTransactionRepository) — no direct DB access here.
/// </summary>
public interface IOrderProcessor
{
    Task<SaleTransaction> ProcessAsync(ExternalOrder externalOrder, CancellationToken cancellationToken = default);
}