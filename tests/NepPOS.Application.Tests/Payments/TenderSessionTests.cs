using NepPOS.Application.Payments;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Payments;

public class TenderSessionTests
{
    private static readonly PaymentResult Approved = new(true, "00", "APPROVED", "123456", "VISA", "**** 4242");
    private static readonly PaymentResult Declined = new(false, "51", "DECLINED");

    [Fact]
    public void Cash_ExactRoundedAmount_RoundsDownAndCompletes()
    {
        var tender = new TenderSession(Guid.NewGuid(), 19.52m);

        Assert.Equal(19.50m, tender.RemainingCash);
        tender.AddCash(19.50m);

        Assert.True(tender.IsComplete);
        Assert.Equal(-0.02m, tender.RoundingAdjustment);
        Assert.Equal(0m, tender.ChangeDue);
        Assert.Equal(19.50m, Assert.Single(tender.Payments).Amount);
    }

    [Fact]
    public void Cash_OverTendered_GivesChangeFromRoundedTotal()
    {
        var tender = new TenderSession(Guid.NewGuid(), 19.53m);

        tender.AddCash(20.00m);

        Assert.True(tender.IsComplete);
        Assert.Equal(0.02m, tender.RoundingAdjustment);
        Assert.Equal(0.45m, tender.ChangeDue);

        var payment = Assert.Single(tender.Payments);
        Assert.Equal(TenderType.Cash, payment.TenderType);
        Assert.Equal(19.55m, payment.Amount);
        Assert.Equal(20.00m, payment.Tendered);
        Assert.Equal(0.45m, payment.Change);
    }

    [Fact]
    public void Cash_AmountNotEndingInZeroOrFiveCents_IsRejected()
    {
        var tender = new TenderSession(Guid.NewGuid(), 19.53m);

        Assert.Throws<InvalidOperationException>(() => tender.AddCash(19.53m));
        Assert.Empty(tender.Payments);
    }

    [Fact]
    public void Cash_LessThanDue_IsAPartPaymentThenCardPaysTheExactRest()
    {
        var tender = new TenderSession(Guid.NewGuid(), 19.53m);

        tender.AddCash(10.00m);
        Assert.False(tender.IsComplete);
        Assert.Equal(9.53m, tender.Remaining);

        tender.AddEftpos(9.53m, Approved);

        Assert.True(tender.IsComplete);
        Assert.Equal(0m, tender.RoundingAdjustment);
        Assert.Equal(19.53m, tender.Paid);
    }

    [Fact]
    public void SplitTender_CardThenCash_RoundsOnlyTheCashPart()
    {
        var tender = new TenderSession(Guid.NewGuid(), 38.49m);

        tender.AddEftpos(20.00m, Approved);
        Assert.Equal(18.49m, tender.Remaining);
        Assert.Equal(18.50m, tender.RemainingCash);

        tender.AddCash(20.00m);

        Assert.True(tender.IsComplete);
        Assert.Equal(0.01m, tender.RoundingAdjustment);
        Assert.Equal(1.50m, tender.ChangeDue);
        Assert.Equal(38.50m, tender.Paid); // 38.49 sale + 0.01 rounding
    }

    [Fact]
    public void Eftpos_FullAmount_Completes()
    {
        var tender = new TenderSession(Guid.NewGuid(), 38.49m);

        tender.AddEftpos(38.49m, Approved);

        Assert.True(tender.IsComplete);
        Assert.Equal("123456", Assert.Single(tender.Payments).AuthCode);
    }

    [Fact]
    public void Eftpos_MoreThanRemaining_IsRejectedBeforeReachingTheTerminal()
    {
        var tender = new TenderSession(Guid.NewGuid(), 10.00m);

        Assert.Throws<InvalidOperationException>(() => tender.ValidateCardAmount(10.05m));
    }

    [Fact]
    public void Eftpos_Declined_IsNotRecorded()
    {
        var tender = new TenderSession(Guid.NewGuid(), 10.00m);

        Assert.Throws<InvalidOperationException>(() => tender.AddEftpos(10.00m, Declined));
        Assert.Empty(tender.Payments);
        Assert.False(tender.IsComplete);
    }

    [Fact]
    public void CompletedSale_CannotTakeMorePayments()
    {
        var tender = new TenderSession(Guid.NewGuid(), 5.00m);
        tender.AddCash(5.00m);

        Assert.Throws<InvalidOperationException>(() => tender.AddCash(5.00m));
    }
}