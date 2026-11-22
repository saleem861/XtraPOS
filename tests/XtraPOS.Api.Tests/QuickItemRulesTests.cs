using XtraPOS.Api.Catalog;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class QuickItemRulesTests
{
    private static readonly Guid Labtop = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    private static QuickItemSet Set(Guid? branch, string? name, int minutes, params string[] numbers) =>
        new(Guid.NewGuid(), branch, name, T0.AddMinutes(minutes), "مدير",
            numbers.Select((number, index) => new QuickItemLine(index + 1, number, null, null)).ToArray());

    [Fact]
    public void Normalize_trims_drops_repeats_and_keeps_order()
    {
        Assert.Equal(new[] { "650", "224", "7" }, QuickItemRules.Normalize(new string?[] { " 650", "", null, "224", "650", "7 " }));
        Assert.Throws<QuickItemException>(() => QuickItemRules.Normalize(Enumerable.Range(1, QuickItemRules.MaxItems + 1).Select(n => n.ToString())));
    }

    [Fact]
    public void Branch_list_wins_and_all_branches_is_the_default()
    {
        var all = Set(null, null, 0, "1", "2");
        var older = Set(null, null, -5, "9");
        Assert.Same(all, QuickItemRules.Effective([older, all], Labtop, "Labtop"));

        var own = Set(Guid.NewGuid(), "Labtop", 1, "650");
        Assert.Same(own, QuickItemRules.Effective([all, own], Labtop, "Labtop"));

        var cleared = Set(Labtop, "Labtop", 2);
        Assert.Same(all, QuickItemRules.Effective([all, own, cleared], Labtop, "Labtop"));

        Assert.Same(all, QuickItemRules.Effective([all, own], Guid.NewGuid(), "Sharbel"));
        Assert.Null(QuickItemRules.Effective([own], Guid.NewGuid(), "Sharbel"));
    }
}
