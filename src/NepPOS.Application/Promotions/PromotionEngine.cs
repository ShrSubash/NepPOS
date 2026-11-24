using NepPOS.Domain.Entities;

namespace NepPOS.Application.Promotions;

/// <summary>One line of the order as the promotions see it. <paramref name="Key"/> is how the discount comes back.</summary>
public sealed record PromoLine(string Key, int? ProductId, string? Category, decimal UnitPrice, int Quantity);

/// <summary>What a promotion takes off one line, and its name for the receipt.</summary>
public sealed record PromoDiscount(string Key, decimal Amount, string Reason);

/// <summary>
/// Works out the promotions on an order. Each line gets at most one promotion (the first that applies, in the
/// order given). A multi-buy mixes any products in it: the dearest items make up the sets first, and each full set
/// costs the promotion price; items left over pay full price. Discounts are rounded to the cent and never more than
/// the line.
/// </summary>
public static class PromotionEngine
{
    public static IReadOnlyList<PromoDiscount> Apply(IEnumerable<PromoLine> lines, IEnumerable<Promotion> promotions, DateTime nowLocal)
    {
        var open = lines.Where(l => l.Quantity > 0 && l.UnitPrice > 0 && l.ProductId is not null).ToList();
        var result = new Dictionary<string, PromoDiscount>();

        foreach (var promotion in promotions.Where(p => p.RunsAt(nowLocal)))
        {
            var ids = promotion.ProductIdList.ToHashSet();
            var matching = open
                .Where(l => !result.ContainsKey(l.Key))
                .Where(l => promotion.Category is { Length: > 0 } category
                    ? string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase)
                    : ids.Contains(l.ProductId!.Value))
                .ToList();
            if (matching.Count == 0)
                continue;

            switch (promotion.Kind)
            {
                case PromotionKind.PercentOff when promotion.Percent is > 0 and <= 100:
                    foreach (var line in matching)
                        Add(result, line, line.UnitPrice * line.Quantity * promotion.Percent / 100m, $"{promotion.Name}");
                    break;

                case PromotionKind.AmountOff when promotion.Amount > 0:
                    foreach (var line in matching)
                        Add(result, line, Math.Min(promotion.Amount, line.UnitPrice) * line.Quantity, $"{promotion.Name}");
                    break;

                case PromotionKind.MultiBuy when promotion.BuyQuantity >= 2 && promotion.Price > 0:
                    MultiBuy(result, matching, promotion);
                    break;
            }
        }

        return result.Values.Where(d => d.Amount > 0).ToList();
    }

    private static void MultiBuy(Dictionary<string, PromoDiscount> result, List<PromoLine> matching, Promotion promotion)
    {
        // Every unit, dearest first; full sets of BuyQuantity are priced at the promotion price.
        var units = matching
            .SelectMany(l => Enumerable.Repeat(l, l.Quantity))
            .OrderByDescending(l => l.UnitPrice)
            .ToList();
        var sets = units.Count / promotion.BuyQuantity;
        if (sets == 0)
            return;

        var discounts = new Dictionary<string, decimal>();
        for (var s = 0; s < sets; s++)
        {
            var set = units.Skip(s * promotion.BuyQuantity).Take(promotion.BuyQuantity).ToList();
            var full = set.Sum(u => u.UnitPrice);
            var saving = full - promotion.Price;
            if (saving <= 0)
                continue;   // the "deal" costs more than full price: no discount

            // Spread the saving over the set's items by price, so each line's GST stays right.
            foreach (var unit in set)
                discounts[unit.Key] = discounts.GetValueOrDefault(unit.Key) + saving * unit.UnitPrice / full;
        }

        foreach (var (key, amount) in discounts)
            Add(result, matching.First(l => l.Key == key), amount, promotion.Name);
    }

    private static void Add(Dictionary<string, PromoDiscount> result, PromoLine line, decimal amount, string reason)
    {
        var rounded = Math.Min(decimal.Round(amount, 2, MidpointRounding.AwayFromZero), line.UnitPrice * line.Quantity);
        if (rounded > 0)
            result[line.Key] = new PromoDiscount(line.Key, rounded, reason);
    }

    /// <summary>
    /// Splits a whole-sale discount over the lines by their value (each line's share rounded to the cent; the last line
    /// takes the cents left over so it adds up exactly). Lines are (key, value after any discount they already have).
    /// </summary>
    public static IReadOnlyList<(string Key, decimal Amount)> Spread(decimal discount, IReadOnlyList<(string Key, decimal Value)> lines)
    {
        var total = lines.Sum(l => l.Value);
        if (discount <= 0 || total <= 0)
            return Array.Empty<(string, decimal)>();
        discount = Math.Min(discount, total);

        var shares = new List<(string Key, decimal Amount)>();
        var given = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var share = i == lines.Count - 1
                ? discount - given
                : decimal.Round(discount * lines[i].Value / total, 2, MidpointRounding.AwayFromZero);
            share = Math.Min(share, lines[i].Value);
            given += share;
            shares.Add((lines[i].Key, share));
        }
        return shares;
    }
}

/// <summary>Promotions kept on this register (Manage → Promotions).</summary>
public interface IPromotionService
{
    Task<IReadOnlyList<Promotion>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Active ones, in the order they're tried.</summary>
    Task<IReadOnlyList<Promotion>> ActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or updates (Id 0 = new). Throws with a message a person can act on.</summary>
    Task<Promotion> SaveAsync(Promotion promotion, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
