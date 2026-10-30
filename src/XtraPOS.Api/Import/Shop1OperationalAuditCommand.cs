using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Import;

public static class Shop1OperationalAuditCommand
{
    public const string CommandName = "--audit-shop1-operations";
    public const string SourceDatabase = "Shop1";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        if (!string.Equals(confirmed, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal)
            || !string.Equals(connections.ConfiguredDatabase, confirmed, StringComparison.Ordinal))
        {
            output.WriteLine("Shop1 audit requires --confirm-database XtraPOS_Branch_01 and that development database.");
            return 1;
        }

        var requestedServer = ReadOption(args, "--source-server");
        var expectedServer = central.Port is > 0 ? central.Server + "," + central.Port.Value : central.Server;
        if (!string.Equals(requestedServer?.Trim(), expectedServer, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestedServer?.Trim(), central.Server, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Shop1 audit requires --source-server matching the saved Central SQL server.");
            return 1;
        }

        output.WriteLine("== BEFORE");
        var beforeBranch = await FingerprintBranchAsync(connections, output, cancellationToken);
        var beforeCentral = await FingerprintCentralAsync(central, output, cancellationToken);
        var beforeOld = await FingerprintOldBranchAsync(output, cancellationToken);

        var shopRows = new List<Shop1OperationalRow>();
        await using (var shop = await OpenAsync(central, SourceDatabase, cancellationToken))
        {
            output.WriteLine("Shop1DateColumns=" + string.Join(",", await ColumnsAsync(shop, cancellationToken)));
            await WriteDateAuditAsync(shop, output, cancellationToken);
            shopRows.AddRange(await ReadItemsAsync(shop, cancellationToken));
        }

        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        var catalog = await ReadCatalogAsync(branch, cancellationToken);
        var units = await ReadUnitsAsync(branch, cancellationToken);
        var prices = await ReadPricesAsync(branch, cancellationToken);
        var dailySalesNullable = await ScalarAsync(
            branch,
            "SELECT COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'DailySalesRate', N'AllowsNull');",
            cancellationToken);
        var plan = Shop1OperationalPlanner.Plan(
            shopRows,
            catalog,
            units,
            prices,
            ["1087", "1300", "1350", "1360"],
            DateTime.UtcNow);

        WritePlan(output, plan, dailySalesNullable);
        output.WriteLine("== AFTER");
        var afterBranch = await FingerprintBranchAsync(connections, output, cancellationToken);
        var afterCentral = await FingerprintCentralAsync(central, output, cancellationToken);
        var afterOld = await FingerprintOldBranchAsync(output, cancellationToken);
        var unchanged = beforeBranch == afterBranch && beforeCentral == afterCentral && beforeOld == afterOld;
        output.WriteLine(unchanged ? "FINGERPRINTS_UNCHANGED" : "FINGERPRINTS_CHANGED");
        output.WriteLine("READ_ONLY");
        return unchanged ? 0 : 2;
    }

    private const string ShopSql =
        """
        SELECT
            ItemNumber,
            ItemName,
            Quantity,
            Cost,
            DealerPrice,
            MajorCustomerPrice,
            SpecialCustomerPrice,
            Price0,
            Price1,
            Price2,
            Price3,
            ItemLastProvidedDate
        FROM dbo.Items;
        """;

    internal static async Task<List<Shop1OperationalRow>> ReadItemsAsync(SqlConnection shop, CancellationToken cancellationToken)
    {
        var shopRows = new List<Shop1OperationalRow>();
        await using var command = new SqlCommand(ShopSql, shop) { CommandTimeout = 180 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            shopRows.Add(ReadShop(reader));
        }

        return shopRows;
    }

