using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Reports;

public class EndOfDayCalculatorTests
{
    private static long _invoice;

    /// <summary>A completed sale paid the way a cashier would, through TenderSession.</summary>
    private static SaleTransaction Sale(string employee, decimal unitPrice, int quantity, Action<TenderSession> pay)
    {
        var sale = new SaleTransaction("STORE-001", "REG-001", 1, employee);
        sale.AddLineItem(new SaleLineItem("101", "Test item", quantity, unitPrice, 0.10m));

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        pay(tender);

        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(Interlocked.Increment(ref _invoice));
        return sale;
    }

    private static PaymentResult Approved => new(true, "00", "APPROVED", "123456", "Visa", "....1234");

    private static JournalEntry Entry(JournalEventType type, decimal? amount = null) =>
        new(1, "STORE-001", "REG-001", 1, "Owner", type, type.ToString(), amount: amount);

    [Fact]
    public void Calculate_TotalsSalesGstTendersAndCashInDrawer()
    {
        var cash = Sale("Owner", 10.02m, 1, t => t.AddCash(20.00m));          // rounds to $10.00, change $10.00
        var card = Sale("Cashier", 5.50m, 2, t => t.AddEftpos(11.00m, Approved));
        var split = Sale("Owner", 30.00m, 1, t =>
        {
            t.AddEftpos(20.00m, Approved);
            t.AddCash(10.00m);
        });

        var report = EndOfDayCalculator.Calculate(new[] { cash, card, split }, Array.Empty<JournalEntry>());

        Assert.Equal(3, report.SaleCount);
        Assert.Equal(4, report.ItemCount);
        Assert.Equal(51.02m, report.GrossSales);
        Assert.Equal(-0.02m, report.CashRounding);
        Assert.Equal(51.00m, report.TotalTaken);                        // gross + rounding
        Assert.Equal(report.GrossSales + report.CashRounding, report.TotalTaken);

        var cashTender = report.Tenders.Single(t => t.Tender == TenderType.Cash);
        var cardTender = report.Tenders.Single(t => t.Tender == TenderType.Eftpos);
        Assert.Equal((2, 20.00m), (cashTender.Count, cashTender.Amount));   // kept cash, not the $20 note
        Assert.Equal((2, 31.00m), (cardTender.Count, cardTender.Amount));
        Assert.Equal(20.00m, report.CashExpected);

        Assert.Equal(report.GrossSales - report.Gst, report.NetSales);
        Assert.Equal(cash.InvoiceNumber, report.FirstInvoice);
        Assert.Equal(split.InvoiceNumber, report.LastInvoice);

        var owner = report.ByEmployee.Single(e => e.Employee == "Owner");
        Assert.Equal((2, 40.02m), (owner.SaleCount, owner.Amount));
    }

    [Fact]
    public void Calculate_CountsActivityFromTheJournal()
    {
        var journal = new[]
        {
            Entry(JournalEventType.SaleVoided, 38.49m),
            Entry(JournalEventType.SaleVoided, 10.00m),
            Entry(JournalEventType.LineRemoved, 4.50m),
            Entry(JournalEventType.NoSale),
            Entry(JournalEventType.CardDeclined, 20.00m),
            Entry(JournalEventType.SignIn)
        };

        var report = EndOfDayCalculator.Calculate(Array.Empty<SaleTransaction>(), journal);

        Assert.Equal((2, 48.49m), (report.VoidCount, report.VoidAmount));
        Assert.Equal((1, 4.50m), (report.LinesRemoved, report.LinesRemovedAmount));
        Assert.Equal(1, report.NoSales);
        Assert.Equal(1, report.CardDeclines);
        Assert.Equal(0, report.SaleCount);
        Assert.Null(report.FirstInvoice);
        Assert.All(report.Tenders, t => Assert.Equal(0m, t.Amount));   // Cash and EFTPOS still listed at $0.00
        Assert.Equal(2, report.Tenders.Count);
    }

    [Fact]
    public void Calculate_IgnoresSalesThatWereNotCompleted()
    {
        var unfinished = new SaleTransaction("STORE-001", "REG-001");
        unfinished.AddLineItem(new SaleLineItem("101", "Test item", 1, 9.99m, 0.10m));

        var report = EndOfDayCalculator.Calculate(new[] { unfinished }, Array.Empty<JournalEntry>());

        Assert.Equal(0, report.SaleCount);
        Assert.Equal(0m, report.GrossSales);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    public void BuildEndOfDay_FitsThePaperAndShowsTheKeyTotals(int width)
    {
        var sale = Sale("A cashier with a very long name indeed", 12.34m, 1, t => t.AddEftpos(12.34m, Approved));
        var report = EndOfDayCalculator.Calculate(new[] { sale }, Array.Empty<JournalEntry>()) with
        {
            IsZ = true,
            ZNumber = 7,
            RegisterId = "REG-001",
            PrintedBy = "Owner",
            PeriodStartUtc = DateTime.UtcNow.AddHours(-8),
            PeriodEndUtc = DateTime.UtcNow
        };

        var doc = ReceiptBuilder.BuildEndOfDay(report, new StoreProfile { BusinessName = "Nep Liquor" }, width);
        var text = doc.Lines.Select(l => l.Text).ToList();

        Assert.All(text, line => Assert.True(line.Length <= width, $"Too wide: '{line}'"));
        Assert.Contains("Z REPORT #7", text);
        Assert.Contains(text, l => l.StartsWith("TOTAL TAKEN") && l.EndsWith("$12.34"));
        Assert.Contains(text, l => l.StartsWith("CASH IN DRAWER") && l.EndsWith("$0.00"));
        Assert.Contains(text, l => l.StartsWith("EFTPOS (1)") && l.EndsWith("$12.34"));
    }
}
