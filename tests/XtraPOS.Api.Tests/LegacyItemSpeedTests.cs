using Xunit;
using XtraPOS.Api.Inventory;

namespace XtraPOS.Api.Tests;

public sealed class LegacyItemSpeedTests
{
    [Theory]
    [InlineData("المقاطعة", "Shop1")]
    [InlineData(" Labtop ", "Shop2")]
    [InlineData("labtop", "Shop2")]
    [InlineData("LAbtop", "Shop2")]
    [InlineData("لابتوب", "Shop2")]
    [InlineData("نابلس", null)]
    [InlineData("", null)]
    public void Maps_the_original_catalog_for_the_two_branches(string branch, string? catalog)
    {
        Assert.Equal(catalog, LegacyItemSpeed.CatalogForBranch(branch));
    }

    [Fact]
    public void Selecting_one_Labtop_assigns_every_branch_with_that_name()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var other = Guid.NewGuid();
        var expanded = CustomInventoryScope.SameNameBranches(
            [(first, "Labtop"), (second, " labtop "), (other, "المقاطعة")],
            [first]);

        Assert.Equal([first, second], expanded);
    }

    [Fact]
    public void A_custom_inventory_matches_a_branch_by_name()
    {
        Assert.Contains("scopeAssignedBranch.BranchName", CustomInventoryScope.AssignedToBranch, StringComparison.Ordinal);
        Assert.Contains("scopeMine.BranchName", CustomInventoryScope.AssignedToBranch, StringComparison.Ordinal);
    }
}
