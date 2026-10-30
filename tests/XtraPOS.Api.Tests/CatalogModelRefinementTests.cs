using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CatalogModelRefinementTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task Migration005_MovesAvailabilityToBranchInventoryAndKeepsSeeds()
    {
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation';"));
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement';"));
        Assert.Equal(1, await QueryIntAsync(
            """
            SELECT CASE
                WHEN (
                    SELECT AppliedAt FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement'
                ) >= (
                    SELECT AppliedAt FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation'
                ) THEN 1 ELSE 0 END;
            """));
        Assert.Equal(0, await ColumnCountAsync("Items", "IsAvailable"));
        Assert.Equal(1, await ColumnCountAsync("Items", "IsActive"));
        Assert.Equal(1, await ColumnCountAsync("ItemBranchInventory", "IsAvailable"));
        Assert.Equal("bit", await QueryStringAsync(
            """
            SELECT TYPE_NAME(c.system_type_id)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            WHERE t.name = N'ItemBranchInventory' AND c.name = N'IsAvailable';
            """));
        Assert.Equal(8, await QueryIntAsync("SELECT COUNT(*) FROM dbo.PriceTypes;"));
        Assert.Equal(16, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovementTypes;"));

        var sql = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(), "src", "XtraPOS.Api", "Data", "Migrations", "005_CatalogModelRefinement.sql"));
        Assert.DoesNotContain("saleemPOSDB", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO dbo.Items", sql, StringComparison.OrdinalIgnoreCase);

        await ExecuteAsync("DELETE FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement';");
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await runner.ApplyPendingAsync(CancellationToken.None);
            await runner.ApplyPendingAsync(CancellationToken.None);
        }
        finally
        {
            await ExecuteAsync(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement')
                BEGIN
                    INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                    VALUES (N'005_CatalogModelRefinement', N'Catalog model refinement', N'test', SYSUTCDATETIME());
                END
                """);
        }

        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement';"));
        Assert.Equal(0, await ColumnCountAsync("Items", "IsAvailable"));
        Assert.Equal(1, await ColumnCountAsync("ItemBranchInventory", "IsAvailable"));
        Assert.Equal(8, await QueryIntAsync("SELECT COUNT(*) FROM dbo.PriceTypes;"));
        Assert.Equal(16, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovementTypes;"));
    }

    [Fact]
    public void DryRun_DoesNotWriteCatalogOrLedgerRows()
    {
        var before = Counts();
        var service = new LegacyItemImportService();
        var report = service.DryRun(
        [
            new LegacyItemObservation(LegacyCatalogs.Central, "DRY-1", "صنف جاف", DealerPrice: 5m, InternalUnitItemNumber: "DRY-1"),
        ]);

        Assert.Equal(1, report.CanonicalItems);
        var master = LegacyMasterCatalogDryRun.Analyze(
            [new LegacyItemObservation(LegacyCatalogs.Central, "DRY-1", "صنف جاف", DealerPrice: 5m, InternalUnitItemNumber: "DRY-1")]);
        Assert.Equal(0, master.PlannedItemBranchInventory);
        Assert.Equal(0, master.PlannedOpeningBalances);
        Assert.Equal(0, master.PlannedRetailPrices);
        Assert.Equal(0, master.PlannedBranchPriceInserts);
        Assert.Equal(before, Counts());
        Assert.Throws<InvalidOperationException>(() => service.Commit(
            [new LegacyItemObservation(LegacyCatalogs.Central, "DRY-2", "لن يُكتب")]));
        Assert.Equal(before, Counts());
    }

    private static string Counts() =>
        $"{QueryInt("SELECT COUNT(*) FROM dbo.Items;")}|{QueryInt("SELECT COUNT(*) FROM dbo.ItemUnits;")}|"
        + $"{QueryInt("SELECT COUNT(*) FROM dbo.ItemPrices;")}|"
        + $"{QueryInt("SELECT COUNT(*) FROM dbo.ItemBranchInventory;")}|"
        + $"{QueryInt("SELECT COUNT(*) FROM dbo.StockMovements;")}";

    private static int QueryInt(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> QueryStringAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<int> ColumnCountAsync(string table, string column)
    {
        return await QueryIntAsync(
            $"""
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            WHERE t.name = N'{table}' AND c.name = N'{column}';
            """);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XtraPOS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
