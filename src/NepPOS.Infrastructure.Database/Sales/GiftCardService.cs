using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Abstractions;
using NepPOS.Application.Sales;
using NepPOS.Domain.Entities;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Sales;

public sealed class GiftCardService : IGiftCardService
{
    private readonly NepPosDbContext _db;
    private readonly RegisterTerminal? _terminal;

    /// <param name="terminal">This register (needed to tell the other stores about a cancelled card).</param>
    public GiftCardService(NepPosDbContext db, RegisterTerminal? terminal = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _terminal = terminal;
    }

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
            // Random, not sequential: a card number shouldn't let anyone guess the next one. Ten digits, so two
            // stores making cards at the same time (before they've heard of each other's) practically never clash.
            var code = prefix + RandomNumberGenerator.GetInt32(10000, 100000) + RandomNumberGenerator.GetInt32(0, 100000).ToString("D5");
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
        if (!card.IsActive)
            return;
        card.IsActive = false;

        // Recorded as a $0 movement, so the cancellation reaches the other stores like any other change.
        var movement = new GiftCardMovement { Amount = 0, AtUtc = DateTime.UtcNow, Note = "Cancelled" };
        card.Movements.Add(movement);
        if (_terminal is not null)
            _db.OutboxMessages.Add(OutboxMessage.GiftCardMoved(card, movement, _terminal.StoreId, _terminal.RegisterId, cancelled: true));
        else
            movement.EventId = Guid.NewGuid();
        await _db.SaveChangesAsync(cancellationToken);
    }
}
