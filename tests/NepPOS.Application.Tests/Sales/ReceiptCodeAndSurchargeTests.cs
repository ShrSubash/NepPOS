using NepPOS.Application.Sales;
using Xunit;

namespace NepPOS.Application.Tests.Sales;

public class ReceiptCodeTests
{
    [Fact]
    public void For_UsesRegisterDigitsAndInvoice()
    {
        Assert.Equal("RT001-19", ReceiptCode.For("REG-001", 19));
    }

    [Fact]
    public void For_RegisterWithoutDigits_UsesLetters()
    {
        Assert.Equal("RTBAR-5", ReceiptCode.For("bar", 5));
    }

    [Theory]
    [InlineData("RT001-19", "001", 19)]
    [InlineData(" rt001-19 ", "001", 19)]
    [InlineData("RTBAR-123456", "BAR", 123456)]
    public void TryParse_ReadsScannedCodes(string code, string register, long invoice)
    {
        Assert.True(ReceiptCode.TryParse(code, out var reg, out var inv));
        Assert.Equal(register, reg);
        Assert.Equal(invoice, inv);
    }

    [Theory]
    [InlineData("9300000000001")]   // a product barcode
    [InlineData("GC-123456")]       // a gift card
    [InlineData("RT001-0")]         // no invoice 0
    [InlineData("RT001")]
    [InlineData("")]
    public void TryParse_RejectsOtherCodes(string code)
    {
        Assert.False(ReceiptCode.TryParse(code, out _, out _));
    }
}

public class SurchargeCalculatorTests
{
    private static readonly SurchargeSettings Settings = new()
    {
        SundayPercent = 10m,
        PublicHolidayPercent = 15m,
        PublicHolidays = new[] { new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 27) }   // 27th is a Sunday
    };

    [Fact]
    public void For_Sunday()
    {
        var surcharge = SurchargeCalculator.For(new DateOnly(2026, 11, 29), Settings);

        Assert.Equal(("Sunday surcharge", 10m), surcharge);
    }

    [Fact]
    public void For_PublicHolidayOnASunday_HolidayWins()
    {
        var surcharge = SurchargeCalculator.For(new DateOnly(2026, 12, 27), Settings);

        Assert.Equal(("Public holiday surcharge", 15m), surcharge);
    }

    [Fact]
    public void For_Weekday_None()
    {
        Assert.Null(SurchargeCalculator.For(new DateOnly(2026, 11, 24), Settings));
    }

    [Fact]
    public void For_NoSettings_None()
    {
        Assert.Null(SurchargeCalculator.For(new DateOnly(2026, 11, 29), null));
    }

    [Theory]
    [InlineData("57.48", "10", "5.75")]
    [InlineData("10.05", "15", "1.51")]   // 1.5075 rounds to 1.51
    [InlineData("0", "10", "0")]
    [InlineData("-20", "10", "0")]        // returns don't attract a surcharge
    [InlineData("20", "0", "0")]
    public void Amount_RoundsToTheCent(string goods, string percent, string expected)
    {
        Assert.Equal(decimal.Parse(expected), SurchargeCalculator.Amount(decimal.Parse(goods), decimal.Parse(percent)));
    }

    [Fact]
    public void Label_ShowsPercent()
    {
        Assert.Equal("Sunday surcharge 10%", SurchargeCalculator.Label("Sunday surcharge", 10m));
        Assert.Equal("Public holiday surcharge 12.5%", SurchargeCalculator.Label("Public holiday surcharge", 12.5m));
    }
}
