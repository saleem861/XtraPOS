using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Import;

public sealed record LegacyMasterCommitExpectation(
    int CanonicalItems,
    int ActiveItems,
    int ItemUnits,
    int ParentLinks,
    int DefaultPrices,
    int NegativePrices);

public sealed record LegacyMasterCommitPlan(
    int CanonicalItems,
    int ActiveItems,
    int InactiveItems,
    int BlockingIssues,
    int BranchConsensusCorrections,
    int MissingItemNameFallbacks,
    int ParentLinks,
    int OrphanParents,
    int ItemUnits,
    int OrphanInternalUnits,
    int SelfUnitReferencesIgnored,
    int DefaultPrices,
    int NegativePrices,
    int RetailPrices,
    int BranchPrices,
    IReadOnlyDictionary<string, int> PricesByCode,
    IReadOnlyList<string> GateFailures,
    bool CanCommit);

public sealed record LegacyMasterCommitReport(
    int CanonicalItems,
    int ItemsCreated,
    int ItemsAlreadyExisting,
    int ActiveItems,
    int InactiveItems,
    int BranchConsensusCorrections,
    int MissingItemNameFallbacks,
    int ParentLinksCreated,
    int OrphanParents,
    int ItemUnitsCreated,
    int OrphanInternalUnits,
    int SelfUnitReferencesIgnored,
    IReadOnlyDictionary<string, int> PricesCreatedByPriceCode,
    int InvalidNegativePricesSkipped,
    int RetailPricesCreatedFromLegacy,
    int BranchPricesCreated,
    int BranchInventoryCreated,
    int StockMovementsCreated);

public static class LegacyMasterCatalogCommit
{
    public static readonly LegacyMasterCommitExpectation Approved = new(33772, 2812, 458, 21067, 234461, 37);

    public static LegacyMasterCommitPlan Prepare(IReadOnlyList<LegacyItemObservation> rows, LegacyMasterCommitExpectation? expectation)
    {
        var staged = Stage(rows);
        var failures = new List<string>();
        if (staged.BlockingIssues > 0)
        {
            failures.Add($"Blocking issues: {staged.BlockingIssues}.");
        }

        if (staged.RetailPrices > 0)
        {
            failures.Add($"Legacy Retail prices planned: {staged.RetailPrices}.");
        }

        if (staged.TrialBranchPrices > 0)
        {
            failures.Add("A price for فرع التجربة was planned.");
        }

        if (expectation is not null)
        {
            Compare(failures, "CanonicalItems", staged.CanonicalItems, expectation.CanonicalItems);
            Compare(failures, "ActiveItems", staged.ActiveItems, expectation.ActiveItems);
            Compare(failures, "ItemUnits", staged.ItemUnits, expectation.ItemUnits);
            Compare(failures, "ParentLinks", staged.ParentLinks, expectation.ParentLinks);
            Compare(failures, "DefaultPrices", staged.DefaultPrices, expectation.DefaultPrices);
            Compare(failures, "NegativePrices", staged.NegativePrices, expectation.NegativePrices);
        }

        return new LegacyMasterCommitPlan(
            staged.CanonicalItems,
            staged.ActiveItems,
            staged.InactiveItems,
            staged.BlockingIssues,
            staged.Corrections,
            staged.MissingNames,
            staged.ParentLinks,
            staged.OrphanParents,
            staged.ItemUnits,
            staged.OrphanUnits,
            staged.SelfUnits,
            staged.DefaultPrices,
            staged.NegativePrices,
            staged.RetailPrices,
            staged.BranchPrices,
            staged.PricesByCode,
            failures,
            failures.Count == 0);
    }

