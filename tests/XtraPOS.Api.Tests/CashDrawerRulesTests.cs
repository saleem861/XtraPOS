using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

public class CashDrawerRulesTests
{
    [Theory]
    [InlineData(100, 100, "Matched", false)]
    [InlineData(90, 100, "Shortage", true)]
    [InlineData(110, 100, "Surplus", false)]
    public void Classifies_the_drawer_difference(decimal counted, decimal expected, string status, bool charged)
    {
        var difference = CashDrawerRules.Difference(counted, expected);
        var actual = CashDrawerRules.Status(difference);
        Assert.Equal(status, actual);
        Assert.Equal(status != "Matched", CashDrawerRules.NeedsJustification(actual));
        Assert.Equal(charged, CashDrawerRules.ChargesEmployee(actual));
    }

    [Fact]
    public void Waiver_applies_only_to_a_shortage()
    {
        Assert.True(CashDrawerRules.DecisionMatches("Shortage", CashDrawerRules.Waive));
        Assert.True(CashDrawerRules.DecisionMatches("Shortage", CashDrawerRules.KeepShortage));
        Assert.True(CashDrawerRules.DecisionMatches("Surplus", CashDrawerRules.KeepSurplus));
        Assert.False(CashDrawerRules.DecisionMatches("Surplus", CashDrawerRules.Waive));
        Assert.False(CashDrawerRules.DecisionMatches("Matched", CashDrawerRules.KeepShortage));
    }
}
