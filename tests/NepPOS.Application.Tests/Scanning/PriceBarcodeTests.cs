using NepPOS.Application.Email;
using NepPOS.Application.Hardware;
using NepPOS.Application.Scanning;
using Xunit;

namespace NepPOS.Application.Tests.Scanning;

public class PriceBarcodeTests
{
    private static readonly PriceBarcodeSettings On = new() { Enabled = true };

    [Fact]
    public void Reads_the_item_code_and_price_from_a_deli_label()
    {
        Assert.True(PriceBarcodes.TryRead("2100123004959", On, out var item, out var price));
        Assert.Equal(("123", 4.95m), (item, price));
    }

    [Fact]
    public void Reads_larger_prices()
    {
        Assert.True(PriceBarcodes.TryRead("2900007012502", On, out var item, out var price));
        Assert.Equal(("7", 12.50m), (item, price));
    }

    [Theory]
    [InlineData("2100123004958")]   // wrong check digit
    [InlineData("9312345678907")]   // a normal product barcode
    [InlineData("210012300495")]    // 12 digits
    public void Anything_else_is_not_a_price_label(string code) =>
        Assert.False(PriceBarcodes.TryRead(code, On, out _, out _));

    [Fact]
    public void Switched_off_or_outside_the_prefixes_is_ignored()
    {
        Assert.False(PriceBarcodes.TryRead("2100123004959", new PriceBarcodeSettings(), out _, out _));
        Assert.False(PriceBarcodes.TryRead("2100123004959", On with { FirstPrefix = "22", LastPrefix = "29" }, out _, out _));
    }

    [Fact]
    public void Item_digits_change_where_the_price_starts()
    {
        // 21 0012 300495 9: 4-digit item code 12, 6-digit price $3,004.95
        Assert.True(PriceBarcodes.TryRead("2100123004959", On with { ItemDigits = 4 }, out var item, out var price));
        Assert.Equal(("12", 3004.95m), (item, price));
    }

    [Fact]
    public void Receipt_email_is_plain_text_with_the_receipt_number_instead_of_a_barcode()
    {
        var receipt = new ReceiptDocument(32, new[]
        {
            new ReceiptLine("Hamro Grocery", ReceiptAlign.Center, Bold: true),
            new ReceiptLine("Ghee 1L                    $9.00"),
            new ReceiptLine("RT001-19", Barcode: true),
        });

        var email = ReceiptEmail.Build(receipt, "Hamro Grocery", 19, " sita@example.com ");

        Assert.Equal(new[] { "sita@example.com" }, email.To);
        Assert.Equal("Your Hamro Grocery receipt #19", email.Subject);
        Assert.Contains("Ghee 1L", email.Body);
        Assert.Contains("Receipt RT001-19", email.Body);
        Assert.Empty(email.Attachments);
    }

    [Fact]
    public void Receipts_need_a_server_and_a_from_address_but_no_z_recipients()
    {
        Assert.False(ReceiptEmail.CanSend(new EmailSettings()));
        Assert.True(ReceiptEmail.CanSend(new EmailSettings { SmtpHost = "smtp.example.com", FromAddress = "shop@example.com" }));
        Assert.NotNull(ReceiptEmail.AddressProblem("not-an-email"));
        Assert.Null(ReceiptEmail.AddressProblem("sita@example.com"));
    }

    [Fact]
    public void Supplier_returns_keep_the_supplier_in_the_reason() =>
        Assert.Equal("Supplier return: Metcash", NepPOS.Application.Stock.Wastage.SupplierReturnReason(" Metcash "));
}
