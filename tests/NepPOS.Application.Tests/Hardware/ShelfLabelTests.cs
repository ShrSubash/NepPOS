using System.Text;
using NepPOS.Application.Hardware;
using Xunit;

namespace NepPOS.Application.Tests.Hardware;

public class ShelfLabelTests
{
    [Fact]
    public void Code128_has_start_B_check_digit_and_stop()
    {
        // "PJJ123C": 104 + 48·1 + 42·2 + 42·3 + 17·4 + 18·5 + 19·6 + 35·7 = 879; 879 mod 103 = 55.
        var modules = Code128.Modules("PJJ123C");

        Assert.Equal(new[] { 2, 1, 1, 2, 1, 4 }, modules.Take(6));              // start B (104)
        Assert.Equal(new[] { 2, 3, 3, 1, 1, 1, 2 }, modules.TakeLast(7));       // stop
        Assert.Equal(new[] { 3, 1, 1, 3, 2, 1 }, modules.SkipLast(7).TakeLast(6)); // check 55
        Assert.Equal(11 * 9 + 13, modules.Sum());                               // start + 7 chars + check = 11 each, stop 13
    }

    [Fact]
    public void Code128_refuses_characters_set_B_cannot_hold()
    {
        Assert.Throws<ArgumentException>(() => Code128.Modules(""));
        Assert.Throws<ArgumentException>(() => Code128.Modules("café"));
    }

    [Fact]
    public void Labels_make_one_page_per_21_counting_skipped_slots()
    {
        var label = new ShelfLabel("Corona Extra 355ml Bottle", 4.50m, "7501064191275", null, 5.00m);

        var onePage = Encoding.ASCII.GetString(ShelfLabels.Pdf(Enumerable.Repeat(label, 21).ToList()));
        var twoPages = Encoding.ASCII.GetString(ShelfLabels.Pdf(Enumerable.Repeat(label, 2).ToList(), skip: 20));

        Assert.StartsWith("%PDF", onePage);
        Assert.Equal(1, Pages(onePage));
        Assert.Equal(2, Pages(twoPages));
    }

    [Fact]
    public void No_labels_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => ShelfLabels.Pdf(Array.Empty<ShelfLabel>()));
    }

    private static int Pages(string pdf) =>
        System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page[^s]").Count;
}
