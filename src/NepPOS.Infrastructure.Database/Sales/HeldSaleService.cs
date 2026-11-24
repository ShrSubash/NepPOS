using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Sales;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Sales;

public sealed class HeldSaleService : IHeldSaleService
{
    private readonly NepPosDbContext _db;

    public HeldSaleService(NepPosDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<HeldSale> HoldAsync(string label, decimal total, int itemCount, string cartJson, string? employeeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cartJson))
            throw new InvalidOperationException("There's nothing to hold.");
        var held = new HeldSale
        {
            Id = Guid.NewGuid(),
            Label = string.IsNullOrWhiteSpace(label) ? "Held sale" : label.Trim()[..Math.Min(200, label.Trim().Length)],
            Total = total,
            ItemCount = itemCount,
            CartJson = cartJson,
            EmployeeName = employeeName,
            HeldUtc = DateTime.UtcNow
        };
        _db.HeldSales.Add(held);
        await _db.SaveChangesAsync(cancellationToken);
        return held;
    }

    public async Task<IReadOnlyList<HeldSale>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.HeldSales.AsNoTracking().OrderBy(h => h.HeldUtc).ToListAsync(cancellationToken);

    public async Task<HeldSale?> RecallAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var held = await _db.HeldSales.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (held is null)
            return null;
        _db.HeldSales.Remove(held);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;   // someone recalled it first
        }
        return held;
    }
}
