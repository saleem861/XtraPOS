using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class StockOwnershipTests
{
    private static readonly Guid Base = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Package = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Outer = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void IndependentItem_OwnsItself()
    {
        var resolution = StockOwnership.Resolve(Base, []);

        Assert.Equal(Base, resolution.StockOwnerItemId);
        Assert.Equal(1m, resolution.BaseQuantityFactor);
        Assert.True(resolution.OwnsPhysicalStock);
    }

    [Fact]
    public void Package_ResolvesToBaseOwnerAndMultipliesTheFactor()
    {
        var links = new[] { new StockUnitLink(Package, Base, 2m), new StockUnitLink(Outer, Package, 4m) };
        var halfKilo = StockOwnership.Resolve(Package, links);
        var box = StockOwnership.Resolve(Outer, links);

        Assert.Equal(Base, halfKilo.StockOwnerItemId);
        Assert.Equal(2m, halfKilo.BaseQuantityFactor);
        Assert.False(halfKilo.OwnsPhysicalStock);
        Assert.Equal(Base, box.StockOwnerItemId);
        Assert.Equal(8m, box.BaseQuantityFactor);
        Assert.Equal(3m, box.DerivedQuantity(24m));
    }

    [Fact]
    public void MultiLevelChain_ConsumesTheProductOfTheFactors()
    {
        var middle = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var links = new[] { new StockUnitLink(Outer, middle, 2m), new StockUnitLink(middle, Base, 3m) };

        var resolution = StockOwnership.Resolve(Outer, links);

        Assert.Equal(Base, resolution.StockOwnerItemId);
        Assert.Equal(6m, resolution.BaseQuantityFactor);
    }

    [Fact]
    public void CircularChain_IsRejected()
    {
        var links = new[] { new StockUnitLink(Package, Base, 2m), new StockUnitLink(Base, Package, 3m) };

        var error = Assert.Throws<StockOwnershipException>(() => StockOwnership.Resolve(Package, links));
        Assert.Contains("circular", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageSale_PlansOneBaseMovementOnly()
    {
        var resolution = StockOwnership.Resolve(Package, [new StockUnitLink(Package, Base, 4m)]);
        var plan = StockOwnership.PlanSale(resolution, 1m);

        Assert.Equal(Package, plan.SellableItemId);
        Assert.Equal(1m, plan.SellableQuantity);
        Assert.Equal(Base, plan.StockOwnerItemId);
        Assert.Equal(-4m, plan.QuantityDelta);
        Assert.False(resolution.OwnsPhysicalStock);
    }

    [Fact]
    public void KnownZero_IsDistinctFromPendingQuantity()
    {
        var knownZero = PhysicalQuantity.Known(0m);
        var pending = PhysicalQuantity.Pending;

        Assert.True(knownZero.IsKnownZero);
        Assert.Equal(0m, knownZero.OnHandQuantity);
        Assert.True(knownZero.IsQuantityInitialized);
        Assert.False(pending.IsQuantityInitialized);
        Assert.Null(pending.OnHandQuantity);
        Assert.False(pending.IsKnownZero);
        Assert.NotEqual(knownZero, pending);
    }

    [Fact]
    public void ValuationAndTurnover_UseTheStockOwnerOnce()
    {
        var links = new[] { new StockUnitLink(Package, Base, 2m), new StockUnitLink(Outer, Base, 4m) };
        var balances = new (Guid ItemId, decimal? OnHandQuantity, decimal? UnitCost)[]
        {
            (Base, 10m, 5m),
            (Package, 5m, 10m),
            (Outer, 2.5m, 20m),
        };

        Assert.Equal([Base], StockOwnership.SelectPhysicalStockItems([Base, Package, Outer], links));
        Assert.Equal(50m, StockOwnership.PhysicalInventoryValue(balances, links));
    }

    [Fact]
    public void ConfirmedTechnicalItem_IsExcludedFromBranchCutover()
    {
        Assert.False(ConfirmedTechnicalItemCleanup.IncludeInBranchCutover(ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber));
        Assert.True(ConfirmedTechnicalItemCleanup.IncludeInBranchCutover("1360"));
        Assert.Contains("XTRAPOS_DELETE_TEST_20260917115506", ConfirmedTechnicalItemCleanup.DeactivateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE", ConfirmedTechnicalItemCleanup.DeactivateSql, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection("Branch")]
public sealed class InventoryOwnershipMigrationTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task BranchStockBalance_SeparatesKnownZeroFromPendingQuantity()
    {
        var branchId = await ScalarAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var itemId = await InsertItemAsync("T5C2-OWNER", "مالك مخزون");
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', 0, 1);
                """);
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}' AND OnHandQuantity = 0 AND IsQuantityInitialized = 1;"));

            await ExecuteAsync($"DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', NULL, 0);
                """);
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}' AND OnHandQuantity IS NULL AND IsQuantityInitialized = 0;"));

            var initializedWithoutQuantity = await Record.ExceptionAsync(() => ExecuteAsync(
                $"""
                UPDATE dbo.BranchStockBalances
                SET IsQuantityInitialized = 1, OnHandQuantity = NULL
                WHERE ItemID = '{itemId}';
                """));
            Assert.IsType<SqlException>(initializedWithoutQuantity);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';");
            await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemID = '{itemId}';");
        }
    }

    [Fact]
    public async Task ConfirmedTechnicalItem_CanBeDeactivatedWithoutDeletingTheMaster()
    {
        await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemNumber = N'{ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber}';");
        var itemId = await InsertItemAsync(ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber, "XTRAPOS delete safety test");
        try
        {
            var beforeActive = await CountAsync("SELECT COUNT(*) FROM dbo.Items WHERE IsActive = 1;");
            await ExecuteAsync(ConfirmedTechnicalItemCleanup.DeactivateSql);
            Assert.Equal(0, await CountAsync($"SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';"));
            Assert.Equal(beforeActive - 1, await CountAsync("SELECT COUNT(*) FROM dbo.Items WHERE IsActive = 1;"));
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemID = '{itemId}';");
        }
    }

    [Fact]
    public async Task PhysicalQuantity_ExistsOnlyOnBranchStockBalances()
    {
        Assert.Equal(0, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.ItemBranchInventory')
              AND name = N'OnHandQuantity';
            """));
        Assert.Equal(
            "BranchStockBalances",
            await ScalarStringAsync(
                """
                SELECT t.name
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                WHERE c.name = N'OnHandQuantity'
                  AND t.schema_id = SCHEMA_ID(N'dbo');
                """));
        Assert.Equal(1, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.key_constraints
            WHERE name = N'UQ_BranchStockBalances_BranchID_ItemID'
              AND parent_object_id = OBJECT_ID(N'dbo.BranchStockBalances');
            """));
        var check = await ScalarStringAsync(
            """
            SELECT definition
            FROM sys.check_constraints
            WHERE name = N'CK_BranchStockBalances_Initialization'
              AND parent_object_id = OBJECT_ID(N'dbo.BranchStockBalances');
            """);
        Assert.Contains("IsQuantityInitialized", check, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnHandQuantity", check, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE name IN (N'FK_BranchStockBalances_Branch', N'FK_BranchStockBalances_Item')
              AND delete_referential_action <> 0;
            """));
        Assert.Equal(2, await CountAsync(
            """
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE name IN (N'FK_BranchStockBalances_Branch', N'FK_BranchStockBalances_Item');
            """));
    }

    [Fact]
    public async Task BranchStockBalance_RejectsASecondRowForTheSameBranchAndOwner()
    {
        var branchId = await ScalarAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var itemId = await InsertItemAsync("T5C2-DUP", "تكرار مالك");
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', 1, 1);
                """);
            var duplicate = await Record.ExceptionAsync(() => ExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', 2, 1);
                """));
            Assert.IsType<SqlException>(duplicate);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';");
            await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemID = '{itemId}';");
        }
    }

    [Fact]
    public async Task DerivedSkuQuantity_IsCalculatedFromTheOwnerBalanceOnly()
    {
        var branchId = (Guid)await ScalarAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var ownerId = await InsertItemAsync("T5C2-BASE", "وحدة أساس");
        var packageId = await InsertItemAsync("T5C2-PACK", "عبوة");
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemUnits (ItemID, UnitsPerPackage, InternalUnitItemID, IsBaseUnit, IsActive)
                VALUES ('{packageId}', 4, '{ownerId}', 0, 1);
                INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, DailySalesRate)
                VALUES ('{branchId}', '{packageId}', 1.5);
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{ownerId}', 20, 1);
                """);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var package = await catalog.GetBranchInventoryAsync(branchId, packageId, CancellationToken.None);
            Assert.Equal(5m, package!.OnHandQuantity);
            Assert.True(package.IsQuantityInitialized);
            Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{packageId}';"));
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{ownerId}' AND OnHandQuantity = 20;"));
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.ItemBranchInventory WHERE ItemID IN ('{packageId}', '{ownerId}');
                DELETE FROM dbo.BranchStockBalances WHERE ItemID IN ('{packageId}', '{ownerId}');
                DELETE FROM dbo.ItemUnits WHERE ItemID IN ('{packageId}', '{ownerId}');
                DELETE FROM dbo.Items WHERE ItemID IN ('{packageId}', '{ownerId}');
                """);
        }
    }

    [Fact]
    public async Task TechnicalItemCleanup_DeactivatesOnlyTheConfirmedItem_OnTheIntegrationDatabase()
    {
        const string activeItem = "OWN-ACTIVE-CLEANUP";
        const string similarItem = "XTRAPOS_DELETE_TEST_NOT_CONFIRMED";
        var technicalItem = ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber;
        await ExecuteAsync(
            $"""
            DELETE FROM dbo.Items
            WHERE ItemNumber IN (N'{activeItem}', N'{similarItem}', N'{technicalItem}');
            """);
        try
        {
            Assert.Equal(TestDatabase.Name, await ScalarStringAsync("SELECT DB_NAME();"));
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Items (ItemNumber, ItemName, IsActive)
                VALUES
                    (N'{activeItem}', N'صنف نشط', 1),
                    (N'{similarItem}', N'رقم مشابه', 1),
                    (N'{technicalItem}', N'صنف تقني', 1);
                """);

            await ExecuteAsync(ConfirmedTechnicalItemCleanup.DeactivateSql);

            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'{technicalItem}';"));
            Assert.Equal(0, await CountAsync($"SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'{technicalItem}';"));
            Assert.Equal(1, await CountAsync($"SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'{activeItem}';"));
            Assert.Equal(1, await CountAsync($"SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'{similarItem}';"));
            Assert.False(ConfirmedTechnicalItemCleanup.IncludeInBranchCutover(technicalItem));
            Assert.True(ConfirmedTechnicalItemCleanup.IncludeInBranchCutover(activeItem));
            Assert.True(ConfirmedTechnicalItemCleanup.IncludeInBranchCutover(similarItem));
        }
        finally
        {
            await ExecuteAsync(
                $"""
                DELETE FROM dbo.Items
                WHERE ItemNumber IN (N'{activeItem}', N'{similarItem}', N'{technicalItem}');
                """);
        }
    }

    private static async Task<Guid> InsertItemAsync(string number, string name)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Items (ItemNumber, ItemName, IsActive)
            OUTPUT INSERTED.ItemID
            VALUES (@number, @name, 1);
            """,
            connection);
        command.Parameters.AddWithValue("@number", number);
        command.Parameters.AddWithValue("@name", name);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> ScalarStringAsync(string sql)
    {
        return Convert.ToString(await ScalarAsync(sql)) ?? "";
    }

    private static async Task<object> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing value.");
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }
}
