using System.Globalization;
using NepPOS.Application.Payments;
using Xunit;

namespace NepPOS.Application.Tests.Payments;

public class CashRoundingTests
{
    // decimal can't be an attribute argument, so amounts are passed as strings.
    [Theory]
    [InlineData("19.50", "19.50")]
    [InlineData("19.51", "19.50")]
    [InlineData("19.52", "19.50")]
    [InlineData("19.53", "19.55")]
    [InlineData("19.54", "19.55")]
    [InlineData("19.55", "19.55")]
    [InlineData("19.56", "19.55")]
    [InlineData("19.57", "19.55")]
    [InlineData("19.58", "19.60")]
    [InlineData("19.59", "19.60")]
    [InlineData("0.02", "0.00")]
    public void Round_FollowsAustralianFiveCentRule(string amount, string expected)
    {
        Assert.Equal(Parse(expected), CashRounding.Round(Parse(amount)));
    }

    [Theory]
    [InlineData("20.00", true)]
    [InlineData("19.55", true)]
    [InlineData("19.53", false)]
    [InlineData("0.01", false)]
    public void IsPayableInCash_OnlyAllowsAmountsEndingInZeroOrFiveCents(string amount, bool expected)
    {
        Assert.Equal(expected, CashRounding.IsPayableInCash(Parse(amount)));
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
