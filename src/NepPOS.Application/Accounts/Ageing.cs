namespace NepPOS.Application.Accounts;

/// <summary>One line of an account as the ageing sees it: positive = owed (with when it's due), negative = paid or credited.</summary>
public readonly record struct AccountLine(decimal Amount, DateTime At, DateTime? Due);

/// <summary>
/// What an account owes, split by how overdue it is. Used for customers (debtors) and suppliers (creditors).
/// <see cref="Current"/> isn't due yet; the others are days past the due date.
/// </summary>
public sealed record Ageing(decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public static readonly Ageing Zero = new(0, 0, 0, 0, 0);

    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;

    /// <summary>Past its due date.</summary>
    public decimal Overdue => Days1To30 + Days31To60 + Days61To90 + Over90;

    public Ageing Add(Ageing other) => new(Current + other.Current, Days1To30 + other.Days1To30,
        Days31To60 + other.Days31To60, Days61To90 + other.Days61To90, Over90 + other.Over90);
}

public static class AccountAgeing
{
    /// <summary>
    /// Ages an account the usual way: payments and credits pay off the oldest charges first, and whatever is
    /// left of each charge counts by how many days it is past its due date on <paramref name="asOf"/>.
    /// Credit left over (they paid too much) shows as a negative <see cref="Ageing.Current"/>.
    /// </summary>
    public static Ageing Calculate(IEnumerable<AccountLine> lines, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var all = lines.ToList();

        var credit = all.Where(l => l.Amount < 0).Sum(l => -l.Amount);
        var charges = all.Where(l => l.Amount > 0)
            .OrderBy(l => l.Due ?? l.At)
            .ThenBy(l => l.At)
            .ToList();

        decimal current = 0, d30 = 0, d60 = 0, d90 = 0, over = 0;
        foreach (var charge in charges)
        {
            var applied = Math.Min(credit, charge.Amount);
            credit -= applied;
            var left = charge.Amount - applied;
            if (left == 0)
                continue;

            var daysOverdue = (asOf.Date - (charge.Due ?? charge.At).Date).Days;
            switch (daysOverdue)
            {
                case <= 0: current += left; break;
                case <= 30: d30 += left; break;
                case <= 60: d60 += left; break;
                case <= 90: d90 += left; break;
                default: over += left; break;
            }
        }

        return new Ageing(current - credit, d30, d60, d90, over);
    }
}
