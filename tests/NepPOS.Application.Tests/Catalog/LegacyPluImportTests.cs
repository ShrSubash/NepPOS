using NepPOS.Application.Catalog;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Catalog;

public class LegacyPluImportTests
{
    // The columns the import reads, in the old POS's order (the real table has ~210; the rest are ignored).
    private const string Header =
        "Number\tdescription\tlongdescrip\tSOH\tpacksize\tsplitsize\tsinglecost\taveragecost\tpackcost\tsingleprice\tsplitprice\tpackprice\tprice4\tprice5\tsplit2price\tsupplier\tdept\tStatus\tonlineshopenabled\tscancodes1\tscancodes2\tscancodes3\tscancodes4\tsuppcodes1\tsuppcodes2\ttaxcode\tsplit2size";

    private static string Row(params string[] cells) => string.Join('\t', cells);

    private static readonly string Asahi = Row("37", "ASAHI SUPER DRY 5%  ", "ASAHI SUPER DRY", "283", "24", "6", "2.25", "2.4200", "54.00", "5.50", "0.00", "72.99", "27.99", "0.00", "0.00",
        "3", "3", "3", "0", "9329982021065     P7", "9329982021072     P3", "9329982021058     P1", "4901004021861     P1", "CUB94148", "CUB94611", "B", "2");
    private static readonly string JackDaniels = Row("935                 ", "JACK DANIELS BOURBON", "JACK DANIELS BOURBON 700ML", "4", "12", "6", "47.58", "47.5800", "571.00", "64.99", "0.00", "0.00", "0.00", "0.00", "0.00",
        "2", "11", "3", "0", "82184045954       P1", "", "", "", "ILG3050600P", "", "B", "2");
    private static readonly string Ara = Row("32", "ARA SAV BLANC", "ARA SAV BLANC", "0", "6", "2", "14.33", "14.3300", "86.00", "35.99", "59.99", "0.00", "0.00", "0.00", "0.00",
        "2", "36", "3", "0", "9421900742097     P1", "", "", "", "ILG2052067", "", "B", "4");

    private const string Departments = "Number\tdpn\n3\tBEER IMPORTED\n11\tSPIRITS\n36\tSAV/BLA\n";
    private const string Suppliers = "Number\tsupplier\n2\tILG\n3\tCUB\n";

    private static LegacyImportPlan Plan(string rows, string[]? plus = null, string[]? barcodes = null) =>
        LegacyPluImport.Plan(Header + "\n" + rows, Departments, Suppliers, plus ?? Array.Empty<string>(), barcodes ?? Array.Empty<string>());

    [Fact]
    public void A_beer_gets_its_6_pack_and_case_with_every_barcode_on_the_right_pack()
    {
        var plan = Plan(Asahi);

        var asahi = Assert.Single(plan.New);
        Assert.Equal("37", asahi.Plu);
        Assert.Equal("ASAHI SUPER DRY", asahi.Name);
        Assert.Equal(5.50m, asahi.Price);
        Assert.Equal(2.42m, asahi.Cost);
        Assert.Equal(283m, asahi.Stock);
        Assert.Equal("BEER IMPORTED", asahi.Department);
        Assert.Equal(new[] { "9329982021058", "4901004021861" }, asahi.Barcodes);

        var six = Assert.Single(asahi.Packs, p => p.Type == PackType.Pack);
        Assert.Equal((6, 27.99m), (six.Units, six.Price));
        Assert.Equal(new[] { "9329982021065" }, six.Barcodes);
        var @case = Assert.Single(asahi.Packs, p => p.Type == PackType.FullCase);
        Assert.Equal((24, 72.99m), (@case.Units, @case.Price));
        Assert.Equal(new[] { "9329982021072" }, @case.Barcodes);

        Assert.Equal("3", asahi.SupplierNumber);
        Assert.Equal(new[] { "CUB94148", "CUB94611" }, asahi.SupplierCodes);
        Assert.Equal("CUB", plan.Suppliers["3"]);
    }

    [Fact]
    public void Packs_without_a_price_are_not_made()
    {
        var plan = Plan(JackDaniels + "\n" + Ara);

        var jack = plan.New.Single(p => p.Plu == "935");
        Assert.Empty(jack.Packs);                         // bought by the case of 12, sold only as singles
        Assert.Equal(new[] { "82184045954" }, jack.Barcodes);
        Assert.Equal("SPIRITS", jack.Department);

        var ara = plan.New.Single(p => p.Plu == "32");
        var two = Assert.Single(ara.Packs);
        Assert.Equal((PackType.SplitCase, 2, 59.99m), (two.Type, two.Units, two.Price));
    }

    [Fact]
    public void Products_already_in_NepPOS_are_left_alone_and_barcodes_never_clash()
    {
        var plan = Plan(Asahi + "\n" + JackDaniels, plus: new[] { "935" }, barcodes: new[] { "04901004021861" });

        var asahi = Assert.Single(plan.New);
        Assert.Equal(1, plan.AlreadyHere);
        Assert.Equal(new[] { "9329982021058" }, asahi.Barcodes);  // the other one is taken (leading zero ignored)
        Assert.Contains(plan.Notes, n => n.Message.Contains("4901004021861"));
    }

    [Fact]
    public void Without_name_lists_numbers_are_kept_and_inactive_and_gst_free_come_across()
    {
        var row = Row("900", "BREAD", "", "0", "0", "0", "1.00", "1.0000", "0", "3.50", "0", "0", "0", "0", "0",
            "7", "8", "9", "1", "", "", "", "", "", "", "A", "0");

        var item = Assert.Single(LegacyPluImport.Plan(Header + "\n" + row, null, null, Array.Empty<string>(), Array.Empty<string>()).New);

        Assert.Equal("BREAD", item.Name);                // short description when there's no long one
        Assert.Equal("Department 8", item.Department);
        Assert.True(item.IsGstFree);
        Assert.False(item.IsActive);
        Assert.True(item.OnWebsite);
    }

    [Fact]
    public void A_file_that_isnt_the_product_table_is_refused()
    {
        var plan = LegacyPluImport.Plan("PLU,Name,Price\n1,Milk,2.00\n", null, null, Array.Empty<string>(), Array.Empty<string>());

        Assert.False(plan.HasWork);
        Assert.Contains("Copy with Headers", Assert.Single(plan.Skipped).Message);
    }
}