    public static async Task<LegacyMasterCommitReport> CommitAsync(
        string connectionString,
        IReadOnlyList<LegacyItemObservation> rows,
        LegacyMasterCommitExpectation? expectation,
        CancellationToken cancellationToken)
    {
        var staged = Stage(rows);
        var plan = Prepare(rows, expectation);
        if (!plan.CanCommit)
        {
            throw new LegacyImportException(string.Join(" ", plan.GateFailures));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, "SET XACT_ABORT ON;", cancellationToken);
            await ExecuteAsync(connection, transaction, StageTablesSql, cancellationToken);
            await BulkAsync(connection, transaction, "#ItemStage", staged.Items, cancellationToken);
            await BulkAsync(connection, transaction, "#UnitStage", staged.Units, cancellationToken);
            await BulkAsync(connection, transaction, "#PriceStage", staged.Prices, cancellationToken);
            var counts = await ReadCountsAsync(connection, transaction, InsertSql, cancellationToken);
            if (counts.PricesCreated != counts.PricesForNewItems)
            {
                throw new LegacyImportException("Default price insert did not match the staged prices for new items.");
            }

            if (counts.RetailCreated != 0 || counts.BranchPricesCreated != 0 || counts.InventoryCreated != 0 || counts.MovementsCreated != 0 || counts.HistoryCreated != 0)
            {
                throw new LegacyImportException("The master commit tried to write retail, branch, inventory, movement, or price-history rows.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new LegacyMasterCommitReport(
                staged.CanonicalItems,
                counts.ItemsCreated,
                counts.ItemsExisting,
                staged.ActiveItems,
                staged.InactiveItems,
                staged.Corrections,
                staged.MissingNames,
                counts.ParentsCreated,
                staged.OrphanParents,
                counts.UnitsCreated,
                staged.OrphanUnits,
                staged.SelfUnits,
                counts.PricesByCode,
                staged.NegativePrices,
                counts.RetailCreated,
                counts.BranchPricesCreated,
                counts.InventoryCreated,
                counts.MovementsCreated);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void Compare(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name} is {actual}, approved plan is {expected}.");
        }
    }

