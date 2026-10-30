using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class Shop1OperationalPlannerTests
{
    private static readonly BranchCatalogItem[] Catalog =
    [
        new("1087", "نكهة"),
        new("1300", "كيلو"),
        new("1350", "نص"),
        new("1360", "وقية"),
        new("2000", "سالب"),
        new("3000", "أساس"),
        new("3001", "عبوة"),
        new("10", "أ"),
        new("20", "ب"),
        new("30", "ج"),
        new("7", "دورة"),
        new("8", "دورة"),
    ];

    private static readonly BranchCatalogUnit[] Units =
    [
        new("1300", "1360", 4),
        new("1350", "1360", 2),
        new("3001", "3000", 2),
        new("10", "20", 2),
        new("20", "30", 3),
        new("7", "8", 2),
        new("8", "7", 2),
    ];

    [Fact]
    public void Plan_TrustsConsistentPackageEchoesAndLeavesConflictsUninitialized()
    {
        var shop = new List<Shop1OperationalRow>
        {
            Row("1087", 3, 1.5m, 10),
            Row("1360", 8, 2, 4),
            Row("1300", 2, 8, 20),
            Row("1350", 8, 5, 12),
            Row("2000", -1, 1, null),
            Row("3000", 5, 3, 9),
            Row("3001", 9, 3, null),
            Row("30", 6, 1, null),
            Row("10", 1, 6, null),
            Row("7", 1, 1, null),
            Row("8", 1, 1, null),
            Row("9000", 4, 1, null),
            Row("", 1, 1, null),
            Row(CentralMasterCatalog.TechnicalItemNumber, 1, 1, null),
        };
        var prices = new List<BranchCatalogPrice>
        {
            new("1087", "Dealer", 10),
            new("1360", "Dealer", 4),
            new("1300", "Dealer", 16),
            new("3000", "Dealer", 9),
        };

        var plan = Shop1OperationalPlanner.Plan(shop, Catalog, Units, prices, ["1087", "1300", "1350", "1360"], DateTime.UtcNow);

        Assert.Equal(1, plan.BlankItemRows);
        Assert.Equal(1, plan.TechnicalRows);
        Assert.Equal(1, plan.ShopOnlyItems);
        var sample1360 = plan.Samples.Single(item => item.ItemNumber == "1360");
        var sample1300 = plan.Samples.Single(item => item.ItemNumber == "1300");
        var sample1350 = plan.Samples.Single(item => item.ItemNumber == "1350");
        Assert.Equal("1360", sample1360.OwnerItemNumber);
        Assert.Equal(Shop1OperationalPlan.Trusted, sample1360.QuantityClassification);
        Assert.Equal(8, sample1360.DerivedQuantity);
        Assert.Equal("1360", sample1300.OwnerItemNumber);
        Assert.Equal(4, sample1300.Factor);
        Assert.Equal(2, sample1300.DerivedQuantity);
        Assert.Equal(4, sample1350.DerivedQuantity);
        Assert.Equal(Shop1OperationalPlan.Trusted, sample1300.QuantityClassification);
        Assert.Contains(plan.Samples, item => item.ItemNumber == "1087" && item.QuantityClassification == Shop1OperationalPlan.Trusted);
        Assert.Equal(1, plan.MultiHopItems);
        Assert.Equal(2, plan.CycleItems);
        Assert.True(plan.InvalidOwners >= 1);
        Assert.True(plan.AmbiguousOwners >= 1);
        Assert.Equal(plan.TrustedOwners, plan.ProposedOpeningMovements);
        Assert.Equal(plan.TrustedOwners, plan.ProposedInitializedBalances);
        Assert.Equal(2, plan.OverridesByPriceType["Dealer"]);
        Assert.Equal(0, plan.NegativePricesExcluded);
    }

    [Fact]
    public void Plan_ExcludesNegativePricesAndKeepsMatchingZerosOutOfOverrides()
    {
        var shop = new List<Shop1OperationalRow> { Row("1087", 1, 0, 0, price0: -2) };
        var prices = new List<BranchCatalogPrice> { new("1087", "Dealer", 0), new("1087", "Price0", 5) };
        var plan = Shop1OperationalPlanner.Plan(
            shop,
            [new("1087", "نكهة")],
            [],
            prices,
            ["1087"],
            DateTime.UtcNow);

        Assert.Equal(0, plan.OverridesByPriceType["Dealer"]);
        Assert.Equal(0, plan.ZeroOverridesByPriceType["Dealer"]);
        Assert.Equal(1, plan.NegativePricesExcluded);
        Assert.Equal(1, plan.ZeroCosts);
        Assert.Equal(1, plan.ProposedUnitCosts);
        Assert.Equal(Shop1OperationalPlan.Trusted, plan.Samples[0].QuantityClassification);
    }

    private static Shop1OperationalRow Row(string number, decimal? quantity, decimal? cost, decimal? dealer, decimal? price0 = null) =>
        new(number, number, quantity, false, cost, false, dealer, null, null, price0, null, null, null, null);
}
