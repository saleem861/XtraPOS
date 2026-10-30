using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Import;

public static class Shop1OperationalImportCommand
{
    public const string CommandName = "--import-shop-operational";
    public const string PrepareCommand = "--prepare-shop1-import-test";
    public const string DropCommand = "--drop-shop1-import-test";
    public const string FailBeforeStockCommitOption = "--fail-before-stock-commit";
    public const string TestDatabaseName = "XtraPOS_Branch_Test_Shop1Import";
    public const string ImportType = "Shop1OperationalImport";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal)
            || string.Equals(arg, PrepareCommand, StringComparison.Ordinal)
            || string.Equals(arg, DropCommand, StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        if (!IsApprovedTarget(confirmed) || !string.Equals(connections.ConfiguredDatabase, confirmed, StringComparison.Ordinal))
        {
            output.WriteLine("Shop1 operational import requires --confirm-database equal to the configured XtraPOS_Branch_01 or XtraPOS_Branch_Test_Shop1Import database.");
            return 1;
        }

        if (args.Any(arg => string.Equals(arg, DropCommand, StringComparison.Ordinal)))
        {
            return await DropTestAsync(confirmed!, output, cancellationToken);
        }

        if (!string.Equals(central.DatabaseName, CentralDatabase.Name, StringComparison.Ordinal))
        {
            output.WriteLine("Shop1 operational import requires the saved Central database XtraPOS_Central.");
            return 1;
        }

        if (args.Any(arg => string.Equals(arg, PrepareCommand, StringComparison.Ordinal)))
        {
            return await PrepareTestAsync(confirmed!, services, central, output, cancellationToken);
        }

        var sourceDatabase = ReadOption(args, "--source-database");
        if (!string.Equals(sourceDatabase, Shop1OperationalAuditCommand.SourceDatabase, StringComparison.Ordinal))
        {
            output.WriteLine("Shop1 operational import requires --source-database Shop1.");
            return 1;
        }

        var requestedServer = ReadOption(args, "--source-server");
        var expectedServer = central.Port is > 0 ? central.Server + "," + central.Port.Value : central.Server;
        if (!string.Equals(requestedServer?.Trim(), expectedServer, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestedServer?.Trim(), central.Server, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Shop1 operational import requires --source-server matching the saved Central SQL server.");
            return 1;
        }

        var failStock = args.Any(arg => string.Equals(arg, FailBeforeStockCommitOption, StringComparison.Ordinal));
        if (failStock && !string.Equals(confirmed, TestDatabaseName, StringComparison.Ordinal))
        {
            output.WriteLine("The stock-phase failure test is allowed only on XtraPOS_Branch_Test_Shop1Import.");
            return 1;
        }

        return await ImportAsync(connections, central, confirmed!, failStock, output, cancellationToken);
    }

    private static async Task<int> PrepareTestAsync(
        string databaseName,
        IServiceProvider services,
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(databaseName, TestDatabaseName, StringComparison.Ordinal) || !BranchDatabase.IsTestName(databaseName))
        {
            output.WriteLine("Test preparation refuses every database except XtraPOS_Branch_Test_Shop1Import.");
            return 1;
        }

        output.WriteLine("== CENTRAL BEFORE PREPARE");
        await PrintCentralAsync(central, output, cancellationToken);
        output.WriteLine("== OLD BRANCH BEFORE PREPARE");
        await PrintOldBranchAsync(output, cancellationToken);
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        await connections.EnsureBranchDatabaseAsync(cancellationToken);
        await using var scope = services.CreateAsyncScope();
        var migrations = scope.ServiceProvider.GetRequiredService<Data.Migrations.MigrationRunner>();
        await migrations.ApplyPendingAsync(cancellationToken);
        var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
        var items = await sync.BootstrapAsync(central, cancellationToken);
        output.WriteLine("BootstrappedItems=" + items);
        output.WriteLine("== CENTRAL AFTER PREPARE");
        await PrintCentralAsync(central, output, cancellationToken);
        output.WriteLine("== OLD BRANCH AFTER PREPARE");
        await PrintOldBranchAsync(output, cancellationToken);
        output.WriteLine("TEST_BRANCH_PREPARED");
        return 0;
    }

    private static async Task<int> DropTestAsync(string databaseName, TextWriter output, CancellationToken cancellationToken)
    {
        if (!string.Equals(databaseName, TestDatabaseName, StringComparison.Ordinal)
            || !BranchDatabase.IsTestName(databaseName)
            || BranchDatabase.IsOperationalName(databaseName)
            || string.Equals(databaseName, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Drop refuses every database except XtraPOS_Branch_Test_Shop1Import.");
            return 1;
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(
            "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            SELECT CASE WHEN DB_ID(N'{databaseName}') IS NULL THEN 1 ELSE 0 END;
            """,
            connection)
        {
            CommandTimeout = 60,
        };
        var dropped = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        SqlConnection.ClearAllPools();
        output.WriteLine(dropped == 1 ? "TEST_BRANCH_DROPPED" : "TEST_BRANCH_STILL_PRESENT");
        return dropped == 1 ? 0 : 2;
    }

    private static async Task<int> ImportAsync(
        SqlConnectionFactory connections,
        SqlEndpointSettings central,
        string databaseName,
        bool failStock,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        output.WriteLine("ReimportPolicy: existing ItemBranchInventory rows update UnitCost and IsAvailable only. DailySalesRate, LastSaleAt, and LastRestockAt are left unchanged. An existing BranchStockBalances row is not updated and does not receive another OpeningBalance. A missing trusted owner gets one balance and one OpeningBalance in the same transaction. A missing untrusted owner gets an uninitialized balance and no movement. An existing branch price override is updated in place. A matching Central price is not inserted.");
        output.WriteLine("== CENTRAL BEFORE");
        await PrintCentralAsync(central, output, cancellationToken);
        output.WriteLine("== OLD BRANCH BEFORE");
        await PrintOldBranchAsync(output, cancellationToken);
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        if (!await TargetIsBranch01Async(branch, output, cancellationToken))
        {
            return 1;
        }

        var allowsNull = await ScalarAsync(branch, "SELECT COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'DailySalesRate', N'AllowsNull');", cancellationToken);
        output.WriteLine("DailySalesRateAllowsNull=" + (allowsNull?.ToString(CultureInfo.InvariantCulture) ?? ""));
        if (allowsNull != 1)
        {
            output.WriteLine("STOP_BEFORE_WRITES DailySalesRate must be nullable before operational rows are inserted.");
            return 2;
        }

        if (string.Equals(databaseName, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
        {
            var existingInventory = await ScalarAsync(branch, "SELECT COUNT(*) FROM dbo.ItemBranchInventory;", cancellationToken) ?? 0;
            var existingBalances = await ScalarAsync(branch, "SELECT COUNT(*) FROM dbo.BranchStockBalances;", cancellationToken) ?? 0;
            var existingMovements = await ScalarAsync(branch, "SELECT COUNT(*) FROM dbo.StockMovements;", cancellationToken) ?? 0;
            var existingOverrides = await ScalarAsync(branch, "SELECT COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL;", cancellationToken) ?? 0;
            output.WriteLine("PreflightInventory=" + existingInventory + " PreflightBalances=" + existingBalances + " PreflightMovements=" + existingMovements + " PreflightOverrides=" + existingOverrides);
            if (existingInventory != 0 || existingBalances != 0 || existingMovements != 0 || existingOverrides != 0)
            {
                output.WriteLine("STOP_BEFORE_WRITES XtraPOS_Branch_01 already contains operational rows.");
                return 2;
            }
        }

        List<Shop1OperationalRow> shopRows;
        await using (var shop = await OpenShopAsync(central, cancellationToken))
        {
            shopRows = await Shop1OperationalAuditCommand.ReadItemsAsync(shop, cancellationToken);
        }

        var catalog = await ReadCatalogAsync(branch, cancellationToken);
        var units = await ReadUnitsAsync(branch, cancellationToken);
        var prices = await ReadPricesAsync(branch, cancellationToken);
        var plan = Shop1OperationalPlanner.Plan(shopRows, catalog, units, prices, ["1087", "1300", "1350", "1360"], DateTime.UtcNow);
        var gate = Gate(plan);
        if (gate is not null)
        {
            output.WriteLine("STOP_BEFORE_WRITES " + gate);
            return 2;
        }

        if (plan.StockDecisions.Any(decision => decision.Classification == Shop1OperationalPlan.Trusted && decision.OnHandQuantity is null or < 0))
        {
            output.WriteLine("STOP_BEFORE_WRITES a trusted owner has a missing or negative quantity.");
            return 2;
        }

        var priceTypes = await ReadPriceTypesAsync(branch, cancellationToken);
        if (plan.PriceDecisions.Any(decision => !priceTypes.ContainsKey(decision.PriceCode)))
        {
            output.WriteLine("STOP_BEFORE_WRITES a price override has no PriceType.");
            return 2;
        }

        var branchId = await GuidAsync(branch, "SELECT BranchID FROM dbo.Branches WHERE BranchCode = N'01';", cancellationToken);
        var openingTypeId = await GuidAsync(branch, "SELECT MovementTypeID FROM dbo.StockMovementTypes WHERE MovementCode = N'OpeningBalance';", cancellationToken);
        output.WriteLine("PLAN_MATCHES_APPROVED_DRY_RUN");
        var started = DateTime.UtcNow;
        try
        {
            var inventory = await WriteInventoryAsync(branch, branchId, plan.InventoryDecisions, cancellationToken);
            output.WriteLine("InventoryInserted=" + inventory.Inserted + " InventoryUpdated=" + inventory.Updated);
            var stock = await WriteStockAsync(branch, branchId, openingTypeId, plan.StockDecisions, failStock, cancellationToken);
            output.WriteLine("BalancesInserted=" + stock.Balances + " OpeningMovementsInserted=" + stock.Movements);
            var overrides = await WritePricesAsync(branch, branchId, priceTypes, plan.PriceDecisions, cancellationToken);
            output.WriteLine("OverridesInserted=" + overrides.Inserted + " OverridesUpdated=" + overrides.Updated);
            await WriteHistoryAsync(branch, started, "Completed", plan.InventoryDecisions.Count, "Shop1 operational import completed.", cancellationToken);
        }
        catch (Exception exception) when (failStock)
        {
            await WriteHistoryAsync(branch, started, "Failed", 0, "Controlled stock-phase failure rolled back.", cancellationToken);
            output.WriteLine("STOCK_PHASE_ROLLED_BACK " + exception.Message);
            await PrintVerificationAsync(branch, output, cancellationToken);
            return 3;
        }

        await PrintVerificationAsync(branch, output, cancellationToken);
        output.WriteLine("== CENTRAL AFTER");
        await PrintCentralAsync(central, output, cancellationToken);
        output.WriteLine("== OLD BRANCH AFTER");
        await PrintOldBranchAsync(output, cancellationToken);
        output.WriteLine(failStock ? "IMPORT_FAILED" : "IMPORT_COMPLETE");
        return 0;
    }

    private static async Task<(int Inserted, int Updated)> WriteInventoryAsync(
        SqlConnection connection,
        Guid branchId,
        IReadOnlyList<Shop1InventoryDecision> decisions,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var table = new DataTable();
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("IsAvailable", typeof(bool));
        table.Columns.Add("UnitCost", typeof(decimal));
        foreach (var decision in decisions)
        {
            table.Rows.Add(decision.ItemNumber, decision.IsAvailable, (object?)decision.UnitCost ?? DBNull.Value);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Inventory (
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                IsAvailable bit NOT NULL,
                UnitCost decimal(19, 4) NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, table, "#Inventory", cancellationToken);
        var inserted = await ExecuteCountAsync(connection, transaction, """
            INSERT INTO dbo.ItemBranchInventory
                (BranchID, ItemID, UnitCost, DailySalesRate, IsAvailable, LastSaleAt, LastRestockAt, IsActive, CreatedAt, UpdatedAt)
            SELECT @branch, item.ItemID, source.UnitCost, NULL, source.IsAvailable, NULL, NULL, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM #Inventory AS source
            INNER JOIN dbo.Items AS item ON item.ItemNumber = source.ItemNumber
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.ItemBranchInventory AS existing
                WHERE existing.BranchID = @branch AND existing.ItemID = item.ItemID);
            SELECT @@ROWCOUNT;
            """, branchId, cancellationToken);
        var updated = await ExecuteCountAsync(connection, transaction, """
            UPDATE existing
            SET UnitCost = source.UnitCost,
                IsAvailable = source.IsAvailable,
                UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ItemBranchInventory AS existing
            INNER JOIN dbo.Items AS item ON item.ItemID = existing.ItemID
            INNER JOIN #Inventory AS source ON source.ItemNumber = item.ItemNumber
            WHERE existing.BranchID = @branch;
            SELECT @@ROWCOUNT;
            """, branchId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (inserted, updated);
    }

    private static async Task<(int Balances, int Movements)> WriteStockAsync(
        SqlConnection connection,
        Guid branchId,
        Guid openingTypeId,
        IReadOnlyList<Shop1StockDecision> decisions,
        bool failBeforeCommit,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var table = new DataTable();
            table.Columns.Add("OwnerNumber", typeof(string));
            table.Columns.Add("Initialize", typeof(bool));
            table.Columns.Add("Quantity", typeof(decimal));
            foreach (var decision in decisions)
            {
                var initialize = decision.Classification == Shop1OperationalPlan.Trusted;
                table.Rows.Add(decision.OwnerItemNumber, initialize, (object?)decision.OnHandQuantity ?? DBNull.Value);
            }

            await ExecuteAsync(connection, transaction, """
                CREATE TABLE #Stock (
                    OwnerNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    Initialize bit NOT NULL,
                    Quantity decimal(18, 4) NULL);
                """, cancellationToken);
            await BulkAsync(connection, transaction, table, "#Stock", cancellationToken);
            var balances = await ExecuteCountAsync(connection, transaction, """
                INSERT INTO dbo.BranchStockBalances
                    (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, CreatedAt, UpdatedAt)
                SELECT @branch, item.ItemID,
                    CASE WHEN source.Initialize = 1 THEN source.Quantity ELSE NULL END,
                    source.Initialize,
                    SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM #Stock AS source
                INNER JOIN dbo.Items AS item ON item.ItemNumber = source.OwnerNumber
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.BranchStockBalances AS existing
                    WHERE existing.BranchID = @branch AND existing.ItemID = item.ItemID);
                SELECT @@ROWCOUNT;
                """, branchId, cancellationToken);
            var movements = await ExecuteCountAsync(connection, transaction, """
                INSERT INTO dbo.StockMovements
                    (BranchID, ItemID, MovementTypeID, QuantityDelta, OccurredAt, ReferenceType, Notes)
                SELECT @branch, item.ItemID, @opening, source.Quantity, SYSUTCDATETIME(), N'Shop1OperationalImport', N'Shop1 opening balance'
                FROM #Stock AS source
                INNER JOIN dbo.Items AS item ON item.ItemNumber = source.OwnerNumber
                WHERE source.Initialize = 1
                  AND NOT EXISTS (
                    SELECT 1
                    FROM dbo.StockMovements AS existing
                    WHERE existing.BranchID = @branch
                      AND existing.ItemID = item.ItemID
                      AND existing.MovementTypeID = @opening);
                SELECT @@ROWCOUNT;
                """, branchId, cancellationToken, openingTypeId);
            if (failBeforeCommit)
            {
                throw new InvalidOperationException("Controlled stock-phase failure before commit.");
            }

            await transaction.CommitAsync(cancellationToken);
            return (balances, movements);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<(int Inserted, int Updated)> WritePricesAsync(
        SqlConnection connection,
        Guid branchId,
        IReadOnlyDictionary<string, Guid> priceTypes,
        IReadOnlyList<Shop1PriceDecision> decisions,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var table = new DataTable();
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("PriceTypeID", typeof(Guid));
        table.Columns.Add("Price", typeof(decimal));
        foreach (var decision in decisions)
        {
            table.Rows.Add(decision.ItemNumber, priceTypes[decision.PriceCode], decision.Price);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE #Overrides (
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                PriceTypeID uniqueidentifier NOT NULL,
                Price decimal(19, 4) NOT NULL);
            """, cancellationToken);
        await BulkAsync(connection, transaction, table, "#Overrides", cancellationToken);
        var inserted = await ExecuteCountAsync(connection, transaction, """
            INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
            SELECT item.ItemID, source.PriceTypeID, @branch, source.Price, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM #Overrides AS source
            INNER JOIN dbo.Items AS item ON item.ItemNumber = source.ItemNumber
            WHERE source.Price >= 0
              AND NOT EXISTS (
                SELECT 1 FROM dbo.ItemPrices AS existing
                WHERE existing.ItemID = item.ItemID
                  AND existing.PriceTypeID = source.PriceTypeID
                  AND existing.BranchID = @branch);
            SELECT @@ROWCOUNT;
            """, branchId, cancellationToken);
        var updated = await ExecuteCountAsync(connection, transaction, """
            UPDATE existing
            SET Price = source.Price, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ItemPrices AS existing
            INNER JOIN dbo.Items AS item ON item.ItemID = existing.ItemID
            INNER JOIN #Overrides AS source ON source.ItemNumber = item.ItemNumber AND source.PriceTypeID = existing.PriceTypeID
            WHERE existing.BranchID = @branch AND source.Price >= 0;
            SELECT @@ROWCOUNT;
            """, branchId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (inserted, updated);
    }

    private static string? Gate(Shop1OperationalPlan plan)
    {
        var differences = new List<string>();
        void Need(string name, int actual, int expected)
        {
            if (actual != expected)
            {
                differences.Add(name + " actual=" + actual + " expected=" + expected);
            }
        }

        Need("ShopIdentities", plan.DistinctShopItemNumbers, 2709);
        Need("Inventory", plan.InventoryRows, 2814);
        Need("Available", plan.Available, 2709);
        Need("Unavailable", plan.Unavailable, 105);
        Need("UnitCosts", plan.ProposedUnitCosts, 2709);
        Need("ZeroCosts", plan.ZeroCosts, 62);
        Need("Owners", plan.StockOwners, 2429);
        Need("Packages", plan.DerivedPackages, 385);
        Need("MultiHop", plan.MultiHopItems, 2);
        Need("Unresolved", plan.UnresolvedItems, 0);
        Need("Cycles", plan.CycleItems, 0);
        Need("Trusted", plan.TrustedOwners, 2111);
        Need("Ambiguous", plan.AmbiguousOwners, 133);
        Need("Invalid", plan.InvalidOwners, 185);
        Need("Opening", plan.ProposedOpeningMovements, 2111);
        Need("Dealer", plan.OverridesByPriceType["Dealer"], 178);
        Need("MajorCustomer", plan.OverridesByPriceType["MajorCustomer"], 178);
        Need("SpecialCustomer", plan.OverridesByPriceType["SpecialCustomer"], 178);
        Need("Price0", plan.OverridesByPriceType["Price0"], 191);
        Need("Price1", plan.OverridesByPriceType["Price1"], 178);
        Need("Price2", plan.OverridesByPriceType["Price2"], 178);
        Need("Price3", plan.OverridesByPriceType["Price3"], 178);
        var overrideTotal = plan.OverridesByPriceType.Values.Sum();
        Need("OverrideTotal", overrideTotal, 1259);
        var zeroOverrides = plan.ZeroOverridesByPriceType.Values.Sum();
        Need("ExplicitZeroOverrides", zeroOverrides, 0);
        Need("NegativePrices", plan.NegativePricesExcluded, 6);
        return differences.Count == 0 ? null : string.Join("; ", differences);
    }

    private static async Task<bool> TargetIsBranch01Async(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        var role = await TextAsync(connection, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';", cancellationToken);
        var branches = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Branches;", cancellationToken);
        var code = await TextAsync(connection, "SELECT BranchCode FROM dbo.Branches WHERE BranchCode = N'01';", cancellationToken);
        output.WriteLine("DatabaseRole=" + role);
        output.WriteLine("BranchCount=" + (branches?.ToString(CultureInfo.InvariantCulture) ?? ""));
        output.WriteLine("BranchCode=" + code);
        return string.Equals(role, DatabaseRole.Branch, StringComparison.Ordinal)
            && branches == 1
            && string.Equals(code, "01", StringComparison.Ordinal);
    }

    private static async Task PrintVerificationAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        await PrintAsync(connection, output, """
            SELECT 'Inventory', COUNT(*) FROM dbo.ItemBranchInventory
            UNION ALL SELECT 'Available', COUNT(*) FROM dbo.ItemBranchInventory WHERE IsAvailable = 1
            UNION ALL SELECT 'Unavailable', COUNT(*) FROM dbo.ItemBranchInventory WHERE IsAvailable = 0
            UNION ALL SELECT 'UnitCost', COUNT(*) FROM dbo.ItemBranchInventory WHERE UnitCost IS NOT NULL
            UNION ALL SELECT 'ZeroCost', COUNT(*) FROM dbo.ItemBranchInventory WHERE UnitCost = 0
            UNION ALL SELECT 'DailySalesRateNull', COUNT(*) FROM dbo.ItemBranchInventory WHERE DailySalesRate IS NULL
            UNION ALL SELECT 'DailySalesRateValue', COUNT(*) FROM dbo.ItemBranchInventory WHERE DailySalesRate IS NOT NULL
            UNION ALL SELECT 'LastSaleAt', COUNT(*) FROM dbo.ItemBranchInventory WHERE LastSaleAt IS NOT NULL
            UNION ALL SELECT 'LastRestockAt', COUNT(*) FROM dbo.ItemBranchInventory WHERE LastRestockAt IS NOT NULL
            UNION ALL SELECT 'Balances', COUNT(*) FROM dbo.BranchStockBalances
            UNION ALL SELECT 'Initialized', COUNT(*) FROM dbo.BranchStockBalances WHERE IsQuantityInitialized = 1
            UNION ALL SELECT 'Uninitialized', COUNT(*) FROM dbo.BranchStockBalances WHERE IsQuantityInitialized = 0
            UNION ALL SELECT 'NegativeBalances', COUNT(*) FROM dbo.BranchStockBalances WHERE OnHandQuantity < 0
            UNION ALL SELECT 'OpeningMovements', COUNT(*) FROM dbo.StockMovements AS movement INNER JOIN dbo.StockMovementTypes AS movementType ON movementType.MovementTypeID = movement.MovementTypeID WHERE movementType.MovementCode = N'OpeningBalance'
            UNION ALL SELECT 'Overrides', COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL
            UNION ALL SELECT 'PackageBalances', COUNT(*) FROM dbo.BranchStockBalances AS balance INNER JOIN dbo.ItemUnits AS unit ON unit.ItemID = balance.ItemID WHERE unit.InternalUnitItemNumber IS NOT NULL
            UNION ALL SELECT 'OrphanInventory', COUNT(*) FROM dbo.ItemBranchInventory AS inventory WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemID = inventory.ItemID)
            UNION ALL SELECT 'OrphanBalances', COUNT(*) FROM dbo.BranchStockBalances AS balance WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemID = balance.ItemID)
            UNION ALL SELECT 'OrphanMovements', COUNT(*) FROM dbo.StockMovements AS movement WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemID = movement.ItemID)
            UNION ALL SELECT 'MovementWithoutBalance', COUNT(*) FROM dbo.StockMovements AS movement WHERE NOT EXISTS (SELECT 1 FROM dbo.BranchStockBalances AS balance WHERE balance.BranchID = movement.BranchID AND balance.ItemID = movement.ItemID AND balance.IsQuantityInitialized = 1)
            UNION ALL SELECT 'BalanceWithoutMovement', COUNT(*) FROM dbo.BranchStockBalances AS balance WHERE balance.IsQuantityInitialized = 1 AND NOT EXISTS (SELECT 1 FROM dbo.StockMovements AS movement INNER JOIN dbo.StockMovementTypes AS movementType ON movementType.MovementTypeID = movement.MovementTypeID WHERE movement.BranchID = balance.BranchID AND movement.ItemID = balance.ItemID AND movementType.MovementCode = N'OpeningBalance');
            SELECT priceType.PriceCode, COUNT(*)
            FROM dbo.ItemPrices AS price
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
            WHERE price.BranchID IS NOT NULL
            GROUP BY priceType.PriceCode
            ORDER BY priceType.PriceCode;
            SELECT item.ItemNumber, inventory.IsAvailable, inventory.UnitCost, inventory.DailySalesRate, balance.OnHandQuantity, balance.IsQuantityInitialized, movement.QuantityDelta
            FROM dbo.Items AS item
            LEFT JOIN dbo.ItemBranchInventory AS inventory ON inventory.ItemID = item.ItemID
            LEFT JOIN dbo.BranchStockBalances AS balance ON balance.ItemID = item.ItemID
            LEFT JOIN dbo.StockMovements AS movement ON movement.ItemID = item.ItemID
            WHERE item.ItemNumber IN (N'1087', N'1300', N'1350', N'1360')
            ORDER BY item.ItemNumber;
            """, cancellationToken);
    }

    private static async Task WriteHistoryAsync(
        SqlConnection connection,
        DateTime started,
        string status,
        int rows,
        string notes,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.DataImportHistory (ImportID, ImportType, Source, StartedAt, CompletedAt, Status, RowsProcessed, Notes)
            VALUES (NEWID(), @type, N'Shop1', @started, SYSUTCDATETIME(), @status, @rows, @notes);
            """,
            connection);
        command.Parameters.AddWithValue("@type", ImportType);
        command.Parameters.AddWithValue("@started", started);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@rows", rows);
        command.Parameters.AddWithValue("@notes", notes);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<BranchCatalogItem>> ReadCatalogAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var items = new List<BranchCatalogItem>();
        await using var command = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BranchCatalogItem(reader.GetString(0), reader.GetString(1)));
        }

        return items;
    }

    private static async Task<List<BranchCatalogUnit>> ReadUnitsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var units = new List<BranchCatalogUnit>();
        await using var command = new SqlCommand(
            """
            SELECT item.ItemNumber, unit.InternalUnitItemNumber, unit.UnitsPerPackage
            FROM dbo.ItemUnits AS unit
            INNER JOIN dbo.Items AS item ON item.ItemID = unit.ItemID
            WHERE unit.InternalUnitItemNumber IS NOT NULL;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            units.Add(new BranchCatalogUnit(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2)));
        }

        return units;
    }

    private static async Task<List<BranchCatalogPrice>> ReadPricesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var prices = new List<BranchCatalogPrice>();
        await using var command = new SqlCommand(
            """
            SELECT item.ItemNumber, priceType.PriceCode, price.Price
            FROM dbo.ItemPrices AS price
            INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
            WHERE price.BranchID IS NULL;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(new BranchCatalogPrice(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2)));
        }

        return prices;
    }

    private static async Task<Dictionary<string, Guid>> ReadPriceTypesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var types = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var command = new SqlCommand("SELECT PriceCode, PriceTypeID FROM dbo.PriceTypes;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            types[reader.GetString(0)] = reader.GetGuid(1);
        }

        return types;
    }

    private static async Task BulkAsync(SqlConnection connection, SqlTransaction transaction, DataTable table, string destination, CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 180,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteCountAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        Guid branchId,
        CancellationToken cancellationToken,
        Guid? openingTypeId = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 180 };
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@opening", (object?)openingTypeId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task PrintCentralAsync(SqlEndpointSettings central, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = await OpenCentralAsync(central, cancellationToken);
        await PrintAsync(connection, output, """
            SELECT 'CentralBusinesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'CentralBranches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'CentralItems', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'CentralPrices', COUNT(*) FROM dbo.ItemPrices
            UNION ALL SELECT 'CentralUsers', COUNT(*) FROM dbo.Users;
            """, cancellationToken);
    }

    private static async Task PrintOldBranchAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = BranchDatabase.Name,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await PrintAsync(connection, output, """
            SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY AppliedAt, MigrationID;
            SELECT 'OldBusinesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'OldBranches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'OldUsers', COUNT(*) FROM dbo.Users
            UNION ALL SELECT 'OldItemsTable', CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;
            """, cancellationToken);
    }

    private static async Task PrintAsync(SqlConnection connection, TextWriter output, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var fields = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    fields[index] = reader.IsDBNull(index) ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
                }

                output.WriteLine(string.Join(" | ", fields));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
    }

    private static async Task<SqlConnection> OpenShopAsync(SqlEndpointSettings central, CancellationToken cancellationToken) =>
        await OpenAsync(central, Shop1OperationalAuditCommand.SourceDatabase, cancellationToken);

    private static async Task<SqlConnection> OpenCentralAsync(SqlEndpointSettings central, CancellationToken cancellationToken) =>
        await OpenAsync(central, CentralDatabase.Name, cancellationToken);

    private static async Task<SqlConnection> OpenAsync(SqlEndpointSettings central, string catalog, CancellationToken cancellationToken)
    {
        if (!string.Equals(catalog, Shop1OperationalAuditCommand.SourceDatabase, StringComparison.Ordinal)
            && !string.Equals(catalog, CentralDatabase.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Shop1 import refuses a source database other than Shop1 or XtraPOS_Central.");
        }

        var dataSource = central.Port is > 0 ? central.Server + "," + central.Port.Value : central.Server;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = catalog,
            UserID = central.Username,
            Password = central.Password,
            Encrypt = central.Encrypt,
            TrustServerCertificate = central.TrustServerCertificate,
            ConnectTimeout = 30,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "XtraPOS.Shop1OperationalImport",
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<int?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> GuidAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid guid ? guid : throw new InvalidOperationException("The branch target did not resolve a required identifier.");
    }

    private static async Task<string?> TextAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool IsApprovedTarget(string? name) =>
        string.Equals(name, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal)
        || (string.Equals(name, TestDatabaseName, StringComparison.Ordinal) && BranchDatabase.IsTestName(name));

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
