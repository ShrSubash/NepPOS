using System.Text.RegularExpressions;
using NepPOS.Application.Accounts;
using NepPOS.Domain.Entities;

namespace NepPOS.Application.Customers;

/// <summary>What the customer form saves. <paramref name="Id"/> null = a new customer.</summary>
public sealed record CustomerInput(
    Guid? Id,
    string Name,
    string? Phone,
    string? Email,
    string? Company,
    string? Abn,
    string? Address,
    string? Notes,
    bool IsAccount,
    decimal CreditLimit,
    int TermsDays,
    bool IsActive = true);

/// <summary>A customer's account: every line, newest first, and how overdue it is.</summary>
public sealed record CustomerStatement(Customer Customer, IReadOnlyList<CustomerAccountEntry> Entries, Ageing Ageing);

/// <summary>One row of the debtors list: an account customer who owes money (or is in credit).</summary>
public sealed record DebtorRow(Customer Customer, Ageing Ageing, DateTime? LastPaymentUtc);

/// <summary>
/// Customers and their accounts, shared with every store through Head Office.
/// Account lines are written with the sale that caused them (see the sale repository), never edited.
/// </summary>
public interface ICustomerService
{
    /// <summary>By name, phone, email, company or code; newest first when <paramref name="term"/> is empty.</summary>
    Task<IReadOnlyList<Customer>> SearchAsync(string? term, bool accountsOnly = false, int take = 50, CancellationToken cancellationToken = default);

    Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>"CUST-12345678" from a scan or the keyboard.</summary>
    Task<Customer?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a customer and sends it to the other stores. Throws with a message a person can act on.</summary>
    Task<Customer> SaveAsync(CustomerInput input, CancellationToken cancellationToken = default);

    Task<CustomerStatement?> StatementAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Account customers with a balance (owing or in credit), biggest overdue first.</summary>
    Task<IReadOnlyList<DebtorRow>> DebtorsAsync(CancellationToken cancellationToken = default);

    /// <summary>A manager's correction to an account (positive = they owe more). Kept forever with the reason.</summary>
    Task AdjustAsync(Guid id, decimal amount, string reason, string? employeeName, CancellationToken cancellationToken = default);
}

/// <summary>Customer codes: "CUST-" and digits, printed on statements and customer cards.</summary>
public static class CustomerCode
{
    private static readonly Regex Pattern = new(@"^CUST-\d{4,12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Looks(string? code) => Pattern.IsMatch((code ?? "").Trim());
}

/// <summary>
/// A payment off a customer's account is rung up as a sale with this one line (no GST: it pays for goods
/// already invoiced), paid with cash, card or any tender; saving it takes the amount off their account.
/// </summary>
public static class AccountPayment
{
    public const string Sku = "ACCOUNTPAY";
    public const string Category = "Account payment";
    public const string Name = "Payment on account";

    public static bool IsAccountPayment(SaleLineItem line) =>
        string.Equals(line.Sku, Sku, StringComparison.OrdinalIgnoreCase);
}

public static class CustomerAgeing
{
    /// <summary>Ages a customer's account (charges by their due date), as of <paramref name="asOfLocal"/>.</summary>
    public static Ageing For(IEnumerable<CustomerAccountEntry> entries, DateTime asOfLocal) =>
        AccountAgeing.Calculate(entries.Select(e => new AccountLine(
            e.Amount,
            e.AtUtc.ToLocalTime(),
            e.DueUtc?.ToLocalTime())), asOfLocal);
}
