namespace NepPOS.Application.Sales;

/// <summary>
/// Sunday and public holiday surcharges (Manage → Surcharges). The ACCC requires the shop to display
/// "A surcharge of x% applies on …" at least as prominently as its prices; the receipt shows it as its own line.
/// </summary>
public sealed record SurchargeSettings
{
    /// <summary>% added on Sundays (0 = none).</summary>
    public decimal SundayPercent { get; init; }

    /// <summary>% added on the public holidays listed below (0 = none). Wins over Sunday when both apply.</summary>
    public decimal PublicHolidayPercent { get; init; }

    public IReadOnlyList<DateOnly> PublicHolidays { get; init; } = Array.Empty<DateOnly>();

    /// <summary>True: added to every sale on those days (the cashier can take it off). False: the cashier adds it.</summary>
    public bool Automatic { get; init; } = true;
}

/// <summary>Which surcharge applies today, and how much it adds to a sale.</summary>
public static class SurchargeCalculator
{
    public const string Sku = "SURCHARGE";
    public const string Category = "Surcharge";

    /// <summary>The surcharge for this day (name and %), or null when none applies.</summary>
    public static (string Name, decimal Percent)? For(DateOnly day, SurchargeSettings? settings)
    {
        if (settings is null)
            return null;
        if (settings.PublicHolidayPercent > 0 && settings.PublicHolidays.Contains(day))
            return ("Public holiday surcharge", settings.PublicHolidayPercent);
        if (settings.SundayPercent > 0 && day.DayOfWeek == DayOfWeek.Sunday)
            return ("Sunday surcharge", settings.SundayPercent);
        return null;
    }

    /// <summary>
    /// The surcharge on the goods being bought (returns and gift cards don't attract it), rounded to the cent.
    /// </summary>
    public static decimal Amount(decimal goodsTotal, decimal percent) =>
        goodsTotal <= 0 || percent <= 0 ? 0 : decimal.Round(goodsTotal * percent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>"Sunday surcharge 10%".</summary>
    public static string Label(string name, decimal percent) => $"{name} {percent:0.##}%";
}
