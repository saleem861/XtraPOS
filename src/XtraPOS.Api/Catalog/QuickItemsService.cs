using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Catalog;

/// <summary>
/// «الأصناف السريعة» في شاشة البيع.
/// - شاشة البيع تقرأ القاعدة المحلية فقط (لا تنتظر المركز).
/// - شاشة الضبط: المركز أولاً بمهلة قصيرة ثم الفرع. الحفظ محلي + سجل الترحيل (QuickItemSet) ثم ترحيل فوري إن أمكن.
/// - كل دقيقة يسحب الفرع القوائم الأحدث من المركز (PullAsync عبر PosDeviceSettingsSyncWorker).
/// - الجهاز المنفرد: قائمته المحلية فقط، والعملية تكتمل فوراً.
/// </summary>
public sealed class QuickItemsService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    ILogger<QuickItemsService> logger)
{
    public const string OperationType = "QuickItemSet";
    internal static readonly TimeSpan CentralTimeout = TimeSpan.FromSeconds(6);

    internal const string CentralUnreachableNotice =
        "تعذر الوصول إلى الإدارة المركزية. تظهر قوائم هذا الفرع فقط، والحفظ يعمل ويُرحَّل تلقائياً عند عودة الاتصال.";

    // ------------------------------------------------------------------
    // شاشة البيع
    // ------------------------------------------------------------------

    public async Task<object> CurrentAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var local = await OpenLocalAsync(cancellationToken);
        if (!await TableExistsAsync(local, "dbo.QuickItemSets", cancellationToken))
        {
            return new { lines = Array.Empty<object>() };
        }

