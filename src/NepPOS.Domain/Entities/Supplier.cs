namespace NepPOS.Domain.Entities;

/// <summary>Who the shop buys from. Any business: a brewery, a wholesaler, a bakery.</summary>
public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The number the supplier had in the shop's previous POS, kept so imports can find it again.</summary>
    public string? LegacyNumber { get; set; }

    /// <summary>The shop's customer number with this supplier (on their e-invoices), used to recognise them on import.</summary>
    public string? AccountNumber { get; set; }

    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Abn { get; set; }
    public string? Address { get; set; }

    /// <summary>Days the shop has to pay this supplier's invoices (e.g. 30 = 30 days from the invoice date).</summary>
    public int TermsDays { get; set; } = 30;

    public string? Notes { get; set; }

    /// <summary>What the shop owes this supplier now: bills less payments and credits (see <see cref="SupplierAccountEntry"/>).</summary>
    public decimal Balance { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum SupplierEntryKind
{
    /// <summary>An invoice to pay (from a received delivery or keyed in).</summary>
    Bill = 1,

    /// <summary>Money paid to the supplier.</summary>
    Payment = 2,

    /// <summary>A credit note or reversed delivery.</summary>
    Credit = 3
}

/// <summary>
/// One line on a supplier's account (creditors): bills add to what the shop owes, payments and credits take it off.
/// Never edited or deleted; a mistake is put right with a credit.
/// </summary>
public sealed class SupplierAccountEntry
{
    public long Id { get; set; }
    public int SupplierId { get; set; }
    public SupplierEntryKind Kind { get; set; }

    /// <summary>Positive = the shop owes more (bill); negative = owes less (payment, credit).</summary>
    public decimal Amount { get; set; }

    /// <summary>The supplier's invoice number, or the payment reference (EFT, cheque number).</summary>
    public string? Reference { get; set; }

    /// <summary>The delivery this bill came from, when it came from Receive stock.</summary>
    public int? StockReceiptId { get; set; }

    /// <summary>The invoice date (bills) or the day it was paid.</summary>
    public DateOnly Date { get; set; }

    /// <summary>When a bill must be paid (invoice date + the supplier's terms). Null for payments and credits.</summary>
    public DateOnly? DueDate { get; set; }

    public string? Note { get; set; }
    public string? EmployeeName { get; set; }
    public DateTime RecordedUtc { get; set; }
}

/// <summary>A supplier's own item code for a product, as on their invoices and price files.</summary>
public sealed class ProductSupplierCode
{
    public int Id { get; set; }
    public int ProductId { get; set; }

    /// <summary>Null when the code's supplier isn't known (old systems often didn't say).</summary>
    public int? SupplierId { get; set; }

    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// How many of the supplier's invoiced units make one of this product's singles. Usually 1; 10 when the
    /// supplier invoices cans and the shop sells the 10-pack as one item.
    /// </summary>
    public int SupplierUnitsPerSingle { get; set; } = 1;
}

/// <summary>A supplier's item the shop never stocks (bags, can handles), so e-invoice import skips it without asking.</summary>
public sealed class SupplierSkippedItem
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
