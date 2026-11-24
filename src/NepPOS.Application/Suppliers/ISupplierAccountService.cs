using NepPOS.Contracts.Accounts;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Suppliers;

/// <summary>What the supplier form saves. <paramref name="Id"/> null = a new supplier.</summary>
public sealed record SupplierInput(
    int? Id,
    string Name,
    string? AccountNumber,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Abn,
    string? Address,
    int TermsDays,
    string? Notes,
    bool IsActive = true);

/// <summary>One row of the suppliers list: what the shop owes them and when the next bill is due.</summary>
public sealed record CreditorRow(Supplier Supplier, Ageing Ageing, DateOnly? NextDue);

/// <summary>A supplier's account: every bill, payment and credit, newest first.</summary>
public sealed record SupplierStatement(Supplier Supplier, IReadOnlyList<SupplierAccountEntry> Entries, Ageing Ageing);

/// <summary>
/// Suppliers and what the shop owes them (creditors). Deliveries received from a supplier add a bill
/// automatically; reversing a delivery adds a credit. Payments are recorded here when they're made.
/// </summary>
public interface ISupplierAccountService
{
    Task<IReadOnlyList<CreditorRow>> ListAsync(string? search = null, bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<SupplierStatement?> StatementAsync(int supplierId, CancellationToken cancellationToken = default);

    Task<Supplier> SaveAsync(SupplierInput input, CancellationToken cancellationToken = default);

    /// <summary>An invoice that didn't come through Receive stock (rent, cleaning, a delivery not tracked as stock).</summary>
    Task AddBillAsync(int supplierId, string reference, DateOnly invoiceDate, decimal amount, string? note, string? employeeName, CancellationToken cancellationToken = default);

    Task AddPaymentAsync(int supplierId, decimal amount, DateOnly date, string? reference, string? note, string? employeeName, CancellationToken cancellationToken = default);

    /// <summary>A credit note from the supplier (damaged goods, short delivery).</summary>
    Task AddCreditAsync(int supplierId, decimal amount, DateOnly date, string? reference, string? note, string? employeeName, CancellationToken cancellationToken = default);
}

public static class SupplierAgeing
{
    /// <summary>Ages a supplier's entries (bills by their due date).</summary>
    public static Ageing For(IEnumerable<SupplierAccountEntry> entries, DateTime asOf) =>
        AccountAgeing.Calculate(entries.Select(e => new AccountLine(
            e.Amount,
            e.Date.ToDateTime(TimeOnly.MinValue),
            e.DueDate?.ToDateTime(TimeOnly.MinValue))), asOf);
}
