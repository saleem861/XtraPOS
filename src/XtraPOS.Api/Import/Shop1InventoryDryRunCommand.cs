using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Import;

public static class Shop1InventoryDryRunCommand
{
    public const string SelectSql =
        """
        SELECT
            ItemNumber,
            ItemName,
            CAST(Quantity AS decimal(18, 4)),
            CAST(Cost AS decimal(19, 4)),
            CAST(ItemSpeed AS decimal(18, 6)),
            ItemLastProvidedDate,
            IsAvailable,
            CAST(DealerPrice AS decimal(19, 4)),
            CAST(MajorCustomerPrice AS decimal(19, 4)),
            CAST(SpecialCustomerPrice AS decimal(19, 4)),
            CAST(Price0 AS decimal(19, 4)),
            CAST(Price1 AS decimal(19, 4)),
            CAST(Price2 AS decimal(19, 4)),
            CAST(Price3 AS decimal(19, 4))
        FROM dbo.Items;
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        var before = await ReadFingerprintAsync(connections, cancellationToken);
        try
        {
            var legacy = services.GetRequiredService<IOptions<LegacyImportOptions>>().Value;
            var central = CentralConnectionString(legacy);
            var shop = await Shop1ConnectionAsync(central, cancellationToken);
            var proposal = await BranchProposalAsync(central, cancellationToken);
            var shopItems = await ReadShop1Async(shop, cancellationToken);
            var master = await ReadMasterAsync(connections, cancellationToken);
            var report = Shop1BranchInventoryPlan.Analyze(shopItems, master, DateOnly.FromDateTime(DateTime.Today), proposal);
            var after = await ReadFingerprintAsync(connections, cancellationToken);
            var unchanged = before == after;
            var readiness = unchanged ? report.Readiness : Shop1BranchReadiness.NotReady;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XtraPOS", "dry-run-5c1");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shop1-inventory.json");
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(new
                {
                    report.Readiness,
                    FingerprintUnchanged = unchanged,
                    Before = before,
                    After = after,
                    Report = report,
                }, Json),
                Encoding.UTF8,
                cancellationToken);
            Write(report, before, after, unchanged, readiness, path);
            return readiness == Shop1BranchReadiness.Ready && unchanged ? 0 : 2;
        }
        catch
        {
            var after = await ReadFingerprintAsync(connections, cancellationToken);
            Console.WriteLine(before == after
                ? "Shop1 dry run failed before completion. XtraPOS_Branch was not changed."
                : "Shop1 dry run failed and XtraPOS_Branch changed.");
            throw;
        }
    }

    private static void Write(
        Shop1BranchInventoryReport report,
        string before,
        string after,
        bool unchanged,
        string readiness,
        string path)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("POS22=Shop1");
        Console.WriteLine($"Shop1Items={report.Shop1ItemCount} Matched={report.MatchedItems} Missing={report.MissingMasterItems} Duplicates={report.DuplicateMatches}");
        Console.WriteLine($"PlannedInventory={report.PlannedItemBranchInventory} OpeningBalances={report.PlannedOpeningBalances}");
        Console.WriteLine($"Quantity positive={report.PositiveQuantities} zero={report.ZeroQuantities} negative={report.NegativeQuantities} null={report.NullQuantities} fractional={report.FractionalQuantities}");
        Console.WriteLine($"QuantitySum planned={report.SumPlannedOnHandQuantity} positive={report.SumPositiveQuantity} negative={report.SumNegativeQuantity} value={report.InventoryValue}");
        Console.WriteLine($"Cost positive={report.PositiveCosts} zero={report.ZeroCosts} null={report.NullCosts} negative={report.NegativeCosts} min={report.MinimumCost} max={report.MaximumCost}");
        Console.WriteLine($"ItemSpeed >0={report.ItemSpeedPositive} =0={report.ItemSpeedZero} <0={report.ItemSpeedNegative} null={report.ItemSpeedNull}");
        Console.WriteLine($"Available={report.Available} Unavailable={report.Unavailable} available+positive={report.AvailableWithPositiveStock} available+zero={report.AvailableWithZeroStock} unavailable+positive={report.UnavailableWithPositiveStock} unavailable+zero/negative={report.UnavailableWithZeroOrNegativeStock}");
        Console.WriteLine("Overrides=" + string.Join(", ", report.PriceOverrides.Select(price => $"{price.PriceCode}:{price.DifferentFromDefault}+{price.MissingDefault}")));
        Console.WriteLine($"NegativePricesSkipped={report.NegativeBranchPricesSkipped} IdenticalSkipped={report.IdenticalBranchPricesSkipped} Retail={report.RetailOverrides}");
        Console.WriteLine($"InactiveMaster={report.InactiveMasterItems}");
        Console.WriteLine($"Restock null={report.RestockNull} future={report.RestockFuture} d0-90={report.RestockLast90Days} d91-180={report.RestockLast91To180Days} d181-365={report.RestockLast181To365Days} older={report.RestockOlderThan365Days}");
        Console.WriteLine($"ProposedBranch={report.ProposedBranch.BranchName} code={report.ProposedBranch.BranchCode} type={report.ProposedBranch.BranchType} station={report.ProposedBranch.StationNumber}:{report.ProposedBranch.StationName}");
        Console.WriteLine($"FingerprintBefore={before}");
        Console.WriteLine($"FingerprintAfter={after}");
        Console.WriteLine(unchanged ? "FingerprintUnchanged=1" : "FingerprintUnchanged=0");
        foreach (var reason in report.BlockingReasons)
        {
            Console.WriteLine(reason);
        }

        Console.WriteLine(path);
        Console.WriteLine(readiness);
    }

    private static async Task<Shop1BranchProposal> BranchProposalAsync(string centralConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(centralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT POSName, Description
            FROM dbo.POSs
            WHERE IsItReference = 1 AND POSNumber = 22;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new LegacyImportException("POS 22 was not found.");
        }

        var description = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
        if (!string.Equals(description, LegacyCatalogs.Shop1, StringComparison.Ordinal))
        {
            throw new LegacyImportException("POS 22 did not describe Shop1.");
        }

        return new Shop1BranchProposal(22, description, "22", "Retail", 1, "كاونتر 1");
    }

    private static async Task<List<Shop1OperationalItem>> ReadShop1Async(string connectionString, CancellationToken cancellationToken)
    {
        var rows = new List<Shop1OperationalItem>(3_000);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(SelectSql, connection) { CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Shop1OperationalItem(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ReadDecimal(reader, 2),
                ReadDecimal(reader, 3),
                ReadDecimal(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                ReadDecimal(reader, 7),
                ReadDecimal(reader, 8),
                ReadDecimal(reader, 9),
                ReadDecimal(reader, 10),
                ReadDecimal(reader, 11),
                ReadDecimal(reader, 12),
                ReadDecimal(reader, 13)));
        }

        return rows;
    }

    private static async Task<List<MasterItemMatch>> ReadMasterAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var priceCommand = new SqlCommand(
            """
            SELECT item.ItemNumber, priceType.PriceCode, price.Price
            FROM dbo.ItemPrices AS price
            INNER JOIN dbo.Items AS item ON item.ItemID = price.ItemID
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
            WHERE price.BranchID IS NULL
              AND priceType.PriceCode <> N'Retail';
            """,
            connection))
        {
            await using var reader = await priceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var number = reader.GetString(0);
                if (!prices.TryGetValue(number, out var codes))
                {
                    codes = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    prices[number] = codes;
                }

                codes[reader.GetString(1)] = reader.GetDecimal(2);
            }
        }

        var items = new List<MasterItemMatch>(34_000);
        await using var itemCommand = new SqlCommand("SELECT ItemNumber, IsActive FROM dbo.Items;", connection);
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            var number = itemReader.GetString(0);
            items.Add(new MasterItemMatch(
                number,
                itemReader.GetBoolean(1),
                prices.GetValueOrDefault(number) ?? new Dictionary<string, decimal>()));
        }

        return items;
    }

    private static async Task<string> Shop1ConnectionAsync(string centralConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(centralConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT POSConnectionString
            FROM dbo.POSs
            WHERE IsItReference = 1 AND POSNumber = 22;
            """,
            connection);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LegacyImportException("POS 22 has no Shop1 catalog connection.");
        }

        var builder = new SqlConnectionStringBuilder(value);
        if (!string.Equals(builder.InitialCatalog, LegacyCatalogs.Shop1, StringComparison.OrdinalIgnoreCase))
        {
            throw new LegacyImportException("POS 22 did not match the Shop1 catalog.");
        }

        builder.ApplicationName = "XtraPOS.Shop1InventoryDryRun";
        builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        if (builder.ConnectTimeout < 20)
        {
            builder.ConnectTimeout = 20;
        }

        return builder.ConnectionString;
    }

    private static string CentralConnectionString(LegacyImportOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Server)
            || !string.Equals(settings.Database, LegacyImportOptions.ExpectedDatabase, StringComparison.Ordinal))
        {
            throw new LegacyImportException("Legacy import is not configured for saleemPOSDB.");
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            InitialCatalog = LegacyImportOptions.ExpectedDatabase,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            ApplicationName = "XtraPOS.Shop1InventoryDryRun",
            ApplicationIntent = ApplicationIntent.ReadOnly,
        }.ConnectionString;
    }

    private static decimal? ReadDecimal(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    public static async Task<string> ReadFingerprintAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.Items), '|',
                (SELECT COUNT(*) FROM dbo.ItemPrices), '|',
                (SELECT COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL), '|',
                (SELECT COUNT(*) FROM dbo.ItemBranchInventory), '|',
                (SELECT COUNT(*) FROM dbo.StockMovements), '|',
                (SELECT COUNT(*) FROM dbo.Branches), '|',
                (SELECT COUNT(*) FROM dbo.POSStations), '|',
                (SELECT COUNT(*) FROM dbo.Users)
            );
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "");
    }
}
