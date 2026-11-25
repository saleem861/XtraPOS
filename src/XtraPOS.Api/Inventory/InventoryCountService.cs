using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// «الجرد» من شاشة البيع (المرحلة 1 من وحدة الجرد والنواقص).
/// - قرار المالك (25 نوفمبر 2026): في النظام المركزي يجري كل جرد (أي نوع، وأي فرع، حتى فرع الجهاز) على المركز،
///   ويُثبَّت الفارق على المركز أولاً (CentralStock)، والفرع يأخذه في أول مزامنة. بلا اتصال بالمركز يتوقف الجرد.
/// - جهاز البيع المنفرد (بلا مركز): كل شيء على قاعدة الفرع.
/// - الجرد على صنف مالك المخزون فقط (ItemUnits). الأصناف الأم (ParentItemID) والعبوات لا تُعدّ مباشرة.
/// - كمية النظام تُقرأ لحظة حفظ العد، والتثبيت يرحّل الفرق عبر StockPostingService (InventoryCount).
/// - التصنيف (مهم / صفحة) والجدول الزمني يُسحبان من المركز كل دقيقة (PullSettingsAsync).
/// </summary>
public sealed class InventoryCountService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    StockPostingService stock,
    ILogger<InventoryCountService> logger)
{
    private static readonly TimeSpan CentralTimeout = TimeSpan.FromSeconds(6);
    private const string MigrationMissing = "قاعدة هذا الفرع لم تُحدَّث بعد للجرد (الترحيل 054).";

    // ------------------------------------------------------------------
    // الإعدادات
    // ------------------------------------------------------------------

    public async Task<object> ConfigurationAsync(CancellationToken cancellationToken, PosDeviceContext? actor = null, Guid? branchId = null)
    {
        await using var target = actor is null
            ? new Target(await OpenLocalAsync(cancellationToken), null, false)
            : await OpenTargetAsync(actor, branchId, cancellationToken);
        var connection = target.Connection;
        await RequireSchemaAsync(connection, cancellationToken);
        var local = InventoryCalendar.LocalNow();
        var period = InventoryCountRules.PeriodType(TimeOnly.FromDateTime(local));
        int? defaultPage = null;
        await using (var command = new SqlCommand(
            """
            SELECT TOP (1) InventoryPage FROM dbo.InventorySchedule
            WHERE DayOfWeek = @day AND PeriodType = @period AND IsEnabled = 1;
            """,
            connection))
        {
            command.Parameters.Add("@day", SqlDbType.TinyInt).Value = (byte)local.DayOfWeek;
            command.Parameters.Add("@period", SqlDbType.NVarChar, 2).Value = period;
            if (await command.ExecuteScalarAsync(cancellationToken) is byte page) defaultPage = page;
        }

        var pages = new Dictionary<int, int>();
        var important = 0;
        await using (var command = new SqlCommand(
            $"""
             SELECT setting.InventoryPage, setting.IsImportant, COUNT(*)
             FROM dbo.ItemInventorySettings AS setting
             INNER JOIN dbo.Items AS item ON item.ItemNumber = setting.ItemNumber AND item.IsActive = 1
             WHERE {StockOwnerFilter}
             GROUP BY setting.InventoryPage, setting.IsImportant;
             """,
            connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var page = reader.IsDBNull(0) ? (int?)null : reader.GetByte(0);
                var isImportant = reader.GetBoolean(1);
                var count = reader.GetInt32(2);
                if (isImportant || page == 0) important += count;
                if (page is >= 1 and <= InventoryCountRules.PeriodicPageCount)
                {
                    pages[page.Value] = pages.GetValueOrDefault(page.Value) + count;
                }
            }
        }

        var customRows = new List<(Guid Id, string Name, int Items)>();
        await using (var custom = new SqlCommand(
            $"""
            IF OBJECT_ID(N'dbo.CustomInventories', N'U') IS NOT NULL
                SELECT inventory.CustomInventoryID, inventory.InventoryName,
                       (SELECT COUNT(*) FROM dbo.CustomInventoryItems AS item WHERE item.CustomInventoryID = inventory.CustomInventoryID)
                FROM dbo.CustomInventories AS inventory
                WHERE inventory.IsActive = 1 {CustomForBranch(target.Remote)}
                ORDER BY inventory.InventoryName;
            """,
            connection))
        {
            custom.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = (object?)target.Actor?.BranchId ?? DBNull.Value;
            await using var reader = await custom.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                customRows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }

        // كما في النظام القديم: تحت كل جرد مخصص فروعه وحالة كل فرع (مكتمل = جرد مثبت بعد آخر تعديل للجرد المخصص).
        var customBranches = new Dictionary<Guid, List<object>>();
        if (target.Remote && customRows.Count > 0)
        {
            await using var statuses = new SqlCommand(
                """
                SELECT assigned.CustomInventoryID, branch.BranchName,
                       CASE WHEN EXISTS (SELECT 1 FROM dbo.InventorySessions AS countSession
                                         WHERE countSession.CustomInventoryID = assigned.CustomInventoryID
                                           AND countSession.BranchID = assigned.BranchID
                                           AND countSession.Status = N'Completed'
                                           AND countSession.CompletedAt >= inventory.UpdatedAt) THEN 1 ELSE 0 END
                FROM dbo.CustomInventoryBranches AS assigned
                INNER JOIN dbo.CustomInventories AS inventory ON inventory.CustomInventoryID = assigned.CustomInventoryID
                INNER JOIN dbo.Branches AS branch ON branch.BranchID = assigned.BranchID AND branch.IsActive = 1
                WHERE inventory.IsActive = 1
                  AND EXISTS (SELECT 1 FROM dbo.CustomInventoryBranches AS mine
                              WHERE mine.CustomInventoryID = assigned.CustomInventoryID AND mine.BranchID = @branch)
                ORDER BY branch.BranchName;
                """,
                connection);
            statuses.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = (object?)target.Actor?.BranchId ?? DBNull.Value;
            await using var reader = await statuses.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!customBranches.TryGetValue(id, out var list)) customBranches[id] = list = [];
                list.Add(new { branchName = reader.GetString(1), completed = reader.GetInt32(2) == 1 });
            }
        }

        var customInventories = customRows
            .Select(row => (object)new
            {
                customInventoryId = row.Id,
                name = row.Name,
                items = row.Items,
                branches = customBranches.TryGetValue(row.Id, out var assignedBranches) ? assignedBranches : new List<object>(),
            })
            .ToList();

        return new
        {
            branchId = target.Actor?.BranchId,
            remote = target.Remote,
            customInventories,
            businessDate = DateOnly.FromDateTime(local).ToString("yyyy-MM-dd"),
            periodType = period,
            cutoff = InventoryCountRules.PeriodCutoff.ToString("HH:mm"),
            pageCount = InventoryCountRules.PeriodicPageCount,
            defaultPage,
            importantItems = important,
            pageItems = Enumerable.Range(1, InventoryCountRules.PeriodicPageCount)
                .Select(page => new { page, items = pages.GetValueOrDefault(page) })
                .ToArray(),
        };
    }

    // ------------------------------------------------------------------
    // الجلسات
    // ------------------------------------------------------------------

    /// <summary>جرد اليوم غير المثبت لنفس الموظف ونفس النوع ونفس مجموعة الصفحات، إن وُجد.</summary>
    public async Task<object> FindOpenAsync(PosDeviceContext device, string? type, IEnumerable<int>? pages, Guid? customInventoryId, CancellationToken cancellationToken, Guid? branchId = null)
    {
        var normalized = RequireType(type);
        var wanted = InventoryCountRules.NormalizePages(normalized, pages);
        var custom = RequireCustom(normalized, customInventoryId);
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        var open = await FindOpenSessionAsync(connection, null, actor, normalized, wanted, custom, InventoryCalendar.Today(), cancellationToken);
        return new { session = open is null ? null : await SessionSummaryAsync(connection, null, open.Value, cancellationToken) };
    }

    public async Task<object> StartAsync(PosDeviceContext device, string? type, IEnumerable<int>? pages, Guid? customInventoryId, bool replaceOpen, CancellationToken cancellationToken, Guid? branchId = null)
    {
        var normalized = RequireType(type);
        var wanted = InventoryCountRules.NormalizePages(normalized, pages);
        var custom = RequireCustom(normalized, customInventoryId);
        var today = InventoryCalendar.Today();
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            // جلسات الأيام السابقة المفتوحة لنفس الموظف تُهمل، ولا تُلمس جلسات الآخرين.
            await using (var stale = new SqlCommand(
                """
                UPDATE dbo.InventorySessions
                SET Status = N'Discarded', DiscardedAt = SYSUTCDATETIME()
                WHERE BranchID = @branch AND UserID = @user AND InventoryType = @type
                  AND Status = N'Open' AND BusinessDate < @today;
                """,
                connection,
                transaction))
            {
                stale.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
                stale.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
                stale.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = normalized;
                stale.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
                await stale.ExecuteNonQueryAsync(cancellationToken);
            }

            var open = await FindOpenSessionAsync(connection, transaction, actor, normalized, wanted, custom, today, cancellationToken);
            if (open is Guid existing)
            {
                if (!replaceOpen)
                {
                    var resumed = await SessionSummaryAsync(connection, transaction, existing, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new { session = resumed, resumed = true };
                }

                await using var discard = new SqlCommand(
                    "UPDATE dbo.InventorySessions SET Status = N'Discarded', DiscardedAt = SYSUTCDATETIME() WHERE SessionID = @id AND Status = N'Open';",
                    connection,
                    transaction);
                discard.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = existing;
                await discard.ExecuteNonQueryAsync(cancellationToken);
            }

            var sessionId = Guid.NewGuid();
            await using (var insert = new SqlCommand(
                """
                INSERT INTO dbo.InventorySessions
                    (SessionID, BusinessID, BranchID, StationID, DeviceID, UserID, UserName, InventoryType, BusinessDate, Status, CustomInventoryID,
                     StartedAt, LastSavedAt, TotalItems, CountedItems, PostedItems)
                VALUES (@id, @business, @branch, @station, @device, @user, @userName, @type, @today, N'Open', @custom,
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 0, 0);
                """,
                connection,
                transaction))
            {
                insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                insert.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.BusinessId;
                insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
                insert.Parameters.Add("@station", SqlDbType.UniqueIdentifier).Value = actor.StationId;
                insert.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = actor.DeviceId;
                insert.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
                insert.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = Limit(actor.DisplayName, 200) ?? (object)DBNull.Value;
                insert.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = normalized;
                insert.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
                insert.Parameters.Add("@custom", SqlDbType.UniqueIdentifier).Value = (object?)custom ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var page in wanted)
            {
                await using var pageInsert = new SqlCommand(
                    "INSERT INTO dbo.InventorySessionPages (SessionID, InventoryPage) VALUES (@id, @page);",
                    connection,
                    transaction);
                pageInsert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                pageInsert.Parameters.Add("@page", SqlDbType.TinyInt).Value = (byte)page;
                await pageInsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var total = normalized switch
            {
                InventoryTypes.Manual => 0,
                InventoryTypes.Custom => await SnapshotCustomItemsAsync(connection, transaction, actor.BranchId, sessionId, custom!.Value, target.Remote, cancellationToken),
                _ => await SnapshotItemsAsync(connection, transaction, actor.BranchId, sessionId, normalized, wanted, target.Remote, cancellationToken),
            };
            if (normalized != InventoryTypes.Manual && total == 0)
            {
                throw new InventoryException(
                    normalized switch
                    {
                        InventoryTypes.Important => "لا توجد أصناف مهمة مصنفة لهذا الفرع. لم يتم إنشاء الجرد.",
                        InventoryTypes.Custom => "هذا الجرد المخصص غير موجود لهذا الفرع أو لا أصناف فيه. لم يتم إنشاء الجرد.",
                        _ => "لا توجد أصناف مصنفة على هذه الصفحات. لم يتم إنشاء الجرد.",
                    },
                    StatusCodes.Status409Conflict);
            }

            await UpdateCountersAsync(connection, transaction, sessionId, cancellationToken);
            var created = await SessionSummaryAsync(connection, transaction, sessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new { session = created, resumed = false };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<object> ItemsAsync(PosDeviceContext device, Guid sessionId, bool canReview, CancellationToken cancellationToken, Guid? branchId = null)
    {
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        await RequireOwnedAsync(connection, null, actor, sessionId, requireOpen: false, cancellationToken);
        var summary = await SessionSummaryAsync(connection, null, sessionId, cancellationToken);
        var items = new List<object>();
        await using var command = new SqlCommand(
            """
            SELECT SessionItemID, ItemID, ItemNumber, ItemName, IsCounted, CountedQuantity, SystemQuantityAtCount,
                   SystemQuantityKnown, CountedAt, CountedByName, PostedAt, QuantityAfterPosting
            FROM dbo.InventorySessionItems
            WHERE SessionID = @id
            ORDER BY CASE WHEN IsCounted = 1 THEN 1 ELSE 0 END, ItemNumber;
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader, canReview));
        }

        return new { session = summary, items };
    }

    /// <summary>بحث برقم الصنف أو الباركود. العبوة تُحوَّل إلى صنف مالك المخزون.</summary>
    public async Task<object> LookupAsync(PosDeviceContext device, Guid sessionId, string? code, CancellationToken cancellationToken, Guid? branchId = null)
    {
        var text = code?.Trim() ?? "";
        if (text.Length == 0) throw new InventoryException("يرجى إدخال رقم الصنف.");
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        var session = await RequireOwnedAsync(connection, null, actor, sessionId, requireOpen: true, cancellationToken);
        var resolved = await ResolveOwnerAsync(connection, null, text, cancellationToken)
            ?? throw new InventoryException("الصنف غير موجود.", StatusCodes.Status404NotFound);

        await using var line = new SqlCommand(
            "SELECT IsCounted, CountedQuantity FROM dbo.InventorySessionItems WHERE SessionID = @id AND ItemID = @item;",
            connection);
        line.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        line.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = resolved.OwnerItemId;
        bool? counted = null;
        decimal? countedQuantity = null;
        await using (var reader = await line.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                counted = reader.GetBoolean(0);
                countedQuantity = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
            }
        }

        if (counted is null && session.Type != InventoryTypes.Manual)
        {
            throw new InventoryException("هذا الصنف ليس ضمن هذا الجرد.", StatusCodes.Status404NotFound);
        }

        return new
        {
            itemId = resolved.OwnerItemId,
            itemNumber = resolved.OwnerItemNumber,
            itemName = resolved.OwnerItemName,
            scannedItemNumber = resolved.ScannedItemNumber,
            convertedFromPackage = resolved.OwnerItemId != resolved.ScannedItemId,
            inSession = counted is not null,
            isCounted = counted == true,
            countedQuantity,
        };
    }

    public async Task<object> SaveCountAsync(PosDeviceContext device, Guid sessionId, Guid itemId, decimal? quantity, bool canReview, CancellationToken cancellationToken, Guid? branchId = null)
    {
        var counted = InventoryCountRules.ValidateCountedQuantity(quantity);
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var session = await RequireOwnedAsync(connection, transaction, actor, sessionId, requireOpen: true, cancellationToken);
            var item = await SaveOneAsync(connection, transaction, actor, session, target.Remote, sessionId, itemId, counted, canReview, cancellationToken);
            await UpdateCountersAsync(connection, transaction, sessionId, cancellationToken);
            var summary = await SessionSummaryAsync(connection, transaction, sessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new { session = summary, item };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public const int MaxBatchCounts = 100;

    /// <summary>
    /// ملاحظة المالك (25 نوفمبر): الحفظ بطيء. الشاشة ترسل كل العدّ المنتظر دفعة واحدة: اتصال واحد بالمركز،
    /// وفحص واحد للجلسة، ومعاملة واحدة، بدل طلب كامل لكل صنف. صنف مرفوض (ليس في الجرد) لا يُسقط الباقي،
    /// ويرجع في rejected برسالته. الترتيب محفوظ، والصنف المكرر يأخذ آخر كمية.
    /// </summary>
    public async Task<object> SaveCountsAsync(
        PosDeviceContext device,
        Guid sessionId,
        IReadOnlyList<(Guid ItemId, decimal? Quantity)> lines,
        bool canReview,
        CancellationToken cancellationToken,
        Guid? branchId = null)
    {
        if (lines.Count == 0) throw new InventoryException("لا يوجد عدّ للحفظ.");
        if (lines.Count > MaxBatchCounts) throw new InventoryException($"أقصى عدد في الدفعة {MaxBatchCounts} صنفاً.");
        var latest = new Dictionary<Guid, decimal>();
        var order = new List<Guid>();
        foreach (var line in lines)
        {
            var counted = InventoryCountRules.ValidateCountedQuantity(line.Quantity);
            if (!latest.ContainsKey(line.ItemId)) order.Add(line.ItemId);
            latest[line.ItemId] = counted;
        }

        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await RequireSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var session = await RequireOwnedAsync(connection, transaction, actor, sessionId, requireOpen: true, cancellationToken);
            var items = new List<object>();
            var rejected = new List<object>();
            foreach (var itemId in order)
            {
                try
                {
                    var item = await SaveOneAsync(connection, transaction, actor, session, target.Remote, sessionId, itemId, latest[itemId], canReview, cancellationToken);
                    if (item is not null) items.Add(item);
                }
                catch (InventoryException exception) when (exception.StatusCode is >= 400 and < 500)
                {
                    rejected.Add(new { itemId, message = exception.Message });
                }
            }

            await UpdateCountersAsync(connection, transaction, sessionId, cancellationToken);
            var summary = await SessionSummaryAsync(connection, transaction, sessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new { session = summary, items, rejected };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>حفظ عدّ صنف واحد داخل معاملة مفتوحة (الكمية المفترضة = الرصيد لحظة العد).</summary>
    private async Task<object?> SaveOneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PosDeviceContext actor,
        SessionRow session,
        bool remote,
        Guid sessionId,
        Guid itemId,
        decimal counted,
        bool canReview,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var find = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.InventorySessionItems WITH (UPDLOCK, HOLDLOCK) WHERE SessionID = @id AND ItemID = @item;",
            connection,
            transaction))
        {
            find.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
            find.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            exists = (int)(await find.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
        }

        if (!exists)
        {
            if (session.Type != InventoryTypes.Manual)
            {
                throw new InventoryException("هذا الصنف ليس ضمن هذا الجرد.", StatusCodes.Status404NotFound);
            }

            await InsertManualItemAsync(connection, transaction, actor.BranchId, sessionId, itemId, remote, cancellationToken);
        }

        var balance = await ReadBalanceAsync(connection, transaction, actor.BranchId, itemId, remote, cancellationToken);
        await using (var update = new SqlCommand(
            """
            UPDATE dbo.InventorySessionItems
            SET IsCounted = 1,
                CountedQuantity = @counted,
                SystemQuantityAtCount = @system,
                SystemQuantityKnown = @known,
                CountedAt = SYSUTCDATETIME(),
                CountedByUserID = @user,
                CountedByName = @userName
            WHERE SessionID = @id AND ItemID = @item;
            """,
            connection,
            transaction))
        {
            AddDecimal(update, "@counted", counted);
            AddNullableDecimal(update, "@system", balance.Known ? balance.Quantity : null);
            update.Parameters.Add("@known", SqlDbType.Bit).Value = balance.Known;
            update.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
            update.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = Limit(actor.DisplayName, 200) ?? (object)DBNull.Value;
            update.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
            update.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = new SqlCommand(
            """
            SELECT SessionItemID, ItemID, ItemNumber, ItemName, IsCounted, CountedQuantity, SystemQuantityAtCount,
                   SystemQuantityKnown, CountedAt, CountedByName, PostedAt, QuantityAfterPosting
            FROM dbo.InventorySessionItems WHERE SessionID = @id AND ItemID = @item;
            """,
            connection,
            transaction);
        read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        read.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader, canReview) : null;
    }

    /// <summary>
    /// «تثبيت الجرد»: يرحّل الأصناف المعدودة فقط، حركة InventoryCount لكل صنف، في معاملة واحدة.
    /// إعادة التثبيت لا تكرر الحركة (SourceLineID = سطر الجرد).
    /// </summary>
    public async Task<object> FinalizeAsync(PosDeviceContext device, Guid sessionId, CancellationToken cancellationToken, Guid? branchId = null)
    {
        if (await CentralConfiguredAsync(cancellationToken))
        {
            return await FinalizeCentralAsync(device, branchId, sessionId, cancellationToken);
        }

        if (branchId is Guid other && other != Guid.Empty && other != device.BranchId)
        {
            throw new InventoryException(StandaloneOtherBranch, StatusCodes.Status409Conflict);
        }

        var actor = device;
        await using (var check = await OpenLocalAsync(cancellationToken))
        {
            await RequireSchemaAsync(check, cancellationToken);
        }

        return await stock.InTransactionAsync<object>(async (posting, token) =>
        {
            var connection = posting.Connection;
            var transaction = posting.Transaction;
            await using (var lockSession = new SqlCommand(
                "SELECT Status FROM dbo.InventorySessions WITH (UPDLOCK, HOLDLOCK) WHERE SessionID = @id;",
                connection,
                transaction))
            {
                lockSession.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await lockSession.ExecuteScalarAsync(token);
            }

            await RequireOwnedAsync(connection, transaction, actor, sessionId, requireOpen: true, token);
            await RequireAllCountedAsync(connection, transaction, sessionId, token);
            var lines = new List<(Guid LineId, Guid ItemId, decimal Counted, decimal? System, bool Known)>();
            await using (var read = new SqlCommand(
                """
                SELECT SessionItemID, ItemID, CountedQuantity, SystemQuantityAtCount, SystemQuantityKnown
                FROM dbo.InventorySessionItems
                WHERE SessionID = @id AND IsCounted = 1 AND CountedQuantity IS NOT NULL AND PostedAt IS NULL
                ORDER BY ItemID;
                """,
                connection,
                transaction))
            {
                read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    lines.Add((
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetDecimal(2),
                        reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                        reader.GetBoolean(4)));
                }
            }

            var now = DateTime.UtcNow;
            foreach (var line in lines)
            {
                var plan = InventoryCountRules.PlanPosting(line.Counted, line.System, line.Known);
                var result = plan.Absolute
                    ? await stock.SetAbsoluteQuantityAsync(
                        new StockAbsoluteRequest(actor.BranchId, line.ItemId, StockSourceTypes.InventoryCount, sessionId, line.LineId, line.Counted, null, now, actor.UserId),
                        posting,
                        token)
                    : await stock.PostDeltaAsync(
                        new StockDeltaRequest(actor.BranchId, line.ItemId, StockSourceTypes.InventoryCount, sessionId, line.LineId, plan.Delta!.Value, null, now, actor.UserId, line.Counted),
                        posting,
                        token);

                await using var mark = new SqlCommand(
                    """
                    UPDATE dbo.InventorySessionItems
                    SET StockMovementID = @movement, PostedAt = SYSUTCDATETIME(),
                        QuantityBeforePosting = @before, QuantityAfterPosting = @after
                    WHERE SessionItemID = @line;
                    """,
                    connection,
                    transaction);
                mark.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = result.StockMovementId;
                AddNullableDecimal(mark, "@before", result.PreviousQuantity);
                AddNullableDecimal(mark, "@after", result.ResultingQuantity);
                mark.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = line.LineId;
                await mark.ExecuteNonQueryAsync(token);
            }

            await using (var complete = new SqlCommand(
                """
                UPDATE dbo.InventorySessions
                SET Status = N'Completed', CompletedAt = SYSUTCDATETIME(), LastSavedAt = SYSUTCDATETIME(),
                    PostedItems = (SELECT COUNT(*) FROM dbo.InventorySessionItems WHERE SessionID = @id AND PostedAt IS NOT NULL)
                WHERE SessionID = @id;
                """,
                connection,
                transaction))
            {
                complete.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await complete.ExecuteNonQueryAsync(token);
            }

            await UpdateCountersAsync(connection, transaction, sessionId, token);
            var summary = await SessionSummaryAsync(connection, transaction, sessionId, token);
            return new { session = summary, posted = lines.Count };
        }, cancellationToken);
    }

    // ------------------------------------------------------------------
    // السحب من المركز
    // ------------------------------------------------------------------

    /// <summary>
    /// ينسخ التصنيف الأحدث والجدول الزمني من المركز. التصنيف لا يُحذف من المركز: إلغاؤه = IsImportant 0 وصفحة فارغة.
    /// </summary>
    public async Task<int> PullSettingsAsync(CancellationToken cancellationToken)
    {
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return 0;
        if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null) return 0;
        await using var local = new SqlConnection(branchConnection);
        await local.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(local, "dbo.ItemInventorySettings", cancellationToken)) return 0;
        var central = await OpenCentralAsync(cancellationToken);
        if (central is null) return 0;
        await using var lease = central;
        if (!await TableExistsAsync(central, "dbo.ItemInventorySettings", cancellationToken)) return 0;

        DateTime since;
        await using (var newest = new SqlCommand("SELECT COALESCE(MAX(UpdatedAt), '2000-01-01') FROM dbo.ItemInventorySettings;", local))
        {
            since = (DateTime)(await newest.ExecuteScalarAsync(cancellationToken))!;
        }

        // نسخة الفرع من نفس النافذة: الصفوف التي لم تتغير لا تُكتب.
        var known = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        await using (var mine = new SqlCommand(
            "SELECT ItemNumber, UpdatedAt FROM dbo.ItemInventorySettings WHERE UpdatedAt >= DATEADD(MINUTE, -10, @since);",
            local))
        {
            mine.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
            await using var reader = await mine.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                known[reader.GetString(0)] = reader.GetDateTime(1);
            }
        }

        // استيراد دفعة واحدة يعطي آلاف الصفوف نفس UpdatedAt، فالقراءة بصفحات (UpdatedAt, ItemNumber) حتى النهاية.
        var copied = 0;
        DateTime? afterTime = null;
        var afterNumber = "";
        while (true)
        {
            var rows = new List<(string Number, bool Important, byte? Page, int? Days, DateTime UpdatedAt, string? By)>();
            await using (var read = new SqlCommand(
                """
                SELECT TOP (2000) ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName
                FROM dbo.ItemInventorySettings
                WHERE UpdatedAt >= DATEADD(MINUTE, -10, @since)
                  AND (@afterTime IS NULL OR UpdatedAt > @afterTime OR (UpdatedAt = @afterTime AND ItemNumber > @afterNumber))
                ORDER BY UpdatedAt, ItemNumber;
                """,
                central))
            {
                read.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
                read.Parameters.Add("@afterTime", SqlDbType.DateTime2).Value = (object?)afterTime ?? DBNull.Value;
                read.Parameters.Add("@afterNumber", SqlDbType.NVarChar, 100).Value = afterNumber;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add((
                        reader.GetString(0),
                        reader.GetBoolean(1),
                        reader.IsDBNull(2) ? (byte?)null : reader.GetByte(2),
                        reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                        reader.GetDateTime(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }

            foreach (var row in rows)
            {
                if (known.TryGetValue(row.Number, out var mineAt) && mineAt == row.UpdatedAt) continue;
                await using var upsert = new SqlCommand(
                    """
                    UPDATE dbo.ItemInventorySettings
                    SET IsImportant = @important, InventoryPage = @page, SupplyDays = @days, UpdatedAt = @updated, UpdatedByName = @by
                    WHERE ItemNumber = @number AND UpdatedAt <> @updated;
                    IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM dbo.ItemInventorySettings WHERE ItemNumber = @number)
                        INSERT INTO dbo.ItemInventorySettings (ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName)
                        VALUES (@number, @important, @page, @days, @updated, @by);
                    """,
                    local);
                upsert.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = row.Number;
                upsert.Parameters.Add("@important", SqlDbType.Bit).Value = row.Important;
                upsert.Parameters.Add("@page", SqlDbType.TinyInt).Value = (object?)row.Page ?? DBNull.Value;
                upsert.Parameters.Add("@days", SqlDbType.Int).Value = (object?)row.Days ?? DBNull.Value;
                upsert.Parameters.Add("@updated", SqlDbType.DateTime2).Value = row.UpdatedAt;
                upsert.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = (object?)row.By ?? DBNull.Value;
                copied += await upsert.ExecuteNonQueryAsync(cancellationToken) > 0 ? 1 : 0;
            }

            if (rows.Count < 2000) break;
            afterTime = rows[^1].UpdatedAt;
            afterNumber = rows[^1].Number;
        }

        if (await TableExistsAsync(central, "dbo.InventorySchedule", cancellationToken))
        {
            var schedule = new List<(byte Day, string Period, byte Page, bool Enabled)>();
            await using (var read = new SqlCommand("SELECT DayOfWeek, PeriodType, InventoryPage, IsEnabled FROM dbo.InventorySchedule;", central))
            {
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    schedule.Add((reader.GetByte(0), reader.GetString(1), reader.GetByte(2), reader.GetBoolean(3)));
                }
            }

            foreach (var row in schedule)
            {
                await using var upsert = new SqlCommand(
                    """
                    UPDATE dbo.InventorySchedule
                    SET InventoryPage = @page, IsEnabled = @enabled, UpdatedAt = SYSUTCDATETIME()
                    WHERE DayOfWeek = @day AND PeriodType = @period AND (InventoryPage <> @page OR IsEnabled <> @enabled);
                    IF NOT EXISTS (SELECT 1 FROM dbo.InventorySchedule WHERE DayOfWeek = @day AND PeriodType = @period)
                        INSERT INTO dbo.InventorySchedule (DayOfWeek, PeriodType, InventoryPage, IsEnabled) VALUES (@day, @period, @page, @enabled);
                    """,
                    local);
                upsert.Parameters.Add("@day", SqlDbType.TinyInt).Value = row.Day;
                upsert.Parameters.Add("@period", SqlDbType.NVarChar, 2).Value = row.Period;
                upsert.Parameters.Add("@page", SqlDbType.TinyInt).Value = row.Page;
                upsert.Parameters.Add("@enabled", SqlDbType.Bit).Value = row.Enabled;
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return copied;
    }

    // ------------------------------------------------------------------
    // مساعدات
    // ------------------------------------------------------------------

    /// <summary>صنف مالك مخزون: لا يشير إلى وحدة داخلية أخرى، وليس صنفاً أماً لتشكيلة.</summary>
    private const string StockOwnerFilter =
        """
        NOT EXISTS (SELECT 1 FROM dbo.ItemUnits AS unit
                    WHERE unit.ItemID = item.ItemID AND unit.IsActive = 1
                      AND unit.InternalUnitItemID IS NOT NULL AND unit.InternalUnitItemID <> item.ItemID)
        AND NOT EXISTS (SELECT 1 FROM dbo.Items AS child WHERE child.ParentItemID = item.ItemID)
        """;

    private sealed record SessionRow(Guid Id, string Type, string Status);

    internal sealed record OwnerResolution(Guid ScannedItemId, string ScannedItemNumber, Guid OwnerItemId, string OwnerItemNumber, string OwnerItemName);

    private static string RequireType(string? type) =>
        InventoryTypes.Normalize(type) ?? throw new InventoryException("نوع الجرد غير صالح.");

    private static Guid? RequireCustom(string type, Guid? customInventoryId)
    {
        if (type != InventoryTypes.Custom) return null;
        return customInventoryId is Guid id && id != Guid.Empty
            ? id
            : throw new InventoryException("يرجى اختيار الجرد المخصص.");
    }

    /// <summary>أصناف الجرد المخصص كما نزلت من المركز لهذا الفرع، مالكو المخزون النشطون فقط.</summary>
    private static async Task<int> SnapshotCustomItemsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid branchId,
        Guid sessionId,
        Guid customInventoryId,
        bool remote,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             INSERT INTO dbo.InventorySessionItems (SessionItemID, SessionID, ItemID, ItemNumber, ItemName, DailySalesRate, SupplyDays, IsCounted, SystemQuantityKnown)
             SELECT NEWID(), @session, item.ItemID, item.ItemNumber, item.ItemName, branchItem.DailySalesRate, setting.SupplyDays, 0, 0
             FROM dbo.CustomInventoryItems AS custom
             INNER JOIN dbo.CustomInventories AS inventory ON inventory.CustomInventoryID = custom.CustomInventoryID AND inventory.IsActive = 1
             INNER JOIN dbo.Items AS item ON item.ItemID = custom.ItemID AND item.IsActive = 1
             LEFT JOIN dbo.ItemInventorySettings AS setting ON setting.ItemNumber = item.ItemNumber
             LEFT JOIN {Card(remote)} AS branchItem ON branchItem.BranchID = @branch AND branchItem.ItemID = item.ItemID
             WHERE custom.CustomInventoryID = @custom {CustomForBranch(remote)}
               AND {StockOwnerFilter};
             """,
            connection,
            transaction);
        command.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@custom", SqlDbType.UniqueIdentifier).Value = customInventoryId;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> SnapshotItemsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid branchId,
        Guid sessionId,
        string type,
        IReadOnlyList<int> pages,
        bool remote,
        CancellationToken cancellationToken)
    {
        var scope = type == InventoryTypes.Important
            ? "(setting.IsImportant = 1 OR setting.InventoryPage = 0)"
            : $"setting.InventoryPage IN ({string.Join(", ", pages.Select((_, index) => $"@page{index}"))})";
        await using var command = new SqlCommand(
            $"""
             INSERT INTO dbo.InventorySessionItems (SessionItemID, SessionID, ItemID, ItemNumber, ItemName, DailySalesRate, SupplyDays, IsCounted, SystemQuantityKnown)
             SELECT NEWID(), @session, item.ItemID, item.ItemNumber, item.ItemName, branchItem.DailySalesRate, setting.SupplyDays, 0, 0
             FROM dbo.ItemInventorySettings AS setting
             INNER JOIN dbo.Items AS item ON item.ItemNumber = setting.ItemNumber AND item.IsActive = 1
             LEFT JOIN {Card(remote)} AS branchItem ON branchItem.BranchID = @branch AND branchItem.ItemID = item.ItemID
             WHERE {scope}
               AND {StockOwnerFilter};
             """,
            connection,
            transaction);
        command.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        for (var index = 0; index < pages.Count; index++)
        {
            command.Parameters.Add($"@page{index}", SqlDbType.TinyInt).Value = (byte)pages[index];
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertManualItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid branchId,
        Guid sessionId,
        Guid itemId,
        bool remote,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             INSERT INTO dbo.InventorySessionItems (SessionItemID, SessionID, ItemID, ItemNumber, ItemName, DailySalesRate, SupplyDays, IsCounted, SystemQuantityKnown)
             SELECT NEWID(), @session, item.ItemID, item.ItemNumber, item.ItemName, branchItem.DailySalesRate, setting.SupplyDays, 0, 0
             FROM dbo.Items AS item
             LEFT JOIN {Card(remote)} AS branchItem ON branchItem.BranchID = @branch AND branchItem.ItemID = item.ItemID
             LEFT JOIN dbo.ItemInventorySettings AS setting ON setting.ItemNumber = item.ItemNumber
             WHERE item.ItemID = @item AND item.IsActive = 1 AND {StockOwnerFilter};
             """,
            connection,
            transaction);
        command.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InventoryException("هذا الصنف لا يُجرد مباشرة. اجرد صنف الوحدة الأساسية.", StatusCodes.Status409Conflict);
        }
    }

    internal static async Task<OwnerResolution?> ResolveOwnerAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string code,
        CancellationToken cancellationToken)
    {
        var digits = ArabicSearchNormalizer.NormalizeDigits(code);
        Guid scannedId;
        string scannedNumber;
        await using (var find = new SqlCommand(
            """
            SELECT TOP (1) ItemID, ItemNumber FROM dbo.Items
            WHERE IsActive = 1 AND (ItemNumber = @code OR ItemNumber = @digits)
            ORDER BY CASE WHEN ItemNumber = @code THEN 0 ELSE 1 END;
            """,
            connection,
            transaction))
        {
            find.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = code;
            find.Parameters.Add("@digits", SqlDbType.NVarChar, 100).Value = digits;
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            scannedId = reader.GetGuid(0);
            scannedNumber = reader.GetString(1);
        }

        var links = new List<StockUnitLink>();
        await using (var read = new SqlCommand(
            "SELECT ItemID, InternalUnitItemID, UnitsPerPackage FROM dbo.ItemUnits WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL AND UnitsPerPackage > 0;",
            connection,
            transaction))
        {
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                links.Add(new StockUnitLink(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
            }
        }

        Guid ownerId;
        try
        {
            ownerId = StockOwnership.Resolve(scannedId, links).StockOwnerItemId;
        }
        catch (StockOwnershipException)
        {
            throw new InventoryException("سلسلة وحدات الصنف غير صالحة.", StatusCodes.Status409Conflict);
        }

        await using var owner = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @id;", connection, transaction);
        owner.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = ownerId;
        await using var ownerReader = await owner.ExecuteReaderAsync(cancellationToken);
        if (!await ownerReader.ReadAsync(cancellationToken)) return null;
        return new OwnerResolution(scannedId, scannedNumber, ownerId, ownerReader.GetString(0), ownerReader.GetString(1));
    }

    private static async Task<(decimal? Quantity, bool Known)> ReadBalanceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid branchId,
        Guid itemId,
        bool remote,
        CancellationToken cancellationToken)
    {
        // جرد فرع آخر: كمية النظام = رصيد ذلك الفرع كما وصل المركز (لحظياً منذ الترحيل الفوري للحركات).
        await using var command = new SqlCommand(
            $"SELECT OnHandQuantity, IsQuantityInitialized FROM {Balance(remote)} WHERE BranchID = @branch AND ItemID = @item;",
            connection,
            transaction);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (null, false);
        var quantity = reader.IsDBNull(0) ? (decimal?)null : reader.GetDecimal(0);
        return (quantity, reader.GetBoolean(1) && quantity is not null);
    }

    private static async Task<Guid?> FindOpenSessionAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PosDeviceContext actor,
        string type,
        IReadOnlyList<int> pages,
        Guid? custom,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        await using (var command = new SqlCommand(
            """
            SELECT SessionID FROM dbo.InventorySessions WITH (UPDLOCK, HOLDLOCK)
            WHERE BranchID = @branch AND UserID = @user AND InventoryType = @type
              AND Status = N'Open' AND BusinessDate = @today
              AND ((@custom IS NULL AND CustomInventoryID IS NULL) OR CustomInventoryID = @custom)
            ORDER BY StartedAt DESC;
            """,
            connection,
            transaction))
        {
            command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            command.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@custom", SqlDbType.UniqueIdentifier).Value = (object?)custom ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add(reader.GetGuid(0));
        }

        if (type != InventoryTypes.Periodic) return candidates.Count > 0 ? candidates[0] : null;
        foreach (var candidate in candidates)
        {
            if (InventoryCountRules.SamePages(await ReadPagesAsync(connection, transaction, candidate, cancellationToken), pages))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<List<int>> ReadPagesAsync(SqlConnection connection, SqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var pages = new List<int>();
        await using var command = new SqlCommand(
            "SELECT InventoryPage FROM dbo.InventorySessionPages WHERE SessionID = @id ORDER BY InventoryPage;",
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) pages.Add(reader.GetByte(0));
        return pages;
    }

    private static async Task<SessionRow> RequireOwnedAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PosDeviceContext actor,
        Guid sessionId,
        bool requireOpen,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT InventoryType, Status FROM dbo.InventorySessions WHERE SessionID = @id AND BranchID = @branch AND UserID = @user;",
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InventoryException("جلسة الجرد غير موجودة.", StatusCodes.Status404NotFound);
        }

        var row = new SessionRow(sessionId, reader.GetString(0), reader.GetString(1));
        if (requireOpen && row.Status != InventorySessionStatuses.Open)
        {
            throw new InventoryException(
                row.Status == InventorySessionStatuses.Completed ? "تم تثبيت هذا الجرد ولا يمكن تعديله." : "هذا الجرد لم يعد مفتوحاً.",
                StatusCodes.Status409Conflict);
        }

        return row;
    }

    private static async Task UpdateCountersAsync(SqlConnection connection, SqlTransaction transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE session
            SET TotalItems = counts.Total, CountedItems = counts.Counted, LastSavedAt = SYSUTCDATETIME()
            FROM dbo.InventorySessions AS session
            CROSS APPLY (
                SELECT COUNT(*) AS Total, COALESCE(SUM(CASE WHEN line.IsCounted = 1 THEN 1 ELSE 0 END), 0) AS Counted
                FROM dbo.InventorySessionItems AS line WHERE line.SessionID = session.SessionID) AS counts
            WHERE session.SessionID = @id;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object> SessionSummaryAsync(SqlConnection connection, SqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        var pages = await ReadPagesAsync(connection, transaction, sessionId, cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT InventoryType, Status, BusinessDate, StartedAt, LastSavedAt, CompletedAt, TotalItems, CountedItems, PostedItems
            FROM dbo.InventorySessions WHERE SessionID = @id;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InventoryException("جلسة الجرد غير موجودة.", StatusCodes.Status404NotFound);
        }

        var type = reader.GetString(0);
        var total = reader.GetInt32(6);
        var counted = reader.GetInt32(7);
        return new
        {
            sessionId,
            inventoryType = type,
            inventoryTypeLabel = InventoryTypes.Label(type),
            status = reader.GetString(1),
            businessDate = DateOnly.FromDateTime(reader.GetDateTime(2)).ToString("yyyy-MM-dd"),
            startedAt = Utc(reader.GetDateTime(3)),
            lastSavedAt = Utc(reader.GetDateTime(4)),
            completedAt = reader.IsDBNull(5) ? (DateTime?)null : Utc(reader.GetDateTime(5)),
            totalItems = total,
            countedItems = counted,
            uncountedItems = Math.Max(total - counted, 0),
            postedItems = reader.GetInt32(8),
            pages,
        };
    }

    private static object ReadItem(SqlDataReader reader, bool canReview)
    {
        var isCounted = reader.GetBoolean(4);
        var counted = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
        var system = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6);
        var known = reader.GetBoolean(7);
        var (expected, difference) = InventoryCountRules.Visible(canReview, isCounted, counted, system, known);
        return new
        {
            sessionItemId = reader.GetGuid(0),
            itemId = reader.GetGuid(1),
            itemNumber = reader.GetString(2),
            itemName = reader.IsDBNull(3) ? null : reader.GetString(3),
            isCounted,
            countedQuantity = counted,
            countedAt = reader.IsDBNull(8) ? (DateTime?)null : Utc(reader.GetDateTime(8)),
            countedByName = reader.IsDBNull(9) ? null : reader.GetString(9),
            posted = !reader.IsDBNull(10),
            expectedQuantity = expected,
            difference,
            quantityAfterPosting = canReview && !reader.IsDBNull(11) ? reader.GetDecimal(11) : (decimal?)null,
        };
    }

    // ------------------------------------------------------------------
    // جرد فرع آخر (ملاحظة المالك: لا يُقيَّد الجرد بالفرع الحالي)
    // ------------------------------------------------------------------

    /// <summary>
    /// القاعدة التي يجري عليها الجرد. في النظام المركزي: المركز دائماً، لأي فرع (نفس الجداول، والرصيد من
    /// BranchStockSnapshots، وهو رصيد المركز المرجعي). جهاز منفرد بلا مركز: قاعدة الفرع.
    /// </summary>
    private sealed record Target(SqlConnection Connection, PosDeviceContext? Actor, bool Remote) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private const string CentralRequired = "الجرد يجري على المركز أولاً، والمركز غير متاح الآن. يتوقف الجرد حتى يعود الاتصال.";
    private const string StandaloneOtherBranch = "جرد فرع آخر يحتاج نظاماً مركزياً. هذا الجهاز يعمل منفرداً.";

    /// <summary>نظام مركزي = هذا الجهاز مربوط بمركز (حتى لو كان المركز غير متاح الآن).</summary>
    internal async Task<bool> CentralConfiguredAsync(CancellationToken cancellationToken) =>
        await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is not null;

    private async Task<Target> OpenTargetAsync(PosDeviceContext device, Guid? branchId, CancellationToken cancellationToken)
    {
        var wanted = branchId is Guid id && id != Guid.Empty ? id : device.BranchId;
        if (!await CentralConfiguredAsync(cancellationToken))
        {
            if (wanted != device.BranchId) throw new InventoryException(StandaloneOtherBranch, StatusCodes.Status409Conflict);
            return new Target(await OpenLocalAsync(cancellationToken), device, false);
        }

        var central = await OpenCentralAsync(cancellationToken)
            ?? throw new InventoryException(CentralRequired, StatusCodes.Status503ServiceUnavailable);
        try
        {
            await using (var check = new SqlCommand(
                """
                SELECT CASE
                    WHEN OBJECT_ID(N'dbo.InventorySessionItems', N'U') IS NULL
                      OR OBJECT_ID(N'dbo.BranchStockSnapshots', N'U') IS NULL
                      OR COL_LENGTH(N'dbo.InventorySessions', N'CustomInventoryID') IS NULL
                      OR COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'DeltaQuantity') IS NULL
                      OR COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'CentralAppliedAt') IS NULL THEN -1
                    WHEN EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchID = @branch AND BusinessID = @business AND IsActive = 1) THEN 1
                    ELSE 0 END;
                """,
                central))
            {
                check.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = wanted;
                check.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = device.BusinessId;
                var state = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken));
                if (state == -1) throw new InventoryException(CentralStock.NotReady, StatusCodes.Status503ServiceUnavailable);
                if (state == 0) throw new InventoryException("الفرع غير موجود أو غير نشط في المركز.", StatusCodes.Status404NotFound);
            }

            return new Target(central, device with { BranchId = wanted }, true);
        }
        catch
        {
            await central.DisposeAsync();
            throw;
        }
    }

    /// <summary>بطاقة الصنف في الفرع (السرعة): المحلية، أو نسخة الفرع على المركز.</summary>
    private static string Card(bool remote) => remote ? "dbo.BranchStockSnapshots" : "dbo.ItemBranchInventory";

    /// <summary>رصيد الصنف في الفرع: المحلي، أو نسخة الفرع على المركز.</summary>
    private static string Balance(bool remote) => remote ? "dbo.BranchStockSnapshots" : "dbo.BranchStockBalances";

    /// <summary>على المركز: الجرد المخصص المخصص لذلك الفرع فقط.</summary>
    private static string CustomForBranch(bool remote) => remote
        ? "AND EXISTS (SELECT 1 FROM dbo.CustomInventoryBranches AS assigned WHERE assigned.CustomInventoryID = inventory.CustomInventoryID AND assigned.BranchID = @branch)"
        : "";

    /// <summary>قاعدة المالك: لا يُثبَّت أي جرد قبل حفظ عدّ كل أصنافه (اليدوي: كل ما أُضيف، وكله معدود عند إضافته).</summary>
    private static async Task RequireAllCountedAsync(SqlConnection connection, SqlTransaction transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*), SUM(CASE WHEN IsCounted = 1 AND CountedQuantity IS NOT NULL THEN 1 ELSE 0 END)
            FROM dbo.InventorySessionItems WHERE SessionID = @id;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var total = reader.GetInt32(0);
        var counted = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        if (!InventoryCountRules.CanFinalize(total, counted))
        {
            throw new InventoryException(total == 0
                ? "لا توجد أصناف معدودة لتثبيتها."
                : $"احفظ عدّ كل الأصناف قبل تثبيت الجرد (بقي {total - counted}).");
        }
    }

    /// <summary>الفروع التي يمكن جردها: فرع الجهاز دائماً، وفروع المركز النشطة (غير المخازن) إن كان متاحاً.</summary>
    public async Task<object> BranchesAsync(PosDeviceContext device, CancellationToken cancellationToken)
    {
        var rows = new List<object>();
        var seen = new HashSet<Guid>();
        string? notice = null;
        var central = await OpenCentralAsync(cancellationToken);
        if (central is not null)
        {
            await using var lease = central;
            try
            {
                await using var command = new SqlCommand(
                    """
                    SELECT branch.BranchID, branch.BranchName, status.LastPushAt
                    FROM dbo.Branches AS branch
                    LEFT JOIN dbo.BranchSnapshotStatus AS status ON status.BranchID = branch.BranchID
                    WHERE branch.BusinessID = @business AND branch.IsActive = 1
                      AND ISNULL(branch.BranchType, N'') <> N'Warehouse'
                    ORDER BY CASE WHEN branch.BranchID = @current THEN 0 ELSE 1 END, branch.BranchName;
                    """,
                    central);
                command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = device.BusinessId;
                command.Parameters.Add("@current", SqlDbType.UniqueIdentifier).Value = device.BranchId;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    if (!seen.Add(id)) continue;
                    var last = reader.IsDBNull(2) ? (DateTime?)null : Utc(reader.GetDateTime(2));
                    var current = id == device.BranchId;
                    // المركز هو المرجع للكمية: لا تُنتظر نسخة الفرع، فلا تنبيه قِدم.
                    rows.Add(new { branchId = id, branchName = reader.GetString(1), current, lastPushAt = last, stale = false });
                }
            }
            catch (SqlException exception)
            {
                logger.LogWarning(exception, "Inventory: central branch list was not read.");
                notice = "تعذر قراءة فروع المركز. يظهر فرع هذا الجهاز فقط.";
            }
        }
        else if (await CentralConfiguredAsync(cancellationToken))
        {
            notice = CentralRequired;
        }

        if (seen.Add(device.BranchId))
        {
            var name = "هذا الفرع";
            await using var local = await OpenLocalAsync(cancellationToken);
            await using var read = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @branch;", local);
            read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = device.BranchId;
            if (await read.ExecuteScalarAsync(cancellationToken) is string found && found.Length > 0) name = found;
            rows.Insert(0, new { branchId = device.BranchId, branchName = name, current = true, lastPushAt = (DateTime?)null, stale = false });
        }

        return new { branches = rows, notice, central = await CentralConfiguredAsync(cancellationToken) };
    }

    /// <summary>
    /// تثبيت الجرد في النظام المركزي (قرار المالك 25 نوفمبر 2026): لكل صنف معدود يُثبَّت الفارق على المركز أولاً
    /// (المعدودة − المفترضة لحظة العد، مضافاً إلى رصيد المركز الحالي)، ويُكتب طلب تصحيح يأخذه الفرع في أول مزامنة.
    /// معرّف الطلب والحركة المركزية = سطر الجرد، فإعادة التثبيت لا تكرر شيئاً. كل ذلك في معاملة واحدة على المركز.
    /// </summary>
    private async Task<object> FinalizeCentralAsync(PosDeviceContext device, Guid? branchId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var target = await OpenTargetAsync(device, branchId, cancellationToken);
        var connection = target.Connection;
        var actor = target.Actor!;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await CentralStock.RequireReadyAsync(connection, transaction, cancellationToken);
            await using (var lockSession = new SqlCommand(
                "SELECT Status FROM dbo.InventorySessions WITH (UPDLOCK, HOLDLOCK) WHERE SessionID = @id;",
                connection,
                transaction))
            {
                lockSession.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await lockSession.ExecuteScalarAsync(cancellationToken);
            }

            var session = await RequireOwnedAsync(connection, transaction, actor, sessionId, requireOpen: true, cancellationToken);
            await RequireAllCountedAsync(connection, transaction, sessionId, cancellationToken);
            var from = "هذا الفرع";
            await using (var origin = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @branch;", connection, transaction))
            {
                origin.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = device.BranchId;
                if (await origin.ExecuteScalarAsync(cancellationToken) is string name && name.Length > 0) from = name;
            }

            var lines = new List<(Guid LineId, Guid ItemId, decimal Counted, decimal? System)>();
            await using (var read = new SqlCommand(
                """
                SELECT SessionItemID, ItemID, CountedQuantity,
                       CASE WHEN SystemQuantityKnown = 1 THEN SystemQuantityAtCount END
                FROM dbo.InventorySessionItems
                WHERE SessionID = @id AND IsCounted = 1 AND CountedQuantity IS NOT NULL AND PostedAt IS NULL
                ORDER BY ItemID;
                """,
                connection,
                transaction))
            {
                read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3)));
                }
            }

            var reason = Limit($"{InventoryTypes.Label(session.Type)} من جهاز في {from}", 500)!;
            foreach (var line in lines)
            {
                var result = await CentralStock.PostCorrectionAsync(
                    connection,
                    transaction,
                    new CentralStock.CorrectionInput(
                        actor.BranchId,
                        line.ItemId,
                        line.LineId,
                        StockSourceTypes.InventoryCount,
                        sessionId,
                        line.Counted,
                        line.System,
                        reason,
                        actor.UserId,
                        actor.DisplayName),
                    cancellationToken);

                await using var mark = new SqlCommand(
                    """
                    UPDATE dbo.InventorySessionItems
                    SET StockMovementID = @movement, PostedAt = SYSUTCDATETIME(),
                        QuantityBeforePosting = @before, QuantityAfterPosting = @after
                    WHERE SessionItemID = @line;
                    """,
                    connection,
                    transaction);
                mark.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = result.MovementId;
                AddNullableDecimal(mark, "@before", result.Previous);
                AddNullableDecimal(mark, "@after", result.Resulting);
                mark.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = line.LineId;
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var complete = new SqlCommand(
                """
                UPDATE dbo.InventorySessions
                SET Status = N'Completed', CompletedAt = SYSUTCDATETIME(), LastSavedAt = SYSUTCDATETIME(),
                    PostedItems = (SELECT COUNT(*) FROM dbo.InventorySessionItems WHERE SessionID = @id AND PostedAt IS NOT NULL)
                WHERE SessionID = @id;
                """,
                connection,
                transaction))
            {
                complete.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await complete.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpdateCountersAsync(connection, transaction, sessionId, cancellationToken);
            var summary = await SessionSummaryAsync(connection, transaction, sessionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new { session = summary, posted = lines.Count, remote = true, central = true };
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal async Task<SqlConnection> OpenLocalAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal async Task<SqlConnection?> OpenCentralAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CentralTimeout);
        try
        {
            var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, timeout.Token);
            if (settings is null) return null;
            var builder = new SqlConnectionStringBuilder(settings.BuildConnectionString(settings.DatabaseName))
            {
                ConnectTimeout = (int)CentralTimeout.TotalSeconds,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync(timeout.Token);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception) when (
            exception is SqlException or InvalidOperationException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation(exception, "Central was not reachable for inventory settings; the branch keeps its copy.");
            return null;
        }
    }

    private static async Task RequireSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "dbo.InventorySessionItems", cancellationToken))
        {
            throw new InventoryException(MigrationMissing, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? Limit(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    private static void AddDecimal(SqlCommand command, string name, decimal value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = value });

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value) =>
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = (object?)value ?? DBNull.Value });
}