    private static void WritePlan(TextWriter output, Shop1OperationalPlan plan, int? dailySalesNullable)
    {
        output.WriteLine("ShopRows=" + plan.ShopRows);
        output.WriteLine("DistinctShopItemNumbers=" + plan.DistinctShopItemNumbers);
        output.WriteLine("BlankItemRows=" + plan.BlankItemRows);
        output.WriteLine("TechnicalRows=" + plan.TechnicalRows);
        output.WriteLine("DuplicateItemNumbers=" + plan.DuplicateItemNumbers);
        output.WriteLine("ConflictingDuplicateItemNumbers=" + plan.ConflictingDuplicateItemNumbers);
        output.WriteLine("MatchedItems=" + plan.MatchedItems);
        output.WriteLine("ShopOnlyItems=" + plan.ShopOnlyItems);
        output.WriteLine("CatalogAbsentFromShop=" + plan.CatalogAbsentFromShop);
        output.WriteLine("Available=" + plan.Available);
        output.WriteLine("Unavailable=" + plan.Unavailable);
        output.WriteLine("InventoryRows=" + plan.InventoryRows);
        output.WriteLine("PositiveCosts=" + plan.PositiveCosts);
        output.WriteLine("ZeroCosts=" + plan.ZeroCosts);
        output.WriteLine("NegativeCosts=" + plan.NegativeCosts);
        output.WriteLine("NullCosts=" + plan.NullCosts);
        output.WriteLine("UnparsedCosts=" + plan.UnparsedCosts);
        output.WriteLine("ProposedUnitCosts=" + plan.ProposedUnitCosts);
        output.WriteLine("CostFamilyAnomalies=" + plan.CostFamilyAnomalies);
        output.WriteLine("StockOwners=" + plan.StockOwners);
        output.WriteLine("DerivedPackages=" + plan.DerivedPackages);
        output.WriteLine("MultiHopItems=" + plan.MultiHopItems);
        output.WriteLine("UnresolvedItems=" + plan.UnresolvedItems);
        output.WriteLine("CycleItems=" + plan.CycleItems);
        output.WriteLine("TrustedOwners=" + plan.TrustedOwners);
        output.WriteLine("AmbiguousOwners=" + plan.AmbiguousOwners);
        output.WriteLine("InvalidOwners=" + plan.InvalidOwners);
        output.WriteLine("ProposedInitializedBalances=" + plan.ProposedInitializedBalances);
        output.WriteLine("ProposedUninitializedBalances=" + plan.ProposedUninitializedBalances);
        output.WriteLine("ProposedOpeningMovements=" + plan.ProposedOpeningMovements);
        output.WriteLine("NegativePricesExcluded=" + plan.NegativePricesExcluded);
        output.WriteLine("IdenticalPricesSkipped=" + plan.IdenticalPricesSkipped);
        foreach (var code in Shop1OperationalPlanner.PriceCodes)
        {
            output.WriteLine("Override " + code + "=" + plan.OverridesByPriceType[code] + " explicitZero=" + plan.ZeroOverridesByPriceType[code]);
        }

        output.WriteLine("LastProvidedPresent=" + plan.LastProvidedPresent);
        output.WriteLine("LastProvidedMissing=" + plan.LastProvidedMissing);
        output.WriteLine("LastProvidedFuture=" + plan.LastProvidedFuture);
        output.WriteLine("DailySalesRateAllowsNull=" + (dailySalesNullable?.ToString(CultureInfo.InvariantCulture) ?? ""));
        foreach (var note in plan.Notes)
        {
            output.WriteLine("Rule: " + note);
        }

        foreach (var sample in plan.Samples.Concat(plan.ExtraFamilies))
        {
            output.WriteLine(string.Join(" | ",
                "Sample",
                sample.ItemNumber,
                sample.ItemName,
                "shop=" + sample.InShop1,
                "qty=" + sample.ShopQuantity,
                "cost=" + sample.ShopCost,
                "owner=" + sample.OwnerItemNumber,
                "factor=" + sample.Factor,
                "derived=" + sample.DerivedQuantity,
                sample.OwnershipState,
                sample.QuantityClassification,
                sample.PriceText));
        }
    }

    private static Shop1OperationalRow ReadShop(SqlDataReader reader)
    {
        var quantity = ReadDecimal(reader, 2, out var quantityUnparsed);
        var cost = ReadDecimal(reader, 3, out var costUnparsed);
        return new Shop1OperationalRow(
            ReadText(reader, 0) ?? "",
            ReadText(reader, 1),
            quantity,
            quantityUnparsed,
            cost,
            costUnparsed,
            ReadDecimal(reader, 4, out _),
            ReadDecimal(reader, 5, out _),
            ReadDecimal(reader, 6, out _),
            ReadDecimal(reader, 7, out _),
            ReadDecimal(reader, 8, out _),
            ReadDecimal(reader, 9, out _),
            ReadDecimal(reader, 10, out _),
            ReadDate(reader, 11));
    }

