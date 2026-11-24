using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Sales;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Sales;

public sealed class GiftCardService : IGiftCardService
{
    private readonly NepPosDbContext _db;

    public GiftCardService(NepPosDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<GiftCardBalance?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        var card = await _db.GiftCards.AsNoTracking().FirstOrDefaultAsync(g => g.Code == normalised, cancellationToken);
        return card is null ? null : new GiftCardBalance(card.Code, card.Kind, card.Balance, card.IsActive, card.IssuedUtc);
    }

    public async Task<string> NewCodeAsync(GiftCardKind kind, CancellationToken cancellationToken = default)
    {
        var prefix = kind == GiftCardKind.StoreCredit ? "SC-" : "GC-";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            // Random, not sequential: a card number shouldn't let anyone guess the next one.
            var code = prefix + RandomNumberGenerator.GetInt32(100000, 1000000);
            if (!await _db.GiftCards.AnyAsync(g => g.Code == code, cancellationToken))
                return code;
        }
        throw new InvalidOperationException("Couldn't find a free card number. Try again.");
    }

    public async Task<IReadOnlyList<GiftCardBalance>> ListAsync(string? search = null, int take = 200, CancellationToken cancellationToken = default)
    {
        var query = _db.GiftCards.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(g => g.Code.Contains(term));
        }
        var cards = await query.OrderByDescending(g => g.Id).Take(take).ToListAsync(cancellationToken);
        return cards.Select(c => new GiftCardBalance(c.Code, c.Kind, c.Balance, c.IsActive, c.IssuedUtc)).ToList();
    }

    public async Task<IReadOnlyList<GiftCardMovement>> HistoryAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        var card = await _db.GiftCards.AsNoTracking().Include(g => g.Movements).FirstOrDefaultAsync(g => g.Code == normalised, cancellationToken);
        return card?.Movements.OrderByDescending(m => m.Id).ToList() ?? new List<GiftCardMovement>();
    }

    public async Task CancelAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalised = (code ?? "").Trim().ToUpperInvariant();
        var card = await _db.GiftCards.FirstOrDefaultAsync(g => g.Code == normalised, cancellationToken)
            ?? throw new InvalidOperationException($"{normalised} wasn't found.");
        card.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
