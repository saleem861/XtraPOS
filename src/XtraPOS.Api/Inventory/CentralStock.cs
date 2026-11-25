using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// قرار المالك (25 نوفمبر 2026): في النظام المركزي المركز هو المرجع للكمية، ولا نعتمد على بيانات الفرع.
/// - رصيد المركز (BranchStockSnapshots) = مجموع فروق الحركات: البيع وغيره يصل من الفرع فوراً ويُضاف بفارقه.
/// - الجرد والتصحيح (أي نوع، وأي فرع، حتى فرع الجهاز) يُثبَّت على المركز أولاً بالفارق، ويُكتب طلب تصحيح
///   يأخذه الفرع في أول مزامنة. حين ينفّذه الفرع ترجع حركته للمركز «صدى» فلا تُضاف ثانية.
/// - الفرع مرجع للكمية فقط في جهاز البيع المنفرد (بلا مركز).
/// </summary>
public static class CentralStock
{
    public const string OriginBranch = "Branch";
    public const string OriginCentral = "Central";
    public const string OriginEcho = "Echo";
    public const string OriginHistory = "History";

    public const string NotReady = "المركز لم يُحدَّث بعد ليكون مرجع الكميات (الترحيل 044). يُحدَّث تلقائياً عند تشغيل نسخة محدثة.";

    private static readonly ConcurrentDictionary<string, bool> ReadyDatabases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>أعمدة الترحيل 044 موجودة على المركز.</summary>
    public static async Task<bool> IsReadyAsync(SqlConnection central, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var key = central.DataSource + "|" + central.Database;
        if (ReadyDatabases.ContainsKey(key)) return true;
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN COL_LENGTH(N'dbo.BranchStockMovements', N'Origin') IS NOT NULL
                         AND COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'CentralAppliedAt') IS NOT NULL
                         AND COL_LENGTH(N'dbo.BranchStockSnapshots', N'BranchReportedQuantity') IS NOT NULL
                         AND COL_LENGTH(N'dbo.BranchSnapshotStatus', N'FirstSaleAt') IS NOT NULL
                        THEN 1 ELSE 0 END;
            """,
            central,
            transaction);
        var ready = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        if (ready) ReadyDatabases[key] = true;
        return ready;
    }

    public static async Task RequireReadyAsync(SqlConnection central, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!await IsReadyAsync(central, transaction, cancellationToken))
        {
            throw new InventoryException(NotReady, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>جرد أو تصحيح لصنف في فرع. RequestId ثابت لكل سطر، فإعادة الإرسال لا تكرر شيئاً.</summary>
    public sealed record CorrectionInput(
        Guid BranchId,
        Guid ItemId,
        Guid RequestId,
        string SourceType,
        Guid? SourceDocumentId,
        decimal Counted,
        decimal? Expected,
        string Reason,
        Guid UserId,
        string? UserName);

    public sealed record CorrectionResult(Guid MovementId, decimal? Previous, decimal? Delta, decimal Resulting, bool Replay);

    /// <summary>
    /// داخل معاملة المركز: يقفل رصيد الصنف في الفرع، ويضيف الفارق (أو يكتب المعدودة إن كان الرصيد غير معروف)،
    /// ويكتب حركة مركزية (معرّفها = معرّف الطلب) وطلب تصحيح للفرع معلَّماً بأنه طُبّق على المركز.
    /// </summary>
    public static async Task<CorrectionResult> PostCorrectionAsync(
        SqlConnection central,
        SqlTransaction transaction,
        CorrectionInput input,
        CancellationToken cancellationToken)
    {
        if (input.RequestId == Guid.Empty) throw new InventoryException("طلب التصحيح غير صالح.");
        await using (var existing = new SqlCommand(
            """
            SELECT CentralPreviousQuantity, DeltaQuantity, COALESCE(CentralResultingQuantity, NewQuantity)
            FROM dbo.InventoryCorrectionRequests WITH (UPDLOCK, HOLDLOCK)
            WHERE RequestID = @id;
            """,
            central,
            transaction))
        {
            existing.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = input.RequestId;
            await using var found = await existing.ExecuteReaderAsync(cancellationToken);
            if (await found.ReadAsync(cancellationToken))
            {
                return new CorrectionResult(
                    input.RequestId,
                    found.IsDBNull(0) ? null : found.GetDecimal(0),
                    found.IsDBNull(1) ? null : found.GetDecimal(1),
                    found.GetDecimal(2),
                    true);
            }
        }

        string itemNumber;
        string? itemName;
        await using (var item = new SqlCommand(
            """
            SELECT item.ItemNumber, item.ItemName
            FROM dbo.Items AS item
            WHERE item.ItemID = @item
              AND EXISTS (SELECT 1 FROM dbo.Branches AS branch WHERE branch.BranchID = @branch);
            """,
            central,
            transaction))
        {
            item.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = input.ItemId;
            item.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = input.BranchId;
            await using var reader = await item.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InventoryException("الصنف أو الفرع غير موجود في المركز.", StatusCodes.Status404NotFound);
            }

            itemNumber = reader.GetString(0);
            itemName = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        decimal? current = null;
        await using (var balance = new SqlCommand(
            """
            SELECT CASE WHEN IsQuantityInitialized = 1 THEN OnHandQuantity END
            FROM dbo.BranchStockSnapshots WITH (UPDLOCK, HOLDLOCK)
            WHERE BranchID = @branch AND ItemID = @item;
            """,
            central,
            transaction))
        {
            balance.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = input.BranchId;
            balance.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = input.ItemId;
            if (await balance.ExecuteScalarAsync(cancellationToken) is decimal known) current = known;
        }

        var plan = InventoryRelayRules.PlanCentral(input.Counted, input.Expected, current);
        var baseline = plan.Delta is null ? (decimal?)null : input.Expected ?? current;
        await using (var write = new SqlCommand(
            """
            DECLARE @now datetime2(0) = SYSUTCDATETIME();

