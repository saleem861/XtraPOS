using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Operations;

namespace XtraPOS.Api.Catalog;

/// <summary>
/// قرار المالك (24 نوفمبر 2026): أي عملية تعدّل الكمية تُرحَّل فوراً للمركز عبر سجل الترحيلات.
/// قرار المالك (25 نوفمبر 2026): المركز هو المرجع للكمية؛ رصيده = مجموع الفروق، ولا يُنسخ رصيد الفرع فوقه.
///
/// كل حركة يكتبها <see cref="StockPostingService"/> تُربط داخل نفس المعاملة بسطر في BranchOperationLog
/// من النوع <see cref="OperationType"/> (سطر واحد لكل مستند: البيعة، الجلسة، طلب التصحيح...).
/// المرسِل يقرأ الحركات غير المرسلة، فيكتبها في المركز (BranchStockMovements) مرة واحدة لكل رقم حركة،
/// ويضيف فارقها إلى BranchStockSnapshots (إلا صدى تصحيح طُبّق على المركز أولاً)، ثم يعلّم الحركات والسطر «تم».
///
/// هذا النوع لا يدخل في ترتيب السجل (رأس الطابور) لأن الحركات فروق مستقلة لا تعتمد على ترتيب غيرها،
/// فلا تؤخر بيع الآجل ولا السداد.
/// </summary>
public static class StockCentralRelay
{
    public const string OperationType = BranchOperationLog.StockMovementType;
    public const int BatchSize = 400;

    private static readonly ConcurrentDictionary<string, bool> ReadyDatabases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>إشارة للمرسِل بأن حركة جديدة كُتبت، فيرسل خلال لحظات بدل انتظار الدورة التالية.</summary>
    public static event Action? MovementWritten;

    public static void Nudge() => MovementWritten?.Invoke();

    /// <summary>معرّف السطر في السجل: ثابت لكل (فرع، نوع، مستند)، فحركات نفس البيعة تجتمع في سطر واحد.</summary>
    public static Guid OperationIdFor(Guid branchId, string sourceType, Guid? sourceDocumentId, Guid sourceLineId)
    {
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"stock|{branchId:N}|{sourceType}|{(sourceDocumentId ?? sourceLineId):N}");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    public static string Label(string sourceType) => sourceType switch
    {
        StockSourceTypes.Sale => "بيع",
        StockSourceTypes.SaleReturn => "مرتجع بيع",
        StockSourceTypes.Purchase => "شراء",
        StockSourceTypes.PurchaseReturn => "مرتجع شراء",
        StockSourceTypes.TransferOut => "تحويل صادر",
        StockSourceTypes.TransferIn => "تحويل وارد",
        StockSourceTypes.TransferReversal => "عكس تحويل",
        StockSourceTypes.TransferRejectReturn => "إرجاع تحويل مرفوض",
        StockSourceTypes.InventoryCount => "جرد",
        StockSourceTypes.OpeningBalance => "رصيد افتتاحي",
        StockSourceTypes.InventoryReconciliation => "تصحيح كمية",
        StockSourceTypes.Damage => "تالف",
        StockSourceTypes.CostAdjustment => "تعديل تكلفة",
        _ => sourceType,
    };

