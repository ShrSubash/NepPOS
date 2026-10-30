namespace NepPOS.Application.Payments;

/// <summary>
/// Australian cash rounding to the nearest 5 cents (cash only — card payments are exact).
/// 1–2c round down, 3–4c round up, 6–7c round down, 8–9c round up.
/// </summary>
public static class CashRounding
{
    public static decimal Round(decimal amount) =>
        Math.Round(amount * 20m, MidpointRounding.AwayFromZero) / 20m;

    /// <summary>True when the amount can be handed over in Australian coins/notes (ends in 0 or 5 cents).</summary>
    public static bool IsPayableInCash(decimal amount) => amount * 20m == decimal.Truncate(amount * 20m);
}
