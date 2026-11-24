using NepPOS.Application.Payments;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Payments;

public class RefundSessionTests
{
    [Fact]
    public void Constructor_PositiveTotal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RefundSession(Guid.NewGuid(), 10m));
    }

    [Fact]
    public void Constructor_EvenExchange_IsAlreadyComplete()
    {
        var refund = new RefundSession(Guid.NewGuid(), 0m);

        Assert.True(refund.IsComplete);
        Assert.Empty(refund.Payments);
    }

    [Fact]
    public void RefundCash_RoundsToFiveCentsAndRecordsRounding()
    {
        // $19.52 back: cash gives $19.50, the 2c goes on the record as rounding.
        var refund = new RefundSession(Guid.NewGuid(), -19.52m);

        Assert.Equal(19.50m, refund.RemainingCash);
        var given = refund.RefundCash();

        Assert.Equal(19.50m, given);
        Assert.True(refund.IsComplete);
        Assert.Equal(0.02m, refund.RoundingAdjustment);
        var payment = Assert.Single(refund.Payments);
        Assert.Equal(TenderType.Cash, payment.TenderType);
        Assert.Equal(-19.50m, payment.Amount);
        Assert.True(payment.IsRefund);
    }

    [Fact]
    public void RefundCash_MoreThanDue_Throws()
    {
        var refund = new RefundSession(Guid.NewGuid(), -10m);

        Assert.Throws<InvalidOperationException>(() => refund.RefundCash(10.05m));
    }

    [Fact]
    public void RefundCash_AmountNotEndingInFiveCents_Throws()
    {
        var refund = new RefundSession(Guid.NewGuid(), -10m);

        Assert.Throws<InvalidOperationException>(() => refund.RefundCash(3.33m));
    }

    [Fact]
    public void Split_CardThenStoreCredit_Completes()
    {
        var refund = new RefundSession(Guid.NewGuid(), -50m);

        refund.RefundCard(30m, " 123456 ");
        Assert.False(refund.IsComplete);
        Assert.Equal(20m, refund.Remaining);

        refund.RefundToVoucher(TenderType.StoreCredit, "sc-123456", 20m);

        Assert.True(refund.IsComplete);
        Assert.Equal(50m, refund.Refunded);
        Assert.Equal("123456", refund.Payments[0].AuthCode);
        Assert.Equal("SC-123456", refund.Payments[1].Reference);
        Assert.Equal(-20m, refund.Payments[1].Amount);
    }

    [Fact]
    public void RefundOther_KeepsTheTenderName()
    {
        var refund = new RefundSession(Guid.NewGuid(), -12m);

        refund.RefundOther("Account", 12m, "ACC-9");

        var payment = Assert.Single(refund.Payments);
        Assert.Equal(TenderType.Other, payment.TenderType);
        Assert.Equal("Account", payment.TenderName);
        Assert.True(refund.IsComplete);
    }

    [Fact]
    public void AfterComplete_MoreRefunds_Throw()
    {
        var refund = new RefundSession(Guid.NewGuid(), -5m);
        refund.RefundCard(5m);

        Assert.Throws<InvalidOperationException>(() => refund.RefundCard(1m));
    }

    [Fact]
    public void RefundToVoucher_CashType_Throws()
    {
        var refund = new RefundSession(Guid.NewGuid(), -5m);

        Assert.Throws<ArgumentOutOfRangeException>(() => refund.RefundToVoucher(TenderType.Cash, "X", 5m));
    }
}
