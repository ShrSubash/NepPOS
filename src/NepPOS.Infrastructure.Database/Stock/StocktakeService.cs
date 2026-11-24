using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Journal;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Stock;

/// <summary>Stock counts: one in progress at a time; posting sets each counted product's stock to what was counted.</summary>
public sealed class StocktakeService : IStocktakeService
{
    private readonly NepPosDbContext _db;
    private readonly IJournal? _journal;

    public StocktakeService(NepPosDbContext db, IJournal? journal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _journal = journal;
    }

    public Task<Stocktake?> CurrentAsync(CancellationToken cancellationToken = default) =>
        _db.Stocktakes.AsNoTracking().Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Status == StocktakeStatus.Counting, cancellationToken);

    public async Task<Stocktake> StartAsync(string name, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (await _db.Stocktakes.AnyAsync(t => t.Status == StocktakeStatus.Counting, cancellationToken))
            throw new InvalidOperationException("A count is already in progress. Post or cancel it first.");
        var stocktake = new Stocktake
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Count {DateTime.Now:d MMM yyyy}" : name.Trim()[..Math.Min(100, name.Trim().Length)],
            StartedUtc = DateTime.UtcNow,
            EmployeeName = employeeName
        };
        _db.Stocktakes.Add(stocktake);
        await _db.SaveChangesAsync(cancellationToken);
        return stocktake;
    }

    public async Task<StocktakeRow> CountAsync(int stocktakeId, int productId, decimal singles, bool replace, CancellationToken cancellationToken = default)
    {
        if (singles < 0 || (!replace && singles == 0))
            throw new InvalidOperationException("Enter how many were counted.");
        await OpenAsync(stocktakeId, cancellationToken);
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new InvalidOperationException("That product no longer exists.");

        var line = await _db.StocktakeLines.FirstOrDefaultAsync(l => l.StocktakeId == stocktakeId && l.ProductId == productId, cancellationToken);
        if (line is null)
        {
            line = new StocktakeLine { StocktakeId = stocktakeId, ProductId = productId, ProductName = product.Name };
            _db.StocktakeLines.Add(line);
        }
        line.Counted = replace ? singles : line.Counted + singles;
        line.CountedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new StocktakeRow(productId, product.Name, line.Counted, product.StockOnHand, product.CostPrice ?? product.LastCost ?? 0m);
    }

    public async Task RemoveAsync(int stocktakeId, int productId, CancellationToken cancellationToken = default)
    {
        await OpenAsync(stocktakeId, cancellationToken);
        var line = await _db.StocktakeLines.FirstOrDefaultAsync(l => l.StocktakeId == stocktakeId && l.ProductId == productId, cancellationToken);
        if (line is null) return;
        _db.StocktakeLines.Remove(line);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StocktakeRow>> RowsAsync(int stocktakeId, CancellationToken cancellationToken = default)
    {
        var stocktake = await _db.Stocktakes.AsNoTracking().Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == stocktakeId, cancellationToken);
        if (stocktake is null)
            return Array.Empty<StocktakeRow>();

        var ids = stocktake.Lines.Select(l => l.ProductId).ToList();
        var products = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        // A posted count shows what it was posted against; an open one compares with stock now.
        return stocktake.Lines
            .Select(l =>
            {
                products.TryGetValue(l.ProductId, out var p);
                var expected = l.Expected ?? p?.StockOnHand ?? 0m;
                var cost = l.UnitCost ?? p?.CostPrice ?? p?.LastCost ?? 0m;
                return new StocktakeRow(l.ProductId, l.ProductName, l.Counted, expected, cost);
            })
            .OrderByDescending(r => Math.Abs(r.Value))
            .ThenBy(r => r.ProductName)
            .ToList();
    }

    public async Task<StocktakePosted> PostAsync(int stocktakeId, string? employeeName, CancellationToken cancellationToken = default)
    {
        var stocktake = await OpenAsync(stocktakeId, cancellationToken);
        await _db.Entry(stocktake).Collection(t => t.Lines).LoadAsync(cancellationToken);
        if (stocktake.Lines.Count == 0)
            throw new InvalidOperationException("Nothing has been counted yet.");

        var ids = stocktake.Lines.Select(l => l.ProductId).ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var reference = $"Stocktake {stocktake.Name}";
        if (reference.Length > 100) reference = reference[..100];

        decimal up = 0, down = 0, value = 0;
        var changedProducts = 0;
        foreach (var line in stocktake.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
                continue;   // deleted since it was counted
            var cost = product.CostPrice ?? product.LastCost ?? 0m;
            line.Expected = product.StockOnHand;
            line.UnitCost = cost;

            var difference = line.Counted - product.StockOnHand;
            if (difference == 0)
                continue;
            _db.StockMovements.Add(product.MoveStock(StockMovementType.Stocktake, difference, reference, now, cost, employeeName));
            changedProducts++;
            if (difference > 0) up += difference; else down -= difference;
            value += decimal.Round(difference * cost, 2, MidpointRounding.AwayFromZero);
        }

        stocktake.Status = StocktakeStatus.Posted;
        stocktake.FinishedUtc = now;
        await _db.SaveChangesAsync(cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.StocktakePosted,
                $"{reference}: {stocktake.Lines.Count} counted, {changedProducts} changed (+{up:0.##} / -{down:0.##}) by {employeeName ?? "unknown"}",
                amount: value, cancellationToken: cancellationToken);

        return new StocktakePosted(changedProducts, up, down, value);
    }

    public async Task CancelAsync(int stocktakeId, CancellationToken cancellationToken = default)
    {
        var stocktake = await OpenAsync(stocktakeId, cancellationToken);
        stocktake.Status = StocktakeStatus.Cancelled;
        stocktake.FinishedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Stocktake>> HistoryAsync(int take = 20, CancellationToken cancellationToken = default) =>
        await _db.Stocktakes.AsNoTracking().Include(t => t.Lines)
            .Where(t => t.Status != StocktakeStatus.Counting)
            .OrderByDescending(t => t.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    private async Task<Stocktake> OpenAsync(int stocktakeId, CancellationToken cancellationToken)
    {
        var stocktake = await _db.Stocktakes.FirstOrDefaultAsync(t => t.Id == stocktakeId, cancellationToken)
            ?? throw new InvalidOperationException("That count no longer exists.");
        if (stocktake.Status != StocktakeStatus.Counting)
            throw new InvalidOperationException("That count is already finished.");
        return stocktake;
    }
}
