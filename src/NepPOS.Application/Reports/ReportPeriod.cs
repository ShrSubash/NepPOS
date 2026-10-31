namespace NepPOS.Application.Reports;

/// <summary>
/// Which trading day a Z belongs to, and the standard report periods.
/// Weekly / monthly / yearly reports never reset anything: they add up the daily Zs
/// whose trading day falls in the period, so any past period can be reprinted.
/// </summary>
public static class ReportPeriod
{
    /// <summary>
    /// A Z closed before 5 am counts for the previous day, so a store that trades
    /// past midnight still gets "Friday's" Z for Friday night.
    /// </summary>
    public static readonly TimeSpan TradingDayCutoff = TimeSpan.FromHours(5);

    /// <summary>The trading day (local) for something that happened at this UTC time.</summary>
    public static DateOnly TradingDay(DateTime utc) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() - TradingDayCutoff);

    /// <summary>Monday to Sunday (Australian standard) containing the day.</summary>
    public static (DateOnly From, DateOnly To) Week(DateOnly day)
    {
        var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return (monday, monday.AddDays(6));
    }

    public static (DateOnly From, DateOnly To) Month(DateOnly day)
    {
        var first = new DateOnly(day.Year, day.Month, 1);
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Australian financial year: 1 July to 30 June.</summary>
    public static (DateOnly From, DateOnly To) FinancialYear(DateOnly day)
    {
        var startYear = day.Month >= 7 ? day.Year : day.Year - 1;
        return (new DateOnly(startYear, 7, 1), new DateOnly(startYear + 1, 6, 30));
    }
}
