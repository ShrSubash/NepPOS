using NepPOS.Application.Email;
using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Email;

/// <summary>Address checks, the Z email's wording, retry timing and "is email set up".</summary>
public class EmailTests
{
    [Fact]
    public void AddressList_SplitsOnCommasAndSemicolons_AndFlagsBadOnes()
    {
        var (valid, invalid) = EmailAddressList.Parse("owner@nep.com.au; Manager@Nep.com.au, owner@nep.com.au, not-an-email, bob@localhost");

        Assert.Equal(new[] { "owner@nep.com.au", "Manager@Nep.com.au" }, valid);
        Assert.Equal(new[] { "not-an-email", "bob@localhost" }, invalid);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a@b.com", true)]
    [InlineData("Owner <a@b.com>", false)]
    [InlineData("a@b", false)]
    public void IsValid_OnlyAcceptsABareAddressWithADomain(string address, bool expected)
    {
        Assert.Equal(expected, EmailAddressList.IsValid(address));
    }

    [Fact]
    public void Settings_AreCompleteOnlyWithServerFromAndTo()
    {
        var settings = new EmailSettings { SmtpHost = "smtp.gmail.com", UserName = "shop@gmail.com", ToAddresses = "owner@nep.com.au" };

        Assert.True(settings.IsComplete);
        Assert.Equal("shop@gmail.com", settings.EffectiveFrom);           // From defaults to the user name
        Assert.False((settings with { SmtpHost = "" }).IsComplete);
        Assert.False((settings with { ToAddresses = "nobody" }).IsComplete);
        Assert.False((settings with { UserName = "", FromAddress = "" }).IsComplete);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(7, 60)]      // 64 minutes, capped at an hour
    [InlineData(50, 60)]
    public void Retry_DoublesTheWaitUpToAnHour(int failedAttempts, int minutes)
    {
        var now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddMinutes(minutes), EmailRetry.NextAttemptUtc(failedAttempts, now));
    }

    [Fact]
    public void ReportEmail_SubjectAndBodyCarryTheHeadlineFigures()
    {
        var z = new EndOfDayReport
        {
            IsZ = true,
            ZNumber = 12,
            RegisterId = "REG-001",
            PeriodStartUtc = new DateTime(2026, 11, 13, 22, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc),
            SaleCount = 142,
            GrossSales = 6843.55m,
            Gst = 622.14m,
            Tenders = new[] { new TenderTotal(TenderType.Cash, 51, 1812.30m), new TenderTotal(TenderType.Eftpos, 96, 5031.20m) },
            Departments = new[] { new DepartmentTotal("Beer", 160, 2980.00m) },
            VoidCount = 2
        };
        var store = new StoreProfile { BusinessName = "Nep Liquor" };

        var subject = ReportEmail.Subject(z, store);
        var body = ReportEmail.Body(z, store);

        Assert.StartsWith("Nep Liquor - Z Report #12 - ", subject);
        Assert.Contains(ReportPeriod.TradingDay(z.PeriodEndUtc).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), subject);
        Assert.Contains("$6,843.55", body);
        Assert.Contains("142 transactions", body);
        Assert.Contains("Cash in drawer:   $1,812.30", body);
        Assert.Contains("EFTPOS:           $5,031.20", body);
        Assert.Contains("Top department:   Beer $2,980.00", body);
        Assert.Contains("Needs a look:     2", body);
    }
}
