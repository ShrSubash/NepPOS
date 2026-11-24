namespace NepPOS.Domain.Entities;

/// <summary>
/// A customer of the business: shared by every register and store of the same owner through Head Office.
/// An account customer (<see cref="IsAccount"/>) can buy now and pay later, up to <see cref="CreditLimit"/>.
/// </summary>
public sealed class Customer
{
    /// <summary>Made on the register that created the customer; the same at every store.</summary>
    public Guid Id { get; set; }

    /// <summary>"CUST-12345678": printed on their card or statement and scanned at the register.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>The business they buy for, if any (a café buying milk, a club buying stock).</summary>
    public string? Company { get; set; }

    public string? Abn { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }

    /// <summary>Can charge sales to an account and pay later.</summary>
    public bool IsAccount { get; set; }

    /// <summary>The most they may owe. 0 = no limit set (a manager still approves every charge over it when set).</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>Days they have to pay an account sale (e.g. 30 = 30 days from the sale).</summary>
    public int TermsDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    /// <summary>What they owe now, across all stores: charges less payments and credits (see <see cref="CustomerAccountEntry"/>).</summary>
    public decimal Balance { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>The newest edit wins when two stores change the same customer.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Over the credit limit if this much more were charged (never when no limit is set).</summary>
    public bool WouldExceedLimit(decimal extra) => CreditLimit > 0 && Balance + extra > CreditLimit;

    public decimal AvailableCredit => CreditLimit <= 0 ? decimal.MaxValue : Math.Max(0, CreditLimit - Balance);
}

public enum AccountEntryKind
{
    /// <summary>A sale charged to the account.</summary>
    Charge = 1,

    /// <summary>Money they paid off the account.</summary>
    Payment = 2,

    /// <summary>A refund back to the account (returned goods), or a credit note.</summary>
    Credit = 3,

    /// <summary>A manager's correction.</summary>
    Adjustment = 4
}

/// <summary>
/// One line on a customer's account (debtors). Append-only and identified by a GUID, so every store can copy
/// every line through Head Office without ever clashing: the balance is simply the sum of the lines.
/// </summary>
public sealed class CustomerAccountEntry
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public AccountEntryKind Kind { get; set; }

    /// <summary>Positive = they owe more (charge); negative = they owe less (payment, credit).</summary>
    public decimal Amount { get; set; }

    public Guid? SaleId { get; set; }
    public long? InvoiceNumber { get; set; }

    /// <summary>Where it happened ("STORE-001 / REG-001").</summary>
    public string? Where { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>When a charge must be paid (sale date + their terms). Null for payments and credits.</summary>
    public DateTime? DueUtc { get; set; }

    public string? Reference { get; set; }
    public string? Note { get; set; }
}
