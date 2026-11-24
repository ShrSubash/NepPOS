using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Sales;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Sales;

/// <summary>Finds sales on this register for returns, with how much of each line can still come back.</summary>
public sealed class ReturnsService : IReturnsService
{
    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal _terminal;

    public ReturnsService(NepPosDbContext db, RegisterTerminal terminal)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    public async Task<ReturnableSale?> FindAsync(string receiptCodeOrInvoice, CancellationToken cancellationToken = default)
    {
        var text = (receiptCodeOrInvoice ?? "").Trim();
        long invoice;
        if (ReceiptCode.TryParse(text, out var registerPart, out var fromCode))
        {
            if (registerPart != ReceiptCode.RegisterPart(_terminal.RegisterId))
                throw new InvalidOperationException(
                    $"This receipt is from another register ({registerPart}). Return it at that register for now.");
            invoice = fromCode;
        }
        else if (!long.TryParse(text.TrimStart('#'), out invoice) || invoice <= 0)
        {
            return null;
        }

        var sale = await _db.SaleTransactions.AsNoTracking()
            .Include(s => s.LineItems)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.StoreId == _terminal.StoreId && s.RegisterId == _terminal.RegisterId
                                      && s.InvoiceNumber == invoice && s.Status == SaleTransactionStatus.Completed, cancellationToken);
        if (sale is null)
            return null;

        var ids = sale.LineItems.Select(li => li.Id).ToList();
        var returned = await _db.SaleLineItems.AsNoTracking()
            .Where(li => li.ReturnOfLineId != null && ids.Contains(li.ReturnOfLineId.Value))
            .Select(li => new { Id = li.ReturnOfLineId!.Value, li.Quantity })
            .ToListAsync(cancellationToken);

        var lines = sale.LineItems
            .Where(li => li.Quantity > 0)
            .Select(li => new ReturnableLine(li, -returned.Where(r => r.Id == li.Id).Sum(r => r.Quantity)))
            .ToList();
        return new ReturnableSale(sale, lines);
    }

    public async Task<IReadOnlyList<SaleTransaction>> SalesOnAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        // Local day → UTC range; SQLite can't compare DateTimeOffset, so filter by invoice in memory after a broad pick.
        var recent = await _db.SaleTransactions.AsNoTracking()
            .Include(s => s.LineItems)
            .Include(s => s.Payments)
            .Where(s => s.RegisterId == _terminal.RegisterId && s.Status == SaleTransactionStatus.Completed)
            .OrderByDescending(s => s.InvoiceNumber)
            .Take(2000)
            .ToListAsync(cancellationToken);
        return recent.Where(s => DateOnly.FromDateTime(s.CreatedAtUtc.ToLocalTime().DateTime) == day).ToList();
    }
}
