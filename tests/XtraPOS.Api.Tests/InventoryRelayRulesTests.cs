using XtraPOS.Api.Catalog;
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
    [Fact]
    public void A_remote_correction_adds_the_difference_to_the_current_balance()
    {
        // مفترض 7، معدود 5، بيع 1 قبل المزامنة (رصيد الفرع 6) → 6 + (−2) = 4.
        var plan = InventoryRelayRules.PlanCorrection(counted: 5m, delta: 5m - 7m, branchInitialized: true);
        Assert.True(plan.ByDifference);
        Assert.Equal(-2m, plan.Value);
        Assert.Equal(4m, 6m + plan.Value);
    }

    [Fact]
    public void A_remote_correction_without_a_known_difference_writes_the_counted_quantity()
    {
        var unknown = InventoryRelayRules.PlanCorrection(counted: 5m, delta: null, branchInitialized: true);
        Assert.False(unknown.ByDifference);
        Assert.Equal(5m, unknown.Value);

        var uninitialized = InventoryRelayRules.PlanCorrection(counted: 5m, delta: -2m, branchInitialized: false);
        Assert.False(uninitialized.ByDifference);
        Assert.Equal(5m, uninitialized.Value);
    }

    [Fact]
    public void Movements_of_one_document_share_one_relay_log_row()
    {
        var branch = Guid.NewGuid();
        var sale = Guid.NewGuid();
        var first = StockCentralRelay.OperationIdFor(branch, StockSourceTypes.Sale, sale, Guid.NewGuid());
        var second = StockCentralRelay.OperationIdFor(branch, StockSourceTypes.Sale, sale, Guid.NewGuid());
        Assert.Equal(first, second);
        Assert.NotEqual(first, StockCentralRelay.OperationIdFor(branch, StockSourceTypes.SaleReturn, sale, Guid.NewGuid()));
        Assert.NotEqual(first, StockCentralRelay.OperationIdFor(Guid.NewGuid(), StockSourceTypes.Sale, sale, Guid.NewGuid()));
        var line = Guid.NewGuid();
        Assert.Equal(
            StockCentralRelay.OperationIdFor(branch, StockSourceTypes.InventoryReconciliation, null, line),
            StockCentralRelay.OperationIdFor(branch, StockSourceTypes.InventoryReconciliation, null, line));
        Assert.Equal("بيع", StockCentralRelay.Label(StockSourceTypes.Sale));
    }
}
