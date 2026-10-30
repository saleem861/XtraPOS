using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class LegacyMasterDryRunTests
{
    [Fact]
    public void SourceSql_ReadsMasterFieldsOnly()
    {
        var sql = SqlLegacyItemSource.ItemSelectSql;
        Assert.Contains("ItemNumber", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Image", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("varbinary", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quantity", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ItemSpeed", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsHiddenTemporary", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsOrdered", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsAvailable", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Aggregation_UnionsItemNumbersAndKeepsSourceIdentity()
    {
        var report = LegacyMasterCatalogDryRun.Analyze(
        [
            Row(LegacyCatalogs.Central, "A", "مشترك"),
            Row(LegacyCatalogs.Shop1, "A", "مشترك"),
            Row(LegacyCatalogs.Swstore, "B", "مخزن فقط"),
        ]);

        Assert.Equal(2, report.CanonicalItems);
        Assert.Equal(1, report.LegacyRowsReadBySource.Single(source => source.Source == LegacyCatalogs.Central).DistinctItemNumbers);
        Assert.Equal(1, report.LegacyRowsReadBySource.Single(source => source.Source == LegacyCatalogs.Swstore).Rows);
        Assert.Equal(1, report.SwstoreOnlyItems);
        Assert.Equal(0, report.AllSourcesItems);
        Assert.Equal(0, report.CentralOnlyItems);
        Assert.Equal(0, report.PlannedItemBranchInventory);
        Assert.Equal(0, report.PlannedOpeningBalances);
    }

    [Fact]
    public void Prices_KeepRetailEmptyAndHoldBranchDifferences()
    {
        var report = LegacyMasterCatalogDryRun.Analyze(
        [
            Row(LegacyCatalogs.Central, "P", "سعر", price0: 10m, dealer: -2m),
            Row(LegacyCatalogs.Shop1, "P", "سعر", price0: 12m),
            Row(LegacyCatalogs.TrialBranch, "P", "سعر", price0: 99m),
        ]);

        Assert.Equal(0, report.PlannedRetailPrices);
        Assert.Equal(0, report.PlannedBranchPriceInserts);
        Assert.Contains(report.DefaultPricesToCreate, price => price.PriceCode == "Price0" && price.Count == 1);
        Assert.DoesNotContain(report.DefaultPricesToCreate, price => price.PriceCode == "Dealer");
        Assert.Equal(1, report.InvalidNegativePrices);
        Assert.Contains(report.PotentialBranchPriceOverrides, price =>
            price.LegacyBranch == LegacyCatalogs.Shop1 && price.PriceCode == "Price0" && price.Count == 1);
        Assert.DoesNotContain(report.PotentialBranchPriceOverrides, price => price.LegacyBranch == LegacyCatalogs.TrialBranch);
    }

    [Fact]
    public void BranchConsensus_IsAutoResolved()
    {
        var report = LegacyMasterCatalogDryRun.Analyze(
        [
            Row(LegacyCatalogs.Central, "1087", "اسم قديم"),
            Row(LegacyCatalogs.Shop1, "1087", "اسم الفرع"),
            Row(LegacyCatalogs.Shop2, "1087", "اسم الفرع"),
        ]);

        Assert.Equal("اسم الفرع", report.NameCorrections.Single().CanonicalItemName);
        Assert.Equal("اسم قديم", report.NameCorrections.Single().CentralItemName);
        Assert.Equal(0, report.BlockingIssues);
        Assert.True(report.AutoResolvedIssues >= 1);
        Assert.Equal(LegacyMasterCommitReadiness.Ready, report.Readiness);
    }

    [Fact]
    public void UnresolvedBranchConflict_IsBlocking()
    {
        var report = LegacyMasterCatalogDryRun.Analyze(
        [
            Row(LegacyCatalogs.Shop1, "50", "اسم أول"),
            Row(LegacyCatalogs.Shop2, "50", "اسم ثان"),
        ]);

        Assert.True(report.BlockingIssues >= 1);
        Assert.Equal(LegacyMasterCommitReadiness.NotReady, report.Readiness);
        Assert.Equal("50", report.BranchOnlySamples.Single().CanonicalItemName);
    }

    [Fact]
    public void BlankItemNumber_IsBlocking()
    {
        var report = LegacyMasterCatalogDryRun.Analyze(
        [
            Row(LegacyCatalogs.Central, "  ", "بدون رقم"),
            Row(LegacyCatalogs.Central, "1", "صالح"),
        ]);

        Assert.Equal(1, report.CanonicalItems);
        Assert.Equal(1, report.LegacyRowsReadBySource.Single(source => source.Source == LegacyCatalogs.Central).BlankItemNumbers);
        Assert.True(report.BlockingIssues >= 1);
    }

    private static LegacyItemObservation Row(string source, string number, string? name, decimal? price0 = null, decimal? dealer = null) =>
        new(source, number, name, Price0: price0, DealerPrice: dealer);
}
