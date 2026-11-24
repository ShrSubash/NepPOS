using NepPOS.Contracts.Accounts;
using NepPOS.Application.Customers;
using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Application.Suppliers;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Accounts;

public class AccountAgeingTests
{
    private static readonly DateTime Today = new(2026, 11, 24, 15, 0, 0);

    private static AccountLine Charge(decimal amount, int daysAgo, int terms) =>
        new(amount, Today.AddDays(-daysAgo), Today.AddDays(-daysAgo + terms));

    private static AccountLine Paid(decimal amount, int daysAgo) => new(-amount, Today.AddDays(-daysAgo), null);

    [Fact]
    public void Nothing_IsZero()
    {
        Assert.Equal(Ageing.Zero, AccountAgeing.Calculate(Array.Empty<AccountLine>(), Today));
    }

    [Fact]
    public void Charges_AreBucketedByDaysPastDue()
    {
        var ageing = AccountAgeing.Calculate(new[]
        {
            Charge(100, 5, 30),    // due in 25 days
            Charge(200, 40, 30),   // 10 days overdue
            Charge(300, 75, 30),   // 45 days overdue
            Charge(400, 100, 30),  // 70 days overdue
            Charge(500, 200, 30)   // 170 days overdue
        }, Today);

        Assert.Equal(new Ageing(100, 200, 300, 400, 500), ageing);
        Assert.Equal(1500, ageing.Total);
        Assert.Equal(1400, ageing.Overdue);
    }

    [Fact]
    public void DueToday_IsNotOverdue()
    {
        Assert.Equal(0, AccountAgeing.Calculate(new[] { Charge(50, 30, 30) }, Today).Overdue);
    }

    [Fact]
    public void Payments_PayOffTheOldestChargesFirst()
    {
        var ageing = AccountAgeing.Calculate(new[]
        {
            Charge(400, 100, 30),
            Charge(200, 40, 30),
            Charge(100, 5, 30),
            Paid(450, 3)          // clears the $400 and $50 of the $200
        }, Today);

        Assert.Equal(new Ageing(100, 150, 0, 0, 0), ageing);
    }

    [Fact]
    public void Overpaid_ShowsAsCredit()
    {
        var ageing = AccountAgeing.Calculate(new[] { Charge(100, 40, 30), Paid(130, 1) }, Today);

        Assert.Equal(-30, ageing.Current);
        Assert.Equal(0, ageing.Overdue);
        Assert.Equal(-30, ageing.Total);
    }

    [Fact]
    public void ChargeWithoutDueDate_IsDueTheSameDay()
    {
        var ageing = AccountAgeing.Calculate(new[] { new AccountLine(80, Today.AddDays(-10), null) }, Today);

        Assert.Equal(80, ageing.Days1To30);
    }

    [Fact]
    public void Add_SumsEachBucket()
    {
        Assert.Equal(new Ageing(2, 4, 6, 8, 10), new Ageing(1, 2, 3, 4, 5).Add(new Ageing(1, 2, 3, 4, 5)));
    }

    [Fact]
    public void SupplierAgeing_UsesBillDueDates()
    {
        var today = DateOnly.FromDateTime(Today);
        var ageing = SupplierAgeing.For(new[]
        {
            new SupplierAccountEntry { Kind = SupplierEntryKind.Bill, Amount = 1000, Date = today.AddDays(-45), DueDate = today.AddDays(-15) },
            new SupplierAccountEntry { Kind = SupplierEntryKind.Bill, Amount = 500, Date = today.AddDays(-5), DueDate = today.AddDays(25) },
            new SupplierAccountEntry { Kind = SupplierEntryKind.Payment, Amount = -600, Date = today.AddDays(-2) }
        }, Today);

        Assert.Equal(new Ageing(500, 400, 0, 0, 0), ageing);
    }
}

public class CustomerAccountTests
{
    [Theory]
    [InlineData(0, 100, 5000, false)]      // no limit
    [InlineData(500, 400, 100, false)]     // exactly at the limit
    [InlineData(500, 400, 100.01, true)]
    [InlineData(500, 0, 600, true)]
    public void WouldExceedLimit(decimal limit, decimal balance, decimal extra, bool expected)
    {
        var customer = new Customer { CreditLimit = limit, Balance = balance };

        Assert.Equal(expected, customer.WouldExceedLimit(extra));
    }

    [Theory]
    [InlineData("CUST-12345678", true)]
    [InlineData(" cust-1234 ", true)]
    [InlineData("CUST-123", false)]
    [InlineData("GC-12345678", false)]
    [InlineData("12345678", false)]
    public void CustomerCode_Looks(string code, bool expected)
    {
        Assert.Equal(expected, CustomerCode.Looks(code));
    }

    [Fact]
    public void SetCustomer_PutsThemOnTheSale_AndTakesThemOff()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        var id = Guid.NewGuid();

        sale.SetCustomer(id, "  Corner Café ");
        Assert.Equal((id, "Corner Café"), (sale.CustomerId!.Value, sale.CustomerName));

