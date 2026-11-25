using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Hardware;

/// <summary>
/// Spice/SCI returns a customer and a merchant slip for every card transaction. The register keeps both with the payment
/// and prints the customer copy only as the store chose (Ask / Print / Don't print), never twice.
/// </summary>
public class CardSlipsTests
{
    private const string Customer = "CUSTOMER COPY\nAPPROVED";
    private const string Merchant = "MERCHANT COPY\nAPPROVED";

    private static PrinterSettings Printer(CardSlipMode mode) =>
        new() { Mode = PrinterMode.Thermal, PrinterName = "TM-T82", CustomerCardSlip = mode };

    private static TenderSession CardSale(bool printedByTerminal = false)
    {
        var tender = new TenderSession(Guid.NewGuid(), 20m);
        tender.AddEftpos(20m, new PaymentResult(true, "00", "APPROVED", "123456", "Visa", "....4242",
            CustomerReceipt: Customer, MerchantReceipt: Merchant, CustomerReceiptPrinted: printedByTerminal));
        return tender;
    }

    [Fact]
    public void CardPayment_KeepsBothSlips()
    {
        var payment = Assert.Single(CardSale().Payments);

        Assert.Equal(Customer, payment.CustomerReceipt);
        Assert.Equal(Merchant, payment.MerchantReceipt);
        Assert.False(payment.CustomerReceiptPrintedByTerminal);
    }

    [Fact]
    public void Ask_IsTheDefault_AndPrintsNothingByItself()
    {
        Assert.Equal(CardSlipMode.Ask, new PrinterSettings().CustomerCardSlip);

        var sale = CardSale();
        Assert.Empty(CardSlips.CustomerCopiesToPrintNow(sale.Payments, Printer(CardSlipMode.Ask)));
        Assert.Equal(new[] { Customer }, CardSlips.CustomerCopies(sale.Payments)); // still there for the button
    }

    [Fact]
    public void Print_PrintsTheCustomerCopyStraightAway()
    {
        Assert.Equal(new[] { Customer }, CardSlips.CustomerCopiesToPrintNow(CardSale().Payments, Printer(CardSlipMode.Print)));
    }

    [Fact]
    public void DontPrint_KeepsTheSlipForTheJournalOnly()
    {
        var sale = CardSale();

        Assert.Empty(CardSlips.CustomerCopiesToPrintNow(sale.Payments, Printer(CardSlipMode.DontPrint)));
        Assert.Equal(new[] { Customer }, CardSlips.CustomerCopies(sale.Payments));
        Assert.Equal(new[] { Merchant }, CardSlips.MerchantCopies(sale.Payments));
    }

    [Fact]
    public void Print_SkipsASlipTheTerminalAlreadyPrinted()
    {
        var sale = CardSale(printedByTerminal: true);

        Assert.Empty(CardSlips.CustomerCopiesToPrintNow(sale.Payments, Printer(CardSlipMode.Print)));
        Assert.Equal(new[] { Customer }, CardSlips.CustomerCopies(sale.Payments)); // reprint on request still works
    }

    [Fact]
    public void NoPrinter_NeverPrints()
    {
        var printer = Printer(CardSlipMode.Print) with { Mode = PrinterMode.None };

        Assert.Empty(CardSlips.CustomerCopiesToPrintNow(CardSale().Payments, printer));
    }

    [Fact]
    public void CashPayment_HasNoSlips()
    {
        var tender = new TenderSession(Guid.NewGuid(), 10m);
        tender.AddCash(10m);

        Assert.Empty(CardSlips.CustomerCopies(tender.Payments));
        Assert.Empty(CardSlips.CustomerCopiesToPrintNow(tender.Payments, Printer(CardSlipMode.Print)));
    }

    [Fact]
    public void CardRefund_KeepsTheTerminalSlips()
    {
        var refund = new RefundSession(Guid.NewGuid(), -20m);

        refund.RefundCard(20m, "654321", new PaymentResult(true, "00", "APPROVED", "654321", "Visa", "....4242",
            CustomerReceipt: Customer, MerchantReceipt: Merchant));

        var payment = Assert.Single(refund.Payments);
        Assert.Equal(-20m, payment.Amount);
        Assert.Equal(Merchant, payment.MerchantReceipt);
        Assert.Equal(new[] { Customer }, CardSlips.CustomerCopiesToPrintNow(refund.Payments, Printer(CardSlipMode.Print)));
    }

    [Fact]
    public void ManualCardRefund_HasNoSlips()
    {
        var refund = new RefundSession(Guid.NewGuid(), -5m);

        refund.RefundCard(5m, "111111");

        Assert.Empty(CardSlips.CustomerCopies(refund.Payments));
    }
}
