using Microsoft.EntityFrameworkCore;
using NepPOS.Application.Customers;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using NepPOS.Infrastructure.Database.Outbox;

namespace NepPOS.Infrastructure.Database.Sales;

/// <summary>
/// Writes a sale's account lines in the same save as the sale: a sale charged to an account adds a charge
/// (due after the customer's terms), a refund back to the account adds a credit, and a "Payment on account"
/// line takes the amount off. Each line also goes in the outbox for the owner's other stores.
/// </summary>
internal sealed class AccountLedger
{
    private readonly NepPosDbContext _db;

    public AccountLedger(NepPosDbContext db) => _db = db;

    public async Task ApplyAsync(SaleTransaction sale, CancellationToken cancellationToken)
    {
        var accountPayments = sale.Payments.Where(p => p.TenderType == TenderType.Account).ToList();
        var paidOff = sale.LineItems.Where(AccountPayment.IsAccountPayment).ToList();
        if (accountPayments.Count == 0 && paidOff.Count == 0)
            return;

        if (sale.CustomerId is not { } customerId)
            throw new InvalidOperationException("Choose the customer before charging or paying an account.");
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new InvalidOperationException("That customer isn't on this register any more.");

        var now = DateTime.UtcNow;
        var where = $"{sale.StoreId} / {sale.RegisterId}";

        foreach (var payment in accountPayments)
        {
            if (!payment.IsRefund && (!customer.IsAccount || !customer.IsActive))
                throw new InvalidOperationException($"{customer.Name} doesn't have an account. Turn it on in Manage → Customers first.");
            if (!string.Equals(payment.Reference, customer.Code, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The account on the payment isn't the customer on the sale.");

            Add(customer, new CustomerAccountEntry
            {
                Kind = payment.IsRefund ? AccountEntryKind.Credit : AccountEntryKind.Charge,
                Amount = payment.Amount,
                DueUtc = payment.IsRefund ? null : now.AddDays(Math.Max(0, customer.TermsDays)),
                Note = payment.IsRefund ? "Refund to account" : "Sale on account"
            });
        }

        foreach (var line in paidOff)
        {
            Add(customer, new CustomerAccountEntry
            {
                Kind = AccountEntryKind.Payment,
                Amount = -line.LineTotalIncGst,
                Note = AccountPayment.Name
            });
        }

        void Add(Customer target, CustomerAccountEntry entry)
        {
            entry.Id = Guid.NewGuid();
            entry.CustomerId = target.Id;
            entry.SaleId = sale.Id;
            entry.InvoiceNumber = sale.InvoiceNumber;
            entry.AtUtc = now;
            entry.Where = where;
            _db.CustomerAccountEntries.Add(entry);
            target.Balance += entry.Amount;
            _db.OutboxMessages.Add(OutboxMessage.AccountEntry(entry, sale.StoreId, sale.RegisterId));
        }
    }
}