    private static StagedCatalog Stage(IReadOnlyList<LegacyItemObservation> rows)
    {
        var plan = LegacyItemCanonicalizer.Plan(rows);
        var canonicalNumbers = plan.Items.Select(item => item.ItemNumber).ToHashSet(StringComparer.Ordinal);
        var activeNumbers = rows
            .Where(row => LegacyCatalogs.IsRealBranch(row.Source) && !string.IsNullOrWhiteSpace(row.ItemNumber))
            .Select(row => row.ItemNumber.Trim())
            .Where(canonicalNumbers.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var activeCount = activeNumbers.Count;
        var prohibited = ProhibitedValues(rows);
        if (prohibited.Disagreements > 0)
        {
            throw new LegacyImportException($"IsProhibited disagrees across branches for {prohibited.Disagreements} items.");
        }

        var items = new DataTable();
        items.Columns.Add("ItemNumber", typeof(string));
        items.Columns.Add("ItemName", typeof(string));
        items.Columns.Add("ItemType", typeof(string));
        items.Columns.Add("Manufacturer", typeof(string));
        items.Columns.Add("Color", typeof(string));
        items.Columns.Add("Taste", typeof(string));
        items.Columns.Add("Size", typeof(string));
        items.Columns.Add("Packing", typeof(string));
        items.Columns.Add("Classification", typeof(string));
        items.Columns.Add("CountryOfOrigin", typeof(string));
        items.Columns.Add("ProductType", typeof(string));
        items.Columns.Add("ParentItemNumber", typeof(string));
        items.Columns.Add("IsProhibited", typeof(bool));
        items.Columns.Add("IsActive", typeof(bool));

        var units = new DataTable();
        units.Columns.Add("ItemNumber", typeof(string));
        units.Columns.Add("UnitsPerPackage", typeof(decimal));
        units.Columns.Add("InternalUnitItemNumber", typeof(string));
        units.Columns.Add("InternalUnitItemName", typeof(string));

        var prices = new DataTable();
        prices.Columns.Add("ItemNumber", typeof(string));
        prices.Columns.Add("PriceCode", typeof(string));
        prices.Columns.Add("Price", typeof(decimal));

        var pricesByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var retail = 0;
        var branchPrices = 0;
        var trialPrices = 0;
        foreach (var item in plan.Items)
        {
            RequireLength(item.ItemNumber, 100, item.ItemNumber, "ItemNumber");
            RequireLength(item.ItemName, 500, item.ItemNumber, "ItemName");
            items.Rows.Add(
                item.ItemNumber,
                item.ItemName,
                Db(Fit(item.ItemType, 100, item.ItemNumber, "ItemType")),
                Db(Fit(item.Manufacturer, 100, item.ItemNumber, "Manufacturer")),
                Db(Fit(item.Color, 100, item.ItemNumber, "Color")),
                Db(Fit(item.Taste, 100, item.ItemNumber, "Taste")),
                Db(Fit(item.Size, 100, item.ItemNumber, "Size")),
                Db(Fit(item.Packing, 100, item.ItemNumber, "Packing")),
                Db(Fit(item.Classification, 200, item.ItemNumber, "Classification")),
                Db(Fit(item.CountryOfOrigin, 100, item.ItemNumber, "CountryOfOrigin")),
                Db(Fit(item.ProductType, 50, item.ItemNumber, "ProductType")),
                Db(Fit(item.ParentResolved ? item.ParentItemNumber : null, 100, item.ItemNumber, "ParentItemNumber")),
                prohibited.Values.GetValueOrDefault(item.ItemNumber),
                activeNumbers.Contains(item.ItemNumber));

            if (item.Unit is not null)
            {
                RequireLength(item.Unit.InternalUnitItemNumber, 100, item.ItemNumber, "InternalUnitItemNumber");
                units.Rows.Add(
                    item.ItemNumber,
                    item.Unit.UnitsPerPackage,
                    item.Unit.InternalUnitItemNumber,
                    Db(Fit(item.Unit.InternalUnitItemName, 500, item.ItemNumber, "InternalUnitItemName")));
            }

            foreach (var price in item.Prices)
            {
                if (price.PriceCode == "Retail")
                {
                    retail++;
                    continue;
                }

                if (price.BranchCatalog is not null)
                {
                    branchPrices++;
                    if (price.BranchCatalog == LegacyCatalogs.TrialBranch)
                    {
                        trialPrices++;
                    }

                    continue;
                }

                if (price.Price < 0)
                {
                    throw new LegacyImportException($"A negative price reached the commit stage for {item.ItemNumber}.");
                }

                prices.Rows.Add(item.ItemNumber, price.PriceCode, price.Price);
                pricesByCode[price.PriceCode] = pricesByCode.GetValueOrDefault(price.PriceCode) + 1;
            }
        }

        return new StagedCatalog(
            plan.CanonicalItems,
            activeCount,
            plan.CanonicalItems - activeCount,
            plan.Issues.Count(issue => issue.Unresolved) + rows.Count(row => LegacyCatalogs.IsKnown(row.Source) && string.IsNullOrWhiteSpace(row.ItemNumber)),
            plan.BranchConsensusCorrections,
            plan.MissingItemNameFallbacks,
            plan.ValidParentLinks,
            plan.OrphanParents,
            plan.ValidUnitLinks,
            plan.OrphanInternalUnits,
            plan.Issues.Count(issue => issue.Category == LegacyImportCategories.SelfReferenceIgnored),
            prices.Rows.Count,
            plan.InvalidNegativePrices,
            retail,
            branchPrices,
            trialPrices,
            pricesByCode,
            items,
            units,
            prices);
    }

    private static (Dictionary<string, bool> Values, int Disagreements) ProhibitedValues(IReadOnlyList<LegacyItemObservation> rows)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        var disagreements = 0;
        foreach (var group in rows.Where(row => LegacyCatalogs.IsKnown(row.Source) && !string.IsNullOrWhiteSpace(row.ItemNumber)).GroupBy(row => row.ItemNumber.Trim(), StringComparer.Ordinal))
        {
            var branchFlags = group
                .Where(row => LegacyCatalogs.IsRealBranch(row.Source) && row.IsProhibited is not null)
                .Select(row => row.IsProhibited!.Value)
                .Distinct()
                .ToList();
            if (branchFlags.Count > 1)
            {
                disagreements++;
                continue;
            }

            if (branchFlags.Count == 1)
            {
                values[group.Key] = branchFlags[0];
                continue;
            }

            var central = group.FirstOrDefault(row => row.Source == LegacyCatalogs.Central && row.IsProhibited is not null);
            values[group.Key] = central?.IsProhibited ?? false;
        }

        return (values, disagreements);
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static string? Fit(string? value, int max, string itemNumber, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        RequireLength(text, max, itemNumber, field);
        return text;
    }

    private static void RequireLength(string value, int max, string itemNumber, string field)
    {
        if (value.Length > max)
        {
            throw new LegacyImportException($"{field} for {itemNumber} is longer than {max} characters.");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task BulkAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable data, CancellationToken cancellationToken)
    {
        if (data.Rows.Count == 0)
        {
            return;
        }

        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = table,
            BatchSize = 5000,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn column in data.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(data, cancellationToken);
    }

    private static async Task<InsertCounts> ReadCountsAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var pricesByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new InsertCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            pricesByCode);
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pricesByCode[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return counts;
    }

    private const string StageTablesSql =
        """
        CREATE TABLE #ItemStage
        (
            ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
            ItemName nvarchar(500) COLLATE DATABASE_DEFAULT NOT NULL,
            ItemType nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Manufacturer nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Color nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Taste nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Size nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Packing nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            Classification nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
            CountryOfOrigin nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            ProductType nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
            ParentItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
            IsProhibited bit NOT NULL,
            IsActive bit NOT NULL
        );

        CREATE TABLE #UnitStage
        (
            ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
            UnitsPerPackage decimal(18, 4) NOT NULL,
            InternalUnitItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
            InternalUnitItemName nvarchar(500) COLLATE DATABASE_DEFAULT NULL
        );

        CREATE TABLE #PriceStage
        (
            ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
            PriceCode nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
            Price decimal(19, 4) NOT NULL
        );

        CREATE TABLE #Inserted
        (
            ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
            ItemID uniqueidentifier NOT NULL
        );
        """;

    private const string InsertSql =
        """
        SET NOCOUNT ON;

        INSERT INTO dbo.Items
        (
            ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing,
            Classification, CountryOfOrigin, ProductType, IsProhibited, IsPublishedOnline, IsActive
        )
        OUTPUT inserted.ItemNumber, inserted.ItemID INTO #Inserted (ItemNumber, ItemID)
        SELECT
            stage.ItemNumber, stage.ItemName, stage.ItemType, stage.Manufacturer, stage.Color, stage.Taste, stage.Size, stage.Packing,
            stage.Classification, stage.CountryOfOrigin, stage.ProductType, stage.IsProhibited, 0, stage.IsActive
        FROM #ItemStage AS stage
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS existing WHERE existing.ItemNumber = stage.ItemNumber);

        UPDATE item
        SET ParentItemID = parent.ItemID,
            UpdatedAt = SYSUTCDATETIME()
        FROM dbo.Items AS item
        INNER JOIN #Inserted AS created ON created.ItemNumber = item.ItemNumber
        INNER JOIN #ItemStage AS stage ON stage.ItemNumber = item.ItemNumber
        INNER JOIN dbo.Items AS parent ON parent.ItemNumber = stage.ParentItemNumber
        WHERE stage.ParentItemNumber IS NOT NULL;

        INSERT INTO dbo.ItemUnits
        (
            ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive
        )
        SELECT item.ItemID, unit.UnitsPerPackage, target.ItemID, unit.InternalUnitItemNumber, unit.InternalUnitItemName, 0, 1
        FROM #UnitStage AS unit
        INNER JOIN #Inserted AS created ON created.ItemNumber = unit.ItemNumber
        INNER JOIN dbo.Items AS item ON item.ItemNumber = unit.ItemNumber
        INNER JOIN dbo.Items AS target ON target.ItemNumber = unit.InternalUnitItemNumber
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM dbo.ItemUnits AS existing
            WHERE existing.ItemID = item.ItemID
              AND existing.InternalUnitItemNumber = unit.InternalUnitItemNumber
        );

        INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive)
        SELECT item.ItemID, priceType.PriceTypeID, NULL, price.Price, 1
        FROM #PriceStage AS price
        INNER JOIN #Inserted AS created ON created.ItemNumber = price.ItemNumber
        INNER JOIN dbo.Items AS item ON item.ItemNumber = price.ItemNumber
        INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceCode = price.PriceCode
        WHERE price.PriceCode <> N'Retail'
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.ItemPrices AS existing
              WHERE existing.ItemID = item.ItemID
                AND existing.PriceTypeID = priceType.PriceTypeID
                AND existing.BranchID IS NULL
          );

        SELECT
            (SELECT COUNT(*) FROM #Inserted) AS ItemsCreated,
            (SELECT COUNT(*) FROM #ItemStage AS stage WHERE EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemNumber = stage.ItemNumber AND NOT EXISTS (SELECT 1 FROM #Inserted AS created WHERE created.ItemNumber = item.ItemNumber))) AS ItemsExisting,
            (SELECT COUNT(*) FROM dbo.Items AS item INNER JOIN #Inserted AS created ON created.ItemID = item.ItemID WHERE item.ParentItemID IS NOT NULL) AS ParentsCreated,
            (SELECT COUNT(*) FROM dbo.ItemUnits AS unit INNER JOIN #Inserted AS created ON created.ItemID = unit.ItemID) AS UnitsCreated,
            (SELECT COUNT(*) FROM dbo.ItemPrices AS price INNER JOIN #Inserted AS created ON created.ItemID = price.ItemID WHERE price.BranchID IS NULL) AS PricesCreated,
            (SELECT COUNT(*) FROM #PriceStage AS price INNER JOIN #Inserted AS created ON created.ItemNumber = price.ItemNumber) AS PricesForNewItems,
            (SELECT COUNT(*) FROM dbo.ItemPrices AS price INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID INNER JOIN #Inserted AS created ON created.ItemID = price.ItemID WHERE priceType.PriceCode = N'Retail') AS RetailCreated,
            (SELECT COUNT(*) FROM dbo.ItemPrices AS price INNER JOIN #Inserted AS created ON created.ItemID = price.ItemID WHERE price.BranchID IS NOT NULL) AS BranchPricesCreated,
            (SELECT COUNT(*) FROM dbo.ItemBranchInventory AS inventory INNER JOIN #Inserted AS created ON created.ItemID = inventory.ItemID) AS InventoryCreated,
            (SELECT COUNT(*) FROM dbo.StockMovements AS movement INNER JOIN #Inserted AS created ON created.ItemID = movement.ItemID) AS MovementsCreated,
            (SELECT COUNT(*) FROM dbo.ItemPriceHistory AS history INNER JOIN #Inserted AS created ON created.ItemID = history.ItemID) AS HistoryCreated;

        SELECT priceType.PriceCode, COUNT(*) AS Created
        FROM dbo.ItemPrices AS price
        INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
        INNER JOIN #Inserted AS created ON created.ItemID = price.ItemID
        WHERE price.BranchID IS NULL
        GROUP BY priceType.PriceCode;
        """;

    private sealed record StagedCatalog(
        int CanonicalItems,
        int ActiveItems,
        int InactiveItems,
        int BlockingIssues,
        int Corrections,
        int MissingNames,
        int ParentLinks,
        int OrphanParents,
        int ItemUnits,
        int OrphanUnits,
        int SelfUnits,
        int DefaultPrices,
        int NegativePrices,
        int RetailPrices,
        int BranchPrices,
        int TrialBranchPrices,
        IReadOnlyDictionary<string, int> PricesByCode,
        DataTable Items,
        DataTable Units,
        DataTable Prices);

    private sealed record InsertCounts(
        int ItemsCreated,
        int ItemsExisting,
        int ParentsCreated,
        int UnitsCreated,
        int PricesCreated,
        int PricesForNewItems,
        int RetailCreated,
        int BranchPricesCreated,
        int InventoryCreated,
        int MovementsCreated,
        int HistoryCreated,
        Dictionary<string, int> PricesByCode);
}
