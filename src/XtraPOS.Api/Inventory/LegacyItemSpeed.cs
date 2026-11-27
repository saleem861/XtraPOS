using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// نقل سرعات الأصناف من القواعد الأصلية مرة واحدة (قرار المالك 27 نوفمبر 2026):
/// Shop1 إلى المقاطعة، وShop2 إلى Labtop. القراءة فقط من القاعدة الأصلية.
/// السرعة المقفلة لا يستبدلها الحساب اليومي.
/// </summary>
public static class LegacyItemSpeed
{
    public const string SettingKey = "LegacyItemSpeedSource";

    /// <summary>اسم فرع XtraPOS إلى قاعدة السرعات الأصلية، أو null إن لم يُطلب نقل لهذا الفرع.</summary>
    public static string? CatalogForBranch(string? branchName)
    {
        var name = (branchName ?? "").Trim();
        if (name.Length == 0) return null;
        if (string.Equals(name, "المقاطعة", StringComparison.Ordinal)) return "Shop1";
        if (name.Equals("Labtop", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Laptop", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LAbtop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "لابتوب", StringComparison.Ordinal))
        {
            return "Shop2";
        }

        return null;
    }

    /// <summary>
    /// ينسخ ItemSpeed إلى DailySalesRate ويقفلها. لا يكتب الإعداد إن غابت القاعدة الأصلية أو تعذر المركز،
    /// فيُعاد في الدورة التالية. يرجع عدد بطاقات الفرع التي تغيّرت.
    /// </summary>
    public static async Task<int> ImportAsync(
        SqlConnection local,
        SqlConnection? central,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (await ColumnMissingAsync(local, "dbo.ItemBranchInventory", "SalesRateLocked", cancellationToken)) return 0;

        string? branchName;
        await using (var read = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @branch;", local))
        {
            read.Parameters.Add("@branch", System.Data.SqlDbType.UniqueIdentifier).Value = branchId;
            branchName = await read.ExecuteScalarAsync(cancellationToken) as string;
        }

        var catalog = CatalogForBranch(branchName);
        if (catalog is null) return 0;

        await using (var done = new SqlCommand("SELECT TOP (1) SettingValue FROM dbo.SystemSettings WHERE SettingKey = @key;", local))
        {
            done.Parameters.Add("@key", System.Data.SqlDbType.NVarChar, 200).Value = SettingKey;
            if (string.Equals(await done.ExecuteScalarAsync(cancellationToken) as string, catalog, StringComparison.Ordinal)) return 0;
        }

        await using (var exists = new SqlCommand("SELECT CASE WHEN DB_ID(@catalog) IS NULL THEN 0 ELSE 1 END;", local))
        {
            exists.Parameters.Add("@catalog", System.Data.SqlDbType.NVarChar, 128).Value = catalog;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return 0;
        }

        if (central is not null && await ColumnMissingAsync(central, "dbo.BranchStockSnapshots", "SalesRateLocked", cancellationToken))
        {
            return 0;
        }

        var updated = await CopyOntoBranchAsync(local, branchId, catalog, cancellationToken);
        // بلا مركز لا يُغلق النقل: الدورة التالية تعيد النسخ المحلي (لا يغيّر سطراً مقفلاً) ثم يكتب المركز.
        if (central is null) return updated;
        await CopyOntoCentralAsync(local, central, branchId, cancellationToken);

        await using var stamp = new SqlCommand(
            """
            UPDATE dbo.SystemSettings SET SettingValue = @value, UpdatedAt = SYSUTCDATETIME() WHERE SettingKey = @key;
            IF @@ROWCOUNT = 0 INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (@key, @value);
            """,
            local);
        stamp.Parameters.Add("@key", System.Data.SqlDbType.NVarChar, 200).Value = SettingKey;
        stamp.Parameters.Add("@value", System.Data.SqlDbType.NVarChar, 200).Value = catalog;
        await stamp.ExecuteNonQueryAsync(cancellationToken);
        return updated;
    }

    private static async Task<int> CopyOntoBranchAsync(SqlConnection local, Guid branchId, string catalog, CancellationToken cancellationToken)
    {
        // الكتالوج ثابت من CatalogForBranch: Shop1 أو Shop2 فقط، ولا يُؤخذ من المستخدم.
        var source = catalog == "Shop1" ? "Shop1.dbo.Items" : "Shop2.dbo.Items";
        await using var command = new SqlCommand(
            $"""
            UPDATE card
            SET DailySalesRate = CAST(legacy.ItemSpeed AS decimal(18, 6)),
                SalesRateLocked = 1,
                UpdatedAt = SYSUTCDATETIME()
            FROM dbo.ItemBranchInventory AS card
            INNER JOIN dbo.Items AS item ON item.ItemID = card.ItemID
            INNER JOIN {source} AS legacy
                ON LTRIM(RTRIM(legacy.ItemNumber)) COLLATE DATABASE_DEFAULT = LTRIM(RTRIM(item.ItemNumber)) COLLATE DATABASE_DEFAULT
            WHERE card.BranchID = @branch
              AND legacy.ItemSpeed IS NOT NULL
              AND legacy.ItemSpeed >= 0
              AND (card.SalesRateLocked = 0 OR card.DailySalesRate IS NULL OR card.DailySalesRate <> CAST(legacy.ItemSpeed AS decimal(18, 6)));

            INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, DailySalesRate, SalesRateLocked, IsAvailable, IsActive)
            SELECT @branch, item.ItemID, CAST(legacy.ItemSpeed AS decimal(18, 6)), 1, 1, 1
            FROM dbo.Items AS item
            INNER JOIN {source} AS legacy
                ON LTRIM(RTRIM(legacy.ItemNumber)) COLLATE DATABASE_DEFAULT = LTRIM(RTRIM(item.ItemNumber)) COLLATE DATABASE_DEFAULT
            WHERE item.IsActive = 1
              AND legacy.ItemSpeed IS NOT NULL
              AND legacy.ItemSpeed >= 0
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.ItemBranchInventory AS card
                  WHERE card.BranchID = @branch AND card.ItemID = item.ItemID);
            """,
            local)
        {
            CommandTimeout = 180,
        };
        command.Parameters.Add("@branch", System.Data.SqlDbType.UniqueIdentifier).Value = branchId;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyOntoCentralAsync(SqlConnection local, SqlConnection central, Guid branchId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT card.ItemID, item.ItemNumber, card.DailySalesRate
            FROM dbo.ItemBranchInventory AS card
            INNER JOIN dbo.Items AS item ON item.ItemID = card.ItemID
            WHERE card.BranchID = @branch AND card.SalesRateLocked = 1 AND card.DailySalesRate IS NOT NULL;
            """,
            local);
        read.Parameters.Add("@branch", System.Data.SqlDbType.UniqueIdentifier).Value = branchId;
        var rows = new List<(Guid ItemId, string Number, decimal Rate)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
            }
        }

        if (rows.Count == 0) return;

        await using (var create = new SqlCommand(
            """
            CREATE TABLE #legacyRates
            (
                ItemID uniqueidentifier NOT NULL PRIMARY KEY,
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                DailySalesRate decimal(18, 6) NOT NULL
            );
            """,
            central))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            using (var bulk = new SqlBulkCopy(central) { DestinationTableName = "#legacyRates", BulkCopyTimeout = 180 })
            {
                var table = new System.Data.DataTable();
                table.Columns.Add("ItemID", typeof(Guid));
                table.Columns.Add("ItemNumber", typeof(string));
                table.Columns.Add("DailySalesRate", typeof(decimal));
                foreach (var row in rows) table.Rows.Add(row.ItemId, row.Number, row.Rate);
                bulk.ColumnMappings.Add("ItemID", "ItemID");
                bulk.ColumnMappings.Add("ItemNumber", "ItemNumber");
                bulk.ColumnMappings.Add("DailySalesRate", "DailySalesRate");
                await bulk.WriteToServerAsync(table, cancellationToken);
            }

            await using var write = new SqlCommand(
                """
                UPDATE target
                SET DailySalesRate = source.DailySalesRate, SalesRateLocked = 1, ItemNumber = source.ItemNumber, ReceivedAt = SYSUTCDATETIME()
                FROM dbo.BranchStockSnapshots AS target
                INNER JOIN #legacyRates AS source ON source.ItemID = target.ItemID
                WHERE target.BranchID = @branch;

                INSERT INTO dbo.BranchStockSnapshots
                    (BranchID, ItemID, ItemNumber, OnHandQuantity, IsQuantityInitialized, DailySalesRate, SalesRateLocked, BalanceUpdatedAt)
                SELECT @branch, source.ItemID, source.ItemNumber, NULL, 0, source.DailySalesRate, 1, SYSUTCDATETIME()
                FROM #legacyRates AS source
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.BranchStockSnapshots AS target
                    WHERE target.BranchID = @branch AND target.ItemID = source.ItemID);
                """,
                central)
            {
                CommandTimeout = 180,
            };
            write.Parameters.Add("@branch", System.Data.SqlDbType.UniqueIdentifier).Value = branchId;
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var drop = new SqlCommand("IF OBJECT_ID(N'tempdb..#legacyRates') IS NOT NULL DROP TABLE #legacyRates;", central);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> ColumnMissingAsync(SqlConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN COL_LENGTH(@table, @column) IS NULL THEN 1 ELSE 0 END;", connection);
        command.Parameters.Add("@table", System.Data.SqlDbType.NVarChar, 128).Value = table;
        command.Parameters.Add("@column", System.Data.SqlDbType.NVarChar, 128).Value = column;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
