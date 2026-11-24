using XtraPOS.Api.Inventory;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class InventoryReviewRulesTests
{
    private static readonly Guid Same = Guid.NewGuid();
    private static readonly Guid Different = Guid.NewGuid();
    private static readonly Guid Unknown = Guid.NewGuid();
    private static readonly Guid NotCounted = Guid.NewGuid();

    private static readonly AuditCandidate[] Lines =
    [
        new(Same, 5m, 5m, true, true),
        new(Different, 3m, 7m, true, true),
        new(Unknown, 2m, null, false, true),
        new(NotCounted, null, 4m, true, false),
    ];

    [Fact]
    public void Audit_all_takes_every_counted_line_only()
    {
        var chosen = InventoryReviewRules.SelectAuditLines(InventoryAuditModes.All, Lines, null);
        Assert.Equal(new[] { Same, Different, Unknown }, chosen);
    }

    [Fact]
    public void Audit_differences_takes_mismatches_and_unknown_system_quantities()
    {
        var chosen = InventoryReviewRules.SelectAuditLines(InventoryAuditModes.Differences, Lines, null);
        Assert.Equal(new[] { Different, Unknown }, chosen);
    }

    [Fact]
    public void Audit_manual_keeps_only_picked_counted_lines()
    {
        var chosen = InventoryReviewRules.SelectAuditLines(InventoryAuditModes.Manual, Lines, [Same, NotCounted, Same]);
        Assert.Equal(new[] { Same }, chosen);
        Assert.Throws<InventoryException>(() => InventoryReviewRules.SelectAuditLines(InventoryAuditModes.Manual, Lines, [NotCounted]));
    }

    [Fact]
    public void Audit_differences_refuses_a_count_without_differences()
    {
        var exact = new[] { new AuditCandidate(Same, 5m, 5m, true, true) };
        var error = Assert.Throws<InventoryException>(() => InventoryReviewRules.SelectAuditLines(InventoryAuditModes.Differences, exact, null));
        Assert.Contains("لا توجد فروقات", error.Message);
    }

    [Fact]
    public void Audit_modes_are_normalized()
    {
        Assert.Equal(InventoryAuditModes.Differences, InventoryAuditModes.Normalize(" Differences "));
        Assert.Null(InventoryAuditModes.Normalize("Random"));
    }

    [Fact]
    public void Audit_difference_is_supervisor_minus_employee()
    {
        Assert.Equal(2m, InventoryReviewRules.AuditDifference(7m, 5m));
        Assert.Equal(-1m, InventoryReviewRules.AuditDifference(4m, 5m));
        Assert.Null(InventoryReviewRules.AuditDifference(null, 5m));
    }

    [Fact]
    public void Correction_reason_is_required_and_trimmed()
    {
        Assert.Equal("تالف", InventoryReviewRules.RequireReason("  تالف  "));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.RequireReason(" "));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.RequireReason(new string('x', 501)));
    }

    [Fact]
    public void Classification_page_zero_means_important_without_a_page()
    {
        Assert.Equal((true, (byte?)null, (int?)7), InventoryReviewRules.NormalizeClassification(false, 0, 7));
        Assert.Equal((false, (byte?)12, (int?)null), InventoryReviewRules.NormalizeClassification(false, 12, null));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.NormalizeClassification(false, 13, null));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.NormalizeClassification(false, null, 0));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.NormalizeClassification(false, null, 366));
    }

    [Fact]
    public void Report_range_defaults_to_the_last_seven_days_and_is_capped()
    {
        var today = new DateOnly(2026, 11, 24);
        Assert.Equal((new DateOnly(2026, 11, 18), today), InventoryReviewRules.ReportRange(null, null, today));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.ReportRange(today, today.AddDays(-1), today));
        Assert.Throws<InventoryException>(() => InventoryReviewRules.ReportRange(today.AddDays(-92), today, today));
        Assert.Equal((today.AddDays(-91), today), InventoryReviewRules.ReportRange(today.AddDays(-91), today, today));
    }
}
