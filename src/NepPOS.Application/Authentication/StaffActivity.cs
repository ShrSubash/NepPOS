using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Authentication;

/// <summary>
/// What one person did at the register in a period: when they were signed in, what they sold, and the
/// things a manager checks twice (refunds, voids, no-sales, discounts, lines removed).
/// </summary>
public sealed record StaffActivityLine(
    int? EmployeeId,
    string EmployeeName,
    DateTime? FirstSignInUtc,
    DateTime? LastSignOutUtc,
    int SignIns,
    int Sales,
    decimal SalesTotal,
    int Refunds,
    decimal RefundsTotal,
    int Voids,
    decimal VoidsTotal,
    int LinesRemoved,
    int NoSales,
    int Discounts,
    decimal DiscountsTotal)
{
    public decimal AverageSale => Sales == 0 ? 0 : Math.Round(SalesTotal / Sales, 2);
}

/// <summary>Builds the staff activity report from the journal (every entry is already stamped with who was signed in).</summary>
public static class StaffActivity
{
    public static IReadOnlyList<StaffActivityLine> Build(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .Where(e => e.EmployeeId is not null || !string.IsNullOrEmpty(e.EmployeeName))
            .GroupBy(e => e.EmployeeId)
            .Select(g =>
            {
                var of = (JournalEventType type) => g.Where(e => e.EventType == type).ToList();
                var signIns = of(JournalEventType.SignIn);
                var signOuts = of(JournalEventType.SignOut);
                var sales = of(JournalEventType.SaleCompleted);
                var refunds = of(JournalEventType.RefundCompleted);
                var voids = of(JournalEventType.SaleVoided);
                var discounts = of(JournalEventType.DiscountGiven);
                return new StaffActivityLine(
                    g.Key,
                    g.OrderByDescending(e => e.OccurredAtUtc).First().EmployeeName ?? "Unknown",
                    signIns.Count == 0 ? null : signIns.Min(e => e.OccurredAtUtc),
                    signOuts.Count == 0 ? null : signOuts.Max(e => e.OccurredAtUtc),
                    signIns.Count,
                    sales.Count,
                    sales.Sum(e => e.Amount ?? 0),
                    refunds.Count,
                    Math.Abs(refunds.Sum(e => e.Amount ?? 0)),   // refunds are journalled as negative amounts
                    voids.Count,
                    voids.Sum(e => e.Amount ?? 0),
                    of(JournalEventType.LineRemoved).Count,
                    of(JournalEventType.NoSale).Count,
                    discounts.Count,
                    discounts.Sum(e => e.Amount ?? 0));
            })
            .OrderByDescending(l => l.SalesTotal)
            .ThenBy(l => l.EmployeeName)
            .ToList();
    }
}
