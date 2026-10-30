using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data.Migrations;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class InventoryFoundationTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task Migration004_AppliesOnceAndDoesNotImportLegacyItems()
    {
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation';"));
        Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber NOT LIKE N'T4-%';"));

        var sql = await File.ReadAllTextAsync(MigrationPath());
        Assert.DoesNotContain("saleemPOSDB", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO dbo.Items", sql, StringComparison.OrdinalIgnoreCase);

        var priceTypes = await QueryIntAsync("SELECT COUNT(*) FROM dbo.PriceTypes;");
        var movementTypes = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovementTypes;");
        await ExecuteAsync("DELETE FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation';");
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
                    SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation')
                BEGIN
                    INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                    VALUES (N'004_ItemsInventoryFoundation', N'Items inventory foundation', N'test', SYSUTCDATETIME());
                END
                """);
        }

        Assert.Equal(priceTypes, await QueryIntAsync("SELECT COUNT(*) FROM dbo.PriceTypes;"));
        Assert.Equal(movementTypes, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovementTypes;"));
        Assert.Equal(8, priceTypes);
        Assert.Equal(16, movementTypes);
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation';"));
    }

    [Fact]
    public async Task Items_SupportArabicNamesParentsAndRejectBranchMeasures()
    {
        await CleanupAsync();
        try
        {
            var parentId = await InsertItemAsync("T4-PARENT", "صنف أب");
            var childId = await InsertItemAsync("T4-CHILD", "شاي أخضر", parentId);
            Assert.Equal(parentId, await QueryGuidAsync($"SELECT ParentItemID FROM dbo.Items WHERE ItemID = '{childId}';"));
            Assert.Equal("شاي أخضر", await QueryStringAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{childId}';"));

            await Assert.ThrowsAnyAsync<SqlException>(() => InsertItemAsync("T4-PARENT", "مكرر"));

            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                WHERE t.name = N'Items'
                  AND c.name IN (
                      N'Quantity', N'Cost', N'ItemSpeed', N'DailySalesRate', N'QuantitySold',
                      N'Turnover', N'InventoryTurnover', N'RequiredQuantity', N'Provider');
                """));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task ItemUnits_PreservePackageLinkAndInternalUnitName()
    {
        await CleanupAsync();
        try
        {
            var boxId = await InsertItemAsync("T4-BOX", "صندوق");
            var unitId = await InsertItemAsync("T4-UNIT", "حبة");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemUnits
                    (ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit)
                VALUES ('{boxId}', 12, '{unitId}', N'T4-UNIT', N'حبة داخلية', 0);
                """);

            Assert.Equal("حبة داخلية", await QueryStringAsync(
                $"SELECT InternalUnitItemName FROM dbo.ItemUnits WHERE ItemID = '{boxId}';"));
            Assert.Equal(unitId, await QueryGuidAsync(
                $"SELECT InternalUnitItemID FROM dbo.ItemUnits WHERE ItemID = '{boxId}';"));
            Assert.Equal("T4-UNIT", await QueryStringAsync(
                $"SELECT InternalUnitItemNumber FROM dbo.ItemUnits WHERE ItemID = '{boxId}';"));
            Assert.Equal(12m, await QueryDecimalAsync(
                $"SELECT UnitsPerPackage FROM dbo.ItemUnits WHERE ItemID = '{boxId}';"));

            var rejected = await Record.ExceptionAsync(() => ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemUnits (ItemID, UnitsPerPackage)
                VALUES ('{boxId}', 0);
                """));
            Assert.IsType<SqlException>(rejected);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var units = await catalog.GetItemUnitsAsync(boxId, CancellationToken.None);
            Assert.Equal("حبة داخلية", units.Single().InternalUnitItemName);
            Assert.Equal(0.2m, PackageSalesRate.PackageDailySalesRate(2.4m, units.Single().UnitsPerPackage));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Prices_SupportLevelsOverridesHistoryAndEffectiveFallback()
    {
        await CleanupAsync();
        try
        {
            var codes = await QueryStringsAsync("SELECT PriceCode FROM dbo.PriceTypes ORDER BY SortOrder;");
            Assert.Equal(
                ["Retail", "Dealer", "MajorCustomer", "SpecialCustomer", "Price0", "Price1", "Price2", "Price3"],
                codes);
            Assert.Equal("decimal", await QueryStringAsync(
                """
                SELECT DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = N'ItemPrices' AND COLUMN_NAME = N'Price';
                """));
            Assert.Equal(19, await QueryIntAsync(
                """
                SELECT NUMERIC_PRECISION
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = N'ItemPrices' AND COLUMN_NAME = N'Price';
                """));
            Assert.Equal(4, await QueryIntAsync(
                """
                SELECT NUMERIC_SCALE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = N'ItemPrices' AND COLUMN_NAME = N'Price';
                """));

            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var itemId = await InsertItemAsync("T4-PRICE", "صنف السعر");
            var retailId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Retail';");
            var dealerId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Dealer';");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price)
                VALUES
                    ('{itemId}', '{retailId}', NULL, 10.5000),
                    ('{itemId}', '{dealerId}', NULL, 9.2500),
                    ('{itemId}', '{retailId}', '{branchId}', 8.7500);

                INSERT INTO dbo.ItemPriceHistory (ItemID, PriceTypeID, BranchID, OldPrice, NewPrice, ChangeSource, Reason)
                VALUES ('{itemId}', '{retailId}', '{branchId}', 10.5000, 8.7500, N'Test', N'تجاوز فرع');
                """);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var prices = await catalog.GetItemPricesAsync(itemId, CancellationToken.None);
            Assert.Equal(3, prices.Count);
            Assert.Equal(8.7500m, (await catalog.GetEffectiveItemPriceAsync(itemId, "Retail", branchId, CancellationToken.None))!.Price);
            var master = await catalog.GetEffectiveItemPriceAsync(itemId, "Retail", null, CancellationToken.None);
            Assert.Equal(10.5000m, master!.Price);
            Assert.False(master.IsBranchOverride);
            Assert.Equal(9.2500m, (await catalog.GetEffectiveItemPriceAsync(itemId, "Dealer", branchId, CancellationToken.None))!.Price);

            await ExecuteAsync(
                $"UPDATE dbo.ItemPrices SET IsActive = 0 WHERE ItemID = '{itemId}' AND BranchID = '{branchId}';");
            Assert.Equal(10.5000m, (await catalog.GetEffectiveItemPriceAsync(itemId, "Retail", branchId, CancellationToken.None))!.Price);
            Assert.Equal(8.7500m, await QueryDecimalAsync(
                $"SELECT NewPrice FROM dbo.ItemPriceHistory WHERE ItemID = '{itemId}';"));

            var duplicate = await Record.ExceptionAsync(() => ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price)
                VALUES ('{itemId}', '{dealerId}', NULL, 1);
                """));
            Assert.IsType<SqlException>(duplicate);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Inventory_IsBranchSpecificAndKeepsDecimalQuantityAndRate()
    {
        await CleanupAsync();
        try
        {
            var firstBranch = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches ORDER BY CreatedAt;");
            var businessId = await QueryGuidAsync("SELECT TOP (1) BusinessID FROM dbo.Branches;");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.Branches (BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES ('{businessId}', N'T4-WH', N'مستودع التجربة', N'Warehouse', 1, SYSUTCDATETIME());
                """);
            var secondBranch = await QueryGuidAsync("SELECT BranchID FROM dbo.Branches WHERE BranchCode = N'T4-WH';");
            var itemId = await InsertItemAsync("T4-STOCK", "صنف المخزون");
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemBranchInventory
                    (BranchID, ItemID, UnitCost, DailySalesRate)
                VALUES
                    ('{firstBranch}', '{itemId}', 3.5000, 2.400000),
                    ('{secondBranch}', '{itemId}', 4.2500, 0.200000);
                INSERT INTO dbo.BranchStockBalances
                    (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES
                    ('{firstBranch}', '{itemId}', 1.2500, 1, 3.5000),
                    ('{secondBranch}', '{itemId}', 8.0000, 1, 4.2500);
                """);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var first = await catalog.GetBranchInventoryAsync(firstBranch, itemId, CancellationToken.None);
            var second = await catalog.GetBranchInventoryAsync(secondBranch, itemId, CancellationToken.None);
            Assert.Equal(1.2500m, first!.OnHandQuantity);
            Assert.Equal(3.5000m, first.UnitCost);
            Assert.Equal(2.400000m, first.DailySalesRate);
            Assert.Equal(8.0000m, second!.OnHandQuantity);
            Assert.Equal(4.2500m, second.UnitCost);
            Assert.Equal(0.200000m, second.DailySalesRate);
            Assert.NotEqual(first.BranchId, second.BranchId);

            var duplicate = await Record.ExceptionAsync(() => ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID)
                VALUES ('{firstBranch}', '{itemId}');
                """));
            Assert.IsType<SqlException>(duplicate);

            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM sys.columns AS c
                INNER JOIN sys.tables AS t ON t.object_id = c.object_id
                INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                WHERE t.name IN (N'Items', N'ItemUnits', N'ItemPrices', N'ItemPriceHistory', N'ItemBranchInventory', N'BranchStockBalances', N'StockMovements')
                  AND ty.name IN (N'float', N'real', N'money', N'smallmoney');
                """));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task StockLedger_KeepsSignedMovementsAndBlocksCascadeDelete()
    {
        await CleanupAsync();
        try
        {
            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var itemId = await InsertItemAsync("T4-MOVE", "صنف الحركة");
            var found = await catalogItem(itemId);
            Assert.Equal("T4-MOVE", found!.ItemNumber);
            await using (var lookup = fixture.Factory.Services.CreateAsyncScope())
            {
                var lookupCatalog = lookup.ServiceProvider.GetRequiredService<ItemCatalogService>();
                Assert.Equal(itemId, (await lookupCatalog.GetItemByNumberAsync("T4-MOVE", CancellationToken.None))!.ItemId);
            }
            var searched = await SearchAsync("صنف الحركة");
            Assert.Contains(searched, item => item.ItemId == itemId);

            var purchaseId = await QueryGuidAsync("SELECT MovementTypeID FROM dbo.StockMovementTypes WHERE MovementCode = N'Purchase';");
            var saleId = await QueryGuidAsync("SELECT MovementTypeID FROM dbo.StockMovementTypes WHERE MovementCode = N'Sale';");
            var groupId = Guid.NewGuid();
            var referenceId = Guid.NewGuid();
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.StockMovements
                    (BranchID, ItemID, MovementTypeID, QuantityDelta, UnitCost, OccurredAt, ReferenceType, ReferenceID, MovementGroupID, Notes)
                VALUES
                    ('{branchId}', '{itemId}', '{purchaseId}', 10.5000, 2.0000, SYSUTCDATETIME(), N'PurchaseInvoice', '{referenceId}', '{groupId}', N'وارد'),
                    ('{branchId}', '{itemId}', '{saleId}', -2.2500, NULL, DATEADD(MINUTE, 1, SYSUTCDATETIME()), N'Sale', '{referenceId}', '{groupId}', N'صادر');
                """);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var history = await catalog.GetStockMovementHistoryAsync(branchId, itemId, CancellationToken.None);
            Assert.Equal([10.5000m, -2.2500m], history.Select(row => row.QuantityDelta).ToArray());
            Assert.All(history, row => Assert.Equal(branchId, row.BranchId));
            Assert.All(history, row => Assert.Equal(itemId, row.ItemId));
            Assert.Equal(groupId, history[0].MovementGroupId);
            Assert.Equal("PurchaseInvoice", history[0].ReferenceType);
            Assert.Equal(referenceId, history[0].ReferenceId);

            var blocked = await Record.ExceptionAsync(() => ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemID = '{itemId}';"));
            var sqlError = Assert.IsType<SqlException>(blocked);
            Assert.Equal(547, sqlError.Number);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE ItemID = '{itemId}';"));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM sys.foreign_keys
                WHERE delete_referential_action <> 0
                  AND parent_object_id IN (
                      OBJECT_ID(N'dbo.StockMovements'),
                      OBJECT_ID(N'dbo.ItemPriceHistory'),
                      OBJECT_ID(N'dbo.ItemPrices'),
                      OBJECT_ID(N'dbo.ItemBranchInventory'),
                      OBJECT_ID(N'dbo.BranchStockBalances'),
                      OBJECT_ID(N'dbo.ItemUnits'),
                      OBJECT_ID(N'dbo.Items'));
                """));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<Catalog.ItemRecord?> catalogItem(Guid itemId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
        return await catalog.GetItemByIdAsync(itemId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Catalog.ItemRecord>> SearchAsync(string term)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
        return await catalog.SearchItemsAsync(term, CancellationToken.None);
    }

    private static async Task<Guid> InsertItemAsync(string number, string name, Guid? parentId = null)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Items (ItemNumber, ItemName, ParentItemID)
            OUTPUT INSERTED.ItemID
            VALUES (@number, @name, @parent);
            """,
            connection);
        command.Parameters.AddWithValue("@number", number);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@parent", (object?)parentId ?? DBNull.Value);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task CleanupAsync()
    {
        await ExecuteAsync(
            """
            DELETE movements
            FROM dbo.StockMovements AS movements
            INNER JOIN dbo.Items AS items ON items.ItemID = movements.ItemID
            WHERE items.ItemNumber LIKE N'T4-%';

            DELETE history
            FROM dbo.ItemPriceHistory AS history
            INNER JOIN dbo.Items AS items ON items.ItemID = history.ItemID
            WHERE items.ItemNumber LIKE N'T4-%';

            DELETE prices
            FROM dbo.ItemPrices AS prices
            INNER JOIN dbo.Items AS items ON items.ItemID = prices.ItemID
            WHERE items.ItemNumber LIKE N'T4-%';

            DELETE inventory
            FROM dbo.ItemBranchInventory AS inventory
            INNER JOIN dbo.Items AS items ON items.ItemID = inventory.ItemID
            WHERE items.ItemNumber LIKE N'T4-%';

            DELETE balances
            FROM dbo.BranchStockBalances AS balances
            INNER JOIN dbo.Items AS items ON items.ItemID = balances.ItemID
            WHERE items.ItemNumber LIKE N'T4-%';

            DELETE units
            FROM dbo.ItemUnits AS units
            WHERE units.ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber LIKE N'T4-%')
               OR units.InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber LIKE N'T4-%');

            DELETE FROM dbo.Items WHERE ItemNumber LIKE N'T4-%' AND ParentItemID IS NOT NULL;
            DELETE FROM dbo.Items WHERE ItemNumber LIKE N'T4-%';
            DELETE FROM dbo.Branches WHERE BranchCode = N'T4-WH';
            """);
    }

    private static string MigrationPath()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "XtraPOS.Api", "Data", "Migrations", "004_ItemsInventoryFoundation.sql"));
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<decimal> QueryDecimalAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<string> QueryStringAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<List<string>> QueryStringsAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(
            TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }
}
