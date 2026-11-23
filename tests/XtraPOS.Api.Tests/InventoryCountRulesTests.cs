using XtraPOS.Api.Endpoints;
using XtraPOS.Api.Inventory;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class InventoryCountRulesTests
{
    [Fact]
    public void Period_switches_at_four_in_the_afternoon()
    {
        Assert.Equal("AM", InventoryCountRules.PeriodType(new TimeOnly(15, 59)));
        Assert.Equal("PM", InventoryCountRules.PeriodType(new TimeOnly(16, 0)));
        Assert.Equal("AM", InventoryCountRules.PeriodType(new TimeOnly(0, 0)));
    }

    [Fact]
    public void Types_are_normalized_and_unknown_types_are_refused()
    {
        Assert.Equal(InventoryTypes.Periodic, InventoryTypes.Normalize(" periodic "));
        Assert.Equal(InventoryTypes.Manual, InventoryTypes.Normalize("MANUAL"));
        Assert.Null(InventoryTypes.Normalize("Daily"));
    }

    [Fact]
    public void Periodic_pages_are_distinct_sorted_and_between_one_and_twelve()
    {
        Assert.Equal(new[] { 1, 2, 11 }, InventoryCountRules.NormalizePages(InventoryTypes.Periodic, new[] { 11, 2, 1, 2 }));
        Assert.Throws<InventoryException>(() => InventoryCountRules.NormalizePages(InventoryTypes.Periodic, Array.Empty<int>()));
        Assert.Throws<InventoryException>(() => InventoryCountRules.NormalizePages(InventoryTypes.Periodic, new[] { 0 }));
        Assert.Throws<InventoryException>(() => InventoryCountRules.NormalizePages(InventoryTypes.Periodic, new[] { 13 }));
        Assert.Empty(InventoryCountRules.NormalizePages(InventoryTypes.Important, new[] { 5 }));
    }

    [Fact]
    public void Same_pages_ignores_order_and_repeats()
    {
        Assert.True(InventoryCountRules.SamePages(new[] { 2, 1 }, new[] { 1, 2, 2 }));
        Assert.False(InventoryCountRules.SamePages(new[] { 1 }, new[] { 1, 2 }));
    }

    [Fact]
    public void Zero_is_a_valid_count_and_negative_is_refused()
    {
        Assert.Equal(0m, InventoryCountRules.ValidateCountedQuantity(0m));
        Assert.Equal(2.1235m, InventoryCountRules.ValidateCountedQuantity(2.12345m));
        Assert.Throws<InventoryException>(() => InventoryCountRules.ValidateCountedQuantity(null));
        Assert.Throws<InventoryException>(() => InventoryCountRules.ValidateCountedQuantity(-1m));
        Assert.Throws<InventoryException>(() => InventoryCountRules.ValidateCountedQuantity(InventoryCountRules.MaxCountedQuantity + 1));
    }

    [Fact]
    public void Known_balance_moves_by_the_difference_at_count_time()
    {
        // Counted 8 while the system said 10: the balance moves by -2, whatever was sold after the count.
        var posting = InventoryCountRules.PlanPosting(8m, 10m, systemQuantityKnown: true);
        Assert.False(posting.Absolute);
        Assert.Equal(-2m, posting.Delta);
    }

    [Fact]
    public void Unknown_balance_is_set_to_the_counted_quantity()
    {
        var posting = InventoryCountRules.PlanPosting(8m, null, systemQuantityKnown: false);
        Assert.True(posting.Absolute);
        Assert.Null(posting.Delta);
    }

    [Fact]
    public void Count_is_blind_for_a_counter_without_review()
    {
        var hidden = InventoryCountRules.Visible(canReview: false, isCounted: true, 8m, 10m, systemQuantityKnown: true);
        Assert.Null(hidden.Expected);
        Assert.Null(hidden.Difference);

        var shown = InventoryCountRules.Visible(canReview: true, isCounted: true, 8m, 10m, systemQuantityKnown: true);
        Assert.Equal(10m, shown.Expected);
        Assert.Equal(-2m, shown.Difference);

        var notCounted = InventoryCountRules.Visible(canReview: true, isCounted: false, null, null, systemQuantityKnown: false);
        Assert.Null(notCounted.Expected);

        var unknown = InventoryCountRules.Visible(canReview: true, isCounted: true, 8m, null, systemQuantityKnown: false);
        Assert.Null(unknown.Expected);
        Assert.Null(unknown.Difference);
    }

    [Fact]
    public void Pages_query_ignores_noise()
    {
        Assert.Equal(new[] { 1, 2 }, InventoryEndpoints.ParsePages("1, 2"));
        Assert.Empty(InventoryEndpoints.ParsePages(null));
        Assert.Equal(new[] { 3, -1 }, InventoryEndpoints.ParsePages("3,x"));
    }
}
