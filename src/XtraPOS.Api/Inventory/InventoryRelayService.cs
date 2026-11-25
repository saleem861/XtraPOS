using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// المرحلة 3: «المركز يعرف الفروع». كل دقيقة، لكل قاعدة فرع على هذا الجهاز:
/// 1. مرة في اليوم: يحسب DailySalesRate لكل صنف في الفرع من حركات البيع (آخر 30 يوماً).
/// 2. يرسل للمركز أرصدة أصناف مالكي المخزون التي تغيّرت منذ آخر إرسال، ونسخة كاملة مرة في اليوم
///    (BranchStockSnapshots + BranchSnapshotStatus).
/// 3. يرسل جلسات الجرد المثبتة والتدقيقات المنتهية وتصحيحات الكمية التي لم تُرسل بعد (RelayedAt).
/// لا يمر بسجل الترحيل (BranchOperationLog) حتى لا يؤخر ترحيل المبيعات: كل خطوة تعيد نفسها بأمان.
/// المركز لا يكتب مخزون الفرع من هذه النسخ.
/// قرار المالك (25 نوفمبر 2026): المركز هو المرجع للكمية. نسخة الفرع لا تكتب فوق رصيد المركز بعد أن يعرفه:
/// تُحفظ في BranchReportedQuantity تقريرَ فروق فقط، وتكون بداية الرصيد لصنف لا يعرفه المركز بعد.
/// سرعة البيع يحسبها المركز من الحركات (لحظياً مع البيع، ومرة في اليوم لكل الأصناف).
/// </summary>
public sealed class InventoryRelayService(
    SqlConnectionFactory connections,
    InventoryCountService counting,
    StockPostingService stock,
    ILogger<InventoryRelayService> logger)
{
    public const string SalesRateSettingKey = "InventorySalesRateDate";
    private const int SessionsPerRun = 20;

    public sealed record RelayResult(int RatesUpdated, int BalancesPushed, int SessionsPushed, int AuditsPushed, int CorrectionsPushed)
    {
        public bool Any => RatesUpdated + BalancesPushed + SessionsPushed + AuditsPushed + CorrectionsPushed > 0;
    }

    public async Task<RelayResult> RunAsync(CancellationToken cancellationToken)
    {
        var none = new RelayResult(0, 0, 0, 0, 0);
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return none;
        await using var local = new SqlConnection(branchConnection);
        await local.OpenAsync(cancellationToken);
        if (!await ExistsAsync(local, "dbo.InventoryQuantityCorrections", cancellationToken)) return none;
        var branchId = await ReadBranchIdAsync(local, cancellationToken);
        if (branchId is not Guid branch) return none;

        var today = InventoryCalendar.Today();
        var rates = await RefreshSalesRatesAsync(local, branch, today, cancellationToken);

        var central = await counting.OpenCentralAsync(cancellationToken);
        if (central is null) return none with { RatesUpdated = rates };
        await using var lease = central;
        if (!await ExistsAsync(central, "dbo.BranchStockSnapshots", cancellationToken))
        {
            return none with { RatesUpdated = rates };
        }

        // المركز لم يُحدَّث بعد للترحيل 044: لا تُرسل الأرصدة حتى يُحدَّث (يُحدَّث تلقائياً عند تشغيل نسخة محدثة).
        var balances = await CentralStock.IsReadyAsync(central, null, cancellationToken)
            ? await PushBalancesAsync(local, central, branch, today, rates > 0, cancellationToken)
            : 0;
        await RefreshCentralRatesAsync(central, branch, today, cancellationToken);
        var sessions = await PushSessionsAsync(local, central, branch, cancellationToken);
        var audits = await PushAuditsAsync(local, central, branch, cancellationToken);
        // المرحلة 6 و4: الجرد المخصص لهذا الفرع، وطلبات تصحيح الكمية التي أرسلها المركز لهذا الفرع.
        if (await ExistsAsync(central, "dbo.CustomInventories", cancellationToken)
            && await ExistsAsync(local, "dbo.CustomInventories", cancellationToken))
        {
            await PullCustomInventoriesAsync(local, central, branch, cancellationToken);
            await ApplyCorrectionRequestsAsync(local, central, branch, cancellationToken);
        }

        var corrections = await PushCorrectionsAsync(local, central, branch, cancellationToken);
        return new RelayResult(rates, balances, sessions, audits, corrections);
    }

    // ------------------------------------------------------------------
    // سرعة البيع
    // ------------------------------------------------------------------

    /// <summary>
    /// مرة في اليوم بتوقيت Asia/Hebron. السرعة = صافي الكمية المباعة (بوحدة مالك المخزون) ÷ عدد الأيام،
    /// على آخر 30 يوماً، أو منذ أول بيع إن كان الفرع أحدث. العبوة = سرعة الوحدة ÷ عدد الوحدات فيها.
    /// فرع بلا أي بيع يبقى بلا سرعة (NULL)، لا صفر.
    /// </summary>
    private async Task<int> RefreshSalesRatesAsync(SqlConnection local, Guid branch, DateOnly today, CancellationToken cancellationToken)
    {
        var done = await ReadSettingAsync(local, SalesRateSettingKey, cancellationToken);
        if (done == today.ToString("yyyy-MM-dd")) return 0;

        var to = DateTime.UtcNow;
        var from = to.AddDays(-InventoryRelayRules.SalesRateWindowDays);
        DateTime? firstSale;
        await using (var first = new SqlCommand(
            "SELECT MIN(OccurredAt) FROM dbo.StockMovements WHERE BranchID = @branch AND SourceType IN (N'Sale', N'SaleReturn');",
            local))
        {
            first.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            firstSale = await first.ExecuteScalarAsync(cancellationToken) as DateTime?;
        }

        var updated = 0;
        if (firstSale is DateTime started)
        {
            var days = InventoryRelayRules.SalesRateDays(DateTime.SpecifyKind(started, DateTimeKind.Utc), to);
            var sold = new Dictionary<Guid, decimal>();
            await using (var read = new SqlCommand(
                """
                SELECT ItemID, -SUM(QuantityDelta)
                FROM dbo.StockMovements
                WHERE BranchID = @branch AND SourceType IN (N'Sale', N'SaleReturn') AND OccurredAt >= @from
                GROUP BY ItemID;
                """,
                local))
            {
                read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
                read.Parameters.Add("@from", SqlDbType.DateTime2).Value = from;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    sold[reader.GetGuid(0)] = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
                }
            }

            var links = await ReadUnitLinksAsync(local, cancellationToken);
            var table = new DataTable();
            table.Columns.Add("ItemID", typeof(Guid));
            table.Columns.Add("DailySalesRate", typeof(decimal));
            var cards = new List<Guid>();
            await using (var read = new SqlCommand("SELECT ItemID FROM dbo.ItemBranchInventory WHERE BranchID = @branch;", local))
            {
                read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) cards.Add(reader.GetGuid(0));
            }

            foreach (var itemId in cards)
            {
                StockOwnerResolution owner;
                try
                {
                    owner = StockOwnership.Resolve(itemId, links);
                }
                catch (StockOwnershipException)
                {
                    continue;
                }

                var ownerSold = sold.TryGetValue(owner.StockOwnerItemId, out var quantity) ? quantity : 0m;
                table.Rows.Add(itemId, InventoryRelayRules.DailyRate(ownerSold, days, owner.BaseQuantityFactor));
            }

            updated = await ApplyRatesAsync(local, branch, table, cancellationToken);
        }

        await WriteSettingAsync(local, SalesRateSettingKey, today.ToString("yyyy-MM-dd"), cancellationToken);
        if (updated > 0) logger.LogInformation("Inventory: daily sales rate updated for {Count} item(s).", updated);
        return updated;
    }

    private static async Task<int> ApplyRatesAsync(SqlConnection local, Guid branch, DataTable rates, CancellationToken cancellationToken)
    {
        if (rates.Rows.Count == 0) return 0;
        await using (var create = new SqlCommand("CREATE TABLE #rates (ItemID uniqueidentifier NOT NULL PRIMARY KEY, DailySalesRate decimal(18, 6) NOT NULL);", local))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            using (var bulk = new SqlBulkCopy(local) { DestinationTableName = "#rates" })
            {
                bulk.ColumnMappings.Add("ItemID", "ItemID");
                bulk.ColumnMappings.Add("DailySalesRate", "DailySalesRate");
                await bulk.WriteToServerAsync(rates, cancellationToken);
            }

            await using var update = new SqlCommand(
                """
                UPDATE card
                SET DailySalesRate = rate.DailySalesRate, UpdatedAt = SYSUTCDATETIME()
                FROM dbo.ItemBranchInventory AS card
                INNER JOIN #rates AS rate ON rate.ItemID = card.ItemID
                WHERE card.BranchID = @branch
                  AND (card.DailySalesRate IS NULL OR card.DailySalesRate <> rate.DailySalesRate);
                """,
                local);
            update.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            return await update.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var drop = new SqlCommand("DROP TABLE #rates;", local);
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------
    // أرصدة الفرع
    // ------------------------------------------------------------------

    private async Task<int> PushBalancesAsync(
        SqlConnection local,
        SqlConnection central,
        Guid branch,
        DateOnly today,
        bool ratesChanged,
        CancellationToken cancellationToken)
    {
        DateTime? lastChanged = null;
        DateTime? lastFull = null;
        await using (var status = new SqlCommand(
            "SELECT LastBalanceUpdatedAt, LastFullPushAt FROM dbo.BranchSnapshotStatus WHERE BranchID = @branch;",
            central))
        {
            status.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            await using var reader = await status.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                lastChanged = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                lastFull = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            }
        }

        var full = ratesChanged || InventoryRelayRules.NeedsFullPush(lastFull, today);
        DateTime? since = full || lastChanged is null ? null : lastChanged.Value.AddMinutes(-InventoryRelayRules.OverlapMinutes);

        var table = new DataTable();
        table.Columns.Add("ItemID", typeof(Guid));
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("OnHandQuantity", typeof(decimal));
        table.Columns.Add("IsQuantityInitialized", typeof(bool));
        table.Columns.Add("DailySalesRate", typeof(decimal));
        table.Columns.Add("BalanceUpdatedAt", typeof(DateTime));
        // العلامة المائية = لحظة بدء القراءة من قاعدة الفرع (لا أحدث UpdatedAt):
        // أصناف كثيرة تحمل نفس الطابع القديم كانت تُعاد كلها كل دقيقة لأن (الأحدث − دقيقتين) يشملها دائماً.
        DateTime readStart;
        await using (var clock = new SqlCommand("SELECT SYSUTCDATETIME();", local))
        {
            readStart = (DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var read = new SqlCommand(
            $"""
            SELECT balance.ItemID, item.ItemNumber, balance.OnHandQuantity, balance.IsQuantityInitialized, card.DailySalesRate,
                   CASE WHEN card.UpdatedAt > balance.UpdatedAt THEN card.UpdatedAt ELSE balance.UpdatedAt END AS ChangedAt
            FROM dbo.BranchStockBalances AS balance
            INNER JOIN dbo.Items AS item ON item.ItemID = balance.ItemID
            LEFT JOIN dbo.ItemBranchInventory AS card ON card.BranchID = balance.BranchID AND card.ItemID = balance.ItemID
            WHERE balance.BranchID = @branch
              AND (@since IS NULL OR balance.UpdatedAt >= @since OR card.UpdatedAt >= @since)
              AND {InventoryRelayRules.StockOwnerSql("item")};
            """,
            local))
        {
            read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            read.Parameters.Add("@since", SqlDbType.DateTime2).Value = (object?)since ?? DBNull.Value;
            read.CommandTimeout = 120;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var changedAt = reader.GetDateTime(5);
                table.Rows.Add(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? DBNull.Value : reader.GetDecimal(2),
                    reader.GetBoolean(3),
                    reader.IsDBNull(4) ? DBNull.Value : reader.GetDecimal(4),
                    changedAt);
            }
        }

        if (table.Rows.Count > 0)
        {
            await using (var create = new SqlCommand(
                """
                CREATE TABLE #snap
                (
                    ItemID uniqueidentifier NOT NULL PRIMARY KEY,
                    ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    OnHandQuantity decimal(18, 4) NULL,
                    IsQuantityInitialized bit NOT NULL,
                    DailySalesRate decimal(18, 6) NULL,
                    BalanceUpdatedAt datetime2(0) NOT NULL
                );
                """,
                central))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            try
            {
                using (var bulk = new SqlBulkCopy(central) { DestinationTableName = "#snap", BulkCopyTimeout = 300 })
                {
                    foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    await bulk.WriteToServerAsync(table, cancellationToken);
                }

                // المركز هو المرجع: رصيد يعرفه المركز لا يُكتب فوقه، ورصيد الفرع يُحفظ تقريرَ فروق فقط.
                // صنف لا يعرف المركز رصيده بعد يبدأ من رصيد الفرع، ومن بعدها الفروق فقط (StockCentralRelay).
                // السرعة من الفرع فقط حتى تصل المركز مبيعات هذا الفرع (FirstSaleAt)، ثم يحسبها المركز.
                await using var merge = new SqlCommand(
                    """
                    DECLARE @centralRates bit = CASE WHEN EXISTS (SELECT 1 FROM dbo.BranchSnapshotStatus
                                                                 WHERE BranchID = @branch AND FirstSaleAt IS NOT NULL) THEN 1 ELSE 0 END;

                    UPDATE target
                    SET ItemNumber = source.ItemNumber,
                        BranchReportedQuantity = CASE WHEN source.IsQuantityInitialized = 1 THEN source.OnHandQuantity END,
                        BranchReportedAt = source.BalanceUpdatedAt,
                        OnHandQuantity = CASE WHEN target.IsQuantityInitialized = 1 AND target.OnHandQuantity IS NOT NULL
                                              THEN target.OnHandQuantity ELSE source.OnHandQuantity END,
                        IsQuantityInitialized = CASE WHEN target.IsQuantityInitialized = 1 AND target.OnHandQuantity IS NOT NULL
                                                     THEN 1 ELSE source.IsQuantityInitialized END,
                        BalanceUpdatedAt = CASE WHEN target.IsQuantityInitialized = 1 AND target.OnHandQuantity IS NOT NULL
                                                THEN target.BalanceUpdatedAt ELSE source.BalanceUpdatedAt END,
                        DailySalesRate = CASE WHEN @centralRates = 1 THEN target.DailySalesRate ELSE source.DailySalesRate END,
                        ReceivedAt = SYSUTCDATETIME()
                    FROM dbo.BranchStockSnapshots AS target
                    INNER JOIN #snap AS source ON source.ItemID = target.ItemID
                    WHERE target.BranchID = @branch;

                    INSERT INTO dbo.BranchStockSnapshots
                        (BranchID, ItemID, ItemNumber, OnHandQuantity, IsQuantityInitialized, DailySalesRate, BalanceUpdatedAt,
                         BranchReportedQuantity, BranchReportedAt)
                    SELECT @branch, source.ItemID, source.ItemNumber, source.OnHandQuantity, source.IsQuantityInitialized,
                           source.DailySalesRate, source.BalanceUpdatedAt,
                           CASE WHEN source.IsQuantityInitialized = 1 THEN source.OnHandQuantity END, source.BalanceUpdatedAt
                    FROM #snap AS source
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.BranchStockSnapshots AS target
                                      WHERE target.BranchID = @branch AND target.ItemID = source.ItemID);
                    """,
                    central)
                { CommandTimeout = 300 };
                merge.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
                await merge.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await using var drop = new SqlCommand("DROP TABLE #snap;", central);
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        string? rateDate = await ReadSettingAsync(local, SalesRateSettingKey, cancellationToken);
        await using (var status = new SqlCommand(
            """
            DECLARE @count int = (SELECT COUNT(*) FROM dbo.BranchStockSnapshots WHERE BranchID = @branch);
            UPDATE dbo.BranchSnapshotStatus
            SET LastPushAt = SYSUTCDATETIME(),
                LastBalanceUpdatedAt = @newest,
                LastFullPushAt = CASE WHEN @full = 1 THEN SYSUTCDATETIME() ELSE LastFullPushAt END,
                SalesRateDate = @rateDate,
                ItemCount = @count
            WHERE BranchID = @branch;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.BranchSnapshotStatus (BranchID, LastPushAt, LastBalanceUpdatedAt, LastFullPushAt, SalesRateDate, ItemCount)
                VALUES (@branch, SYSUTCDATETIME(), @newest, CASE WHEN @full = 1 THEN SYSUTCDATETIME() END, @rateDate, @count);
            """,
            central))
        {
            status.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            status.Parameters.Add("@newest", SqlDbType.DateTime2).Value = readStart;
            status.Parameters.Add("@full", SqlDbType.Bit).Value = full;
            status.Parameters.Add("@rateDate", SqlDbType.Date).Value =
                DateOnly.TryParse(rateDate, out var parsed) ? parsed.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
            await status.ExecuteNonQueryAsync(cancellationToken);
        }

        return table.Rows.Count;
    }

    /// <summary>مرة في اليوم: يعيد المركز سرعة كل أصناف هذا الفرع، فتنزل سرعة صنف توقف بيعه مع مرور الأيام.</summary>
    private async Task RefreshCentralRatesAsync(SqlConnection central, Guid branch, DateOnly today, CancellationToken cancellationToken)
    {
        if (!await CentralStock.IsReadyAsync(central, null, cancellationToken)) return;
        await using (var check = new SqlCommand(
            "SELECT CASE WHEN FirstSaleAt IS NOT NULL AND (CentralRateDate IS NULL OR CentralRateDate < @today) THEN 1 ELSE 0 END FROM dbo.BranchSnapshotStatus WHERE BranchID = @branch;",
            central))
        {
            check.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            check.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
            if (await check.ExecuteScalarAsync(cancellationToken) is not 1) return;
        }

        var updated = await CentralStock.RecomputeRatesAsync(central, null, branch, null, cancellationToken);
        await using var stamp = new SqlCommand("UPDATE dbo.BranchSnapshotStatus SET CentralRateDate = @today WHERE BranchID = @branch;", central);
        stamp.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
        stamp.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
        await stamp.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Inventory: central sales rate recalculated for {Count} item(s).", updated);
    }

    // ------------------------------------------------------------------
    // الجرد والتدقيق والتصحيح
    // ------------------------------------------------------------------

    private static readonly string[] SessionColumns =
    [
        "SessionID", "BusinessID", "BranchID", "StationID", "DeviceID", "UserID", "UserName", "InventoryType", "BusinessDate", "Status",
        "StartedAt", "LastSavedAt", "CompletedAt", "DiscardedAt", "TotalItems", "CountedItems", "PostedItems", "CustomInventoryID",
    ];

    private static readonly string[] SessionItemColumns =
    [
        "SessionItemID", "SessionID", "ItemID", "ItemNumber", "ItemName", "DailySalesRate", "SupplyDays", "IsCounted", "CountedQuantity",
        "SystemQuantityAtCount", "SystemQuantityKnown", "CountedAt", "CountedByUserID", "CountedByName", "StockMovementID", "PostedAt",
        "QuantityBeforePosting", "QuantityAfterPosting",
    ];

    private static readonly string[] AuditColumns =
    [
        "AuditID", "SessionID", "BranchID", "UserID", "UserName", "AuditMode", "Status", "StartedAt", "CompletedAt",
        "TotalItems", "CountedItems", "MatchedItems",
    ];

    private static readonly string[] AuditItemColumns =
    [
        "AuditItemID", "AuditID", "SessionItemID", "ItemID", "ItemNumber", "ItemName", "IsCounted", "AuditQuantity", "CountedAt",
    ];

    private static readonly string[] CorrectionColumns =
    [
        "CorrectionID", "BranchID", "ItemID", "ItemNumber", "ItemName", "PreviousQuantity", "NewQuantity", "Reason",
        "UserID", "UserName", "CorrectedAt", "StockMovementID", "ExpectedQuantity", "CountedQuantity", "DeltaQuantity",
    ];

    private async Task<int> PushSessionsAsync(SqlConnection local, SqlConnection central, Guid branch, CancellationToken cancellationToken)
    {
        var pending = await PendingAsync(
            local,
            $"SELECT TOP ({SessionsPerRun}) SessionID FROM dbo.InventorySessions WHERE BranchID = @branch AND Status = N'Completed' AND RelayedAt IS NULL ORDER BY CompletedAt;",
            branch,
            cancellationToken);
        foreach (var sessionId in pending)
        {
            await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
            {
                await CopyAsync(local, central, transaction, "dbo.InventorySessions", ["SessionID"], SessionColumns, "SessionID", sessionId, cancellationToken);
                await CopyAsync(local, central, transaction, "dbo.InventorySessionPages", ["SessionID", "InventoryPage"], ["SessionID", "InventoryPage"], "SessionID", sessionId, cancellationToken);
                await CopyAsync(local, central, transaction, "dbo.InventorySessionItems", ["SessionItemID"], SessionItemColumns, "SessionID", sessionId, cancellationToken);
                await StampCentralAsync(central, transaction, "dbo.InventorySessions", "SessionID", sessionId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await StampLocalAsync(local, "dbo.InventorySessions", "SessionID", sessionId, cancellationToken);
        }

        return pending.Count;
    }

    private async Task<int> PushAuditsAsync(SqlConnection local, SqlConnection central, Guid branch, CancellationToken cancellationToken)
    {
        var pending = await PendingAsync(
            local,
            $"SELECT TOP ({SessionsPerRun}) AuditID FROM dbo.InventoryAudits WHERE BranchID = @branch AND Status = N'Completed' AND RelayedAt IS NULL ORDER BY CompletedAt;",
            branch,
            cancellationToken);
        foreach (var auditId in pending)
        {
            await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
            {
                await CopyAsync(local, central, transaction, "dbo.InventoryAudits", ["AuditID"], AuditColumns, "AuditID", auditId, cancellationToken);
                await CopyAsync(local, central, transaction, "dbo.InventoryAuditItems", ["AuditItemID"], AuditItemColumns, "AuditID", auditId, cancellationToken);
                await StampCentralAsync(central, transaction, "dbo.InventoryAudits", "AuditID", auditId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await StampLocalAsync(local, "dbo.InventoryAudits", "AuditID", auditId, cancellationToken);
        }

        return pending.Count;
    }

    private async Task<int> PushCorrectionsAsync(SqlConnection local, SqlConnection central, Guid branch, CancellationToken cancellationToken)
    {
        var pending = await PendingAsync(
            local,
            "SELECT TOP (200) CorrectionID FROM dbo.InventoryQuantityCorrections WHERE BranchID = @branch AND RelayedAt IS NULL ORDER BY CorrectedAt;",
            branch,
            cancellationToken);
        foreach (var correctionId in pending)
        {
            await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
            {
                await CopyAsync(local, central, transaction, "dbo.InventoryQuantityCorrections", ["CorrectionID"], CorrectionColumns, "CorrectionID", correctionId, cancellationToken);
                await StampCentralAsync(central, transaction, "dbo.InventoryQuantityCorrections", "CorrectionID", correctionId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await StampLocalAsync(local, "dbo.InventoryQuantityCorrections", "CorrectionID", correctionId, cancellationToken);
        }

        return pending.Count;
    }

    // ------------------------------------------------------------------
    // من المركز إلى الفرع: الجرد المخصص وطلبات التصحيح
    // ------------------------------------------------------------------

    private static async Task PullCustomInventoriesAsync(SqlConnection local, SqlConnection central, Guid branch, CancellationToken cancellationToken)
    {
        var remote = new List<(Guid Id, string Name, bool Active, DateTime UpdatedAt)>();
        await using (var read = new SqlCommand(
            """
            SELECT inventory.CustomInventoryID, inventory.InventoryName, inventory.IsActive, inventory.UpdatedAt
            FROM dbo.CustomInventories AS inventory
            INNER JOIN dbo.CustomInventoryBranches AS target ON target.CustomInventoryID = inventory.CustomInventoryID
            WHERE target.BranchID = @branch;
            """,
            central))
        {
            read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                remote.Add((reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetDateTime(3)));
            }
        }

        var mine = new Dictionary<Guid, DateTime>();
        await using (var read = new SqlCommand("SELECT CustomInventoryID, UpdatedAt FROM dbo.CustomInventories;", local))
        {
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) mine[reader.GetGuid(0)] = reader.GetDateTime(1);
        }

        foreach (var inventory in remote)
        {
            if (mine.TryGetValue(inventory.Id, out var updated) && updated == inventory.UpdatedAt) continue;
            var items = new List<(Guid ItemId, string Number, string? Name)>();
            await using (var read = new SqlCommand(
                "SELECT ItemID, ItemNumber, ItemName FROM dbo.CustomInventoryItems WHERE CustomInventoryID = @id;",
                central))
            {
                read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = inventory.Id;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            await using var transaction = (SqlTransaction)await local.BeginTransactionAsync(cancellationToken);
            await using (var header = new SqlCommand(
                """
                UPDATE dbo.CustomInventories SET InventoryName = @name, IsActive = @active, UpdatedAt = @updated WHERE CustomInventoryID = @id;
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.CustomInventories (CustomInventoryID, InventoryName, IsActive, UpdatedAt) VALUES (@id, @name, @active, @updated);
                DELETE FROM dbo.CustomInventoryItems WHERE CustomInventoryID = @id;
                """,
                local,
                transaction))
            {
                header.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = inventory.Id;
                header.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = inventory.Name;
                header.Parameters.Add("@active", SqlDbType.Bit).Value = inventory.Active;
                header.Parameters.Add("@updated", SqlDbType.DateTime2).Value = inventory.UpdatedAt;
                await header.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var item in items)
            {
                await using var insert = new SqlCommand(
                    "INSERT INTO dbo.CustomInventoryItems (CustomInventoryID, ItemID, ItemNumber, ItemName) VALUES (@id, @item, @number, @name);",
                    local,
                    transaction);
                insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = inventory.Id;
                insert.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = item.ItemId;
                insert.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = item.Number;
                insert.Parameters.Add("@name", SqlDbType.NVarChar, 500).Value = (object?)item.Name ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // جرد مخصص أُزيل هذا الفرع منه: يتوقف هنا ولا يُحذف، حتى تبقى الجلسات القديمة مقروءة.
        var assigned = remote.Select(entry => entry.Id).ToHashSet();
        foreach (var id in mine.Keys.Where(id => !assigned.Contains(id)))
        {
            await using var stop = new SqlCommand("UPDATE dbo.CustomInventories SET IsActive = 0 WHERE CustomInventoryID = @id AND IsActive = 1;", local);
            stop.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
            await stop.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// تصحيح كمية طلبه المركز لهذا الفرع (من «معالجة النواقص» على جهاز فرع آخر). الفرع يرحّله بنفسه:
    /// حركة InventoryReconciliation واحدة، ومعرّف الطلب هو سطر المصدر ومعرّف التصحيح، فلا يتكرر.
    /// </summary>
    private async Task ApplyCorrectionRequestsAsync(SqlConnection local, SqlConnection central, Guid branch, CancellationToken cancellationToken)
    {
        var pending = new List<(Guid Id, Guid ItemId, decimal Quantity, string Reason, Guid UserId, string? UserName, decimal? Expected, decimal? Delta)>();
        await using (var read = new SqlCommand(
            """
            SELECT TOP (200) RequestID, ItemID, NewQuantity, Reason, RequestedByUserID, RequestedByName, ExpectedQuantity, DeltaQuantity
            FROM dbo.InventoryCorrectionRequests
            WHERE BranchID = @branch AND Status = N'Pending'
            ORDER BY RequestedAt;
            """,
            central))
        {
            read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetString(3), reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7)));
            }
        }

        foreach (var request in pending)
        {
            string status;
            string? message = null;
            decimal? previous = null;
            decimal? resulting = null;
            await using (var check = new SqlCommand(
                $"SELECT COUNT(*) FROM dbo.Items AS item WHERE item.ItemID = @item AND item.IsActive = 1 AND {InventoryRelayRules.StockOwnerSql("item")};",
                local))
            {
                check.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = request.ItemId;
                if ((int)(await check.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
                {
                    status = "Failed";
                    message = "الصنف غير موجود في هذا الفرع أو لا يُجرد مباشرة.";
                    await MarkRequestAsync(central, request.Id, status, previous, null, message, cancellationToken);
                    continue;
                }
            }

            try
            {
                (previous, resulting) = await stock.InTransactionAsync(async (session, token) =>
                {
                    await using (var existing = new SqlCommand(
                        "SELECT PreviousQuantity, NewQuantity FROM dbo.InventoryQuantityCorrections WHERE CorrectionID = @id;",
                        session.Connection,
                        session.Transaction))
                    {
                        existing.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.Id;
                        await using var found = await existing.ExecuteReaderAsync(token);
                        if (await found.ReadAsync(token))
                        {
                            return (found.IsDBNull(0) ? (decimal?)null : found.GetDecimal(0), (decimal?)found.GetDecimal(1));
                        }
                    }

                    bool initialized;
                    await using (var balance = new SqlCommand(
                        "SELECT IsQuantityInitialized FROM dbo.BranchStockBalances WITH (UPDLOCK, HOLDLOCK) WHERE BranchID = @branch AND ItemID = @item;",
                        session.Connection,
                        session.Transaction))
                    {
                        balance.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
                        balance.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = request.ItemId;
                        initialized = await balance.ExecuteScalarAsync(token) is true;
                    }

                    var plan = InventoryRelayRules.PlanCorrection(request.Quantity, request.Delta, initialized);
                    var now = DateTime.UtcNow;
                    var result = plan.ByDifference
                        ? await stock.PostDeltaAsync(
                            new StockDeltaRequest(branch, request.ItemId, StockSourceTypes.InventoryReconciliation, request.Id, request.Id, plan.Value, null, now, request.UserId, request.Quantity),
                            session,
                            token)
                        : await stock.SetAbsoluteQuantityAsync(
                            new StockAbsoluteRequest(branch, request.ItemId, StockSourceTypes.InventoryReconciliation, request.Id, request.Id, plan.Value, null, now, request.UserId),
                            session,
                            token);
                    var after = result.ResultingQuantity ?? request.Quantity;
                    await using var insert = new SqlCommand(
                        """
                        INSERT INTO dbo.InventoryQuantityCorrections
                            (CorrectionID, BranchID, ItemID, ItemNumber, ItemName, PreviousQuantity, NewQuantity,
                             ExpectedQuantity, CountedQuantity, DeltaQuantity, Reason, UserID, UserName, StockMovementID)
                        SELECT @id, @branch, item.ItemID, item.ItemNumber, item.ItemName, @previous, @new,
                               @expected, @counted, @delta, @reason, @user, @userName, @movement
                        FROM dbo.Items AS item WHERE item.ItemID = @item;
                        """,
                        session.Connection,
                        session.Transaction);
                    insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.Id;
                    insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
                    insert.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = request.ItemId;
                    Quantity(insert, "@previous", result.PreviousQuantity);
                    Quantity(insert, "@new", after);
                    Quantity(insert, "@expected", request.Expected);
                    Quantity(insert, "@counted", request.Quantity);
                    Quantity(insert, "@delta", result.QuantityDelta);
                    insert.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = request.Reason;
                    insert.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = request.UserId;
                    insert.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = $"{request.UserName ?? "المركز"} (من المركز)";
                    insert.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = result.StockMovementId;
                    await insert.ExecuteNonQueryAsync(token);
                    return (result.PreviousQuantity, (decimal?)after);
                }, cancellationToken);
                status = "Applied";
            }
            catch (StockPostingException exception)
            {
                status = "Failed";
                message = exception.Message;
            }

            await MarkRequestAsync(central, request.Id, status, previous, resulting, message, cancellationToken);
            logger.LogInformation("Inventory: central correction {Request} {Status}.", request.Id, status);
        }
    }

    private static void Quantity(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 4;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static async Task MarkRequestAsync(SqlConnection central, Guid id, string status, decimal? previous, decimal? resulting, string? message, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.InventoryCorrectionRequests
            SET Status = @status, AppliedAt = SYSUTCDATETIME(), PreviousQuantity = @previous, ResultingQuantity = @resulting, Message = @message
            WHERE RequestID = @id AND Status = N'Pending';
            """,
            central);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = status;
        Quantity(command, "@previous", previous);
        Quantity(command, "@resulting", resulting);
        command.Parameters.Add("@message", SqlDbType.NVarChar, 500).Value = (object?)message ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<Guid>> PendingAsync(SqlConnection local, string sql, Guid branch, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var command = new SqlCommand(sql, local);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// ينسخ صفوف جدول من الفرع إلى نفس الجدول في المركز، ويتجاوز الصف الموجود بنفس المفتاح.
    /// أسماء الجداول والأعمدة ثابتة في هذا الملف، لا تأتي من الطلب.
    /// </summary>
    private static async Task CopyAsync(
        SqlConnection local,
        SqlConnection central,
        SqlTransaction transaction,
        string table,
        string[] keys,
        string[] columns,
        string filterColumn,
        Guid filterValue,
        CancellationToken cancellationToken)
    {
        var rows = new List<object[]>();
        await using (var read = new SqlCommand($"SELECT {string.Join(", ", columns)} FROM {table} WHERE {filterColumn} = @id;", local))
        {
            read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = filterValue;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object[columns.Length];
                reader.GetValues(values);
                rows.Add(values);
            }
        }

        var names = columns.Select((_, index) => $"@c{index}").ToArray();
        var keyMatch = string.Join(" AND ", keys.Select(key => $"existing.{key} = @c{Array.IndexOf(columns, key)}"));
        var sql = $"""
            IF NOT EXISTS (SELECT 1 FROM {table} AS existing WITH (UPDLOCK, HOLDLOCK) WHERE {keyMatch})
                INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", names)});
            """;
        foreach (var row in rows)
        {
            await using var insert = new SqlCommand(sql, central, transaction);
            for (var index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue(names[index], row[index] ?? DBNull.Value);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task StampCentralAsync(SqlConnection central, SqlTransaction transaction, string table, string keyColumn, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"UPDATE {table} SET RelayedAt = SYSUTCDATETIME() WHERE {keyColumn} = @id AND RelayedAt IS NULL;", central, transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task StampLocalAsync(SqlConnection local, string table, string keyColumn, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"UPDATE {table} SET RelayedAt = SYSUTCDATETIME() WHERE {keyColumn} = @id AND RelayedAt IS NULL;", local);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // مساعدات
    // ------------------------------------------------------------------

    private static async Task<IReadOnlyList<StockUnitLink>> ReadUnitLinksAsync(SqlConnection local, CancellationToken cancellationToken)
    {
        var links = new List<StockUnitLink>();
        await using var read = new SqlCommand(
            "SELECT ItemID, InternalUnitItemID, UnitsPerPackage FROM dbo.ItemUnits WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL AND UnitsPerPackage > 0;",
            local);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new StockUnitLink(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
        }

        return links;
    }

    private static async Task<Guid?> ReadBranchIdAsync(SqlConnection local, CancellationToken cancellationToken)
    {
        var value = await ReadSettingAsync(local, "BranchID", cancellationToken);
        return Guid.TryParse(value, out var branch) ? branch : null;
    }

    private static async Task<string?> ReadSettingAsync(SqlConnection local, string key, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT TOP (1) SettingValue FROM dbo.SystemSettings WHERE SettingKey = @key;", local);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task WriteSettingAsync(SqlConnection local, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.SystemSettings SET SettingValue = @value, UpdatedAt = SYSUTCDATETIME() WHERE SettingKey = @key;
            IF @@ROWCOUNT = 0 INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (@key, @value);
            """,
            local);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
        command.Parameters.Add("@value", SqlDbType.NVarChar, 400).Value = value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = table;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }
}
