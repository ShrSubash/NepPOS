using NepPOS.Application.Promotions;
using NepPOS.Domain.Entities;
using Xunit;

namespace NepPOS.Application.Tests.Promotions;

public class PromotionEngineTests
{
    private static readonly DateTime Friday6pm = new(2026, 11, 27, 18, 0, 0);

    private static Promotion TwoForTen() => new()
    {
        Name = "2 for $10", Kind = PromotionKind.MultiBuy, BuyQuantity = 2, Price = 10m, ProductIds = "1,2", IsActive = true
    };

    [Fact]
    public void Multi_buy_mixes_products_and_leaves_the_odd_one_at_full_price()
    {
        // Three items at $6, $6 and $5.50: one set ($12 -> $10), the $5.50 pays full price.
        var lines = new[] { new PromoLine("a", 1, "Snacks", 6m, 2), new PromoLine("b", 2, "Snacks", 5.50m, 1) };

        var discounts = PromotionEngine.Apply(lines, new[] { TwoForTen() }, Friday6pm);

        var only = Assert.Single(discounts);
        Assert.Equal(("a", 2m, "2 for $10"), (only.Key, only.Amount, only.Reason));
    }

    [Fact]
    public void Multi_buy_spreads_the_saving_by_price_across_a_mixed_set()
    {
        var lines = new[] { new PromoLine("a", 1, null, 6m, 1), new PromoLine("b", 2, null, 6m, 1) };

        var discounts = PromotionEngine.Apply(lines, new[] { TwoForTen() }, Friday6pm);

        Assert.Equal(2m, discounts.Sum(d => d.Amount));
        Assert.All(discounts, d => Assert.Equal(1m, d.Amount));
    }

    [Fact]
    public void Percent_off_a_department()
    {
        var promo = new Promotion { Name = "Wine 10% off", Kind = PromotionKind.PercentOff, Percent = 10, Category = "Wine", IsActive = true };
        var lines = new[] { new PromoLine("a", 1, "wine", 19.99m, 2), new PromoLine("b", 2, "Beer", 20m, 1) };

        var only = Assert.Single(PromotionEngine.Apply(lines, new[] { promo }, Friday6pm));
        Assert.Equal(("a", 4m), (only.Key, only.Amount));
    }

    [Fact]
    public void Amount_off_is_never_more_than_the_item()
    {
        var promo = new Promotion { Name = "$5 off", Kind = PromotionKind.AmountOff, Amount = 5m, ProductIds = "1", IsActive = true };

        var only = Assert.Single(PromotionEngine.Apply(new[] { new PromoLine("a", 1, null, 3m, 2) }, new[] { promo }, Friday6pm));
        Assert.Equal(6m, only.Amount);
    }

    [Fact]
    public void One_promotion_per_line_first_wins()
    {
        var percent = new Promotion { Name = "10%", Kind = PromotionKind.PercentOff, Percent = 10, ProductIds = "1", IsActive = true };
        var amount = new Promotion { Name = "$1", Kind = PromotionKind.AmountOff, Amount = 1, ProductIds = "1", IsActive = true };

        var only = Assert.Single(PromotionEngine.Apply(new[] { new PromoLine("a", 1, null, 20m, 1) }, new[] { percent, amount }, Friday6pm));
        Assert.Equal("10%", only.Reason);
    }

    [Theory]
    [InlineData(17, 0, true)]
    [InlineData(18, 59, true)]
    [InlineData(19, 0, false)]
    [InlineData(16, 59, false)]
    public void Happy_hour_runs_between_its_times_on_its_days(int hour, int minute, bool runs)
    {
        var promo = new Promotion { Name = "Happy hour", IsActive = true, Days = "Fri", FromTime = new TimeOnly(17, 0), ToTime = new TimeOnly(19, 0) };
        Assert.Equal(runs, promo.RunsAt(new DateTime(2026, 11, 27, hour, minute, 0)));
        Assert.False(promo.RunsAt(new DateTime(2026, 11, 26, 17, 30, 0)));   // Thursday
    }

    [Fact]
    public void Overnight_hours_and_dates_and_switched_off()
    {
        var late = new Promotion { IsActive = true, FromTime = new TimeOnly(22, 0), ToTime = new TimeOnly(2, 0) };
        Assert.True(late.RunsAt(new DateTime(2026, 11, 27, 1, 0, 0)));
        Assert.False(late.RunsAt(new DateTime(2026, 11, 27, 3, 0, 0)));

        var dated = new Promotion { IsActive = true, StartDate = new DateOnly(2026, 12, 1), EndDate = new DateOnly(2026, 12, 24) };
        Assert.False(dated.RunsAt(Friday6pm));
        Assert.True(dated.RunsAt(new DateTime(2026, 12, 24, 23, 0, 0)));

        Assert.False(new Promotion { IsActive = false }.RunsAt(Friday6pm));
    }

    [Fact]
    public void Spread_adds_up_exactly_and_follows_line_values()
    {
        var shares = PromotionEngine.Spread(10m, new[] { ("a", 33.33m), ("b", 33.33m), ("c", 33.34m) });

        Assert.Equal(10m, shares.Sum(s => s.Amount));
        Assert.Equal(new[] { 3.33m, 3.33m, 3.34m }, shares.Select(s => s.Amount).ToArray());
    }

    [Fact]
    public void Spread_never_gives_more_than_the_sale()
    {
        var shares = PromotionEngine.Spread(50m, new[] { ("a", 20m), ("b", 10m) });
        Assert.Equal(30m, shares.Sum(s => s.Amount));
    }
}
