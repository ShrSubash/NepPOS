using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Application.Reports;
using NepPOS.Domain.Entities;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Hardware;

/// <summary>Receipts and end-of-day figures for refunds, exchanges, gift cards and named tenders.</summary>
public class ReceiptReturnsTests
{
    private static readonly StoreProfile Store = new() { BusinessName = "Nep Liquor", Abn = "12345678901" };

    /// <summary>Returns one Corona ($19.50, faulty) from invoice 19; refunded as store credit.</summary>
    private static SaleTransaction Refund()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", -1, 19.50m, 0.10m,
            returnOfLineId: Guid.NewGuid(), returnReason: "Faulty", restock: false));
        sale.MarkAsReturnOf(Guid.NewGuid(), 19);

        var refund = new RefundSession(sale.Id, sale.GrandTotal);
        refund.RefundToVoucher(TenderType.StoreCredit, "SC-654321", 19.50m);

        sale.RecordPayments(refund.Payments, refund.RoundingAdjustment);
        sale.Complete(20);
        return sale;
    }

    private static List<string> Text(ReceiptDocument receipt) => receipt.Lines.Select(l => l.Text).ToList();

    [Fact]
    public void Refund_IsAnAdjustmentNoteWithReasonAndOriginalInvoice()
    {
        var text = Text(ReceiptBuilder.Build(Refund(), Store, 48));

        Assert.Contains("REFUND - ADJUSTMENT NOTE", text);
        Assert.Contains("Return of invoice #19", text);
        Assert.Contains(text, l => l.StartsWith("RETURN Corona Extra 6-Pack"));
        Assert.Contains("  Reason: Faulty", text);
        Assert.Contains(text, l => l.StartsWith("TOTAL REFUND") && l.EndsWith("$19.50"));
        Assert.Contains(text, l => l.StartsWith("GST refunded") && l.EndsWith("$1.77"));
        Assert.Contains(text, l => l.StartsWith("Store credit issued SC-654321") && l.EndsWith("$19.50"));
    }

    [Fact]
    public void Refund_PrintsTheStoreCreditCodeAsABarcode()
    {
        var receipt = ReceiptBuilder.Build(Refund(), Store, 48);

        Assert.Contains(receipt.Lines, l => l.Barcode && l.Text == "SC-654321");
    }

    [Fact]
    public void EveryReceipt_EndsWithItsOwnBarcodeAndTheNote()
    {
        var receipt = ReceiptBuilder.Build(Refund(), Store, 48, receiptNote: "Keep this receipt for returns.");

        var barcode = receipt.Lines.Last(l => l.Barcode);
        Assert.Equal("RT001-20", barcode.Text);
        Assert.Equal("Keep this receipt for returns.", receipt.Lines[^1].Text);
    }

    [Fact]
    public void Exchange_PaidForStaysATaxInvoice()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", -1, 19.50m, 0.10m, returnOfLineId: Guid.NewGuid(),
            returnReason: "Change of mind"));
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 21.00m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(1.50m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(21);

        var text = Text(ReceiptBuilder.Build(sale, Store, 48));

        Assert.Contains("TAX INVOICE - EXCHANGE", text);
        Assert.Contains(text, l => l.StartsWith("TOTAL") && l.EndsWith("$1.50"));
    }

    [Fact]
    public void GiftCardSold_PrintsItsCodeAndOtherTenderName()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("GIFTCARD", "Gift card", 1, 50m, 0m, giftCardCode: "GC-123456"));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddOther("Account", 50m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(22);

        var receipt = ReceiptBuilder.Build(sale, Store, 48);

        Assert.Contains(receipt.Lines, l => l.Barcode && l.Text == "GC-123456");
        Assert.Contains(receipt.Lines, l => l.Text.StartsWith("Account") && l.Text.EndsWith("$50.00"));
        Assert.DoesNotContain(receipt.Lines, l => l.Text.StartsWith("*Gift card"));   // not flagged GST-free
    }

    [Fact]
    public void EndOfDay_CountsRefundsAndNamesOtherTenders()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 1, 40m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddOther("Account", 25m);
        tender.AddCash(15m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(30);

        var cashBack = new SaleTransaction("STORE-001", "REG-001");
        cashBack.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", -1, 10m, 0.10m, returnOfLineId: Guid.NewGuid()));
        var refund = new RefundSession(cashBack.Id, cashBack.GrandTotal);
        refund.RefundCash();
        cashBack.RecordPayments(refund.Payments, refund.RoundingAdjustment);
        cashBack.Complete(31);

        var report = EndOfDayCalculator.Calculate(new[] { sale, cashBack }, Array.Empty<JournalEntry>());

        Assert.Equal((1, 10m), (report.RefundCount, report.RefundAmount));
        Assert.Equal(30m, report.GrossSales);
        Assert.Equal(5m, report.Tenders.Single(t => t.Tender == TenderType.Cash).Amount);   // $15 in, $10 back
        var account = report.Tenders.Single(t => t.Tender == TenderType.Other);
        Assert.Equal(("Account", 25m), (account.Label, account.Amount));
    }
}
