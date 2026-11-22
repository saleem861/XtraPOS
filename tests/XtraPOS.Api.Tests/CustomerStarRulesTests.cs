using XtraPOS.Api.Customers;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CustomerStarRulesTests
{
    private static readonly CustomerStarThresholds Limits = CustomerStarRules.Validate(90, 100m, 500m, 1500m, 3000m, 6000m);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99.99, 0)]
    [InlineData(100, 1)]
    [InlineData(499, 1)]
    [InlineData(500, 2)]
    [InlineData(2999.5, 3)]
    [InlineData(3000, 4)]
    [InlineData(6000, 5)]
    [InlineData(100000, 5)]
    public void Stars_follow_the_thresholds(double total, int stars)
    {
        Assert.Equal(stars, CustomerStarRules.StarsFor((decimal)total, Limits));
    }

    [Fact]
    public void Standing_compares_with_the_previous_window_and_shows_the_next_step()
    {
        var up = CustomerStarRules.Standing(1200m, 800m, Limits);
        Assert.Equal(2, up.Stars);
        Assert.Equal(CustomerStarRules.TrendUp, up.Trend);
        Assert.Equal(50m, up.ChangePercent);
        Assert.Equal(1500m, up.NextStarAt);
        Assert.Equal(300m, up.NeededForNext);

        Assert.Equal(CustomerStarRules.TrendDown, CustomerStarRules.Standing(500m, 1000m, Limits).Trend);
        Assert.Equal(CustomerStarRules.TrendSteady, CustomerStarRules.Standing(950m, 1000m, Limits).Trend);
        Assert.Equal(CustomerStarRules.TrendNew, CustomerStarRules.Standing(200m, 0m, Limits).Trend);
        Assert.Equal(CustomerStarRules.TrendInactive, CustomerStarRules.Standing(0m, 0m, Limits).Trend);

        var top = CustomerStarRules.Standing(7000m, 7000m, Limits);
        Assert.Equal(5, top.Stars);
        Assert.Null(top.NextStarAt);
        Assert.Null(top.NeededForNext);
    }

    [Fact]
    public void Thresholds_must_rise_and_the_window_stays_reasonable()
    {
        Assert.Throws<CustomerStarException>(() => CustomerStarRules.Validate(3, 1m, 2m, 3m, 4m, 5m));
        Assert.Throws<CustomerStarException>(() => CustomerStarRules.Validate(90, 0m, 2m, 3m, 4m, 5m));
        Assert.Throws<CustomerStarException>(() => CustomerStarRules.Validate(90, 1m, 2m, 2m, 4m, 5m));
        Assert.True(CustomerStarRules.Default.IsDefault);
        Assert.False(Limits.IsDefault);
    }
}
