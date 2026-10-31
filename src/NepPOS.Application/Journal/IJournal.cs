using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;

namespace NepPOS.Application.Journal;

/// <summary>
/// The register's audit trail. Every entry is stamped with this register and the
/// signed-in employee automatically.
/// Writing NEVER throws: a journal problem is logged to the log file but must not
/// block a sale, a sign-in or a payment that has already happened.
/// </summary>
public interface IJournal
{
    Task WriteAsync(
        JournalEventType eventType,
        string detail,
        Guid? saleId = null,
        long? invoiceNumber = null,
        decimal? amount = null,
        CancellationToken cancellationToken = default);

    /// <summary>Entries for the query, newest first.</summary>
    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken cancellationToken = default);
}

/// <param name="From">First local calendar day included.</param>
/// <param name="To">Last local calendar day included.</param>
public sealed record JournalQuery(
    DateOnly From,
    DateOnly To,
    int? EmployeeId = null,
    JournalEventType? EventType = null,
    int MaxResults = 500);
