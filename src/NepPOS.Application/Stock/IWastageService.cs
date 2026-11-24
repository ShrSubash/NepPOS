namespace NepPOS.Application.Stock;

/// <summary>A pack size a product can be written off in: "Single" (1), "6-pack" (6), "Carton" (24)…</summary>
public sealed record WastagePack(string Name, int Units);

/// <summary>A product found for the wastage screen, with what one single costs and the pack sizes it comes in.</summary>
public sealed record WastageProduct(int ProductId, string Name, string? Plu, decimal CostPerSingle, decimal StockOnHand, IReadOnlyList<WastagePack> Packs);

/// <summary>One line being written off: <paramref name="Quantity"/> of a pack of <paramref name="PackUnits"/> singles.</summary>
public sealed record WastageLine(int ProductId, string ProductName, string PackName, int PackUnits, decimal Quantity, decimal CostPerSingle)
{
    public decimal Singles => Quantity * PackUnits;
    public decimal Cost => decimal.Round(Singles * CostPerSingle, 2, MidpointRounding.AwayFromZero);
}

/// <summary>What was written off: the docket number (printed and in the Journal), how many singles and their cost.</summary>
public sealed record WastageRecorded(string Docket, DateTime AtUtc, decimal Singles, decimal Cost, IReadOnlyList<string> NegativeStock);

/// <summary>One written-off product in the history, at cost.</summary>
public sealed record WastageEntry(DateTime AtUtc, int ProductId, string ProductName, decimal Singles, decimal Cost, string Reason, string? EmployeeName);

/// <summary>
/// Ullages (wastage): stock that's broken, expired or used, written off at cost. Each docket takes the stock off,
/// keeps a Wastage stock movement per product, and goes in the Journal so the day's Z shows its value.
/// </summary>
public interface IWastageService
{
    /// <summary>By barcode, PLU or name.</summary>
    Task<IReadOnlyList<WastageProduct>> FindProductsAsync(string term, CancellationToken cancellationToken = default);

    /// <summary>Writes the lines off together under one reason. Throws with a message a person can act on.</summary>
    Task<WastageRecorded> RecordAsync(IReadOnlyList<WastageLine> lines, string reason, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>Everything written off between two local days (inclusive), newest first (returns written off as faulty too).</summary>
    Task<IReadOnlyList<WastageEntry>> HistoryAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public static class Wastage
{
    /// <summary>The reasons offered as buttons (any other can be typed).</summary>
    public static readonly IReadOnlyList<string> Reasons = new[]
    {
        "Broken", "Expired", "Damaged", "Spoiled", "Staff use", "Tasting / sample", "Theft", "Supplier return"
    };

    /// <summary>"W" + local date and time: unique per register, easy to read out ("W241126-1432").</summary>
    public static string DocketFor(DateTime localTime) => "W" + localTime.ToString("yyMMdd-HHmmss");

    /// <summary>The Journal line and stock movement reference for a docket.</summary>
    public static string Reference(string docket, string reason) =>
        $"Wastage {docket}: {reason}".Length > 100 ? $"Wastage {docket}: {reason}"[..100] : $"Wastage {docket}: {reason}";

    /// <summary>The reason back from a stored reference ("Wastage W…: Broken" → "Broken").</summary>
    public static string ReasonOf(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        var colon = reference.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 && reference.StartsWith("Wastage ", StringComparison.Ordinal) ? reference[(colon + 2)..] : reference;
    }
}
