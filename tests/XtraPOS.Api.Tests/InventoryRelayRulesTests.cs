using XtraPOS.Api.Inventory;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class InventoryRelayRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sales_rate_divides_by_thirty_days_or_by_the_days_since_the_first_sale()
    {
        Assert.Equal(30m, InventoryRelayRules.SalesRateDays(Now.AddDays(-400), Now));
        Assert.Equal(10m, InventoryRelayRules.SalesRateDays(Now.AddDays(-10), Now));
        Assert.Equal(1m, InventoryRelayRules.SalesRateDays(Now.AddHours(-3), Now));
    }

    [Fact]
    public void Package_rate_is_the_unit_rate_divided_by_units_per_package()
    {
        Assert.Equal(2m, InventoryRelayRules.DailyRate(60m, 30m, 1m));
        Assert.Equal(0.1m, InventoryRelayRules.DailyRate(60m, 30m, 20m));
        Assert.Equal(0m, InventoryRelayRules.DailyRate(-5m, 30m, 1m));
        Assert.Equal(0.333333m, InventoryRelayRules.DailyRate(10m, 30m, 1m));
    }

    [Fact]
    public void A_full_copy_goes_once_and_again_each_new_local_day()
    {
        var today = InventoryCalendar.Today(Now);
        Assert.True(InventoryRelayRules.NeedsFullPush(null, today));
        Assert.False(InventoryRelayRules.NeedsFullPush(Now.AddMinutes(-5), today));
        Assert.True(InventoryRelayRules.NeedsFullPush(Now.AddDays(-1), today));
    }

    [Fact]
    public void A_branch_copy_is_stale_after_thirty_minutes()
    {
        Assert.False(InventoryRelayRules.IsStale(Now.AddMinutes(-29), Now));
        Assert.True(InventoryRelayRules.IsStale(Now.AddMinutes(-31), Now));
        Assert.True(InventoryRelayRules.IsStale(null, Now));
    }

    [Fact]
    public void Stock_owner_filter_targets_the_given_alias()
    {
        var sql = InventoryRelayRules.StockOwnerSql("item");
        Assert.Contains("unit.ItemID = item.ItemID", sql);
        Assert.Contains("child.ParentItemID = item.ItemID", sql);
    }
}
