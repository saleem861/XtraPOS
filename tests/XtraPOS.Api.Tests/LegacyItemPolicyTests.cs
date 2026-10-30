using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class LegacyItemPolicyTests
{
    [Fact]
    public void BranchConsensus_ReplacesStaleCentralName()
    {
        var item = Plan(
            Row(LegacyCatalogs.Central, "1087", "تونز ستاتي"),
            Row(LegacyCatalogs.Shop1, "1087", "نكهة لومي"),
            Row(LegacyCatalogs.Shop2, "1087", "نكهة لومي"),
            Row(LegacyCatalogs.Swstore, "1087", "نكهة لومي")).Items.Single();

        Assert.Equal("نكهة لومي", item.ItemName);
        Assert.Contains(Plan(
            Row(LegacyCatalogs.Central, "1087", "تونز ستاتي"),
            Row(LegacyCatalogs.Shop1, "1087", "نكهة لومي"),
            Row(LegacyCatalogs.Shop2, "1087", "نكهة لومي"),
            Row(LegacyCatalogs.Swstore, "1087", "نكهة لومي")).Issues, issue =>
            issue.Category == LegacyImportCategories.BranchConsensusCorrection && issue.Field == "ItemName");
    }

    [Fact]
    public void MatchingCentralAndBranches_KeepTheSharedValue()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "10", "شاي"),
            Row(LegacyCatalogs.Shop1, "10", "شاي"),
            Row(LegacyCatalogs.Shop2, "10", "شاي"));

        Assert.Equal("شاي", report.Items.Single().ItemName);
        Assert.DoesNotContain(report.Issues, issue => issue.Category == LegacyImportCategories.BranchConsensusCorrection);
        Assert.DoesNotContain(report.Issues, issue => issue.Category == LegacyImportCategories.MasterFieldConflict);
    }

    [Fact]
    public void CentralOnlyItem_UsesCentralDescription()
    {
        var report = Plan(Row(LegacyCatalogs.Central, "C-1", "صنف مركزي", productType: "simple"));

        Assert.Equal("صنف مركزي", report.Items.Single().ItemName);
        Assert.Equal("simple", report.Items.Single().ProductType);
        Assert.Equal(1, report.CentralOnlyItems);
        Assert.Equal(0, report.BranchOnlyItems);
    }

    [Fact]
    public void BranchOnlyItem_UsesTheUnambiguousBranchValue()
    {
        var report = Plan(Row(LegacyCatalogs.Swstore, "B-1", "صنف مخزن"));

        Assert.Equal("صنف مخزن", report.Items.Single().ItemName);
        Assert.Equal(1, report.BranchOnlyItems);
        Assert.Equal(0, report.CentralOnlyItems);
        Assert.DoesNotContain(report.Issues, issue => issue.Category == LegacyImportCategories.MasterFieldConflict);
    }

    [Fact]
    public void ConflictingBranches_UseCentralAndReportTheConflict()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "50", "اسم مركزي"),
            Row(LegacyCatalogs.Shop1, "50", "اسم أول"),
            Row(LegacyCatalogs.Shop2, "50", "اسم ثان"));

        Assert.Equal("اسم مركزي", report.Items.Single().ItemName);
        Assert.Equal(1, report.MasterFieldConflicts);
        Assert.Equal(0, report.BranchConsensusCorrections);
    }

    [Fact]
    public void ConflictingBranches_WithoutCentral_DoNotPickAWinner()
    {
        var report = Plan(
            Row(LegacyCatalogs.Shop1, "51", "اسم أول"),
            Row(LegacyCatalogs.Shop2, "51", "اسم ثان"));

        Assert.Equal("51", report.Items.Single().ItemName);
        Assert.Equal(1, report.MasterFieldConflicts);
        Assert.Equal(1, report.MissingItemNameFallbacks);
    }

    [Fact]
    public void BlankSource_DoesNotReplaceAUsefulValue()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "60", "  "),
            Row(LegacyCatalogs.Shop1, "60", "اسم مفيد"),
            Row(LegacyCatalogs.Shop2, "60", null));

        Assert.Equal("اسم مفيد", report.Items.Single().ItemName);
        Assert.Equal(0, report.MissingItemNameFallbacks);
        Assert.Equal(0, report.MasterFieldConflicts);
    }

    [Fact]
    public void MissingItemName_FallsBackToItemNumber()
    {
        var report = Plan(Row(LegacyCatalogs.Central, "77", "   "));

        Assert.Equal("77", report.Items.Single().ItemName);
        Assert.Equal(1, report.MissingItemNameFallbacks);
        Assert.False(report.Items.Single().IsPublishedOnline);
    }

    [Fact]
    public void SelfInternalUnit_CreatesNoCandidate()
    {
        var item = Plan(Row(LegacyCatalogs.Central, "U-1", "صنف", units: 12, unitNumber: "U-1", unitName: "حبة")).Items.Single();

        Assert.Null(item.Unit);
    }

    [Fact]
    public void ValidInternalUnit_PreservesCountAndHistoricalName()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "BOX", "صندوق", units: 24, unitNumber: "EACH", unitName: "علبة قديمة"),
            Row(LegacyCatalogs.Central, "EACH", "علبة حالية"));

        var box = report.Items.Single(item => item.ItemNumber == "BOX");
        Assert.NotNull(box.Unit);
        Assert.Equal(24m, box.Unit!.UnitsPerPackage);
        Assert.Equal("EACH", box.Unit.InternalUnitItemNumber);
        Assert.Equal("علبة قديمة", box.Unit.InternalUnitItemName);
        Assert.Equal(1, report.ValidUnitLinks);
        Assert.Null(report.Items.Single(item => item.ItemNumber == "EACH").Unit);
    }

    [Fact]
    public void OrphanInternalUnit_ImportsTheItemWithoutAPlaceholder()
    {
        var report = Plan(Row(LegacyCatalogs.Central, "BOX", "صندوق", units: 6, unitNumber: "MISSING", unitName: "داخلية"));

        Assert.Single(report.Items);
        Assert.Null(report.Items[0].Unit);
        Assert.Equal(1, report.OrphanInternalUnits);
        Assert.DoesNotContain(report.Items, item => item.ItemNumber == "MISSING");
    }

    [Fact]
    public void MultiLevelUnit_StaysDirect()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "A", "كبير", units: 12, unitNumber: "B", unitName: "وسط"),
            Row(LegacyCatalogs.Central, "B", "وسط", units: 6, unitNumber: "C", unitName: "صغير"),
            Row(LegacyCatalogs.Central, "C", "صغير"));

        Assert.Equal("B", report.Items.Single(item => item.ItemNumber == "A").Unit!.InternalUnitItemNumber);
        Assert.Equal("C", report.Items.Single(item => item.ItemNumber == "B").Unit!.InternalUnitItemNumber);
        Assert.Equal(2, report.ValidUnitLinks);
    }

    [Fact]
    public void ValidParent_ResolvesAndKeepsProductType()
    {
        var report = Plan(
            Row(LegacyCatalogs.Central, "PARENT", "أب", productType: "configurable"),
            Row(LegacyCatalogs.Central, "CHILD", "ابن", productType: "virtual", parent: "PARENT"));

        var child = report.Items.Single(item => item.ItemNumber == "CHILD");
        Assert.True(child.ParentResolved);
        Assert.Equal("PARENT", child.ParentItemNumber);
        Assert.Equal("virtual", child.ProductType);
        Assert.Equal(1, report.ValidParentLinks);
    }

    [Fact]
    public void OrphanParent_LeavesTheLinkEmptyAndCreatesNoPlaceholder()
    {
        var report = Plan(Row(LegacyCatalogs.Central, "CHILD", "ابن", parent: "NO-PARENT"));

        Assert.Single(report.Items);
        Assert.False(report.Items[0].ParentResolved);
        Assert.Null(report.Items[0].ParentItemNumber);
        Assert.Equal(1, report.OrphanParents);
        Assert.DoesNotContain(report.Items, item => item.ItemNumber == "NO-PARENT");
    }

    [Fact]
    public void Prices_MapLegacyCodesAndNeverCreateRetail()
    {
        var item = Plan(Row(
            LegacyCatalogs.Central,
            "P-1",
            "مسعر",
            dealer: 1m,
            major: 2m,
            special: 3m,
            price0: 4m,
            price1: 5m,
            price2: 6m,
            price3: 7m)).Items.Single();

        Assert.Equal(
            ["Dealer", "MajorCustomer", "SpecialCustomer", "Price0", "Price1", "Price2", "Price3"],
            item.Prices.Select(price => price.PriceCode).ToArray());
        Assert.DoesNotContain(item.Prices, price => price.PriceCode == "Retail");
        Assert.All(item.Prices, price => Assert.Null(price.BranchCatalog));
    }

    [Fact]
    public void BranchPrice_RequiresAVerifiedRealBranch()
    {
        var item = Plan(
            Row(LegacyCatalogs.Central, "P-2", "سعر", price0: 10m),
            Row(LegacyCatalogs.Shop1, "P-2", "سعر", price0: 11m),
            Row(LegacyCatalogs.Shop2, "P-2", "سعر", price0: 10m),
            Row(LegacyCatalogs.TrialBranch, "P-2", "سعر", price0: 99m)).Items.Single();

        Assert.Equal(2, item.Prices.Count);
        Assert.Contains(item.Prices, price => price.PriceCode == "Price0" && price.Price == 10m && price.BranchCatalog is null);
        Assert.Contains(item.Prices, price => price.PriceCode == "Price0" && price.Price == 11m && price.BranchCatalog == LegacyCatalogs.Shop1);
        Assert.DoesNotContain(item.Prices, price => price.BranchCatalog == LegacyCatalogs.TrialBranch);
    }

    [Fact]
    public void NegativePrice_IsReportedAndDoesNotBecomeZero()
    {
        var report = Plan(Row(LegacyCatalogs.Central, "P-3", "سالب", price0: -4m, dealer: 0m));

        Assert.Single(report.Items);
        Assert.Equal(1, report.InvalidNegativePrices);
        Assert.DoesNotContain(report.Items[0].Prices, price => price.Price < 0 || (price.PriceCode == "Price0"));
        Assert.Contains(report.Items[0].Prices, price => price.PriceCode == "Dealer" && price.Price == 0m);
    }

    [Fact]
    public void NegativeOpeningQuantity_IsPreserved()
    {
        Assert.Equal(-3m, LegacyOpeningBalancePolicy.QuantityDelta(-3m));
    }

    private static LegacyItemImportReport Plan(params LegacyItemObservation[] rows) =>
        new LegacyItemImportService().DryRun(rows);

    private static LegacyItemObservation Row(
        string source,
        string number,
        string? name,
        string? productType = null,
        string? parent = null,
        decimal? units = null,
        string? unitNumber = null,
        string? unitName = null,
        decimal? dealer = null,
        decimal? major = null,
        decimal? special = null,
        decimal? price0 = null,
        decimal? price1 = null,
        decimal? price2 = null,
        decimal? price3 = null) =>
        new(
            source,
            number,
            name,
            ProductType: productType,
            ParentItemNumber: parent,
            UnitsPerPackage: units,
            InternalUnitItemNumber: unitNumber,
            InternalUnitItemName: unitName,
            DealerPrice: dealer,
            MajorCustomerPrice: major,
            SpecialCustomerPrice: special,
            Price0: price0,
            Price1: price1,
            Price2: price2,
            Price3: price3);
}
