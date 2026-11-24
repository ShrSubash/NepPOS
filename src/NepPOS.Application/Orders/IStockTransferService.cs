using NepPOS.Contracts.Orders;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Orders;

/// <summary>A product to send: how many singles.</summary>
public sealed record TransferItem(int ProductId, decimal Quantity);

/// <summary>A line of an incoming transfer and the product it matched here (null when this store doesn't have it).</summary>
public sealed record IncomingLine(StockTransferLine Line, int? ProductId, string? MatchedName);

/// <summary>Stock moved between the owner's stores, through Head Office.</summary>
public interface IStockTransferService
{
    /// <summary>The owner's other stores (from Head Office), or the ones this store has traded with when offline.</summary>
    Task<IReadOnlyList<StoreRef>> OtherStoresAsync(CancellationToken cancellationToken = default);

    /// <summary>Transfers on their way (both directions) first, then the latest received.</summary>
    Task<IReadOnlyList<StockTransfer>> ListAsync(CancellationToken cancellationToken = default);

    Task<StockTransfer?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Takes the stock off here at cost and sends the transfer to <paramref name="toStore"/>.</summary>
    Task<StockTransfer> SendAsync(string toStore, IReadOnlyList<TransferItem> items, string? note, string? employeeName,
        CancellationToken cancellationToken = default);

    /// <summary>An incoming transfer's lines matched to this store's products (by product, then barcode, then PLU).</summary>
    Task<IReadOnlyList<IncomingLine>> MatchAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts an incoming transfer in: each line (by line id) adds what arrived to stock at the sender's cost. Lines left
    /// out count as all arrived; lines for products this store doesn't have are skipped.
    /// </summary>
    Task<StockTransfer> ReceiveAsync(Guid id, IReadOnlyDictionary<int, decimal>? arrived, string? employeeName,
        CancellationToken cancellationToken = default);

    /// <summary>Transfers waiting to be received here: the number on the Manage menu.</summary>
    Task<int> IncomingCountAsync(CancellationToken cancellationToken = default);
}
