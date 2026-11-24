using NepPOS.Application.Payments;
using NepPOS.Domain.Enums;
using Xunit;

namespace NepPOS.Application.Tests.Payments;

/// <summary>Split payments: gift cards, store credit, owner-defined tenders and split evenly.</summary>
public class SplitTenderTests
{
    [Fact]
    public void AddVoucher_TakesOnlyWhatTheCardHas()
    {
        var tender = new TenderSession(Guid.NewGuid(), 50m);

        var taken = tender.AddVoucher(TenderType.GiftCard, "gc-111111", 0m, 20m);

        Assert.Equal(20m, taken);
        Assert.Equal(30m, tender.Remaining);
        Assert.Equal("GC-111111", tender.Payments[0].Reference);
        Assert.False(tender.IsComplete);
    }

    [Fact]
    public void AddVoucher_NeverTakesMoreThanDue()
    {
        var tender = new TenderSession(Guid.NewGuid(), 15m);

        var taken = tender.AddVoucher(TenderType.StoreCredit, "SC-222222", 0m, 100m);

        Assert.Equal(15m, taken);
        Assert.True(tender.IsComplete);
    }

    [Fact]
    public void AddVoucher_SameCardTwice_Throws()
    {
        var tender = new TenderSession(Guid.NewGuid(), 50m);
        tender.AddVoucher(TenderType.GiftCard, "GC-111111", 10m, 50m);

        Assert.Throws<InvalidOperationException>(() => tender.AddVoucher(TenderType.GiftCard, "gc-111111", 10m, 40m));
    }

    [Fact]
    public void AddVoucher_EmptyCard_Throws()
    {
        var tender = new TenderSession(Guid.NewGuid(), 50m);

        Assert.Throws<InvalidOperationException>(() => tender.AddVoucher(TenderType.GiftCard, "GC-111111", 10m, 0m));
    }

    [Fact]
    public void GiftCardThenCash_Completes()
    {
        var tender = new TenderSession(Guid.NewGuid(), 32.48m);

        tender.AddVoucher(TenderType.GiftCard, "GC-111111", 0m, 20m);
        tender.AddCash(15m);

        Assert.True(tender.IsComplete);
        Assert.Equal(2.50m, tender.ChangeDue);   // $12.48 rounds to $12.50
    }

    [Fact]
    public void AddOther_KeepsNameAndReference()
    {
        var tender = new TenderSession(Guid.NewGuid(), 40m);

        tender.AddOther("Account", 40m, "ACC-7");

        var payment = Assert.Single(tender.Payments);
        Assert.Equal(TenderType.Other, payment.TenderType);
        Assert.Equal("Account", payment.TenderName);
        Assert.Equal("ACC-7", payment.Reference);
        Assert.True(tender.IsComplete);
    }

    [Fact]
    public void AddOther_MoreThanDue_Throws()
    {
        var tender = new TenderSession(Guid.NewGuid(), 40m);

        Assert.Throws<InvalidOperationException>(() => tender.AddOther("Account", 41m));
    }

    [Fact]
    public void EvenShare_LastPersonPaysTheCentsLeft()
    {
        var tender = new TenderSession(Guid.NewGuid(), 100m);

        Assert.Equal(33.33m, tender.EvenShare(3));
        tender.AddOther("Account", 33.33m);
        Assert.Equal(33.33m, tender.EvenShare(2));
    }

    [Fact]
    public void EvenShare_OneWay_Throws()
    {
        var tender = new TenderSession(Guid.NewGuid(), 100m);

        Assert.Throws<ArgumentOutOfRangeException>(() => tender.EvenShare(1));
    }
}