    private static string? ReadText(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal? ReadDecimal(SqlDataReader reader, int ordinal, out bool unparsed)
    {
        unparsed = false;
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        switch (value)
        {
            case decimal number:
                return number;
            case int number:
                return number;
            case long number:
                return number;
            case short number:
                return number;
            case byte number:
                return number;
            case double number when double.IsFinite(number):
                try
                {
                    return (decimal)number;
                }
                catch (OverflowException)
                {
                    unparsed = true;
                    return null;
                }
            case float number when float.IsFinite(number):
                try
                {
                    return (decimal)number;
                }
                catch (OverflowException)
                {
                    unparsed = true;
                    return null;
                }
            case string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                unparsed = true;
                return null;
        }
    }

    private static DateTime? ReadDate(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime date => date,
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) => parsed,
            _ => null,
        };
    }

    private static async Task WriteDateAuditAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        foreach (var column in new[] { "ItemLastProvidedDate", "DateInserted", "HidingLastDate" })
        {
            await using var command = new SqlCommand(
                $"""
                SELECT
                    MAX(DATA_TYPE),
                    COUNT(*),
                    SUM(CASE WHEN [{column}] IS NULL THEN 1 ELSE 0 END),
                    CONVERT(varchar(30), MIN([{column}]), 126),
                    CONVERT(varchar(30), MAX([{column}]), 126)
                FROM INFORMATION_SCHEMA.COLUMNS
                CROSS JOIN dbo.Items
                WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'Items' AND COLUMN_NAME = N'{column}';
                """,
                connection)
            {
                CommandTimeout = 180,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
            {
                output.WriteLine(string.Join(" | ",
                    "DateAudit",
                    column,
                    "type=" + reader.GetString(0),
                    "rows=" + reader.GetInt32(1),
                    "nulls=" + (reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)),
                    "min=" + (reader.IsDBNull(3) ? "" : reader.GetString(3)),
                    "max=" + (reader.IsDBNull(4) ? "" : reader.GetString(4))));
            }
        }
    }

    private static async Task<List<string>> ColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await using var command = new SqlCommand(
            """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'Items'
              AND (COLUMN_NAME LIKE N'%Date%' OR COLUMN_NAME LIKE N'%Sale%' OR COLUMN_NAME LIKE N'%Speed%'
                   OR COLUMN_NAME LIKE N'%Cost%' OR COLUMN_NAME LIKE N'%Quant%' OR COLUMN_NAME LIKE N'%Provided%'
                   OR COLUMN_NAME LIKE N'%Avail%')
            ORDER BY COLUMN_NAME;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
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

    private static async Task<string> FingerprintBranchAsync(
        SqlConnectionFactory connections,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var text = await ReadLinesAsync(connection, """
            SELECT 'BranchItems', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'BranchUnits', COUNT(*) FROM dbo.ItemUnits
            UNION ALL SELECT 'BranchInventory', COUNT(*) FROM dbo.ItemBranchInventory
            UNION ALL SELECT 'BranchBalances', COUNT(*) FROM dbo.BranchStockBalances
            UNION ALL SELECT 'BranchMovements', COUNT(*) FROM dbo.StockMovements
            UNION ALL SELECT 'BranchOverrides', COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL;
            """, cancellationToken);
        output.WriteLine(text);
        return text;
    }

    private static async Task<string> FingerprintCentralAsync(
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(central, CentralDatabase.Name, cancellationToken);
        var text = await ReadLinesAsync(connection, """
            SELECT 'CentralBusinesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'CentralBranches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'CentralItems', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'CentralPrices', COUNT(*) FROM dbo.ItemPrices
            UNION ALL SELECT 'CentralUsers', COUNT(*) FROM dbo.Users;
            """, cancellationToken);
        output.WriteLine(text);
        return text;
    }

    private static async Task<string> FingerprintOldBranchAsync(TextWriter output, CancellationToken cancellationToken)
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
        var text = await ReadLinesAsync(connection, """
            SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY AppliedAt, MigrationID;
            SELECT 'OldBusinesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'OldBranches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'OldUsers', COUNT(*) FROM dbo.Users
            UNION ALL SELECT 'OldItemsTable', CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;
            """, cancellationToken);
        output.WriteLine(text);
        return text;
    }

    private static async Task<string> ReadLinesAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await using var command = new SqlCommand(sql, connection);
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

                lines.Add(string.Join(" | ", fields));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<int?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<SqlConnection> OpenAsync(SqlEndpointSettings central, string catalog, CancellationToken cancellationToken)
    {
        if (!string.Equals(catalog, SourceDatabase, StringComparison.Ordinal) && !string.Equals(catalog, CentralDatabase.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Shop1 audit refuses a database other than Shop1 or XtraPOS_Central.");
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
            ApplicationName = "XtraPOS.Shop1OperationalAudit",
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

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
