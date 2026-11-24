using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Application.Orders;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Orders;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Orders;

/// <summary>
/// Stock sent between the owner's stores. Sending takes the stock off here and queues <c>transfer.sent</c> in the same
/// save; Head Office passes it to the other store, which counts it in and answers with <c>transfer.received</c>.
/// </summary>
public sealed class StockTransferService : IStockTransferService
{
    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal _terminal;
    private readonly IHeadOfficeConnectionStore? _connections;
    private readonly IHeadOfficeClient? _headOffice;
    private readonly IJournal? _journal;

    public StockTransferService(NepPosDbContext db, RegisterTerminal terminal, IHeadOfficeConnectionStore? connections = null,
        IHeadOfficeClient? headOffice = null, IJournal? journal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        (_connections, _headOffice, _journal) = (connections, headOffice, journal);
    }

    public async Task<IReadOnlyList<StoreRef>> OtherStoresAsync(CancellationToken cancellationToken = default)
    {
        if (_connections?.Load() is { } connection && _headOffice is not null)
        {
            try
            {
                var stores = await _headOffice.GetStoresAsync(connection, cancellationToken);
                if (stores.Count > 0)
                    return stores.Where(s => !IsHere(s.StoreCode)).OrderBy(s => s.StoreCode).ToList();
            }
            catch (Exception ex) when (ex is HeadOfficeException or HttpRequestException or TaskCanceledException)
            {
                // Offline: fall back to the stores this one has traded with.
            }
        }

        var known = await _db.StockTransfers.AsNoTracking().Select(t => t.Outgoing ? t.ToStore : t.FromStore).Distinct().ToListAsync(cancellationToken);
        return known.Where(code => !IsHere(code)).OrderBy(code => code).Select(code => new StoreRef(code, code)).ToList();
    }

