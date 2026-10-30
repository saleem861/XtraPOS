using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Import;

public static class LegacyMasterDryRunCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        var before = await ReadFingerprintAsync(connections, cancellationToken);
        var read = Stopwatch.StartNew();
        var rows = await services.GetRequiredService<ILegacyItemSource>().ReadAsync(cancellationToken);
        read.Stop();
        var planning = Stopwatch.StartNew();
        var report = LegacyMasterCatalogDryRun.Analyze(rows);
        planning.Stop();
        var after = await ReadFingerprintAsync(connections, cancellationToken);
        total.Stop();
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            Console.WriteLine("Dry-run changed XtraPOS catalog state. This is a failure.");
            return 1;
        }

        if (report.PlannedRetailPrices != 0
            || report.PlannedBranchPriceInserts != 0
            || report.PlannedItemBranchInventory != 0
            || report.PlannedOpeningBalances != 0
            || report.PlannedPublishedOnline != 0)
        {
            Console.WriteLine("Dry-run policy failed. No catalog rows were written.");
            return 1;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XtraPOS",
            "dry-run-5b1");
        Directory.CreateDirectory(directory);
        await WriteAsync(directory, "summary.json", new
        {
            report.LegacyRowsReadBySource,
            report.CanonicalItems,
            report.CentralOnlyItems,
            report.BranchOnlyItems,
            report.Shop1OnlyItems,
            report.Shop2OnlyItems,
            report.SwstoreOnlyItems,
            report.MultiBranchWithoutCentral,
            report.AllSourcesItems,
            report.BranchConsensusCorrections,
            report.ItemNameBranchConsensusCorrections,
            report.MasterFieldConflicts,
            report.ItemNameMasterFieldConflicts,
            report.ExactNameAgreement,
            report.MissingItemNameFallbacks,
            report.ValidUnitLinks,
            report.SelfUnitReferencesIgnored,
            report.OrphanInternalUnits,
            report.UnitsPerPackageInvalid,
            report.MultiLevelDirectLinks,
            report.CircularPackageRelations,
            report.ValidParentLinks,
            report.OrphanParents,
            report.DefaultPricesToCreate,
            report.InvalidNegativePrices,
            report.PotentialBranchPriceOverrides,
            report.PlannedRetailPrices,
            report.PlannedBranchPriceInserts,
            report.PlannedItemBranchInventory,
            report.PlannedOpeningBalances,
            report.PlannedIsActive,
            report.PlannedPublishedOnline,
            report.BlockingIssues,
            report.ReviewRecommendedIssues,
            report.AutoResolvedIssues,
            report.ProhibitedRecommendation,
            report.ProhibitedTrueCentral,
            report.ProhibitedTrueShop1,
            report.ProhibitedTrueShop2,
            report.ProhibitedTrueSwstore,
            report.ProhibitedDisagreements,
            report.ProductTypes,
            report.Readiness,
            Timing = new
            {
                SourceReadMs = read.ElapsedMilliseconds,
                CanonicalizationMs = planning.ElapsedMilliseconds,
                TotalMs = total.ElapsedMilliseconds,
            },
            Fingerprint = before,
        }, cancellationToken);
        await WriteAsync(directory, "name-corrections.json", report.NameCorrections, cancellationToken);
        await WriteAsync(directory, "orphan-parents.json", report.OrphanParentDetails, cancellationToken);
        await WriteAsync(directory, "negative-prices.json", report.NegativePrices, cancellationToken);
        await WriteAsync(directory, "blocking.json", report.BlockingDetails, cancellationToken);
        await WriteAsync(directory, "samples.json", new
        {
            report.BranchConsensusSamples,
            report.MasterConflictSamples,
            report.OrphanUnitSamples,
            report.BranchOnlySamples,
        }, cancellationToken);

        var summary = new StringBuilder();
        summary.AppendLine("Legacy master catalog dry-run");
        foreach (var source in report.LegacyRowsReadBySource)
        {
            summary.AppendLine(
                $"{source.Source}: rows={source.Rows}, distinct={source.DistinctItemNumbers}, duplicates={source.DuplicateItemNumbers}, blank={source.BlankItemNumbers}");
        }

        summary.AppendLine($"CanonicalItems={report.CanonicalItems}");
        summary.AppendLine($"CentralOnly={report.CentralOnlyItems} BranchOnly={report.BranchOnlyItems} Shop1Only={report.Shop1OnlyItems} Shop2Only={report.Shop2OnlyItems} SwstoreOnly={report.SwstoreOnlyItems}");
        summary.AppendLine($"MultiBranchWithoutCentral={report.MultiBranchWithoutCentral} AllSources={report.AllSourcesItems}");
        summary.AppendLine($"NameAgreement={report.ExactNameAgreement} NameCorrections={report.ItemNameBranchConsensusCorrections} NameConflicts={report.ItemNameMasterFieldConflicts} MissingNames={report.MissingItemNameFallbacks}");
        summary.AppendLine($"FieldCorrections={report.BranchConsensusCorrections} FieldConflicts={report.MasterFieldConflicts}");
        summary.AppendLine($"Units valid={report.ValidUnitLinks} self={report.SelfUnitReferencesIgnored} orphan={report.OrphanInternalUnits} invalidPackage={report.UnitsPerPackageInvalid} multiLevel={report.MultiLevelDirectLinks} circular={report.CircularPackageRelations}");
        summary.AppendLine($"Parents valid={report.ValidParentLinks} orphan={report.OrphanParents}");
        summary.AppendLine("DefaultPrices=" + string.Join(", ", report.DefaultPricesToCreate.Select(price => $"{price.PriceCode}:{price.Count}")));
        summary.AppendLine($"InvalidNegativePrices={report.InvalidNegativePrices} RetailPlanned={report.PlannedRetailPrices}");
        summary.AppendLine("PotentialOverrides=" + string.Join(", ", report.PotentialBranchPriceOverrides.Select(price => $"{price.LegacyBranch}/{price.PriceCode}:{price.Count}")));
        summary.AppendLine($"Blocking={report.BlockingIssues} Review={report.ReviewRecommendedIssues} AutoResolved={report.AutoResolvedIssues}");
        summary.AppendLine(report.ProhibitedRecommendation);
        summary.AppendLine($"InventoryPlanned={report.PlannedItemBranchInventory} OpeningBalancesPlanned={report.PlannedOpeningBalances}");
        summary.AppendLine($"ReadMs={read.ElapsedMilliseconds} PlanMs={planning.ElapsedMilliseconds} TotalMs={total.ElapsedMilliseconds}");
        summary.AppendLine($"Report={directory}");
        summary.AppendLine(report.Readiness);
        summary.AppendLine("NO CATALOG ROWS WRITTEN");
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(summary.ToString());
        return 0;
    }

    public static async Task<int> RunCommitAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        var before = await ReadFingerprintAsync(connections, cancellationToken);
        var devicesBefore = await CountDevicesAsync(connections, cancellationToken);
        var rows = await services.GetRequiredService<ILegacyItemSource>().ReadAsync(cancellationToken);
        var plan = LegacyItemCanonicalizer.Plan(rows);
        var name1087 = plan.Items.SingleOrDefault(item => item.ItemNumber == "1087");
        var name50 = plan.Items.SingleOrDefault(item => item.ItemNumber == "50");
        if (name1087?.ItemName != "نكهة لومي فراولة وبطيخ 10 ml علبة" || name50?.ItemName != "50")
        {
            Console.WriteLine("Approved sample names changed. Nothing was written.");
            Console.WriteLine($"1087={name1087?.ItemName ?? "(missing)"}");
            Console.WriteLine($"50={name50?.ItemName ?? "(missing)"}");
            return 1;
        }

        var gate = LegacyMasterCatalogCommit.Prepare(rows, LegacyMasterCatalogCommit.Approved);
        if (!gate.CanCommit)
        {
            Console.WriteLine("Master commit was not started.");
            foreach (var failure in gate.GateFailures)
            {
                Console.WriteLine(failure);
            }

            return 1;
        }

        var report = await LegacyMasterCatalogCommit.CommitAsync(
            connections.CreateBranchConnectionString(),
            rows,
            LegacyMasterCatalogCommit.Approved,
            cancellationToken);
        var after = await ReadFingerprintAsync(connections, cancellationToken);
        var devicesAfter = await CountDevicesAsync(connections, cancellationToken);
        var protectedPrefix = before.Split('|').Skip(6).Take(6);
        var protectedAfter = after.Split('|').Skip(6).Take(6);
        if (!protectedPrefix.SequenceEqual(protectedAfter) || devicesBefore != devicesAfter)
        {
            Console.WriteLine("Master commit changed users, setup, or schema history.");
            return 1;
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var verification = await VerifyAsync(connection, rows, cancellationToken);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"CanonicalItems={report.CanonicalItems}");
        Console.WriteLine($"ItemsCreated={report.ItemsCreated} AlreadyExisting={report.ItemsAlreadyExisting}");
        Console.WriteLine($"ActiveItems={report.ActiveItems} InactiveItems={report.InactiveItems}");
        Console.WriteLine($"BranchConsensusCorrections={report.BranchConsensusCorrections} MissingNames={report.MissingItemNameFallbacks}");
        Console.WriteLine($"ParentLinksCreated={report.ParentLinksCreated} OrphanParents={report.OrphanParents}");
        Console.WriteLine($"ItemUnitsCreated={report.ItemUnitsCreated} OrphanUnits={report.OrphanInternalUnits} SelfIgnored={report.SelfUnitReferencesIgnored}");
        Console.WriteLine("PricesCreated=" + string.Join(", ", report.PricesCreatedByPriceCode.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
        Console.WriteLine($"NegativeSkipped={report.InvalidNegativePricesSkipped} RetailCreated={report.RetailPricesCreatedFromLegacy}");
        Console.WriteLine($"BranchPrices={report.BranchPricesCreated} Inventory={report.BranchInventoryCreated} Movements={report.StockMovementsCreated}");
        Console.WriteLine(verification);
        if (!verification.StartsWith("VERIFIED", StringComparison.Ordinal))
        {
            return 1;
        }

        await CatalogConsistency.RecordMasterCatalogAsync(connection, report.CanonicalItems, cancellationToken);
        Console.WriteLine("MASTER_CATALOG_COMMIT_COMPLETE");
        return 0;
    }

    private static async Task<string> VerifyAsync(SqlConnection connection, IReadOnlyList<LegacyItemObservation> rows, CancellationToken cancellationToken)
    {
        var branchNumbers = rows
            .Where(row => LegacyCatalogs.IsRealBranch(row.Source) && !string.IsNullOrWhiteSpace(row.ItemNumber))
            .Select(row => row.ItemNumber.Trim())
            .ToHashSet(StringComparer.Ordinal);
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM dbo.Items),
                (SELECT COUNT(*) FROM dbo.Items WHERE IsActive = 1),
                (SELECT COUNT(*) FROM dbo.Items WHERE IsActive = 0),
                (SELECT COUNT(*) FROM dbo.ItemUnits),
                (SELECT COUNT(*) FROM dbo.Items WHERE ParentItemID IS NOT NULL),
                (SELECT COUNT(*) FROM dbo.ItemPrices),
                (SELECT COUNT(*) FROM dbo.ItemPriceHistory),
                (SELECT COUNT(*) FROM dbo.ItemBranchInventory),
                (SELECT COUNT(*) FROM dbo.StockMovements),
                (SELECT COUNT(*) FROM dbo.ItemPrices AS price INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID WHERE priceType.PriceCode = N'Retail'),
                (SELECT COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL),
                (SELECT ItemName FROM dbo.Items WHERE ItemNumber = N'1087'),
                (SELECT ItemName FROM dbo.Items WHERE ItemNumber = N'50'),
                (SELECT CAST(IsActive AS int) FROM dbo.Items WHERE ItemNumber = N'50');
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var items = reader.GetInt32(0);
        var active = reader.GetInt32(1);
        var inactive = reader.GetInt32(2);
        var units = reader.GetInt32(3);
        var parents = reader.GetInt32(4);
        var prices = reader.GetInt32(5);
        var history = reader.GetInt32(6);
        var inventory = reader.GetInt32(7);
        var movements = reader.GetInt32(8);
        var retail = reader.GetInt32(9);
        var branchPrices = reader.GetInt32(10);
        var name1087 = reader.IsDBNull(11) ? "" : reader.GetString(11);
        var name50 = reader.IsDBNull(12) ? "" : reader.GetString(12);
        var active50 = reader.IsDBNull(13) ? -1 : reader.GetInt32(13);
        await reader.CloseAsync();

        var problems = new List<string>();
        if (items != 33772) problems.Add($"Items={items}");
        if (active != 2812) problems.Add($"Active={active}");
        if (inactive != 30960) problems.Add($"Inactive={inactive}");
        if (units != 458) problems.Add($"Units={units}");
        if (parents != 21067) problems.Add($"Parents={parents}");
        if (prices != 234461) problems.Add($"Prices={prices}");
        if (history != 0) problems.Add($"History={history}");
        if (inventory != 0) problems.Add($"Inventory={inventory}");
        if (movements != 0) problems.Add($"Movements={movements}");
        if (retail != 0) problems.Add($"Retail={retail}");
        if (branchPrices != 0) problems.Add($"BranchPrices={branchPrices}");
        if (name1087 != "نكهة لومي فراولة وبطيخ 10 ml علبة") problems.Add("1087 name");
        if (name50 != "50" || active50 != 0) problems.Add("item 50");

        problems.AddRange(await SampleAsync(connection, branchNumbers, true, cancellationToken));
        problems.AddRange(await SampleAsync(connection, branchNumbers, false, cancellationToken));
        return problems.Count == 0
            ? "VERIFIED"
            : "VERIFICATION FAILED: " + string.Join("; ", problems);
    }

    private static async Task<List<string>> SampleAsync(
        SqlConnection connection,
        HashSet<string> branchNumbers,
        bool active,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        await using var command = new SqlCommand(
            active
                ? "SELECT TOP (10) ItemNumber FROM dbo.Items WHERE IsActive = 1 ORDER BY NEWID();"
                : "SELECT TOP (10) ItemNumber FROM dbo.Items WHERE IsActive = 0 ORDER BY NEWID();",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            var number = reader.GetString(0);
            var present = branchNumbers.Contains(number);
            if (active && !present)
            {
                problems.Add($"Active item {number} is not in a real branch catalog.");
            }

            if (!active && present)
            {
                problems.Add($"Inactive item {number} is in a real branch catalog.");
            }
        }

        if (count != 10)
        {
            problems.Add(active ? "Fewer than 10 active items." : "Fewer than 10 inactive items.");
        }

        return problems;
    }

    private static async Task WriteAsync<T>(string directory, string name, T value, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json), Encoding.UTF8, cancellationToken);
    }

    private static async Task<int> CountDevicesAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Devices;", connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public static async Task<string> ReadFingerprintAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.Items), '|',
                (SELECT COUNT(*) FROM dbo.ItemUnits), '|',
                (SELECT COUNT(*) FROM dbo.ItemPrices), '|',
                (SELECT COUNT(*) FROM dbo.ItemPriceHistory), '|',
                (SELECT COUNT(*) FROM dbo.ItemBranchInventory), '|',
                (SELECT COUNT(*) FROM dbo.StockMovements), '|',
                (SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory), '|',
                (SELECT COUNT(*) FROM dbo.Users), '|',
                (SELECT COUNT(*) FROM dbo.UserRoles), '|',
                (SELECT COUNT(*) FROM dbo.Businesses), '|',
                (SELECT COUNT(*) FROM dbo.Branches), '|',
                (SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max),
                    (SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY MigrationID FOR XML PATH('m')))), 2))
            );
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "");
    }
}