    /// <summary>داخل معاملة الحركة: يربطها بسطر السجل (وينشئه أو يعيد فتحه).</summary>
    internal static async Task LinkAsync(
        StockPostingSession session,
        Guid movementId,
        Guid branchId,
        string sourceType,
        Guid? sourceDocumentId,
        Guid sourceLineId,
        Guid? actorUserId,
        CancellationToken cancellationToken)
    {
        if (!await ReadyAsync(session, cancellationToken))
        {
            return;
        }

        var operationId = OperationIdFor(branchId, sourceType, sourceDocumentId, sourceLineId);
        await using (var reopen = new SqlCommand(
            """
            UPDATE dbo.BranchOperationLog
            SET Status = N'Pending', CompletedAt = NULL
            WHERE OperationID = @operation AND Status = N'Completed';
            SELECT COUNT(*) FROM dbo.BranchOperationLog WHERE OperationID = @operation;
            """,
            session.Connection,
            session.Transaction))
        {
            reopen.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            var exists = Convert.ToInt32(await reopen.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
            if (!exists)
            {
                await BranchOperationLog.EnqueueAsync(session.Connection, session.Transaction, new BranchOperationDraft(
                    operationId,
                    OperationType,
                    string.Create(CultureInfo.InvariantCulture, $"{{\"sourceType\":\"{sourceType}\",\"sourceDocumentId\":\"{sourceDocumentId ?? sourceLineId:D}\"}}"),
                    "ترحيل كمية: " + Label(sourceType),
                    "StockMovements",
                    sourceDocumentId ?? sourceLineId,
                    branchId,
                    actorUserId ?? Guid.Empty,
                    null), cancellationToken);
            }
        }

        await using var link = new SqlCommand(
            "UPDATE dbo.StockMovements SET RelayOperationID = @operation WHERE StockMovementID = @movement;",
            session.Connection,
            session.Transaction);
        link.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        link.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = movementId;
        await link.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ReadyAsync(StockPostingSession session, CancellationToken cancellationToken)
    {
        var key = session.Connection.DataSource + "|" + session.Connection.Database;
        if (ReadyDatabases.ContainsKey(key))
        {
            return true;
        }

        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL
                         AND COL_LENGTH(N'dbo.StockMovements', N'RelayOperationID') IS NOT NULL
                         AND COL_LENGTH(N'dbo.StockMovements', N'CentralRelayedAt') IS NOT NULL
                        THEN 1 ELSE 0 END;
            """,
            session.Connection,
            session.Transaction);
        var ready = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        if (ready)
        {
            ReadyDatabases[key] = true;
        }

        return ready;
    }

    public sealed record RelayBatch(int Sent, bool More);

    private sealed record PendingMovement(
        Guid Id,
        Guid BranchId,
        Guid ItemId,
        string ItemNumber,
        string SourceType,
        Guid? SourceDocumentId,
        Guid SourceLineId,
        string PostingMode,
        decimal? PreviousQuantity,
        decimal QuantityDelta,
        decimal? ResultingQuantity,
        DateTime OccurredAt,
        DateTime CreatedAt,
        bool HistoryOnly);

    /// <summary>
    /// يرسل دفعة من الحركات غير المرسلة. المركز أولاً (مرة واحدة لكل رقم حركة)، ثم يُعلَّم الفرع؛
    /// انقطاع بين الخطوتين يعيد الإرسال فقط، ولا يكرر التطبيق.
    /// </summary>
    public static async Task<RelayBatch> RelayAsync(SqlConnection local, SqlConnection central, CancellationToken cancellationToken)
    {
        var pending = new List<PendingMovement>();
        await using (var read = new SqlCommand(
            $"""
            SELECT TOP ({BatchSize}) movement.StockMovementID, movement.BranchID, movement.ItemID, item.ItemNumber,
                   movement.SourceType, movement.SourceDocumentID, movement.SourceLineID, movement.PostingMode,
                   movement.PreviousQuantity, movement.QuantityDelta, movement.ResultingQuantity,
                   movement.OccurredAt, movement.CreatedAt, CAST(ISNULL(movement.CentralHistoryOnly, 0) AS bit)
            FROM dbo.StockMovements AS movement
            INNER JOIN dbo.Items AS item ON item.ItemID = movement.ItemID
            WHERE movement.CentralRelayedAt IS NULL
            ORDER BY movement.CreatedAt, movement.StockMovementID;
            """,
            local))
        {
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add(new PendingMovement(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.GetGuid(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    reader.GetDecimal(9),
                    reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    reader.GetDateTime(11),
                    reader.GetDateTime(12),
                    reader.GetBoolean(13)));
            }
        }

        if (pending.Count == 0)
        {
            await CompleteLogAsync(local, cancellationToken);
            return new RelayBatch(0, false);
        }

        try
        {
            await ApplyCentralAsync(central, pending, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            await NoteFailureAsync(local, exception.Message, cancellationToken);
            throw;
        }

        await MarkSentAsync(local, pending, cancellationToken);
        await CompleteLogAsync(local, cancellationToken);
        return new RelayBatch(pending.Count, pending.Count == BatchSize);
    }

    /// <summary>
    /// قرار المالك (25 نوفمبر 2026): المركز هو المرجع للكمية. كل حركة جديدة تُحفظ مرة واحدة لكل رقم حركة، ثم:
    /// - حركة فرع عادية (بيع، مرتجع، شراء...): يُضاف فارقها إلى رصيد المركز، ولا يُنسخ رصيد الفرع فوقه.
    /// - صدى تصحيح طُبّق على المركز أولاً (InventoryReconciliation ومعرّف مستندها طلب CentralAppliedAt): لا يُضاف ثانية.
    /// - مبيعات سابقة أُرسلت لحساب السرعة فقط (CentralHistoryOnly): لا تُضاف.
    /// رصيد المركز غير المعروف بعد يأخذ رصيد الفرع بعد آخر حركة كبداية، ومن بعدها الفروق فقط.
    /// ثم تُعاد سرعة البيع لحظياً للأصناف التي بيعت.
    /// </summary>
    private static async Task ApplyCentralAsync(SqlConnection central, IReadOnlyList<PendingMovement> pending, CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("Seq", typeof(int));
        table.Columns.Add("StockMovementID", typeof(Guid));
        table.Columns.Add("BranchID", typeof(Guid));
        table.Columns.Add("ItemID", typeof(Guid));
        table.Columns.Add("ItemNumber", typeof(string));
        table.Columns.Add("SourceType", typeof(string));
        table.Columns.Add("SourceDocumentID", typeof(Guid));
        table.Columns.Add("SourceLineID", typeof(Guid));
        table.Columns.Add("PostingMode", typeof(string));
        table.Columns.Add("PreviousQuantity", typeof(decimal));
        table.Columns.Add("QuantityDelta", typeof(decimal));
        table.Columns.Add("ResultingQuantity", typeof(decimal));
        table.Columns.Add("OccurredAt", typeof(DateTime));
        table.Columns.Add("CreatedAt", typeof(DateTime));
        table.Columns.Add("HistoryOnly", typeof(bool));
        for (var index = 0; index < pending.Count; index++)
        {
            var movement = pending[index];
            table.Rows.Add(
                index,
                movement.Id,
                movement.BranchId,
                movement.ItemId,
                movement.ItemNumber,
                movement.SourceType,
                (object?)movement.SourceDocumentId ?? DBNull.Value,
                movement.SourceLineId,
                movement.PostingMode,
                (object?)movement.PreviousQuantity ?? DBNull.Value,
                movement.QuantityDelta,
                (object?)movement.ResultingQuantity ?? DBNull.Value,
                movement.OccurredAt,
                movement.CreatedAt,
                movement.HistoryOnly);
        }

        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await Inventory.CentralStock.IsReadyAsync(central, transaction, cancellationToken))
            {
                throw new InvalidOperationException(Inventory.CentralStock.NotReady);
            }

            await using (var create = new SqlCommand(
                """
                CREATE TABLE #mov
                (
                    Seq int NOT NULL PRIMARY KEY,
                    StockMovementID uniqueidentifier NOT NULL,
                    BranchID uniqueidentifier NOT NULL,
                    ItemID uniqueidentifier NOT NULL,
                    ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    SourceType nvarchar(40) COLLATE DATABASE_DEFAULT NOT NULL,
                    SourceDocumentID uniqueidentifier NULL,
                    SourceLineID uniqueidentifier NOT NULL,
                    PostingMode nvarchar(20) COLLATE DATABASE_DEFAULT NOT NULL,
                    PreviousQuantity decimal(18, 4) NULL,
                    QuantityDelta decimal(18, 4) NOT NULL,
                    ResultingQuantity decimal(18, 4) NULL,
                    OccurredAt datetime2(0) NOT NULL,
                    CreatedAt datetime2(0) NOT NULL,
                    HistoryOnly bit NOT NULL,
                    Origin nvarchar(10) COLLATE DATABASE_DEFAULT NULL
                );
                CREATE TABLE #new (StockMovementID uniqueidentifier NOT NULL PRIMARY KEY);
                """,
                central,
                transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var bulk = new SqlBulkCopy(central, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#mov", BulkCopyTimeout = 120 })
            {
                foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await bulk.WriteToServerAsync(table, cancellationToken);
            }

            var rated = new List<(Guid Branch, Guid Item)>();
            await using (var apply = new SqlCommand(
                """
                UPDATE source
                SET Origin = CASE
                        WHEN source.HistoryOnly = 1 THEN N'History'
                        WHEN source.SourceType = N'InventoryReconciliation'
                             AND EXISTS (SELECT 1 FROM dbo.InventoryCorrectionRequests AS request
                                         WHERE request.RequestID = source.SourceDocumentID
                                           AND request.BranchID = source.BranchID
                                           AND request.CentralAppliedAt IS NOT NULL) THEN N'Echo'
                        ELSE N'Branch' END
                FROM #mov AS source;

                INSERT INTO dbo.BranchStockMovements
                    (StockMovementID, BranchID, ItemID, ItemNumber, SourceType, SourceDocumentID, SourceLineID, PostingMode,
                     PreviousQuantity, QuantityDelta, ResultingQuantity, OccurredAt, CreatedAt, Origin, AppliedToBalance)
                OUTPUT inserted.StockMovementID INTO #new (StockMovementID)
                SELECT source.StockMovementID, source.BranchID, source.ItemID, source.ItemNumber, source.SourceType,
                       source.SourceDocumentID, source.SourceLineID, source.PostingMode, source.PreviousQuantity,
                       source.QuantityDelta, source.ResultingQuantity, source.OccurredAt, source.CreatedAt,
                       source.Origin, CASE WHEN source.Origin = N'Branch' THEN 1 ELSE 0 END
                FROM #mov AS source
                WHERE NOT EXISTS (SELECT 1 FROM dbo.BranchStockMovements AS existing WHERE existing.StockMovementID = source.StockMovementID);

                -- الفارق الفعلي: رصيد مطلق كتبه فرع لم يكن رصيده معروفاً ليس فارقاً (QuantityDelta فيه = الرصيد كله).
                ;WITH applied AS
                (
                    SELECT source.*,
                           CASE WHEN source.PostingMode = N'Absolute' AND source.PreviousQuantity IS NULL THEN NULL
                                ELSE source.QuantityDelta END AS EffectiveDelta,
                           ROW_NUMBER() OVER (PARTITION BY source.BranchID, source.ItemID
                                              ORDER BY CASE WHEN source.ResultingQuantity IS NULL THEN 1 ELSE 0 END, source.Seq DESC) AS Position
                    FROM #mov AS source
                    INNER JOIN #new AS added ON added.StockMovementID = source.StockMovementID
                    WHERE source.Origin = N'Branch'
                )
                SELECT BranchID, ItemID,
                       MAX(ItemNumber) AS ItemNumber,
                       SUM(EffectiveDelta) AS TotalDelta,
                       MAX(CASE WHEN Position = 1 THEN ResultingQuantity END) AS LatestResulting,
                       MAX(CreatedAt) AS LatestAt
                INTO #latest
                FROM applied
                GROUP BY BranchID, ItemID;

                UPDATE target
                SET OnHandQuantity = CASE
                        WHEN target.IsQuantityInitialized = 1 AND target.OnHandQuantity IS NOT NULL
                            THEN target.OnHandQuantity + ISNULL(latest.TotalDelta, 0)
                        ELSE COALESCE(latest.LatestResulting, target.OnHandQuantity)
                    END,
                    IsQuantityInitialized = CASE
                        WHEN target.IsQuantityInitialized = 1 AND target.OnHandQuantity IS NOT NULL THEN 1
                        WHEN latest.LatestResulting IS NOT NULL THEN 1
                        ELSE target.IsQuantityInitialized END,
                    LastMovementAt = CASE WHEN target.LastMovementAt IS NULL OR target.LastMovementAt < latest.LatestAt
                                          THEN latest.LatestAt ELSE target.LastMovementAt END,
                    BalanceUpdatedAt = CASE WHEN target.BalanceUpdatedAt < latest.LatestAt THEN latest.LatestAt ELSE target.BalanceUpdatedAt END,
                    ReceivedAt = SYSUTCDATETIME()
                FROM dbo.BranchStockSnapshots AS target
                INNER JOIN #latest AS latest ON latest.BranchID = target.BranchID AND latest.ItemID = target.ItemID;

                INSERT INTO dbo.BranchStockSnapshots
                    (BranchID, ItemID, ItemNumber, OnHandQuantity, IsQuantityInitialized, DailySalesRate, BalanceUpdatedAt, LastMovementAt)
                SELECT latest.BranchID, latest.ItemID, latest.ItemNumber, latest.LatestResulting,
                       CASE WHEN latest.LatestResulting IS NOT NULL THEN 1 ELSE 0 END, NULL, latest.LatestAt, latest.LatestAt
                FROM #latest AS latest
                WHERE NOT EXISTS (SELECT 1 FROM dbo.BranchStockSnapshots AS target
                                  WHERE target.BranchID = latest.BranchID AND target.ItemID = latest.ItemID);

                -- أول بيع وصل المركز لكل فرع: أساس عدد أيام السرعة.
                ;WITH sales AS
                (
                    SELECT source.BranchID, MIN(source.OccurredAt) AS FirstSale
                    FROM #mov AS source
                    INNER JOIN #new AS added ON added.StockMovementID = source.StockMovementID
                    WHERE source.SourceType IN (N'Sale', N'SaleReturn')
                    GROUP BY source.BranchID
                )
                MERGE dbo.BranchSnapshotStatus AS status
                USING sales ON status.BranchID = sales.BranchID
                WHEN MATCHED AND (status.FirstSaleAt IS NULL OR status.FirstSaleAt > sales.FirstSale)
                    THEN UPDATE SET FirstSaleAt = sales.FirstSale
                WHEN NOT MATCHED
                    THEN INSERT (BranchID, LastPushAt, FirstSaleAt) VALUES (sales.BranchID, SYSUTCDATETIME(), sales.FirstSale);

                SELECT DISTINCT source.BranchID, source.ItemID
                FROM #mov AS source
                INNER JOIN #new AS added ON added.StockMovementID = source.StockMovementID
                WHERE source.SourceType IN (N'Sale', N'SaleReturn');

                DROP TABLE #latest;
                DROP TABLE #new;
                DROP TABLE #mov;
                """,
                central,
                transaction)
            { CommandTimeout = 120 })
            {
                await using var reader = await apply.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (reader.FieldCount == 2) rated.Add((reader.GetGuid(0), reader.GetGuid(1)));
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));
            }

            // قرار المالك: السرعة الجديدة تُحسب لحظياً مع كل بيع.
            foreach (var branch in rated.GroupBy(pair => pair.Branch))
            {
                await Inventory.CentralStock.RecomputeRatesAsync(
                    central,
                    transaction,
                    branch.Key,
                    branch.Select(pair => pair.Item).Distinct().ToList(),
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task MarkSentAsync(SqlConnection local, IReadOnlyList<PendingMovement> pending, CancellationToken cancellationToken)
    {
        var ids = new StringBuilder();
        await using var command = new SqlCommand { Connection = local };
        for (var index = 0; index < pending.Count; index++)
        {
            if (index > 0) ids.Append(',');
            var name = "@m" + index.ToString(CultureInfo.InvariantCulture);
            ids.Append(name);
            command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = pending[index].Id;
        }

        command.CommandText = $"UPDATE dbo.StockMovements SET CentralRelayedAt = SYSUTCDATETIME() WHERE StockMovementID IN ({ids}) AND CentralRelayedAt IS NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteLogAsync(SqlConnection local, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL
                UPDATE log
                SET Status = N'Completed',
                    CompletedAt = SYSUTCDATETIME(),
                    LastAttemptAt = SYSUTCDATETIME(),
                    AttemptCount = log.AttemptCount + 1,
                    ResultMessage = N'تم ترحيل الكميات إلى المركز.'
                FROM dbo.BranchOperationLog AS log
                WHERE log.OperationType = @type
                  AND log.Status NOT IN (N'Completed', N'Cancelled')
                  AND NOT EXISTS (SELECT 1 FROM dbo.StockMovements AS movement
                                  WHERE movement.RelayOperationID = log.OperationID AND movement.CentralRelayedAt IS NULL);
            """,
            local);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task NoteFailureAsync(SqlConnection local, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(
                """
                UPDATE dbo.BranchOperationLog
                SET ResultMessage = @message, LastAttemptAt = SYSUTCDATETIME()
                WHERE OperationType = @type AND Status NOT IN (N'Completed', N'Cancelled');
                """,
                local);
            command.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
            command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = "لم تصل الكميات إلى المركز بعد، ستُعاد المحاولة: " + message;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
        }
    }
}
