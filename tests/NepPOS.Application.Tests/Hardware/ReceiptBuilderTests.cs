using NepPOS.Application.Hardware;
using NepPOS.Application.Payments;
using NepPOS.Domain.Entities;
using Xunit;

namespace NepPOS.Application.Tests.Hardware;

public class ReceiptBuilderTests
{
    private static readonly StoreProfile Store = new()
    {
        BusinessName = "Nep Liquor",
        Abn = "12345678901",
        AddressLines = new[] { "1 George St", "Sydney NSW 2000" },
        Phone = "02 9000 0000",
        Footer = "Thank you - please drink responsibly"
    };

    /// <summary>A completed sale: Corona $19.50 + 2 x Heineken $18.99 = $57.48, paid $60 cash.</summary>
    private static SaleTransaction CompletedCashSale()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", 1, 19.50m, 0.10m));
        sale.AddLineItem(new SaleLineItem("101", "Heineken 6-Pack", 2, 18.99m, 0.10m));

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(60.00m);

        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(42);
        return sale;
    }

    private static List<string> Text(ReceiptDocument receipt) => receipt.Lines.Select(l => l.Text).ToList();

    [Fact]
    public void Build_HasEveryAtoTaxInvoiceElement()
    {
        var text = Text(ReceiptBuilder.Build(CompletedCashSale(), Store, 48));

        Assert.Contains("TAX INVOICE", text);                        // intended to be a tax invoice
        Assert.Contains("Nep Liquor", text);                         // seller identity
        Assert.Contains("ABN 12 345 678 901", text);                 // seller ABN, formatted
        Assert.Contains(text, l => l.StartsWith("Invoice #42"));     // invoice number + date line
        Assert.Contains(text, l => l.StartsWith("Corona Extra 6-Pack") && l.EndsWith("$19.50"));
        Assert.Contains(text, l => l.Contains("2 x $18.99") && l.EndsWith("$37.98"));
        Assert.Contains(text, l => l.StartsWith("GST included") && l.EndsWith("$5.23"));
        Assert.Contains("Total price includes GST", text);
    }

    [Fact]
    public void Build_ShowsTotalCashChangeAndRounding()
    {
        var text = Text(ReceiptBuilder.Build(CompletedCashSale(), Store, 48));

        // $57.48 rounds to $57.50 in cash: rounding +0.02, change $2.50 from $60.
        Assert.Contains(text, l => l.StartsWith("TOTAL") && l.EndsWith("$57.48"));
        Assert.Contains(text, l => l.StartsWith("Cash rounding") && l.EndsWith("+0.02"));
        Assert.Contains(text, l => l.StartsWith("Cash") && l.EndsWith("$60.00"));
        Assert.Contains(text, l => l.StartsWith("Change") && l.EndsWith("$2.50"));
    }

    [Fact]
    public void Build_ShowsCardSchemeAndLastFourOnly()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", 1, 19.50m, 0.10m));

        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddEftpos(19.50m, new PaymentResult(true, "000", "APPROVED", "851898", "Mastercard", "............1025"));
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(7);

        var text = Text(ReceiptBuilder.Build(sale, Store, 48));

        Assert.Contains(text, l => l.StartsWith("EFTPOS Mastercard ...1025") && l.EndsWith("$19.50"));
        Assert.DoesNotContain(text, l => l.Contains("............")); // never the long masked number
    }

    [Theory]
    [InlineData(48)] // 80 mm paper
    [InlineData(32)] // 58 mm paper
    public void Build_NoLineIsWiderThanThePaper(int width)
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("999", "Penfolds Bin 389 Cabernet Shiraz 750ml Gift Box Limited Edition", 1, 120.00m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(120.00m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(1);

        var receipt = ReceiptBuilder.Build(sale, Store, width);

        Assert.All(receipt.Lines, l => Assert.True(l.Text.Length <= width, $"'{l.Text}' is {l.Text.Length} chars"));
        Assert.Contains(receipt.Lines, l => l.Text.EndsWith("$120.00")); // long name wrapped, price kept
    }

    [Fact]
    public void Build_MarksGstFreeItems()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("201", "Bread Loaf", 1, 4.00m, 0m));
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", 1, 19.50m, 0.10m));
        var tender = new TenderSession(sale.Id, sale.GrandTotal);
        tender.AddCash(23.50m);
        sale.RecordPayments(tender.Payments, tender.RoundingAdjustment);
        sale.Complete(3);

        var text = Text(ReceiptBuilder.Build(sale, Store, 48));

        Assert.Contains(text, l => l.StartsWith("*Bread Loaf"));
        Assert.Contains("* GST-free item", text);
    }

    [Fact]
    public void Build_Reprint_IsLabelled()
    {
        var text = Text(ReceiptBuilder.Build(CompletedCashSale(), Store, 48, reprint: true));

        Assert.Contains("TAX INVOICE (REPRINT)", text);
    }

    [Fact]
    public void Build_RejectsSaleThatIsNotCompleted()
    {
        var sale = new SaleTransaction("STORE-001", "REG-001");
        sale.AddLineItem(new SaleLineItem("102", "Corona Extra 6-Pack", 1, 19.50m, 0.10m));

        Assert.Throws<InvalidOperationException>(() => ReceiptBuilder.Build(sale, Store, 48));
    }

    [Theory]
    [InlineData("12345678901", "12 345 678 901")]
    [InlineData("12 345 678 901", "12 345 678 901")]
    [InlineData("123", "123")]
    public void FormatAbn_GroupsElevenDigits(string input, string expected)
    {
        Assert.Equal(expected, ReceiptBuilder.FormatAbn(input));
    }

    // Shape of a real Spice/Gecko slip: CRLF lines, ~20 chars wide, blank lines around it.
    private const string GeckoSlip =
        "\r\n     Gecko Bank\r\n   Switchlink POS\r\n\r\nAUTH          851898\r\nCARD      ......1025\r\n\r\n" +
        "PURCHASE      $19.50\r\n\r\n      APPROVED\r\n  *CUSTOMER COPY*\r\n";

    [Fact]
    public void BuildCardSlip_CentresTheTerminalLayoutAndKeepsInnerBlankLines()
    {
        var slip = ReceiptBuilder.BuildCardSlip(GeckoSlip, 48);
        var text = slip.Lines.Select(l => l.Text).ToList();

        // 20-char slip on 48-char paper -> indented 14 spaces; terminal spacing kept as-is.
        Assert.Equal(new string(' ', 14) + "     Gecko Bank", text[0]);
        Assert.Contains(new string(' ', 14) + "AUTH          851898", text);
        Assert.Equal(new string(' ', 14) + "  *CUSTOMER COPY*", text[^1]); // trailing blank lines dropped
        Assert.Contains("", text.Select(t => t.Trim()));                    // inner blank lines kept
    }

    [Fact]
    public void BuildCardSlip_NeverExceedsThePaperWidth()
    {
        var slip = ReceiptBuilder.BuildCardSlip(GeckoSlip + new string('X', 60), 32);

        Assert.All(slip.Lines, l => Assert.True(l.Text.Length <= 32));
    }

    [Fact]
    public void BuildCardSlip_EmptyText_GivesAnEmptySlip()
    {
        Assert.Empty(ReceiptBuilder.BuildCardSlip("\r\n  \r\n", 48).Lines);
    }
}
