using Microsoft.EntityFrameworkCore;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Sales;

/// <summary>
/// Changes gift card and store credit balances for a sale being saved, in the same save as the sale:
/// spent cards go down, refunds to a card or new store credit go up, gift cards sold are loaded.
/// Refuses (throws) if a card would go below zero or is cancelled, so a sale can never overspend one.
/// Each movement also goes in the outbox, so Head Office passes it to the owner's other registers.
/// </summary>
internal sealed class GiftCardLedger
{
    private readonly NepPosDbContext _db;

    public GiftCardLedger(NepPosDbContext db) => _db = db;

    public async Task ApplyAsync(SaleTransaction sale, CancellationToken cancellationToken)
    {
        var changes = new List<(string Code, GiftCardKind Kind, decimal Amount, string Note)>();

        foreach (var p in sale.Payments.Where(p => p.TenderType is TenderType.GiftCard or TenderType.StoreCredit && p.Reference is not null))
        {
            var kind = p.TenderType == TenderType.GiftCard ? GiftCardKind.GiftCard : GiftCardKind.StoreCredit;
            changes.Add((p.Reference!, kind, -p.Amount, p.IsRefund ? "Refund" : "Spent"));
        }
        foreach (var line in sale.LineItems.Where(li => li.GiftCardCode is not null && li.Quantity > 0))
            changes.Add((line.GiftCardCode!, GiftCardKind.GiftCard, line.LineTotalIncGst, "Sold / topped up"));

        if (changes.Count == 0)
            return;

        var codes = changes.Select(c => c.Code.ToUpperInvariant()).Distinct().ToList();
        var cards = await _db.GiftCards.Where(g => codes.Contains(g.Code)).ToDictionaryAsync(g => g.Code, cancellationToken);
        var now = DateTime.UtcNow;

        foreach (var change in changes)
        {
            var code = change.Code.ToUpperInvariant();
            if (!cards.TryGetValue(code, out var card))
            {
                if (change.Amount < 0)
                    throw new InvalidOperationException(
                        $"{code} isn't known here. If it was sold at another store, check this register is connected to Head Office.");
                card = new GiftCard { Code = code, Kind = change.Kind, IssuedUtc = now };
                _db.GiftCards.Add(card);
                cards[code] = card;
            }
            if (!card.IsActive && change.Amount < 0)
                throw new InvalidOperationException($"{code} has been cancelled.");
            if (card.Balance + change.Amount < 0)
                throw new InvalidOperationException($"{code} only has {card.Balance:C} left.");

            card.Balance += change.Amount;
            card.LastUsedUtc = now;
            var movement = new GiftCardMovement
            {
                Amount = change.Amount,
                SaleId = sale.Id,
                InvoiceNumber = sale.InvoiceNumber,
                AtUtc = now,
                Note = change.Note
            };
            card.Movements.Add(movement);
            _db.OutboxMessages.Add(OutboxMessage.GiftCardMoved(card, movement, sale.StoreId, sale.RegisterId));
        }
    }
}
