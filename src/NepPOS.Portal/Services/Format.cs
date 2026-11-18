namespace NepPOS.Portal.Services;

/// <summary>How the Portal shows times: "5 min ago" in lists, the exact UTC time on hover.</summary>
public static class Format
{
    public static string Ago(DateTime? utc, DateTime? now = null)
    {
        if (utc is null)
            return "never";

        var span = (now ?? DateTime.UtcNow) - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return span.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)span.TotalMinutes} min ago",
            < 86400 => $"{(int)span.TotalHours} h ago",
            < 86400 * 30 => $"{(int)span.TotalDays} d ago",
            _ => utc.Value.ToString("d MMM yyyy")
        };
    }

    public static string Exact(DateTime? utc) => utc is null ? "never" : utc.Value.ToString("d MMM yyyy HH:mm") + " UTC";

    public static string When(DateTime? utc) => utc is null ? "Never seen" : "Last seen " + Ago(utc);
}