        var branchName = await BranchNameAsync(local, actor.BranchId, cancellationToken);
        var sets = await ReadSetsAsync(local, actor.BusinessId, cancellationToken);
        var effective = QuickItemRules.Effective(sets, actor.BranchId, branchName);
        if (effective is null) return new { lines = Array.Empty<object>() };
        var lines = await DescribeAsync(local, effective.Lines, activeOnly: true, cancellationToken);
        return new
        {
            setId = effective.SetId,
            scope = effective.BranchId is null && string.IsNullOrWhiteSpace(effective.BranchName) ? "All" : "Branch",
            lines,
        };
    }

    // ------------------------------------------------------------------
    // شاشة الضبط
    // ------------------------------------------------------------------

    public async Task<object> ManageAsync(PosDeviceContext actor, Guid? branchId, CancellationToken cancellationToken)
    {
        var (connection, source, notice) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var options = await CashDrawerService.ReviewOptionsAsync(connection, cancellationToken);
        if (!await TableExistsAsync(connection, "dbo.QuickItemSets", cancellationToken))
        {
            return new { source, notice, options, branchId, branchName = (string?)null, inherited = false, set = (object?)null, lines = Array.Empty<object>(), maxItems = QuickItemRules.MaxItems };
        }

        var branchName = branchId is null ? null : await BranchNameAsync(connection, branchId.Value, cancellationToken);
        var sets = await ReadSetsAsync(connection, actor.BusinessId, cancellationToken);
        QuickItemSet? chosen;
        var inherited = false;
        if (branchId is null)
        {
            chosen = sets.Where(set => set.BranchId is null && string.IsNullOrWhiteSpace(set.BranchName))
                .OrderByDescending(set => set.CreatedAt).FirstOrDefault();
        }
        else
        {
            chosen = sets.Where(set => QuickItemRules.IsBranch(set, branchId.Value, branchName))
                .OrderByDescending(set => set.CreatedAt).FirstOrDefault();
            if (chosen is not { Lines.Count: > 0 })
            {
                var all = QuickItemRules.Effective(sets, branchId.Value, branchName);
                inherited = all is not null && all.BranchId is null;
                chosen = all;
            }
        }

        IReadOnlyList<object> lines = chosen is null ? [] : await DescribeAsync(connection, chosen.Lines, activeOnly: false, cancellationToken);
        return new
        {
            source,
            notice,
            options,
            branchId,
            branchName,
            inherited,
            set = chosen is null ? null : new { chosen.SetId, chosen.CreatedAt, chosen.CreatedByName },
            lines,
            maxItems = QuickItemRules.MaxItems,
        };
    }

    /// <summary>الأكثر ظهوراً في فواتير الفرع المختار (أو كل الفروع) خلال آخر أيام، ثم بالكمية.</summary>
    public async Task<object> SuggestAsync(PosDeviceContext actor, Guid? branchId, int days, int take, CancellationToken cancellationToken)
    {
        var (connection, source, notice) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var branchName = branchId is null ? null : await BranchNameAsync(connection, branchId.Value, cancellationToken);
        days = Math.Clamp(days, 7, 365);
        take = Math.Clamp(take, 1, QuickItemRules.MaxItems);
        await using var command = new SqlCommand(
            $"""
             SELECT TOP (@take) line.ItemNumberSnapshot, MAX(line.ItemNameSnapshot), COUNT(DISTINCT sale.SaleID) AS Invoices, SUM(line.Quantity) AS Quantity,
                    MAX(item.Classification)
             FROM dbo.SaleItems AS line
             INNER JOIN dbo.Sales AS sale ON sale.SaleID = line.SaleID
             INNER JOIN dbo.Items AS item ON item.ItemNumber = line.ItemNumberSnapshot AND item.IsActive = 1
             {(branchName is null ? "" : "INNER JOIN dbo.Branches AS branch ON branch.BranchID = sale.BranchID AND branch.BranchName = @branchName")}
             WHERE sale.Status = N'Completed'
               AND sale.BusinessID = @business
               AND sale.CreatedAt >= @since
               AND line.ItemNumberSnapshot IS NOT NULL
             GROUP BY line.ItemNumberSnapshot
             ORDER BY Invoices DESC, Quantity DESC, line.ItemNumberSnapshot;
             """,
            connection);
        command.Parameters.Add("@take", SqlDbType.Int).Value = take;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.BusinessId;
        command.Parameters.Add("@since", SqlDbType.DateTime2).Value = DateTime.UtcNow.AddDays(-days);
        if (branchName is not null) command.Parameters.Add("@branchName", SqlDbType.NVarChar, 200).Value = branchName;
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new
            {
                itemNumber = reader.GetString(0),
                itemName = reader.IsDBNull(1) ? null : reader.GetString(1),
                invoices = reader.GetInt32(2),
                quantity = reader.GetDecimal(3),
                classification = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return new { source, notice, days, branchName, items = rows };
    }

    public async Task<object> SaveAsync(PosDeviceContext actor, Guid? branchId, IEnumerable<string?>? itemNumbers, CancellationToken cancellationToken)
    {
        var numbers = QuickItemRules.Normalize(itemNumbers);
        var (connection, _, _) = await OpenReadAsync(cancellationToken);
        string? branchName;
        IReadOnlyList<QuickItemLine> lines;
        await using (connection)
        {
            branchName = branchId is null ? null : await BranchNameAsync(connection, branchId.Value, cancellationToken);
            if (branchId is not null && branchName is null)
            {
                throw new QuickItemException("الفرع غير موجود.", StatusCodes.Status404NotFound);
            }

            var described = await LookupItemsAsync(connection, numbers, cancellationToken);
            var missing = numbers.Where(number => !described.ContainsKey(number)).ToArray();
            if (missing.Length > 0)
            {
                throw new QuickItemException($"أصناف غير موجودة أو غير فعّالة: {string.Join("، ", missing)}");
            }

            lines = numbers.Select((number, index) => new QuickItemLine(index + 1, number, described[number].Name, described[number].Classification)).ToArray();
        }

        await using var branch = await OpenLocalAsync(cancellationToken);
        if (!await TableExistsAsync(branch, "dbo.QuickItemSets", cancellationToken))
        {
            throw new QuickItemException("قاعدة هذا الفرع لم تُحدَّث بعد للأصناف السريعة (الترحيل 053).", StatusCodes.Status503ServiceUnavailable);
        }

        var setId = Guid.NewGuid();
        var set = new QuickItemSet(setId, branchId, branchName, DateTime.UtcNow, actor.DisplayName, lines);
        await using (var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken))
        {
            await InsertSetAsync(branch, transaction, set, actor.BusinessId, actor.UserId, "Branch", relayed: false, cancellationToken);
            await BranchOperationLog.EnqueueAsync(branch, transaction, new BranchOperationDraft(
                setId,
                OperationType,
                setId.ToString("N"),
                $"الأصناف السريعة — {(branchName ?? "كل الفروع")} ({lines.Count} صنف)",
                "QuickItemSets",
                setId,
                actor.BranchId,
                actor.UserId,
                actor.StationId), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var relayed = false;
        var central = await OpenCentralAsync(cancellationToken);
        if (central is not null)
        {
            await using (central)
            {
                try
                {
                    relayed = await RelaySetAsync(central, branch, setId, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogInformation(exception, "Quick item set {SetId} stays queued for relay.", setId);
                }
            }
        }
        else if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null)
        {
            await BranchOperationLog.MarkAsync(branch, null, setId, "Completed", "جهاز منفرد: حُفظت القائمة على هذا الجهاز فقط.", cancellationToken);
        }

        return new
        {
            setId,
            relayed,
            count = lines.Count,
            message = relayed
                ? "تم حفظ الأصناف السريعة ووصلت إلى الإدارة المركزية، وتصل إلى الفروع خلال دقيقة."
                : "تم حفظ الأصناف السريعة على هذا الجهاز.",
        };
    }

    // ------------------------------------------------------------------
    // الترحيل والسحب
    // ------------------------------------------------------------------

    public static async Task<string?> RelayLoggedAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        Guid setId;
        await using (var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled') AND OperationType = @type
            ORDER BY Serial;
            """,
            branch))
        {
            read.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
            if (await read.ExecuteScalarAsync(cancellationToken) is not Guid found) return null;
            setId = found;
        }

        try
        {
            return await RelaySetAsync(central, branch, setId, cancellationToken) ? "Completed" : "Blocked";
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            await BranchOperationLog.MarkAsync(branch, null, setId, "Blocked", "جداول الأصناف السريعة غير موجودة على المركز بعد (الترحيل 038).", cancellationToken);
            return "Blocked";
        }
    }

    private static async Task<bool> RelaySetAsync(SqlConnection central, SqlConnection branch, Guid setId, CancellationToken cancellationToken)
    {
        var row = await ReadSetAsync(branch, setId, cancellationToken);
        if (row is null)
        {
            await BranchOperationLog.MarkAsync(branch, null, setId, "Blocked", "قائمة الأصناف السريعة غير موجودة على هذا الفرع.", cancellationToken);
            return false;
        }

        await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
        {
            await InsertSetAsync(central, transaction, row.Set, row.BusinessId, row.UserId, "Branch", relayed: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var mark = new SqlCommand("UPDATE dbo.QuickItemSets SET RelayedAt = SYSUTCDATETIME() WHERE SetID = @id AND RelayedAt IS NULL;", branch))
        {
            mark.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = setId;
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await BranchOperationLog.MarkAsync(branch, null, setId, "Completed", "تم ترحيل الأصناف السريعة إلى المركز.", cancellationToken);
        return true;
    }

    /// <summary>ينسخ من المركز كل قائمة أحدث مما عند هذا الفرع. يعيد عدد القوائم المنسوخة.</summary>
    public async Task<int> PullAsync(CancellationToken cancellationToken)
    {
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return 0;
        if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null) return 0;
        await using var local = new SqlConnection(branchConnection);
        await local.OpenAsync(cancellationToken);
        if (!await TableExistsAsync(local, "dbo.QuickItemSets", cancellationToken)) return 0;
        var central = await OpenCentralAsync(cancellationToken);
        if (central is null) return 0;
        await using var lease = central;
        if (!await TableExistsAsync(central, "dbo.QuickItemSets", cancellationToken)) return 0;

        DateTime since;
        await using (var newest = new SqlCommand("SELECT COALESCE(MAX(CreatedAt), '2000-01-01') FROM dbo.QuickItemSets;", local))
        {
            since = (DateTime)(await newest.ExecuteScalarAsync(cancellationToken))!;
        }

        var ids = new List<Guid>();
        await using (var read = new SqlCommand(
            """
            SELECT TOP (200) remote.SetID FROM dbo.QuickItemSets AS remote
            WHERE remote.CreatedAt >= DATEADD(MINUTE, -10, @since)
            ORDER BY remote.CreatedAt;
            """,
            central))
        {
            read.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }

        var copied = 0;
        foreach (var id in ids)
        {
            if (await SetExistsAsync(local, id, cancellationToken)) continue;
            var row = await ReadSetAsync(central, id, cancellationToken);
            if (row is null) continue;
            await using var transaction = (SqlTransaction)await local.BeginTransactionAsync(cancellationToken);
            await InsertSetAsync(local, transaction, row.Set, row.BusinessId, row.UserId, "Central", relayed: true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            copied++;
        }

        return copied;
    }

    // ------------------------------------------------------------------
    // قراءة وكتابة الجداول
    // ------------------------------------------------------------------

    private sealed record SetRow(QuickItemSet Set, Guid BusinessId, Guid? UserId);

    private static async Task<IReadOnlyList<QuickItemSet>> ReadSetsAsync(SqlConnection connection, Guid businessId, CancellationToken cancellationToken)
    {
        // أحدث قائمة لكل نطاق (كل الفروع أو فرع بالاسم) تكفي.
        var ids = new List<Guid>();
        await using (var command = new SqlCommand(
            """
            SELECT SetID FROM (
                SELECT SetID, ROW_NUMBER() OVER (PARTITION BY COALESCE(BranchName, N''), BranchID ORDER BY CreatedAt DESC, SetID DESC) AS RowNumber
                FROM dbo.QuickItemSets WHERE BusinessID = @business
            ) AS ranked WHERE RowNumber = 1;
            """,
            connection))
        {
            command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }

        var sets = new List<QuickItemSet>();
        foreach (var id in ids)
        {
            var row = await ReadSetAsync(connection, id, cancellationToken);
            if (row is not null) sets.Add(row.Set);
        }

        return sets;
    }

    private static async Task<SetRow?> ReadSetAsync(SqlConnection connection, Guid setId, CancellationToken cancellationToken)
    {
        Guid businessId;
        Guid? branchId;
        string? branchName;
        DateTime createdAt;
        Guid? userId;
        string? userName;
        await using (var head = new SqlCommand(
            "SELECT BusinessID, BranchID, BranchName, CreatedAt, CreatedByUserID, CreatedByName FROM dbo.QuickItemSets WHERE SetID = @id;",
            connection))
        {
            head.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = setId;
            await using var reader = await head.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            businessId = reader.GetGuid(0);
            branchId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            branchName = reader.IsDBNull(2) ? null : reader.GetString(2);
            createdAt = reader.GetDateTime(3);
            userId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
            userName = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        var lines = new List<QuickItemLine>();
        await using (var body = new SqlCommand(
            "SELECT SortOrder, ItemNumber, ItemName, Classification FROM dbo.QuickItemSetLines WHERE SetID = @id ORDER BY SortOrder;",
            connection))
        {
            body.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = setId;
            await using var reader = await body.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new QuickItemLine(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new SetRow(new QuickItemSet(setId, branchId, branchName, createdAt, userName, lines), businessId, userId);
    }

    private static async Task InsertSetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        QuickItemSet set,
        Guid businessId,
        Guid? userId,
        string source,
        bool relayed,
        CancellationToken cancellationToken)
    {
        await using (var head = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.QuickItemSets WITH (UPDLOCK, HOLDLOCK) WHERE SetID = @id)
                INSERT INTO dbo.QuickItemSets (SetID, BusinessID, BranchID, BranchName, CreatedAt, CreatedByUserID, CreatedByName, Source, RelayedAt)
                VALUES (@id, @business, @branch, @branchName, @created, @user, @userName, @source, CASE WHEN @relayed = 1 THEN SYSUTCDATETIME() ELSE NULL END);
            """,
            connection,
            transaction))
        {
            head.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = set.SetId;
            head.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
            head.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = (object?)set.BranchId ?? DBNull.Value;
            head.Parameters.Add("@branchName", SqlDbType.NVarChar, 200).Value = (object?)set.BranchName ?? DBNull.Value;
            head.Parameters.Add("@created", SqlDbType.DateTime2).Value = set.CreatedAt;
            head.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = (object?)userId ?? DBNull.Value;
            head.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = (object?)set.CreatedByName ?? DBNull.Value;
            head.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
            head.Parameters.Add("@relayed", SqlDbType.Bit).Value = relayed;
            await head.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var line in set.Lines)
        {
            await using var body = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.QuickItemSetLines WHERE SetID = @id AND SortOrder = @sort)
                    INSERT INTO dbo.QuickItemSetLines (SetID, SortOrder, ItemNumber, ItemName, Classification)
                    VALUES (@id, @sort, @number, @name, @class);
                """,
                connection,
                transaction);
            body.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = set.SetId;
            body.Parameters.Add("@sort", SqlDbType.Int).Value = line.SortOrder;
            body.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = line.ItemNumber;
            body.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = (object?)Limit(line.ItemName, 300) ?? DBNull.Value;
            body.Parameters.Add("@class", SqlDbType.NVarChar, 200).Value = (object?)Limit(line.Classification, 200) ?? DBNull.Value;
            await body.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> SetExistsAsync(SqlConnection connection, Guid setId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT 1 FROM dbo.QuickItemSets WHERE SetID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = setId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private sealed record ItemInfo(string Name, string? Classification, bool Active);

    private static async Task<Dictionary<string, ItemInfo>> LookupItemsAsync(SqlConnection connection, IReadOnlyList<string> numbers, CancellationToken cancellationToken, bool activeOnly = true)
    {
        var result = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        if (numbers.Count == 0) return result;
        await using var command = new SqlCommand { Connection = connection };
        var names = new List<string>();
        for (var index = 0; index < numbers.Count; index++)
        {
            names.Add($"@n{index}");
            command.Parameters.Add($"@n{index}", SqlDbType.NVarChar, 100).Value = numbers[index];
        }

        command.CommandText =
            $"SELECT ItemNumber, ItemName, Classification, IsActive FROM dbo.Items WHERE ItemNumber IN ({string.Join(", ", names)}){(activeOnly ? " AND IsActive = 1" : "")};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = new ItemInfo(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3));
        }

        return result;
    }

    /// <summary>الأسطر مع الاسم والتصنيف الحاليين من جدول الأصناف (أو المحفوظين إن غاب الصنف).</summary>
    private static async Task<IReadOnlyList<object>> DescribeAsync(SqlConnection connection, IReadOnlyList<QuickItemLine> lines, bool activeOnly, CancellationToken cancellationToken)
    {
        var known = await LookupItemsAsync(connection, lines.Select(line => line.ItemNumber).ToArray(), cancellationToken, activeOnly: false);
        var result = new List<object>();
        foreach (var line in lines.OrderBy(line => line.SortOrder))
        {
            known.TryGetValue(line.ItemNumber, out var item);
            if (activeOnly && item is { Active: false }) continue;
            result.Add(new
            {
                itemNumber = line.ItemNumber,
                itemName = item?.Name ?? line.ItemName ?? line.ItemNumber,
                classification = item?.Classification ?? line.Classification,
                active = item?.Active ?? false,
            });
        }

        return result;
    }

    private static async Task<string?> BranchNameAsync(SqlConnection connection, Guid branchId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = branchId;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    // ------------------------------------------------------------------
    // الاتصال
    // ------------------------------------------------------------------

    private async Task<(SqlConnection Connection, string Source, string? Notice)> OpenReadAsync(CancellationToken cancellationToken)
    {
        if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null)
        {
            return (await OpenLocalAsync(cancellationToken), "local", null);
        }

        var central = await OpenCentralAsync(cancellationToken);
        return central is null
            ? (await OpenLocalAsync(cancellationToken), "local", CentralUnreachableNotice)
            : (central, "central", null);
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
            logger.LogInformation(exception, "Central was not reachable for quick items; using the branch database.");
            return null;
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static string? Limit(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
