using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Journal;
using NepPOS.Application.Stock;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Infrastructure.Database.Stock;

/// <summary>
/// Writes stock off as wastage (ullages): one Wastage movement per product at its cost, all in one save, then a
/// Journal line with the docket's value (which the day's Z adds up).
/// </summary>
public sealed class WastageService : IWastageService
{
    private readonly NepPosDbContext _db;
    private readonly IJournal? _journal;

    /// <param name="journal">Where the docket is recorded for the Z (null in tests that don't need it).</param>
    public WastageService(NepPosDbContext db, IJournal? journal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _journal = journal;
    }

    public async Task<IReadOnlyList<WastageProduct>> FindProductsAsync(string term, CancellationToken cancellationToken = default)
    {
        var text = (term ?? "").Trim();
        if (text.Length == 0)
            return Array.Empty<WastageProduct>();

        // A scanned barcode or typed PLU first (the product's own, a pack size's, or an extra barcode); then by name.
        var key = ProductBarcode.KeyFor(text);
        var byCode = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && (p.Barcode == text || p.Plu == text
                        || _db.ProductVariants.Any(v => v.ProductId == p.Id && (v.Barcode == text || v.Plu == text))
                        || _db.ProductBarcodes.Any(b => b.ProductId == p.Id && b.LookupKey == key)))
            .ToListAsync(cancellationToken);
        var products = byCode.Count > 0
            ? byCode
            : await _db.Products.AsNoTracking()
                .Where(p => p.IsActive && EF.Functions.Like(p.Name, $"%{text}%"))
                .OrderBy(p => p.Name)
                .Take(30)
                .ToListAsync(cancellationToken);

        var ids = products.Select(p => p.Id).ToList();
        var packs = (await _db.ProductVariants.AsNoTracking()
                .Where(v => ids.Contains(v.ProductId) && v.IsActive && v.UnitQuantity > 1)
                .ToListAsync(cancellationToken))
            .ToLookup(v => v.ProductId);

        return products.Select(p => new WastageProduct(p.Id, p.Name, p.Plu, p.CostPrice ?? p.LastCost ?? 0m, p.StockOnHand,
                new[] { new WastagePack("Single", 1) }
                    .Concat(packs[p.Id].OrderBy(v => v.UnitQuantity).Select(v => new WastagePack(v.DisplayName, v.UnitQuantity)))
                    .DistinctBy(pack => pack.Units)
                    .ToList()))
            .ToList();
    }

    public async Task<WastageRecorded> RecordAsync(IReadOnlyList<WastageLine> lines, string reason, string? employeeName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        reason = (reason ?? "").Trim();
        var wanted = lines.Where(l => l.Quantity != 0).ToList();
        if (wanted.Count == 0)
            throw new InvalidOperationException("Add at least one product to write off.");
        if (wanted.Any(l => l.Quantity < 0 || l.PackUnits < 1))
            throw new InvalidOperationException("Quantities must be more than 0.");
        if (reason.Length == 0)
            throw new InvalidOperationException("Choose or type a reason.");

        var now = DateTime.UtcNow;
        var docket = Wastage.DocketFor(now.ToLocalTime());
        var reference = Wastage.Reference(docket, reason);

        var ids = wanted.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        decimal singles = 0, cost = 0;
        var negative = new List<string>();
        foreach (var group in wanted.GroupBy(l => l.ProductId))
        {
            if (!products.TryGetValue(group.Key, out var product))
                throw new InvalidOperationException($"{group.First().ProductName} no longer exists.");

            var count = group.Sum(l => l.Singles);
            var unitCost = product.CostPrice ?? product.LastCost ?? 0m;
            _db.StockMovements.Add(product.MoveStock(StockMovementType.Wastage, -count, reference, now, unitCost, employeeName));
            singles += count;
            cost += decimal.Round(count * unitCost, 2, MidpointRounding.AwayFromZero);
            if (product.StockOnHand < 0)
                negative.Add(product.Name);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_journal is not null)
            await _journal.WriteAsync(JournalEventType.WastageRecorded,
                $"{reference} - {singles:0.##} item{(singles == 1 ? "" : "s")} written off by {employeeName ?? "unknown"}",
                amount: cost, cancellationToken: cancellationToken);

        return new WastageRecorded(docket, now, singles, cost, negative);
    }

    public async Task<IReadOnlyList<WastageEntry>> HistoryAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

        var rows = await _db.StockMovements.AsNoTracking()
            .Where(m => m.Type == StockMovementType.Wastage && m.AtUtc >= fromUtc && m.AtUtc < toUtc)
            .Join(_db.Products.AsNoTracking(), m => m.ProductId, p => p.Id,
                (m, p) => new { m.AtUtc, m.ProductId, p.Name, m.Quantity, m.UnitCost, p.CostPrice, m.Reference, m.EmployeeName })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new WastageEntry(DateTime.SpecifyKind(r.AtUtc, DateTimeKind.Utc), r.ProductId, r.Name, -r.Quantity,
                decimal.Round(-r.Quantity * (r.UnitCost ?? r.CostPrice ?? 0m), 2, MidpointRounding.AwayFromZero),
                Wastage.ReasonOf(r.Reference), r.EmployeeName))
            .OrderByDescending(r => r.AtUtc)
            .ToList();
    }
}
