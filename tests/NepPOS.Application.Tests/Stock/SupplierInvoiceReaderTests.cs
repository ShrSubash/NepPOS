using System.Text;
using NepPOS.Application.Stock;
using Xunit;

namespace NepPOS.Application.Tests.Stock;

public class SupplierInvoiceReaderTests
{
    /// <summary>An ALM (Metcash v8) e-invoice cut down to four lines; the shop's details are made up.</summary>
    internal const string AlmInvoice = """
        <?xml version="1.0" encoding="UTF-8"?><ns0:InvoiceBatch xmlns:ns0="http://metcash.com/invoices/8.0.0">
        <ns0:TransmissionHeader><ns0:TransmissionType>LIQUOR INVOICING</ns0:TransmissionType><ns0:Version>8.1.0</ns0:Version></ns0:TransmissionHeader>
        <ns0:Invoice InvoiceNumber="SI00285912"><ns0:Header><ns0:CustomerNumber>11112222</ns0:CustomerNumber><ns0:CustomerName>TEST BOTTLE SHOP</ns0:CustomerName>
        <ns0:OrderNumber>0000000003</ns0:OrderNumber><ns0:InvoiceDate>2026-08-17</ns0:InvoiceDate><ns0:BusinessPillar>ALM</ns0:BusinessPillar></ns0:Header>
        <ns0:Detail LineNumber="1"><ns0:Status>AVL</ns0:Status><ns0:Indicator>P</ns0:Indicator><ns0:ProductDescription>POETS COUNTRY CRISP      375ML</ns0:ProductDescription>
        <ns0:InvoicedProductCode>00069664</ns0:InvoicedProductCode><ns0:OrderedProductCode>00069664</ns0:OrderedProductCode><ns0:WholesalePackQuantity>30.000</ns0:WholesalePackQuantity>
        <ns0:RetailPackQuantity>1.000</ns0:RetailPackQuantity><ns0:UnitsInvoiced>120</ns0:UnitsInvoiced><ns0:CostPrice>49.72</ns0:CostPrice><ns0:ExtendedCostTotal>198.88</ns0:ExtendedCostTotal>
        <ns0:ExtendedGSTTotal>18.08</ns0:ExtendedGSTTotal><ns0:LandedUnitPriceExGST>1.50</ns0:LandedUnitPriceExGST>
        <ns0:GTINPLUS><ns0:GTINPLUUnit Multiple="1.0"><ns0:GTINPLU Type="G"><ns0:Number>9347937002587</ns0:Number></ns0:GTINPLU></ns0:GTINPLUUnit></ns0:GTINPLUS></ns0:Detail>
        <ns0:Detail LineNumber="6"><ns0:Status>AVL</ns0:Status><ns0:ProductDescription>SUNTORY -196 LEMON 10PK 330ML</ns0:ProductDescription>
        <ns0:InvoicedProductCode>00283386</ns0:InvoicedProductCode><ns0:OrderedProductCode>00283386</ns0:OrderedProductCode><ns0:WholesalePackQuantity>30.000</ns0:WholesalePackQuantity>
        <ns0:RetailPackQuantity>10.000</ns0:RetailPackQuantity><ns0:UnitsInvoiced>30</ns0:UnitsInvoiced><ns0:ExtendedCostTotal>120.48</ns0:ExtendedCostTotal><ns0:ExtendedGSTTotal>10.95</ns0:ExtendedGSTTotal>
        <ns0:GTINPLUS><ns0:GTINPLUUnit Multiple="10.0"><ns0:GTINPLU Type="G"><ns0:Number>9316417007961</ns0:Number></ns0:GTINPLU></ns0:GTINPLUUnit></ns0:GTINPLUS></ns0:Detail>
        <ns0:Detail LineNumber="8"><ns0:Status>AVL</ns0:Status><ns0:Indicator>S</ns0:Indicator><ns0:ProductDescription>COOPERS PALE ALE BTL     375ML</ns0:ProductDescription>
        <ns0:InvoicedProductCode>00504460</ns0:InvoicedProductCode><ns0:OrderedProductCode>00513508</ns0:OrderedProductCode><ns0:WholesalePackQuantity>24.000</ns0:WholesalePackQuantity>
        <ns0:RetailPackQuantity>1.000</ns0:RetailPackQuantity><ns0:UnitsInvoiced>216</ns0:UnitsInvoiced><ns0:ExtendedCostTotal>507.57</ns0:ExtendedCostTotal><ns0:ExtendedGSTTotal>46.14</ns0:ExtendedGSTTotal>
        <ns0:GTINPLUS><ns0:GTINPLUUnit Multiple="1.0"><ns0:GTINPLU Type="G"><ns0:Number>93302036</ns0:Number></ns0:GTINPLU></ns0:GTINPLUUnit></ns0:GTINPLUS></ns0:Detail>
        <ns0:Detail LineNumber="9"><ns0:Status>AVL</ns0:Status><ns0:ProductDescription>PAKTECH 6PK CAN HANDLE</ns0:ProductDescription>
        <ns0:InvoicedProductCode>00724235</ns0:InvoicedProductCode><ns0:OrderedProductCode>00724235</ns0:OrderedProductCode><ns0:WholesalePackQuantity>336.000</ns0:WholesalePackQuantity>
        <ns0:RetailPackQuantity>1.000</ns0:RetailPackQuantity><ns0:UnitsInvoiced>336</ns0:UnitsInvoiced><ns0:ExtendedCostTotal>173.15</ns0:ExtendedCostTotal><ns0:ExtendedGSTTotal>15.74</ns0:ExtendedGSTTotal>
        <ns0:GTINPLUS><ns0:GTINPLUUnit Multiple="1.0"><ns0:GTINPLU Type="G"><ns0:Number>7242359</ns0:Number></ns0:GTINPLU></ns0:GTINPLUUnit></ns0:GTINPLUS></ns0:Detail>
        <ns0:Trailer><ns0:SubTotal>1000.08</ns0:SubTotal><ns0:TotalGST>90.91</ns0:TotalGST></ns0:Trailer></ns0:Invoice></ns0:InvoiceBatch>
        """;