        sale.SetCustomer(null, "ignored");
        Assert.Null(sale.CustomerId);
        Assert.Null(sale.CustomerName);
    }

    [Fact]
    public void SetCustomer_AfterCompletion_Throws()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("1", "Milk", 1, 2m, 0m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(2m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(1);

        Assert.Throws<InvalidOperationException>(() => sale.SetCustomer(Guid.NewGuid(), "Late"));
    }

    [Fact]
    public void TenderSession_AddAccount_ChargesTheRest()
    {
        var tender = new TenderSession(Guid.NewGuid(), 80m);
        tender.AddCash(20m);

        var taken = tender.AddAccount("cust-12345678", "Corner Café", 0m);

        Assert.Equal(60m, taken);
        Assert.True(tender.IsComplete);
        var account = tender.Payments.Single(p => p.TenderType == TenderType.Account);
        Assert.Equal(("CUST-12345678", "Corner Café"), (account.Reference, account.TenderName));
    }

    [Fact]
    public void TenderSession_AddAccount_Twice_Throws()
    {
        var tender = new TenderSession(Guid.NewGuid(), 80m);
        tender.AddAccount("CUST-12345678", "Corner Café", 30m);

        Assert.Throws<InvalidOperationException>(() => tender.AddAccount("CUST-12345678", "Corner Café", 10m));
    }

    [Fact]
    public void RefundSession_RefundToAccount_IsANegativeAccountPayment()
    {
        var refund = new RefundSession(Guid.NewGuid(), -45m);

        refund.RefundToAccount("CUST-12345678", "Corner Café", 45m);

        var payment = Assert.Single(refund.Payments);
        Assert.Equal((TenderType.Account, -45m, true), (payment.TenderType, payment.Amount, payment.IsRefund));
        Assert.True(refund.IsComplete);
    }
}

public class AccountReceiptTests
{
    private static readonly StoreProfile Store = new() { BusinessName = "Nep Grocery", Phone = "02 9000 0000" };

    [Fact]
    public void SaleOnAccount_ShowsCustomerAndASignatureLine()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("1", "Milk 2L", 10, 3.50m, 0m));
        sale.SetCustomer(Guid.NewGuid(), "Corner Café");
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddAccount("CUST-12345678", "Corner Café", 0m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(7);

        var text = ReceiptBuilder.Build(sale, Store, 42).Lines.Select(l => l.Text).ToList();

        Assert.Contains("Customer: Corner Café", text);
        Assert.Contains(text, l => l.StartsWith("On account CUST-12345678") && l.EndsWith("$35.00"));
        Assert.Contains(text, l => l.StartsWith("Signature ___"));
    }

    [Fact]
    public void PaymentOnAccount_IsNotMarkedGstFree()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem(AccountPayment.Sku, "Payment on account (Corner Café)", 1, 200m, 0m, AccountPayment.Category));
        sale.SetCustomer(Guid.NewGuid(), "Corner Café");
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(200m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(8);

        var text = ReceiptBuilder.Build(sale, Store, 42).Lines.Select(l => l.Text).ToList();

        Assert.Contains(text, l => l.StartsWith("Payment on account (Corner Café)"));
        Assert.DoesNotContain("* GST-free item", text);
        Assert.DoesNotContain(text, l => l.StartsWith("Signature"));
    }

    [Fact]
    public void Statement_HasRunningBalanceAgeingAndAmountDue()
    {
        var customer = new Customer { Id = Guid.NewGuid(), Code = "CUST-12345678", Name = "Corner Café", TermsDays = 14, IsAccount = true };
        var now = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Local);
        var entries = new List<CustomerAccountEntry>
        {
            new() { Kind = AccountEntryKind.Charge, Amount = 300m, InvoiceNumber = 12, AtUtc = now.AddDays(-40).ToUniversalTime(), DueUtc = now.AddDays(-26).ToUniversalTime() },
            new() { Kind = AccountEntryKind.Payment, Amount = -100m, AtUtc = now.AddDays(-20).ToUniversalTime() },
            new() { Kind = AccountEntryKind.Charge, Amount = 50m, InvoiceNumber = 40, AtUtc = now.AddDays(-2).ToUniversalTime(), DueUtc = now.AddDays(12).ToUniversalTime() },
        };
        var statement = new CustomerStatement(customer, entries, CustomerAgeing.For(entries, now));

        var receipt = ReceiptBuilder.BuildStatement(statement, Store, 42, now);
        var text = receipt.Lines.Select(l => l.Text).ToList();

        Assert.Contains("ACCOUNT STATEMENT", text);
        Assert.Contains(text, l => l.Contains("Invoice #12") && l.EndsWith("$300.00"));
        Assert.Contains(text, l => l.EndsWith("bal $250.00"));
        Assert.Contains(text, l => l.StartsWith("1-30 days overdue") && l.EndsWith("$200.00"));
        Assert.Contains(text, l => l.StartsWith("AMOUNT DUE") && l.EndsWith("$250.00"));
        Assert.Contains(text, l => l.StartsWith("Overdue now") && l.EndsWith("$200.00"));
        Assert.Contains(receipt.Lines, l => l.Barcode && l.Text == "CUST-12345678");
    }

    [Fact]
    public void Statement_OldLinesAreBroughtForward()
    {
        var customer = new Customer { Id = Guid.NewGuid(), Code = "CUST-12345678", Name = "Club", IsAccount = true };
        var now = DateTime.Now;
        var entries = Enumerable.Range(1, 5)
            .Select(i => new CustomerAccountEntry { Kind = AccountEntryKind.Charge, Amount = 10m, AtUtc = now.AddDays(-10 + i).ToUniversalTime(), DueUtc = now.AddDays(30).ToUniversalTime() })
            .ToList();
        var statement = new CustomerStatement(customer, entries, CustomerAgeing.For(entries, now));

        var text = ReceiptBuilder.BuildStatement(statement, Store, 42, now, maxLines: 2).Lines.Select(l => l.Text).ToList();

        Assert.Contains(text, l => l.StartsWith("Brought forward") && l.EndsWith("$30.00"));
        Assert.Contains(text, l => l.EndsWith("bal $50.00"));
    }
}
