namespace NepPOS.Contracts.Accounts;

// What a business's website (website key) reads and changes: its customers, their store accounts and its
// gift cards. Head Office keeps a copy of all three from the registers' shared events; anything the website
// changes goes back to every register the same way (GET /api/v1/sync/shared).

/// <summary>A customer as the website sees them. <c>Balance</c> = what they owe on their store account.</summary>
public sealed record CustomerAccount(
    Guid CustomerId,
    string Code,
    string Name,
    string? Email,
    string? Phone,
    bool IsAccount,
    decimal CreditLimit,
    int TermsDays,
    bool IsActive,
    decimal Balance,
    decimal Overdue);

/// <summary>
/// POST /api/v1/customers: a customer who signed up on the website. If a customer with the same email already
/// exists (e.g. added at the register), that customer is returned and nothing is created.
/// </summary>
public sealed record CustomerSignUp(string Email, string Name, string? Phone);

/// <summary>One line on a customer's account. <c>Amount</c> positive = they owe more.</summary>
public sealed record AccountStatementLine(
    Guid EntryId,
    string Kind,
    decimal Amount,
    long? InvoiceNumber,
    DateTime AtUtc,
    DateTime? DueUtc,
    string? Where,
    string? Note);

/// <summary>GET /api/v1/customers/{id}/account: the customer, how overdue they are, and their account lines (newest first).</summary>
public sealed record CustomerStatementView(CustomerAccount Customer, Ageing Ageing, IReadOnlyList<AccountStatementLine> Lines);

/// <summary>GET /api/v1/giftcards/{code}: a gift card or store credit and what's left on it.</summary>
public sealed record GiftCardView(string Code, string Kind, decimal Balance, bool IsActive, DateTime? LastUsedUtc);

/// <summary>
/// POST /api/v1/giftcards/{code}/redeem (spend) or /refund (put back, e.g. an online order cancelled).
/// <c>RequestId</c> makes a retry harmless: the same id is only ever applied once.
/// </summary>
public sealed record GiftCardChangeRequest(Guid RequestId, decimal Amount, string Reference);

/// <summary>What was taken or put back, and the balance now.</summary>
public sealed record GiftCardChangeResult(string Code, decimal Amount, decimal Balance);

/// <summary>
/// One in-store sale (or refund, negative) with a customer on it, so the website can give them points.
/// <c>ChangeNumber</c> is Head Office's number for the sale; it counts up.
/// </summary>
public sealed record CustomerPurchase(long ChangeNumber, Guid CustomerId, Guid SaleId, long InvoiceNumber, decimal Total, DateTime AtUtc, Guid StoreId);

/// <summary>
/// Answer to GET /api/v1/customers/purchases?after=N (website key): in-store sales with a customer, after change
/// number N, oldest first. Keep <c>Next</c> and send it as <c>after</c> next time; while <c>More</c>, ask again.
/// </summary>
public sealed record CustomerPurchaseChanges(IReadOnlyList<CustomerPurchase> Purchases, long Next, bool More)
{
    /// <summary>Sales looked at per page (most have no customer, so a page of purchases is usually shorter).</summary>
    public const int PageSize = 500;
}