    internal static SupplierInvoice ReadSample(string xml = AlmInvoice) =>
        Assert.Single(SupplierInvoiceReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml.Trim()))));

    [Fact]
    public void Reads_the_header()
    {
        var invoice = ReadSample();

        Assert.Equal(("ALM", "11112222", "SI00285912", new DateOnly(2026, 8, 17)),
            (invoice.SupplierName, invoice.AccountNumber, invoice.InvoiceNumber, invoice.InvoiceDate));
        Assert.Equal(4, invoice.Lines.Count);
        Assert.Equal(1000.08m - 90.91m, invoice.CostExGst);
    }

    [Fact]
    public void Line_cost_is_ex_gst_per_invoiced_unit()
    {
        var poets = ReadSample().Lines[0];

        // CostPrice 49.72 a case includes GST; 120 units cost 198.88 - 18.08 GST = 180.80 before GST.
        Assert.Equal(("00069664", 30, 120m, 180.80m), (poets.ItemCode, poets.CasePack, poets.Units, poets.CostExGst));
        Assert.Equal(1.5067m, poets.UnitCostExGst);
        Assert.Equal("9347937002587", poets.Barcode);
        Assert.False(poets.IsSubstitute);
    }

    [Fact]
    public void Reads_pack_barcodes_and_substitutes()
    {
        var lines = ReadSample().Lines;

        Assert.Equal(10, lines[1].BarcodeMultiple);
        Assert.True(lines[2].IsSubstitute);
        Assert.Equal("00513508", lines[2].OrderedItemCode);
    }

    [Theory]
    [InlineData("POETS COUNTRY CRISP      375ML", "Poets Country Crisp 375ml")]
    [InlineData("WOODFORD RES DOUBLE OAKED700ML", "Woodford Res Double Oaked 700ml")]
    [InlineData("SMITHS CRINKL SLT/VIN     90GM", "Smiths Crinkl Slt/Vin 90g")]
    [InlineData("SUNTORY -196 LEMON 10PK 330ML", "Suntory -196 Lemon 10pk 330ml")]
    public void Cleans_names_for_new_products(string description, string expected) =>
        Assert.Equal(expected, SupplierInvoiceReader.CleanName(description));

    [Fact]
    public void Refuses_a_file_whose_lines_do_not_add_up()
    {
        var damaged = AlmInvoice.Replace("<ns0:SubTotal>1000.08</ns0:SubTotal>", "<ns0:SubTotal>1200.00</ns0:SubTotal>");

        var error = Assert.Throws<InvalidDataException>(() => ReadSample(damaged));
        Assert.Contains("incomplete", error.Message);
    }

    [Theory]
    [InlineData("<Invoice><Number>1</Number></Invoice>")]
    [InlineData("PLU,Name,Price")]
    public void Refuses_files_that_are_not_supported_invoices(string content) =>
        Assert.Throws<InvalidDataException>(() => SupplierInvoiceReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(content))));
}
