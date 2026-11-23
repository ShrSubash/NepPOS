using System.Text;
using NepPOS.Application.Catalog;
using NepPOS.Domain.Entities;
using Xunit;

namespace NepPOS.Application.Tests.Catalog;

public class ProductCsvTests
{
    private static List<Product> Saved() => new()
    {
        new() { Id = 1, Plu = "101", Name = "Basmati Rice 5kg", Price = 14.99m, Category = "Rice", Barcode = "8901234567890", IsActive = true },
        new() { Id = 2, Plu = "102", Name = "Toor Dal 2kg", Price = 8.50m, Category = "Lentils", IsActive = true },
        new() { Id = 3, Name = "Gundruk 100g", Price = 5.50m, Category = "Dried", IsActive = true },
    };

    [Fact]
    public void New_and_changed_products_are_found_and_unchanged_ones_counted()
    {
        var csv = "PLU,Name,Price,Department\n101,Basmati Rice 5kg,15.49,Rice\n102,Toor Dal 2kg,8.50,Lentils\n200,Chicken Momo (10),9.50,Frozen\n";

        var plan = ProductCsv.Plan(csv, Saved());

        Assert.Empty(plan.Problems);
        Assert.Equal(3, plan.Rows);
        var created = Assert.Single(plan.New);
        Assert.Equal("200", created.Plu);
        Assert.Equal(9.50m, created.Price);
        Assert.True(created.IsActive);
        var update = Assert.Single(plan.Updates);
        Assert.Equal(1, update.Updated.Id);
        Assert.Equal(15.49m, update.Updated.Price);
        Assert.Contains(update.Changes, c => c.StartsWith("Price 14.99 → 15.49"));
        Assert.Equal(1, plan.Unchanged);
    }

    [Fact]
    public void Headings_from_other_systems_and_extra_columns_are_understood()
    {
        var csv = "Item Code;Description;Sell Price;Dept;APN;SOH;Supplier\n101;Basmati Rice 5kg;$14.99;Rice;8901234567890;22;ABC Foods\n";

        var plan = ProductCsv.Plan(csv, Saved());

        Assert.Empty(plan.Problems);
        var update = Assert.Single(plan.Updates);
        Assert.Equal(22m, update.Updated.StockOnHand);
        Assert.Single(update.Changes);
    }

    [Fact]
    public void An_empty_cell_keeps_what_is_saved()
    {
        var plan = ProductCsv.Plan("PLU,Name,Price,Department\n102,,9.00,\n", Saved());

        var update = Assert.Single(plan.Updates);
        Assert.Equal("Toor Dal 2kg", update.Updated.Name);
        Assert.Equal("Lentils", update.Updated.Category);
        Assert.Equal(9.00m, update.Updated.Price);
    }

    [Fact]
    public void Rows_match_by_barcode_then_by_name_only_without_plu_or_barcode()
    {
        var csv = "PLU,Barcode,Name,Price\n,8901234567890,,13.99\n,,Gundruk 100g,6.00\n";

        var plan = ProductCsv.Plan(csv, Saved());

        Assert.Empty(plan.New);
        Assert.Equal(new[] { 1, 3 }, plan.Updates.Select(u => u.Updated.Id).OrderBy(id => id));
    }

    [Fact]
    public void Bad_lines_are_reported_and_the_rest_still_import()
    {
        var csv = "PLU,Name,Price,Barcode,Active\n"
                  + "300,No price,,,\n"            // new without a price
                  + "301,Bad price,abc,,\n"
                  + "302,Excel barcode,2.00,9.31E+12,\n"
                  + "303,Bad flag,2.00,,maybe\n"
                  + "304,Good,2.00,,\n"
                  + "304,Twice,2.00,,\n"
                  + "305,Taken barcode,2.00,8901234567890,\n";

        var plan = ProductCsv.Plan(csv, Saved());

        Assert.Single(plan.New);
        Assert.Equal("304", plan.New[0].Plu);
        Assert.Equal(new[] { 2, 3, 4, 5, 7, 8 }, plan.Problems.Select(p => p.Line));
    }

    [Fact]
    public void A_file_without_headings_is_refused()
    {
        var plan = ProductCsv.Plan("101,Basmati,14.99\n", Saved());

        Assert.False(plan.HasWork);
        Assert.Equal(1, Assert.Single(plan.Problems).Line);
    }

    [Fact]
    public void Quoted_cells_with_commas_quotes_and_line_breaks_are_read()
    {
        var csv = "PLU,Name,Price\n400,\"Momo, \"\"jhol\"\" style\nlarge\",12.00\n401,Plain,1.00\n";

        var plan = ProductCsv.Plan(csv, Saved());

        Assert.Empty(plan.Problems);
        Assert.Equal("Momo, \"jhol\" style\nlarge", plan.New[0].Name);
        Assert.Equal(2, plan.New.Count);
    }

    [Fact]
    public void Export_imports_back_with_nothing_to_change()
    {
        var products = Saved();
        products.Add(new Product { Id = 4, Plu = "-5", Name = "=Odd, \"name\"", Price = 1m, Category = "Misc", CostPrice = 0.40m, StockOnHand = 3, EnableOnWebsite = true, IsActive = false });

        var text = ProductCsv.Decode(ProductCsv.Export(products));
        var plan = ProductCsv.Plan(text, products);

        Assert.Empty(plan.Problems);
        Assert.False(plan.HasWork);
        Assert.Equal(products.Count, plan.Unchanged);
    }

    [Fact]
    public void Excel_ansi_files_are_read()
    {
        var bytes = Encoding.Latin1.GetBytes("PLU,Name,Price\n500,Café latte,4.50\n");

        var plan = ProductCsv.Plan(ProductCsv.Decode(bytes), Saved());

        Assert.Equal("Café latte", Assert.Single(plan.New).Name);
    }
}
