using XtraPOS.Api.Inventory;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class ShortageRulesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Need_and_surplus_are_per_branch_then_balanced_before_buying()
    {
        // A: target ceil(2 × 7) = 14, has 4 → needs 10. B: target ceil(0.5 × 7) = 4, has 10 → surplus 6.
        var result = ShortageRules.Calculate([new(A, 4m, 2m), new(B, 10m, 0.5m)], 7);
        Assert.Equal(10m, result.TotalNeed);
        Assert.Equal(6m, result.TotalSurplus);
        Assert.Equal(4m, result.Required);
        Assert.Equal(6m, result.Balance);
        Assert.True(result.IsShort);
        Assert.Equal(14m, result.Branches[0].Target);
    }

    [Fact]
    public void Target_rounds_up_and_a_full_branch_is_not_short()
    {
        var result = ShortageRules.Calculate([new(A, 3m, 0.3m)], 10);
        Assert.Equal(3m, result.Branches[0].Target);
        Assert.False(result.IsShort);
    }

    [Fact]
    public void Missing_rate_or_quantity_counts_as_zero_and_is_flagged()
    {
        var result = ShortageRules.Calculate([new(A, null, 1m), new(B, 5m, null)], 3);
        Assert.True(result.MissingQuantity);
        Assert.True(result.MissingRate);
        Assert.Equal(3m, result.TotalNeed);
        Assert.Equal(5m, result.TotalSurplus);
        Assert.Equal(0m, result.Required);
        Assert.Equal(3m, result.Balance);
    }

    [Fact]
    public void A_decision_needs_an_action_and_delete_stands_alone()
    {
        Assert.Throws<InventoryException>(() => ShortageRules.ValidateDecision(new(null, null, null, false, false, null)));
        Assert.Throws<InventoryException>(() => ShortageRules.ValidateDecision(new(5m, null, null, true, false, null)));
        Assert.Throws<InventoryException>(() => ShortageRules.ValidateDecision(new(-1m, null, null, false, false, null)));
        var clean = ShortageRules.ValidateDecision(new(5m, 0m, 2m, false, false, "  ملاحظة  "));
        Assert.Equal(5m, clean.Purchase);
        Assert.Null(clean.Warehouse);
        Assert.Equal("ملاحظة", clean.Note);
        Assert.True(ShortageRules.ValidateDecision(new(null, null, null, false, true, null)).AlreadySupplied);
    }

    [Fact]
    public void Finalizing_turns_each_decision_into_follow_up_tasks()
    {
        var tasks = ShortageRules.TasksFor(new(5m, 2m, 3m, false, false, null));
        Assert.Equal(new[] { ShortageTaskTypes.Purchase, ShortageTaskTypes.Warehouse, ShortageTaskTypes.Balance }, tasks.Select(task => task.Type));
        Assert.Equal(ShortageTaskTypes.Delete, Assert.Single(ShortageRules.TasksFor(new(null, null, null, true, false, null))).Type);
        Assert.Empty(ShortageRules.TasksFor(new(null, null, null, false, true, null)));
    }

    [Fact]
    public void Moving_purchase_to_the_warehouse_takes_the_oldest_tasks_first()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var reduced = ShortageRules.ReducePurchases([(first, 3m), (second, 5m)], 4m);
        Assert.Equal(new[] { (first, 0m), (second, 4m) }, reduced);
        Assert.Throws<InventoryException>(() => ShortageRules.ReducePurchases([(first, 3m)], 4m));
        Assert.Throws<InventoryException>(() => ShortageRules.ReducePurchases([(first, 3m)], 0m));
    }

    [Fact]
    public void Audit_score_is_out_of_ten()
    {
        Assert.Equal(10m, ShortageRules.AuditScore([(5m, 5m), (2m, 2m)]));
        Assert.Equal(6.7m, ShortageRules.AuditScore([(5m, 5m), (2m, 2m), (1m, 0m)]));
        Assert.Equal(0m, ShortageRules.AuditScore([]));
    }

    [Fact]
    public void Supply_days_and_ranges_are_validated()
    {
        Assert.Equal(7, ShortageRules.RequireSupplyDays(7));
        Assert.Throws<InventoryException>(() => ShortageRules.RequireSupplyDays(0));
        Assert.Throws<InventoryException>(() => ShortageRules.RequireSupplyDays(366));
        var today = new DateOnly(2026, 11, 24);
        Assert.Equal((new DateOnly(2026, 11, 10), today), ShortageRules.Range(null, null, today, 15));
    }

    [Fact]
    public void Custom_is_a_count_type()
    {
        Assert.Equal(InventoryTypes.Custom, InventoryTypes.Normalize("custom"));
        Assert.Equal("جرد مخصص", InventoryTypes.Label(InventoryTypes.Custom));
        Assert.Empty(InventoryCountRules.NormalizePages(InventoryTypes.Custom, [1, 2]));
    }

    [Fact]
    public void A_substitute_goes_only_with_a_delete_request()
    {
        var delete = ShortageRules.ValidateDecision(new ShortageDecision(null, null, null, true, false, null, " 650 "));
        Assert.Equal("650", delete.Substitute);
        Assert.Throws<InventoryException>(() => ShortageRules.ValidateDecision(new ShortageDecision(5m, null, null, false, false, null, "650")));
        Assert.Null(ShortageRules.ValidateDecision(new ShortageDecision(null, null, null, true, false, null, "  ")).Substitute);
    }

    [Fact]
    public void Item_search_text_is_matched_literally_inside_like()
    {
        Assert.Equal("فحم", ShortageRules.EscapeLike("فحم"));
        Assert.Equal("50!%", ShortageRules.EscapeLike("50%"));
        Assert.Equal("a!_b![c!!", ShortageRules.EscapeLike("a_b[c!"));
    }
}