            UPDATE dbo.BranchStockSnapshots
            SET OnHandQuantity = @resulting, IsQuantityInitialized = 1, LastMovementAt = @now,
                BalanceUpdatedAt = @now, ReceivedAt = @now
            WHERE BranchID = @branch AND ItemID = @item;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.BranchStockSnapshots
                    (BranchID, ItemID, ItemNumber, OnHandQuantity, IsQuantityInitialized, DailySalesRate, BalanceUpdatedAt, LastMovementAt)
                VALUES (@branch, @item, @number, @resulting, 1, NULL, @now, @now);

            INSERT INTO dbo.BranchStockMovements
                (StockMovementID, BranchID, ItemID, ItemNumber, SourceType, SourceDocumentID, SourceLineID, PostingMode,
                 PreviousQuantity, QuantityDelta, ResultingQuantity, OccurredAt, CreatedAt, Origin, AppliedToBalance)
            VALUES (@id, @branch, @item, @number, @source, @document, @id, @mode,
                    @previous, @movementDelta, @resulting, @now, @now, @origin, 1);

            INSERT INTO dbo.InventoryCorrectionRequests
                (RequestID, BranchID, ItemID, ItemNumber, ItemName, NewQuantity, ExpectedQuantity, DeltaQuantity,
                 Reason, RequestedByUserID, RequestedByName, CentralAppliedAt, CentralPreviousQuantity,
                 CentralResultingQuantity, SourceType)
            VALUES (@id, @branch, @item, @number, @name, @counted, @expected, @delta,
                    @reason, @user, @userName, @now, @previous, @resulting, @source);
            """,
            central,
            transaction))
        {
            write.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = input.RequestId;
            write.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = input.BranchId;
            write.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = input.ItemId;
            write.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = itemNumber;
            write.Parameters.Add("@name", SqlDbType.NVarChar, 500).Value = (object?)itemName ?? DBNull.Value;
            write.Parameters.Add("@source", SqlDbType.NVarChar, 40).Value = input.SourceType;
            write.Parameters.Add("@document", SqlDbType.UniqueIdentifier).Value = (object?)input.SourceDocumentId ?? DBNull.Value;
            write.Parameters.Add("@mode", SqlDbType.NVarChar, 20).Value = plan.Delta is null ? StockPostingModes.Absolute : StockPostingModes.Delta;
            write.Parameters.Add("@origin", SqlDbType.NVarChar, 10).Value = OriginCentral;
            Quantity(write, "@previous", current);
            Quantity(write, "@movementDelta", plan.Delta ?? plan.Resulting - (current ?? 0m));
            Quantity(write, "@resulting", plan.Resulting);
            Quantity(write, "@counted", input.Counted);
            Quantity(write, "@expected", baseline);
            Quantity(write, "@delta", plan.Delta);
            write.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = Limit(input.Reason, 500) ?? "جرد";
            write.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = input.UserId;
            write.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = (object?)Limit(input.UserName, 200) ?? DBNull.Value;
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        return new CorrectionResult(input.RequestId, current, plan.Delta, plan.Resulting, false);
    }

    /// <summary>
    /// سرعة البيع على المركز (قرار المالك: طريقة جديدة غير مرتبطة بالبرنامج القديم): صافي المباع ÷ عدد الأيام،
    /// على آخر 30 يوماً، أو منذ أول بيع وصل المركز إن كان أحدث. items = null: كل أصناف الفرع (التحديث اليومي).
    /// </summary>
    public static async Task<int> RecomputeRatesAsync(
        SqlConnection central,
        SqlTransaction? transaction,
        Guid branchId,
        IReadOnlyCollection<Guid>? items,
        CancellationToken cancellationToken)
    {
        DateTime? firstSale;
        await using (var first = new SqlCommand("SELECT FirstSaleAt FROM dbo.BranchSnapshotStatus WHERE BranchID = @branch;", central, transaction))
        {
            first.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            firstSale = await first.ExecuteScalarAsync(cancellationToken) as DateTime?;
        }

        if (firstSale is not DateTime started || items is { Count: 0 }) return 0;
        var now = DateTime.UtcNow;
        var days = InventoryRelayRules.SalesRateDays(DateTime.SpecifyKind(started, DateTimeKind.Utc), now);
        var filter = items is null
            ? ""
            : $"AND snapshot.ItemID IN ({string.Join(", ", items.Select((_, index) => $"@i{index}"))})";
        await using var update = new SqlCommand(
            $"""
             UPDATE snapshot
             SET DailySalesRate = ROUND(CASE WHEN ISNULL(sold.Quantity, 0) > 0 THEN sold.Quantity ELSE 0 END / @days, 6)
             FROM dbo.BranchStockSnapshots AS snapshot
             LEFT JOIN (
                 SELECT ItemID, -SUM(QuantityDelta) AS Quantity
                 FROM dbo.BranchStockMovements
                 WHERE BranchID = @branch AND SourceType IN (N'Sale', N'SaleReturn') AND OccurredAt >= @from
                 GROUP BY ItemID) AS sold ON sold.ItemID = snapshot.ItemID
             WHERE snapshot.BranchID = @branch {filter};
             """,
            central,
            transaction) { CommandTimeout = 120 };
        update.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        update.Parameters.Add("@from", SqlDbType.DateTime2).Value = now.AddDays(-InventoryRelayRules.SalesRateWindowDays);
        update.Parameters.Add(new SqlParameter("@days", SqlDbType.Decimal) { Precision = 18, Scale = 6, Value = days });
        if (items is not null)
        {
            var index = 0;
            foreach (var item in items)
            {
                update.Parameters.Add($"@i{index++}", SqlDbType.UniqueIdentifier).Value = item;
            }
        }

        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Quantity(SqlCommand command, string name, decimal? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object?)value ?? DBNull.Value });

    private static string? Limit(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
