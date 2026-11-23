using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// «الجرد» من شاشة البيع (المرحلة 1 من وحدة الجرد والنواقص).
/// - كل شيء على قاعدة الفرع: يعمل دون المركز.
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

    public async Task<object> ConfigurationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenLocalAsync(cancellationToken);
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

        return new
        {
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
    public async Task<object> FindOpenAsync(PosDeviceContext actor, string? type, IEnumerable<int>? pages, CancellationToken cancellationToken)
    {
        var normalized = RequireType(type);
        var wanted = InventoryCountRules.NormalizePages(normalized, pages);
        await using var connection = await OpenLocalAsync(cancellationToken);
        await RequireSchemaAsync(connection, cancellationToken);
        var open = await FindOpenSessionAsync(connection, null, actor, normalized, wanted, InventoryCalendar.Today(), cancellationToken);
        return new { session = open is null ? null : await SessionSummaryAsync(connection, null, open.Value, cancellationToken) };
    }

    public async Task<object> StartAsync(PosDeviceContext actor, string? type, IEnumerable<int>? pages, bool replaceOpen, CancellationToken cancellationToken)
    {
        var normalized = RequireType(type);
        var wanted = InventoryCountRules.NormalizePages(normalized, pages);
        var today = InventoryCalendar.Today();
        await using var connection = await OpenLocalAsync(cancellationToken);
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

            var open = await FindOpenSessionAsync(connection, transaction, actor, normalized, wanted, today, cancellationToken);
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
                    (SessionID, BusinessID, BranchID, StationID, DeviceID, UserID, UserName, InventoryType, BusinessDate, Status)
                VALUES (@id, @business, @branch, @station, @device, @user, @userName, @type, @today, N'Open');
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

            var total = normalized == InventoryTypes.Manual
                ? 0
                : await SnapshotItemsAsync(connection, transaction, actor.BranchId, sessionId, normalized, wanted, cancellationToken);
            if (normalized != InventoryTypes.Manual && total == 0)
            {
                throw new InventoryException(
                    normalized == InventoryTypes.Important
                        ? "لا توجد أصناف مهمة مصنفة لهذا الفرع. لم يتم إنشاء الجرد."
                        : "لا توجد أصناف مصنفة على هذه الصفحات. لم يتم إنشاء الجرد.",
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

    public async Task<object> ItemsAsync(PosDeviceContext actor, Guid sessionId, bool canReview, CancellationToken cancellationToken)
    {
        await using var connection = await OpenLocalAsync(cancellationToken);
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
    public async Task<object> LookupAsync(PosDeviceContext actor, Guid sessionId, string? code, CancellationToken cancellationToken)
    {
        var text = code?.Trim() ?? "";
        if (text.Length == 0) throw new InventoryException("يرجى إدخال رقم الصنف.");
        await using var connection = await OpenLocalAsync(cancellationToken);
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

    public async Task<object> SaveCountAsync(PosDeviceContext actor, Guid sessionId, Guid itemId, decimal? quantity, bool canReview, CancellationToken cancellationToken)
    {
        var counted = InventoryCountRules.ValidateCountedQuantity(quantity);
        await using var connection = await OpenLocalAsync(cancellationToken);
        await RequireSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var session = await RequireOwnedAsync(connection, transaction, actor, sessionId, requireOpen: true, cancellationToken);
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

                await InsertManualItemAsync(connection, transaction, actor.BranchId, sessionId, itemId, cancellationToken);
            }

            var balance = await ReadBalanceAsync(connection, transaction, actor.BranchId, itemId, cancellationToken);
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

            await UpdateCountersAsync(connection, transaction, sessionId, cancellationToken);
            object? item = null;
            await using (var read = new SqlCommand(
                """
                SELECT SessionItemID, ItemID, ItemNumber, ItemName, IsCounted, CountedQuantity, SystemQuantityAtCount,
                       SystemQuantityKnown, CountedAt, CountedByName, PostedAt, QuantityAfterPosting
                FROM dbo.InventorySessionItems WHERE SessionID = @id AND ItemID = @item;
                """,
                connection,
                transaction))
            {
                read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                read.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) item = ReadItem(reader, canReview);
            }

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

    /// <summary>
    /// «تثبيت الجرد»: يرحّل الأصناف المعدودة فقط، حركة InventoryCount لكل صنف، في معاملة واحدة.
    /// إعادة التثبيت لا تكرر الحركة (SourceLineID = سطر الجرد).
    /// </summary>
    public async Task<object> FinalizeAsync(PosDeviceContext actor, Guid sessionId, CancellationToken cancellationToken)
    {
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

        var rows = new List<(string Number, bool Important, byte? Page, int? Days, DateTime UpdatedAt, string? By)>();
        await using (var read = new SqlCommand(
            """
            SELECT TOP (2000) ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName
            FROM dbo.ItemInventorySettings
            WHERE UpdatedAt >= DATEADD(MINUTE, -10, @since)
            ORDER BY UpdatedAt, ItemNumber;
            """,
            central))
        {
            read.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
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

        var copied = 0;
        foreach (var row in rows)
        {
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

    private sealed record OwnerResolution(Guid ScannedItemId, string ScannedItemNumber, Guid OwnerItemId, string OwnerItemNumber, string OwnerItemName);

    private static string RequireType(string? type) =>
        InventoryTypes.Normalize(type) ?? throw new InventoryException("نوع الجرد غير صالح.");

    private static async Task<int> SnapshotItemsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid branchId,
        Guid sessionId,
        string type,
        IReadOnlyList<int> pages,
        CancellationToken cancellationToken)
    {
        var scope = type == InventoryTypes.Important
            ? "(setting.IsImportant = 1 OR setting.InventoryPage = 0)"
            : $"setting.InventoryPage IN ({string.Join(", ", pages.Select((_, index) => $"@page{index}"))})";
        await using var command = new SqlCommand(
            $"""
             INSERT INTO dbo.InventorySessionItems (SessionItemID, SessionID, ItemID, ItemNumber, ItemName, DailySalesRate, SupplyDays)
             SELECT NEWID(), @session, item.ItemID, item.ItemNumber, item.ItemName, branchItem.DailySalesRate, setting.SupplyDays
             FROM dbo.ItemInventorySettings AS setting
             INNER JOIN dbo.Items AS item ON item.ItemNumber = setting.ItemNumber AND item.IsActive = 1
             LEFT JOIN dbo.ItemBranchInventory AS branchItem ON branchItem.BranchID = @branch AND branchItem.ItemID = item.ItemID
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
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             INSERT INTO dbo.InventorySessionItems (SessionItemID, SessionID, ItemID, ItemNumber, ItemName, DailySalesRate, SupplyDays)
             SELECT NEWID(), @session, item.ItemID, item.ItemNumber, item.ItemName, branchItem.DailySalesRate, setting.SupplyDays
             FROM dbo.Items AS item
             LEFT JOIN dbo.ItemBranchInventory AS branchItem ON branchItem.BranchID = @branch AND branchItem.ItemID = item.ItemID
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

    private static async Task<OwnerResolution?> ResolveOwnerAsync(
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
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT OnHandQuantity, IsQuantityInitialized FROM dbo.BranchStockBalances WHERE BranchID = @branch AND ItemID = @item;",
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
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        await using (var command = new SqlCommand(
            """
            SELECT SessionID FROM dbo.InventorySessions WITH (UPDLOCK, HOLDLOCK)
            WHERE BranchID = @branch AND UserID = @user AND InventoryType = @type
              AND Status = N'Open' AND BusinessDate = @today
            ORDER BY StartedAt DESC;
            """,
            connection,
            transaction))
        {
            command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            command.Parameters.Add("@today", SqlDbType.Date).Value = today.ToDateTime(TimeOnly.MinValue);
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

    private async Task<SqlConnection> OpenLocalAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<SqlConnection?> OpenCentralAsync(CancellationToken cancellationToken)
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
