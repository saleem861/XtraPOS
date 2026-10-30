using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class Shop1BranchInventoryPlanTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static readonly Shop1BranchProposal Proposal = new(22, "Shop1", "22", "Retail", 1, "كاونتر 1");

    [Fact]
    public void MatchedShop1Item_PlansInventoryOpeningBalanceAndBranchFields()
    {
        var report = Shop1BranchInventoryPlan.Analyze(
            [
                Item("A", "شاي", quantity: 4.5m, cost: 2m, speed: 1.25m, available: true, dealer: 9m, price0: 10m),
                Item("B", "صفر", quantity: 0m, cost: 0m, speed: 0m, available: false, price0: 8m),
                Item("C", "سالب", quantity: -3m, cost: 1.5m, speed: -1m, available: false, dealer: -2m, price0: 8m),
            ],
            [
                Master("A", true, ("Dealer", 8m), ("Price0", 10m)),
                Master("B", true, ("Price0", 8m)),
                Master("C", true, ("Price0", 7m)),
            ],
            Today,
            Proposal);

        Assert.Equal(3, report.MatchedItems);
        Assert.Equal(0, report.MissingMasterItems);
        Assert.Equal(0, report.DuplicateMatches);
        Assert.Equal(3, report.PlannedItemBranchInventory);
        Assert.Equal(4.5m, report.SumPlannedOnHandQuantity - report.SumNegativeQuantity - 0m);
        Assert.Equal(-3m, report.NegativeQuantityItems.Single().Quantity);
        Assert.Equal("C", report.NegativeQuantityItems.Single().ItemNumber);
        Assert.Equal(1, report.FractionalQuantities);
        Assert.Equal(2, report.PlannedOpeningBalances);
        Assert.Contains("Do not create a zero OpeningBalance", report.ZeroQuantityRecommendation, StringComparison.Ordinal);
        Assert.Equal(2, report.PositiveCosts);
        Assert.Equal(1, report.ZeroCosts);
        Assert.Equal(1.25m, report.TopDailySalesRates[0].DailySalesRate);
        Assert.Equal(1, report.AvailableWithPositiveStock);
        Assert.Equal(2, report.UnavailableWithZeroOrNegativeStock);
        Assert.Equal(1, report.PriceOverrides.Single(price => price.PriceCode == "Dealer").DifferentFromDefault);
        Assert.Equal(1, report.PriceOverrides.Single(price => price.PriceCode == "Price0").DifferentFromDefault);
        Assert.Equal(0, report.PriceOverrides.Single(price => price.PriceCode == "Price0").MissingDefault);
        Assert.Equal(1, report.NegativeBranchPricesSkipped);
        Assert.Equal(0, report.RetailOverrides);
        Assert.Equal(Shop1BranchReadiness.Ready, report.Readiness);
        Assert.Equal(4.5m * 2m + -3m * 1.5m, report.InventoryValue);
    }

    [Fact]
    public void MissingOrInactiveMaster_IsBlocking()
    {
        var report = Shop1BranchInventoryPlan.Analyze(
            [
                Item("GONE", "غير موجود", quantity: 1m),
                Item("OLD", "غير نشط", quantity: 2m),
                Item("A", "نشط", quantity: 1m),
                Item("A", "مكرر", quantity: 1m),
            ],
            [
                Master("OLD", false),
                Master("A", true),
            ],
            Today,
            Proposal);

        Assert.Equal(1, report.MatchedItems);
        Assert.Equal(1, report.MissingMasterItems);
        Assert.Equal(1, report.DuplicateMatches);
        Assert.Equal(1, report.InactiveMasterItems);
        Assert.Contains("OLD", report.InactiveMasterItemNumbers);
        Assert.Equal(Shop1BranchReadiness.NotReady, report.Readiness);
    }

    [Fact]
    public void BranchPrice_IsPlannedOnlyWhenShop1DiffersFromTheDefault()
    {
        var report = Shop1BranchInventoryPlan.Analyze(
            [Item("A", "سعر", quantity: 1m, dealer: 5m, price0: 0m, price1: 4m)],
            [Master("A", true, ("Dealer", 5m), ("Price1", 4m))],
            Today,
            Proposal);

        Assert.Equal(0, report.PriceOverrides.Single(price => price.PriceCode == "Dealer").DifferentFromDefault);
        Assert.Equal(1, report.PriceOverrides.Single(price => price.PriceCode == "Price0").MissingDefault);
        Assert.Equal(0, report.PriceOverrides.Single(price => price.PriceCode == "Price1").DifferentFromDefault);
        Assert.Equal(2, report.IdenticalBranchPricesSkipped);
        Assert.Equal(0, report.RetailOverrides);
    }

    [Fact]
    public void NullCostAndNegativeQuantity_DoNotBlock()
    {
        var report = Shop1BranchInventoryPlan.Analyze(
            [Item("A", "بدون كلفة", quantity: -1m, cost: null, available: true)],
            [Master("A", true)],
            Today,
            Proposal);

        Assert.Equal(1, report.NullCosts);
        Assert.Equal(1, report.NegativeQuantities);
        Assert.Equal(1, report.PlannedOpeningBalances);
        Assert.Null(report.InventoryValue);
        Assert.Equal(1, report.Available);
        Assert.Equal(0, report.AvailableWithPositiveStock);
        Assert.Equal(0, report.UnavailableWithZeroOrNegativeStock);
        Assert.Equal(Shop1BranchReadiness.Ready, report.Readiness);
    }

    [Fact]
    public void RestockDates_StayInTheSourceBuckets()
    {
        var report = Shop1BranchInventoryPlan.Analyze(
            [
                Item("A", "حديث", quantity: 1m, restock: new DateTime(2026, 9, 1)),
                Item("B", "قديم", quantity: 1m, restock: new DateTime(2020, 1, 1)),
                Item("C", "مستقبل", quantity: 1m, restock: new DateTime(2026, 12, 1)),
                Item("D", "فارغ", quantity: 1m),
            ],
            [
                Master("A", true),
                Master("B", true),
                Master("C", true),
                Master("D", true),
            ],
            Today,
            Proposal);

        Assert.Equal(1, report.RestockLast90Days);
        Assert.Equal(1, report.RestockOlderThan365Days);
        Assert.Equal(1, report.RestockFuture);
        Assert.Equal(1, report.RestockNull);
    }

    [Fact]
    public void DryRunCommand_DoesNotWriteBranchData()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "XtraPOS.Api", "Import", "Shop1InventoryDryRunCommand.cs"));
        Assert.Contains("SELECT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MERGE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ItemBranchInventory", source.Split("ReadFingerprintAsync")[0], StringComparison.Ordinal);
    }

    private static Shop1OperationalItem Item(
        string number,
        string name,
        decimal? quantity,
        decimal? cost = null,
        decimal? speed = null,
        bool? available = null,
        decimal? dealer = null,
        decimal? price0 = null,
        decimal? price1 = null,
        DateTime? restock = null) =>
        new(number, name, quantity, cost, speed, restock, available, dealer, Price0: price0, Price1: price1);

    private static MasterItemMatch Master(string number, bool active, params (string Code, decimal Price)[] prices) =>
        new(number, active, prices.ToDictionary(price => price.Code, price => price.Price, StringComparer.Ordinal));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XtraPOS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the XtraPOS solution.");
    }
}
