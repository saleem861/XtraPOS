using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class LegacyMasterCatalogCommitTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task MasterCommit_ImportsCanonicalCatalogOnceAndLeavesBranchDataEmpty()
    {
        await CleanupAsync();
        try
        {
            var rows = Sample();
            var first = await LegacyMasterCatalogCommit.CommitAsync(TestDatabase.ConnectionString(), rows, expectation: null, CancellationToken.None);

            Assert.Equal(7, first.CanonicalItems);
            Assert.Equal(7, first.ItemsCreated);
            Assert.Equal(0, first.ItemsAlreadyExisting);
            Assert.Equal(5, first.ActiveItems);
            Assert.Equal(2, first.InactiveItems);
            Assert.Equal(1, first.BranchConsensusCorrections);
            Assert.Equal(1, first.MissingItemNameFallbacks);
            Assert.Equal(1, first.ParentLinksCreated);
            Assert.Equal(1, first.OrphanParents);
            Assert.Equal(1, first.ItemUnitsCreated);
            Assert.Equal(1, first.OrphanInternalUnits);
            Assert.Equal(1, first.SelfUnitReferencesIgnored);
            Assert.Equal(1, first.InvalidNegativePricesSkipped);
            Assert.Equal(0, first.RetailPricesCreatedFromLegacy);
            Assert.Equal(0, first.BranchPricesCreated);
            Assert.Equal(0, first.BranchInventoryCreated);
            Assert.Equal(0, first.StockMovementsCreated);
            Assert.Equal(4, first.PricesCreatedByPriceCode.Values.Sum());
            Assert.Equal(1, first.PricesCreatedByPriceCode["Price0"]);
            Assert.Equal(1, first.PricesCreatedByPriceCode["Dealer"]);
            Assert.Equal(1, first.PricesCreatedByPriceCode["SpecialCustomer"]);
            Assert.Equal(1, first.PricesCreatedByPriceCode["Price2"]);
            Assert.False(first.PricesCreatedByPriceCode.ContainsKey("Retail"));

            Assert.Equal(1, await QueryIntAsync("SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'T5B-A';"));
            Assert.Equal(1, await QueryIntAsync("SELECT CAST(IsProhibited AS int) FROM dbo.Items WHERE ItemNumber = N'T5B-A';"));
            Assert.Equal(0, await QueryIntAsync("SELECT CAST(IsPublishedOnline AS int) FROM dbo.Items WHERE ItemNumber = N'T5B-A';"));
            Assert.Equal("اسم الفرع", await QueryStringAsync("SELECT ItemName FROM dbo.Items WHERE ItemNumber = N'T5B-A';"));
            Assert.Equal(0, await QueryIntAsync("SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'T5B-C';"));
            Assert.Equal("T5B-C", await QueryStringAsync("SELECT ItemName FROM dbo.Items WHERE ItemNumber = N'T5B-C';"));
            Assert.Equal(1, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.Items AS child
                INNER JOIN dbo.Items AS parent ON parent.ItemID = child.ParentItemID
                WHERE child.ItemNumber = N'T5B-A' AND parent.ItemNumber = N'T5B-P';
                """));
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'T5B-C' AND ParentItemID IS NOT NULL;"));
            Assert.Equal(12m, await QueryDecimalAsync(
                "SELECT UnitsPerPackage FROM dbo.ItemUnits WHERE InternalUnitItemNumber = N'T5B-U';"));
            Assert.Equal("حبة", await QueryStringAsync(
                "SELECT InternalUnitItemName FROM dbo.ItemUnits WHERE InternalUnitItemNumber = N'T5B-U';"));
            Assert.Equal(1, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemUnits AS unit
                INNER JOIN dbo.Items AS target ON target.ItemID = unit.InternalUnitItemID
                WHERE unit.InternalUnitItemNumber = N'T5B-U' AND target.ItemNumber = N'T5B-U';
                """));
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.ItemUnits WHERE InternalUnitItemNumber IN (N'T5B-S', N'T5B-GONE');"));
            Assert.Equal(0m, await QueryDecimalAsync(
                """
                SELECT price.Price
                FROM dbo.ItemPrices AS price
                INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
                INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
                WHERE item.ItemNumber = N'T5B-C' AND priceType.PriceCode = N'Price2';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemPrices AS price
                INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
                WHERE item.ItemNumber = N'T5B-Z';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemPrices AS price
                INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%'
                  AND (price.BranchID IS NOT NULL OR price.Price < 0);
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemPrices AS price
                INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
                INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%' AND priceType.PriceCode = N'Retail';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemBranchInventory AS inventory
                INNER JOIN dbo.Items AS item ON item.ItemID = inventory.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.StockMovements AS movement
                INNER JOIN dbo.Items AS item ON item.ItemID = movement.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemPriceHistory AS history
                INNER JOIN dbo.Items AS item ON item.ItemID = history.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%';
                """));

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var catalog = new ItemCatalogService(scope.ServiceProvider.GetRequiredService<BranchDbContext>());
                var item = await catalog.GetItemByNumberAsync("T5B-A", CancellationToken.None);
                Assert.NotNull(item);
                Assert.Equal("اسم الفرع", item.ItemName);
                Assert.True(item.IsActive);
                Assert.True(item.IsProhibited);
                Assert.False(item.IsPublishedOnline);
            }

            await ExecuteAsync("UPDATE dbo.Items SET ItemName = N'تعديل لاحق' WHERE ItemNumber = N'T5B-A';");
            var second = await LegacyMasterCatalogCommit.CommitAsync(TestDatabase.ConnectionString(), rows, expectation: null, CancellationToken.None);
            Assert.Equal(0, second.ItemsCreated);
            Assert.Equal(7, second.ItemsAlreadyExisting);
            Assert.Equal(0, second.ParentLinksCreated);
            Assert.Equal(0, second.ItemUnitsCreated);
            Assert.Equal(0, second.PricesCreatedByPriceCode.Values.Sum());
            Assert.Equal("تعديل لاحق", await QueryStringAsync("SELECT ItemName FROM dbo.Items WHERE ItemNumber = N'T5B-A';"));
            Assert.Equal(7, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber LIKE N'T5B-%';"));
            Assert.Equal(1, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.ItemUnits AS unit
                INNER JOIN dbo.Items AS item ON item.ItemID = unit.ItemID
                WHERE item.ItemNumber LIKE N'T5B-%';
                """));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task MasterCommit_StopsBeforeWritingWhenTheApprovedGateFails()
    {
        await CleanupAsync();
        try
        {
            var rows = new List<LegacyItemObservation>
            {
                new(LegacyCatalogs.Shop1, "T5B-X", "اسم أول"),
                new(LegacyCatalogs.Shop2, "T5B-X", "اسم ثان"),
            };
            var blocked = LegacyMasterCatalogCommit.Prepare(rows, expectation: null);
            Assert.False(blocked.CanCommit);
            Assert.True(blocked.BlockingIssues > 0);
            await Assert.ThrowsAsync<LegacyImportException>(() =>
                LegacyMasterCatalogCommit.CommitAsync(TestDatabase.ConnectionString(), rows, expectation: null, CancellationToken.None));
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber LIKE N'T5B-%';"));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private static List<LegacyItemObservation> Sample() =>
    [
        new(LegacyCatalogs.Central, "T5B-A", "اسم قديم", ParentItemNumber: "T5B-P", UnitsPerPackage: 12m, InternalUnitItemNumber: "T5B-U", InternalUnitItemName: "حبة", DealerPrice: 0m, Price0: 10m, Price1: -3m, IsProhibited: true),
        new(LegacyCatalogs.Shop1, "T5B-A", "اسم الفرع", ParentItemNumber: "T5B-P", UnitsPerPackage: 12m, InternalUnitItemNumber: "T5B-U", InternalUnitItemName: "حبة", Price0: 12m, IsProhibited: true),
        new(LegacyCatalogs.Shop2, "T5B-A", "اسم الفرع", IsProhibited: true),
        new(LegacyCatalogs.Shop1, "T5B-P", "أب"),
        new(LegacyCatalogs.Central, "T5B-U", "وحدة"),
        new(LegacyCatalogs.Central, "T5B-C", "  ", ParentItemNumber: "T5B-MISSING", SpecialCustomerPrice: 5m, Price2: 0m),
        new(LegacyCatalogs.Shop2, "T5B-S", "ذاتي", UnitsPerPackage: 1m, InternalUnitItemNumber: "T5B-S"),
        new(LegacyCatalogs.Shop1, "T5B-O", "يتيم", UnitsPerPackage: 6m, InternalUnitItemNumber: "T5B-GONE", InternalUnitItemName: "غير موجود"),
        new(LegacyCatalogs.Shop1, "T5B-Z", "صفر", Price2: 8m),
    ];

    private static async Task CleanupAsync()
    {
        await ExecuteAsync(
            """
            DELETE history
            FROM dbo.ItemPriceHistory AS history
            INNER JOIN dbo.Items AS item ON item.ItemID = history.ItemID
            WHERE item.ItemNumber LIKE N'T5B-%';
            DELETE price
            FROM dbo.ItemPrices AS price
            INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
            WHERE item.ItemNumber LIKE N'T5B-%';
            DELETE unit
            FROM dbo.ItemUnits AS unit
            INNER JOIN dbo.Items AS item ON item.ItemID = unit.ItemID
            WHERE item.ItemNumber LIKE N'T5B-%';
            UPDATE dbo.Items SET ParentItemID = NULL WHERE ItemNumber LIKE N'T5B-%';
            DELETE FROM dbo.Items WHERE ItemNumber LIKE N'T5B-%';
            """);
    }

    private static async Task<int> QueryIntAsync(string sql) => Convert.ToInt32(await ScalarAsync(sql));

    private static async Task<decimal> QueryDecimalAsync(string sql) => Convert.ToDecimal(await ScalarAsync(sql));

    private static async Task<string> QueryStringAsync(string sql) => (string)(await ScalarAsync(sql) ?? "");

    private static async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