    public async Task<IReadOnlyList<StockTransfer>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await _db.StockTransfers.AsNoTracking().Include(t => t.Lines).ToListAsync(cancellationToken);
        return all.Where(t => t.Status == StockTransferStatus.InTransit).OrderBy(t => t.SentUtc)
            .Concat(all.Where(t => t.Status == StockTransferStatus.Received).OrderByDescending(t => t.ReceivedUtc).Take(30))
            .ToList();
    }

    public Task<StockTransfer?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.StockTransfers.AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<int> IncomingCountAsync(CancellationToken cancellationToken = default) =>
        _db.StockTransfers.CountAsync(t => !t.Outgoing && t.Status == StockTransferStatus.InTransit, cancellationToken);

    public async Task<StockTransfer> SendAsync(string toStore, IReadOnlyList<TransferItem> items, string? note, string? employeeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var to = (toStore ?? "").Trim().ToUpperInvariant();
        if (to.Length == 0)
            throw new InvalidOperationException("Choose the store to send to.");
        if (IsHere(to))
            throw new InvalidOperationException("That's this store. Choose another one.");
        var wanted = items.Where(i => i.Quantity > 0).GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        if (wanted.Count == 0)
            throw new InvalidOperationException("Add at least one product.");

        var ids = wanted.Keys.ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        if (products.Count != wanted.Count)
            throw new InvalidOperationException("A product on the transfer no longer exists.");

        var count = await _db.StockTransfers.CountAsync(t => t.Outgoing, cancellationToken);
        var now = DateTime.UtcNow;
        var transfer = new StockTransfer
        {
            Id = Guid.NewGuid(),
            Number = $"TR-{_terminal.StoreId}-{count + 1:D5}",
            FromStore = _terminal.StoreId,
            ToStore = to,
            Outgoing = true,
            SentUtc = now,
            SentBy = employeeName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)]
        };

        var number = 0;
        foreach (var (productId, quantity) in wanted.OrderBy(w => products[w.Key].Name))
        {
            var product = products[productId];
            var cost = product.LastCost ?? product.CostPrice ?? 0m;
            transfer.Lines.Add(new StockTransferLine
            {
                LineNumber = number++,
                ProductSyncId = product.SyncId,
                ProductName = product.Name,
                Barcode = product.Barcode,
                Sku = product.Plu,
                Quantity = quantity,
                UnitCost = cost
            });
            _db.StockMovements.Add(product.MoveStock(StockMovementType.TransferOut, -quantity, $"{transfer.Number} to {to}", now, cost, employeeName));
        }

        _db.StockTransfers.Add(transfer);
        _db.OutboxMessages.Add(OutboxMessage.TransferSent(new TransferSentV1(transfer.Id, transfer.Number, transfer.FromStore, to, now,
                employeeName, transfer.Note,
                transfer.Lines.Select(l => new TransferLineV1(l.ProductSyncId, l.ProductName, l.Barcode, l.Sku, l.Quantity, l.UnitCost)).ToList()),
            _terminal.StoreId, _terminal.RegisterId));
        await _db.SaveChangesAsync(cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.TransferSent,
                $"{transfer.Number} to {to}: {transfer.Lines.Count} products by {employeeName ?? "unknown"}",
                amount: decimal.Round(transfer.Value, 2), cancellationToken: cancellationToken);
        return transfer;
    }

    public async Task<IReadOnlyList<IncomingLine>> MatchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var transfer = await GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("That transfer no longer exists.");
        var matches = await MatchProductsAsync(transfer, cancellationToken);
        return transfer.Lines.OrderBy(l => l.LineNumber)
            .Select(l => matches.TryGetValue(l.LineNumber, out var p) ? new IncomingLine(l, p.Id, p.Name) : new IncomingLine(l, null, null))
            .ToList();
    }

    public async Task<StockTransfer> ReceiveAsync(Guid id, IReadOnlyDictionary<int, decimal>? arrived, string? employeeName,
        CancellationToken cancellationToken = default)
    {
        var transfer = await _db.StockTransfers.Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("That transfer no longer exists.");
        if (transfer.Outgoing)
            throw new InvalidOperationException("This store sent it. The other store counts it in.");
        if (transfer.Status == StockTransferStatus.Received)
            throw new InvalidOperationException($"{transfer.Number} has already been received.");

        var matches = await MatchProductsAsync(transfer, cancellationToken);
        var productIds = matches.Values.Select(p => p.Id).ToList();
        var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var reference = $"{transfer.Number} from {transfer.FromStore}";

        foreach (var line in transfer.Lines)
        {
            var quantity = arrived is not null && arrived.TryGetValue(line.Id, out var counted) ? counted : line.Quantity;
            if (quantity < 0)
                throw new InvalidOperationException($"{line.ProductName}: enter 0 or more.");
            if (!matches.TryGetValue(line.LineNumber, out var match))
            {
                line.Received = 0;   // not stocked here: nothing to add it to
                continue;
            }
            line.Received = quantity;
            if (quantity > 0)
                _db.StockMovements.Add(products[match.Id].MoveStock(StockMovementType.TransferIn, quantity, reference, now, line.UnitCost, employeeName));
        }

        transfer.Status = StockTransferStatus.Received;
        transfer.ReceivedUtc = now;
        transfer.ReceivedBy = employeeName;
        _db.OutboxMessages.Add(OutboxMessage.TransferReceived(new TransferReceivedV1(transfer.Id, _terminal.StoreId, now, employeeName,
                transfer.Lines.OrderBy(l => l.LineNumber).Select(l => new TransferReceivedLineV1(l.LineNumber, l.Received ?? 0)).ToList()),
            _terminal.StoreId, _terminal.RegisterId));
        await _db.SaveChangesAsync(cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.TransferReceived,
                $"{reference}: {transfer.Lines.Count(l => l.Received > 0)} products counted in by {employeeName ?? "unknown"}",
                amount: decimal.Round(transfer.Lines.Sum(l => (l.Received ?? 0) * l.UnitCost), 2), cancellationToken: cancellationToken);
        return transfer;
    }

    /// <summary>Line number → this store's product: same product id, else the same barcode, else the same PLU.</summary>
    private async Task<Dictionary<int, (int Id, string Name)>> MatchProductsAsync(StockTransfer transfer, CancellationToken cancellationToken)
    {
        var syncIds = transfer.Lines.Select(l => l.ProductSyncId).ToList();
        var barcodes = transfer.Lines.Where(l => l.Barcode != null).Select(l => l.Barcode!).ToList();
        var plus = transfer.Lines.Where(l => l.Sku != null).Select(l => l.Sku!).ToList();
        var candidates = await _db.Products.AsNoTracking()
            .Where(p => syncIds.Contains(p.SyncId) || (p.Barcode != null && barcodes.Contains(p.Barcode)) || (p.Plu != null && plus.Contains(p.Plu)))
            .Select(p => new { p.Id, p.Name, p.SyncId, p.Barcode, p.Plu, p.IsActive })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, (int, string)>();
        foreach (var line in transfer.Lines)
        {
            var match = candidates.FirstOrDefault(p => p.SyncId == line.ProductSyncId)
                        ?? candidates.Where(p => p.IsActive).FirstOrDefault(p => line.Barcode != null && p.Barcode == line.Barcode)
                        ?? candidates.Where(p => p.IsActive).FirstOrDefault(p => line.Sku != null && p.Plu == line.Sku);
            if (match is not null)
                result[line.LineNumber] = (match.Id, match.Name);
        }
        return result;
    }

    private bool IsHere(string? storeCode) => string.Equals(storeCode?.Trim(), _terminal.StoreId, StringComparison.OrdinalIgnoreCase);
}
