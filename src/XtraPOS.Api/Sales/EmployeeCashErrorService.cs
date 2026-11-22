using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Sales;

public sealed record CashErrorFilter(
    DateTime From,
    DateTime To,
    Guid? EmployeeId,
    Guid? BranchId,
    Guid? StationId,
    string? Type,
    string? Status);

/// <summary>
/// شاشة «أخطاء الموظفين»: الأخطاء من `EmployeeCashErrors` (العد النهائي فقط) مع قرار المشرف،
/// والإجراءات المتراكمة (مقاصة، تسوية، تأجيل، ملاحظة) من `EmployeeCashErrorActions`.
/// القراءة من المركز أولاً بمهلة قصيرة ثم من الفرع. الحفظ على الفرع مع سجل الترحيل ثم يُرحَّل فوراً إن أمكن.
/// </summary>
public sealed class EmployeeCashErrorService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    ILogger<EmployeeCashErrorService> logger)
{
    public const string OperationType = "EmployeeCashErrorAction";
    internal static readonly TimeSpan CentralTimeout = TimeSpan.FromSeconds(6);

    internal const string CentralUnreachableNotice =
        "تعذر الوصول إلى الإدارة المركزية. تظهر أخطاء هذا الفرع فقط، والحفظ يعمل ويُرحَّل تلقائياً عند عودة الاتصال.";

    internal const string CentralNotLinkedNotice =
        "هذا الفرع غير مربوط بالإدارة المركزية. تظهر أخطاء هذا الفرع فقط.";

    public async Task<object> ListAsync(PosDeviceContext actor, bool manageAll, CashErrorFilter filter, CancellationToken cancellationToken)
    {
        var (connection, source, notice) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var items = (await ReadItemsAsync(connection, actor, manageAll, filter, null, cancellationToken))
            .Where(item => EmployeeCashErrorRules.MatchesType(item, filter.Type) && EmployeeCashErrorRules.MatchesStatus(item, filter.Status))
            .ToArray();
        return new
        {
            source,
            notice,
            canManageAll = manageAll,
            scopedBranchId = manageAll ? (Guid?)null : actor.BranchId,
            scopedBranchName = manageAll ? null : await BranchNameAsync(connection, actor.BranchId, cancellationToken),
            summary = EmployeeCashErrorRules.Summarize(items),
            employees = EmployeeCashErrorRules.Balances(items),
            items,
            options = await CashDrawerService.ReviewOptionsAsync(connection, cancellationToken),
        };
    }

    /// <summary>الأخطاء المصفّاة كما في الشاشة، لتصديرها إلى Excel.</summary>
    public async Task<(IReadOnlyList<CashErrorItem> Items, CashErrorSummary Summary, string Source)> ExportAsync(
        PosDeviceContext actor,
        bool manageAll,
        CashErrorFilter filter,
        CancellationToken cancellationToken)
    {
        var (connection, source, _) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var items = (await ReadItemsAsync(connection, actor, manageAll, filter, null, cancellationToken))
            .Where(item => EmployeeCashErrorRules.MatchesType(item, filter.Type) && EmployeeCashErrorRules.MatchesStatus(item, filter.Status))
            .ToArray();
        return (items, EmployeeCashErrorRules.Summarize(items), source);
    }

    public async Task<object> StatementAsync(PosDeviceContext actor, bool manageAll, Guid employeeId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var (connection, source, notice) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var filter = new CashErrorFilter(from, to, employeeId, null, null, null, null);
        var items = await ReadItemsAsync(connection, actor, manageAll, filter, null, cancellationToken);
        var actions = await ReadActionsAsync(connection, employeeId, from, to, cancellationToken);
        var name = items.FirstOrDefault()?.EmployeeName ?? await EmployeeNameAsync(connection, employeeId, cancellationToken);
        return new
        {
            source,
            notice,
            employee = new { employeeId, name },
            from = from.Date,
            to = to.Date,
            summary = EmployeeCashErrorRules.Summarize(items),
            items,
            actions,
        };
    }

    public async Task<object> SaveActionAsync(PosDeviceContext actor, bool manageAll, CashErrorActionRequest request, CancellationToken cancellationToken)
    {
        // التحقق على أحدث صورة متاحة (المركز إن أمكن)، بلا قيد فترة: البنود تُعرَّف بمعرّفها.
        var ids = (request.ItemIds ?? []).Distinct().ToArray();
        if (ids.Length == 0)
        {
            EmployeeCashErrorRules.Validate(request, new Dictionary<Guid, CashErrorItem>());
        }

        var (connection, _, _) = await OpenReadAsync(cancellationToken);
        Dictionary<Guid, CashErrorItem> known;
        await using (connection)
        {
            var wide = new CashErrorFilter(DateTime.MinValue, DateTime.MaxValue, null, null, null, null, null);
            known = (await ReadItemsAsync(connection, actor, manageAll, wide, ids, cancellationToken)).ToDictionary(item => item.ItemId);
        }

        var draft = EmployeeCashErrorRules.Validate(request, known);
        var employeeName = known.Values.First().EmployeeName;
        var actionId = Guid.NewGuid();

        await using var branch = await OpenLocalAsync(cancellationToken);
        if (!await TableExistsAsync(branch, null, "dbo.EmployeeCashErrorActions", cancellationToken))
        {
            throw new CashErrorException("قاعدة هذا الفرع لم تُحدَّث بعد لأخطاء الموظفين (الترحيل 051).", StatusCodes.Status503ServiceUnavailable);
        }

        await using (var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken))
        {
            await InsertActionAsync(branch, transaction, actionId, draft, actor, relayed: false, cancellationToken);
            await BranchOperationLog.EnqueueAsync(branch, transaction, new BranchOperationDraft(
                actionId,
                OperationType,
                actionId.ToString("N"),
                EmployeeCashErrorRules.Describe(draft, employeeName),
                "EmployeeCashErrorActions",
                actionId,
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
                    relayed = await RelayActionAsync(central, branch, actionId, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogInformation(exception, "Cash error action {ActionId} stays queued for relay.", actionId);
                }
            }
        }

        return new
        {
            actionId,
            type = draft.Type,
            amount = draft.Amount,
            relayed,
            message = relayed
                ? "تم الحفظ ووصل إلى الإدارة المركزية."
                : "تم الحفظ على هذا الفرع، ويُرحَّل إلى الإدارة المركزية عند عودة الاتصال.",
        };
    }

    /// <summary>يرحّل أول عملية «EmployeeCashErrorAction» معلّقة في سجل الترحيل.</summary>
    public static async Task<string?> RelayLoggedAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        Guid operationId;
        await using (var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled') AND OperationType = @type
            ORDER BY Serial;
            """,
            branch))
        {
            read.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = OperationType;
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid id)
            {
                return null;
            }

            operationId = id;
        }

        try
        {
            return await RelayActionAsync(central, branch, operationId, cancellationToken) ? "Completed" : "Blocked";
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            await BranchOperationLog.MarkAsync(branch, null, operationId, "Blocked", "جداول أخطاء الموظفين غير موجودة على المركز بعد (الترحيل 036).", cancellationToken);
            return "Blocked";
        }
    }

    private static async Task<bool> RelayActionAsync(SqlConnection central, SqlConnection branch, Guid actionId, CancellationToken cancellationToken)
    {
        object[]? action = null;
        await using (var read = new SqlCommand(
            """
            SELECT ActionID, ActionType, BranchID, EmployeeUserID, Amount, Method, DeferToDate, Note, ActorUserID, ActorName, CreatedAt
            FROM dbo.EmployeeCashErrorActions WHERE ActionID = @id;
            """,
            branch))
        {
            read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = actionId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                action = new object[reader.FieldCount];
                reader.GetValues(action);
            }
        }

        if (action is null)
        {
            await BranchOperationLog.MarkAsync(branch, null, actionId, "Blocked", "إجراء أخطاء الموظفين غير موجود على هذا الفرع.", cancellationToken);
            return false;
        }

        var items = new List<(Guid ItemId, decimal Amount)>();
        await using (var read = new SqlCommand("SELECT ItemID, ItemAmount FROM dbo.EmployeeCashErrorActionItems WHERE ActionID = @id;", branch))
        {
            read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = actionId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((reader.GetGuid(0), reader.GetDecimal(1)));
            }
        }

        await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
        {
            await using (var insert = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.EmployeeCashErrorActions WITH (UPDLOCK, HOLDLOCK) WHERE ActionID = @ActionID)
                    INSERT INTO dbo.EmployeeCashErrorActions
                        (ActionID, ActionType, BranchID, EmployeeUserID, Amount, Method, DeferToDate, Note, ActorUserID, ActorName, CreatedAt, RelayedAt)
                    VALUES
                        (@ActionID, @ActionType, @BranchID, @EmployeeUserID, @Amount, @Method, @DeferToDate, @Note, @ActorUserID, @ActorName, @CreatedAt, SYSUTCDATETIME());
                """,
                central,
                transaction))
            {
                string[] names = ["@ActionID", "@ActionType", "@BranchID", "@EmployeeUserID", "@Amount", "@Method", "@DeferToDate", "@Note", "@ActorUserID", "@ActorName", "@CreatedAt"];
                for (var index = 0; index < names.Length; index++)
                {
                    insert.Parameters.AddWithValue(names[index], action[index] ?? DBNull.Value);
                }

                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var item in items)
            {
                await using var line = new SqlCommand(
                    """
                    IF NOT EXISTS (SELECT 1 FROM dbo.EmployeeCashErrorActionItems WHERE ActionID = @action AND ItemID = @item)
                        INSERT INTO dbo.EmployeeCashErrorActionItems (ActionID, ItemID, ItemAmount) VALUES (@action, @item, @amount);
                    """,
                    central,
                    transaction);
                line.Parameters.Add("@action", SqlDbType.UniqueIdentifier).Value = actionId;
                line.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = item.ItemId;
                AddMoney(line, "@amount", item.Amount);
                await line.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await using (var mark = new SqlCommand("UPDATE dbo.EmployeeCashErrorActions SET RelayedAt = SYSUTCDATETIME() WHERE ActionID = @id AND RelayedAt IS NULL;", branch))
        {
            mark.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = actionId;
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await BranchOperationLog.MarkAsync(branch, null, actionId, "Completed", "تم ترحيل إجراء أخطاء الموظفين إلى المركز.", cancellationToken);
        return true;
    }

    // ------------------------------------------------------------------
    // القراءة
    // ------------------------------------------------------------------

    private async Task<(SqlConnection Connection, string Source, string? Notice)> OpenReadAsync(CancellationToken cancellationToken)
    {
        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null)
        {
            return (await OpenLocalAsync(cancellationToken), "local", CentralNotLinkedNotice);
        }

        var central = await OpenCentralAsync(cancellationToken);
        return central is null
            ? (await OpenLocalAsync(cancellationToken), "local", CentralUnreachableNotice)
            : (central, "central", null);
    }

    private static async Task<IReadOnlyList<CashErrorItem>> ReadItemsAsync(
        SqlConnection connection,
        PosDeviceContext actor,
        bool manageAll,
        CashErrorFilter filter,
        IReadOnlyCollection<Guid>? onlyItems,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, null, "dbo.EmployeeCashErrors", cancellationToken))
        {
            return [];
        }

        var hasActions = await TableExistsAsync(connection, null, "dbo.EmployeeCashErrorActions", cancellationToken)
            && await TableExistsAsync(connection, null, "dbo.EmployeeCashErrorActionItems", cancellationToken);
        await using var command = new SqlCommand { Connection = connection };
        var where = new List<string> { "1 = 1" };
        if (filter.From > DateTime.MinValue)
        {
            where.Add("CAST(s.EffectiveAt AS date) >= @from");
            command.Parameters.Add("@from", SqlDbType.Date).Value = filter.From.Date;
        }

        if (filter.To < DateTime.MaxValue.Date)
        {
            where.Add("CAST(s.EffectiveAt AS date) <= @to");
            command.Parameters.Add("@to", SqlDbType.Date).Value = filter.To.Date;
        }

        if (filter.EmployeeId is not null)
        {
            where.Add("s.EmployeeID = @employee");
            command.Parameters.Add("@employee", SqlDbType.UniqueIdentifier).Value = filter.EmployeeId.Value;
        }

        // المشرف يرى فرعه فقط (الفرع بالاسم، لأن الفرع الواحد قد يظهر بأكثر من معرّف بين القواعد).
        var branch = manageAll ? filter.BranchId : actor.BranchId;
        if (branch is not null)
        {
            where.Add(
                """
                s.BranchID IN (
                    SELECT same.BranchID FROM dbo.Branches AS chosen
                    INNER JOIN dbo.Branches AS same ON same.BranchName = chosen.BranchName
                    WHERE chosen.BranchID = @branch)
                """);
            command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch.Value;
        }

        if (filter.StationId is not null)
        {
            where.Add(
                """
                s.StationID IN (
                    SELECT same.StationID FROM dbo.POSStations AS chosen
                    INNER JOIN dbo.POSStations AS same
                        ON same.StationNumber = chosen.StationNumber AND same.StationName = chosen.StationName
                    WHERE chosen.StationID = @station)
                """);
            command.Parameters.Add("@station", SqlDbType.UniqueIdentifier).Value = filter.StationId.Value;
        }

        if (onlyItems is { Count: > 0 })
        {
            var names = new List<string>();
            var index = 0;
            foreach (var id in onlyItems.Take(500))
            {
                var name = $"@item{index++}";
                names.Add(name);
                command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = id;
            }

            where.Add($"s.ItemID IN ({string.Join(", ", names)})");
        }

        var residuals = hasActions
            ? """
              UNION ALL
              SELECT a.ActionID, N'Residual', a.EmployeeUserID, a.BranchID, CAST(NULL AS uniqueidentifier), a.Amount, a.CreatedAt, a.Note, CAST(NULL AS nvarchar(20))
              FROM dbo.EmployeeCashErrorActions AS a
              WHERE a.ActionType = N'Offset' AND a.Amount <> 0
              """
            : "";
        var state = hasActions
            ? """
              closing.ActionID AS ClosedBy, closing.ActionType AS ClosedType, closing.Method AS ClosedMethod,
              closing.CreatedAt AS ClosedAt, closing.ActorName AS ClosedByName, deferral.DeferToDate AS DeferredTo,
              COALESCE(notes.NoteCount, 0) AS NoteCount
              FROM items AS i
              OUTER APPLY (
                  SELECT TOP (1) a.ActionID, a.ActionType, a.Method, a.CreatedAt, a.ActorName
                  FROM dbo.EmployeeCashErrorActionItems AS x
                  INNER JOIN dbo.EmployeeCashErrorActions AS a ON a.ActionID = x.ActionID
                  WHERE x.ItemID = i.ItemID AND a.ActionType IN (N'Offset', N'Settle')
                  ORDER BY a.CreatedAt, a.ActionID) AS closing
              OUTER APPLY (
                  SELECT TOP (1) a.DeferToDate
                  FROM dbo.EmployeeCashErrorActionItems AS x
                  INNER JOIN dbo.EmployeeCashErrorActions AS a ON a.ActionID = x.ActionID
                  WHERE x.ItemID = i.ItemID AND a.ActionType = N'Defer'
                  ORDER BY a.CreatedAt DESC, a.ActionID) AS deferral
              OUTER APPLY (
                  SELECT COUNT(*) AS NoteCount
                  FROM dbo.EmployeeCashErrorActionItems AS x
                  INNER JOIN dbo.EmployeeCashErrorActions AS a ON a.ActionID = x.ActionID
                  WHERE x.ItemID = i.ItemID AND a.Note IS NOT NULL) AS notes
              """
            : """
              CAST(NULL AS uniqueidentifier) AS ClosedBy, CAST(NULL AS nvarchar(20)) AS ClosedType, CAST(NULL AS nvarchar(20)) AS ClosedMethod,
              CAST(NULL AS datetime2(0)) AS ClosedAt, CAST(NULL AS nvarchar(200)) AS ClosedByName, CAST(NULL AS date) AS DeferredTo,
              0 AS NoteCount
              FROM items AS i
              """;

        command.CommandText =
            $"""
             WITH items AS (
                 SELECT e.ErrorID AS ItemID, CAST(N'Error' AS nvarchar(10)) AS Kind, e.UserID AS EmployeeID, e.BranchID, e.POSStationID AS StationID,
                        CAST(CASE WHEN e.ErrorType = N'Shortage' THEN -e.Amount ELSE e.Amount END AS decimal(19, 4)) AS Amount,
                        e.OccurredAt, e.Justification, d.DecisionCode
                 FROM dbo.EmployeeCashErrors AS e
                 LEFT JOIN dbo.CashDrawerDecisions AS d ON d.CountID = e.CountID
                 {residuals}
             ),
             shaped AS (
                 SELECT i.ItemID, i.Kind, i.EmployeeID, i.BranchID, i.StationID, i.Amount, i.OccurredAt, i.Justification, i.DecisionCode,
                        {state}
             ),
             s AS (
                 SELECT shaped.*, COALESCE(CAST(shaped.DeferredTo AS datetime2(0)), shaped.OccurredAt) AS EffectiveAt
                 FROM shaped
             )
             SELECT TOP (3000)
                    s.ItemID, s.Kind, s.EmployeeID, COALESCE(u.DisplayName, N'موظف غير معروف'), s.BranchID, b.BranchName,
                    st.StationNumber, st.StationName, s.Amount, s.OccurredAt, s.EffectiveAt, s.Justification, s.DecisionCode,
                    s.ClosedBy, s.ClosedType, s.ClosedMethod, s.ClosedAt, s.ClosedByName, s.DeferredTo, s.NoteCount
             FROM s
             LEFT JOIN dbo.Users AS u ON u.UserID = s.EmployeeID
             LEFT JOIN dbo.Branches AS b ON b.BranchID = s.BranchID
             LEFT JOIN dbo.POSStations AS st ON st.StationID = s.StationID
             WHERE {string.Join(" AND ", where)}
             ORDER BY s.EffectiveAt DESC, s.OccurredAt DESC;
             """;

        var result = new List<CashErrorItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var decision = reader.IsDBNull(12) ? null : reader.GetString(12);
            var closedType = reader.IsDBNull(14) ? null : reader.GetString(14);
            DateTime? deferredTo = reader.IsDBNull(18) ? null : reader.GetDateTime(18);
            result.Add(new CashErrorItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetDecimal(8),
                reader.GetDateTime(9),
                reader.GetDateTime(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                decision,
                EmployeeCashErrorRules.StatusOf(decision, closedType, deferredTo),
                reader.IsDBNull(13) ? null : reader.GetGuid(13),
                closedType,
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                deferredTo,
                Convert.ToInt32(reader.GetValue(19), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<CashErrorActionView>> ReadActionsAsync(
        SqlConnection connection,
        Guid employeeId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, null, "dbo.EmployeeCashErrorActions", cancellationToken))
        {
            return [];
        }

        var items = new Dictionary<Guid, List<Guid>>();
        var rows = new List<(Guid Id, string Type, decimal Amount, string? Method, DateTime? DeferTo, string? Note, string? Actor, DateTime At)>();
        await using (var command = new SqlCommand(
            """
            SELECT a.ActionID, a.ActionType, a.Amount, a.Method, a.DeferToDate, a.Note, a.ActorName, a.CreatedAt, x.ItemID
            FROM dbo.EmployeeCashErrorActions AS a
            LEFT JOIN dbo.EmployeeCashErrorActionItems AS x ON x.ActionID = a.ActionID
            WHERE a.EmployeeUserID = @employee
              AND CAST(a.CreatedAt AS date) BETWEEN @from AND @to
            ORDER BY a.CreatedAt, a.ActionID;
            """,
            connection))
        {
            command.Parameters.Add("@employee", SqlDbType.UniqueIdentifier).Value = employeeId;
            command.Parameters.Add("@from", SqlDbType.Date).Value = from.Date;
            command.Parameters.Add("@to", SqlDbType.Date).Value = to.Date;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!items.TryGetValue(id, out var list))
                {
                    list = [];
                    items[id] = list;
                    rows.Add((
                        id,
                        reader.GetString(1),
                        reader.GetDecimal(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetDateTime(7)));
                }

                if (!reader.IsDBNull(8))
                {
                    list.Add(reader.GetGuid(8));
                }
            }
        }

        return rows
            .Select(row => new CashErrorActionView(row.Id, row.Type, employeeId, row.Amount, row.Method, row.DeferTo, row.Note, row.Actor, row.At, items[row.Id]))
            .ToArray();
    }

    private static async Task<string?> BranchNameAsync(SqlConnection connection, Guid branchId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT BranchName FROM dbo.Branches WHERE BranchID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = branchId;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string> EmployeeNameAsync(SqlConnection connection, Guid employeeId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT DisplayName FROM dbo.Users WHERE UserID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = employeeId;
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? "موظف غير معروف";
    }

    // ------------------------------------------------------------------
    // الكتابة والاتصال
    // ------------------------------------------------------------------

    private static async Task InsertActionAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        Guid actionId,
        CashErrorActionDraft draft,
        PosDeviceContext actor,
        bool relayed,
        CancellationToken cancellationToken)
    {
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.EmployeeCashErrorActions
                (ActionID, ActionType, BranchID, EmployeeUserID, Amount, Method, DeferToDate, Note, ActorUserID, ActorName, CreatedAt, RelayedAt)
            VALUES
                (@id, @type, @branch, @employee, @amount, @method, @defer, @note, @actor, @actorName, SYSUTCDATETIME(),
                 CASE WHEN @relayed = 1 THEN SYSUTCDATETIME() ELSE NULL END);
            """,
            branch,
            transaction))
        {
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = actionId;
            insert.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = draft.Type;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            insert.Parameters.Add("@employee", SqlDbType.UniqueIdentifier).Value = draft.EmployeeId;
            AddMoney(insert, "@amount", draft.Amount);
            insert.Parameters.Add("@method", SqlDbType.NVarChar, 20).Value = (object?)draft.Method ?? DBNull.Value;
            insert.Parameters.Add("@defer", SqlDbType.Date).Value = (object?)draft.DeferTo ?? DBNull.Value;
            insert.Parameters.Add("@note", SqlDbType.NVarChar, 500).Value = (object?)draft.Note ?? DBNull.Value;
            insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actor.UserId;
            insert.Parameters.Add("@actorName", SqlDbType.NVarChar, 200).Value = actor.DisplayName;
            insert.Parameters.Add("@relayed", SqlDbType.Bit).Value = relayed;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in draft.Items)
        {
            await using var line = new SqlCommand(
                "INSERT INTO dbo.EmployeeCashErrorActionItems (ActionID, ItemID, ItemAmount) VALUES (@action, @item, @amount);",
                branch,
                transaction);
            line.Parameters.Add("@action", SqlDbType.UniqueIdentifier).Value = actionId;
            line.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = item.ItemId;
            AddMoney(line, "@amount", item.Amount);
            await line.ExecuteNonQueryAsync(cancellationToken);
        }
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
            if (settings is null)
            {
                return null;
            }

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
            logger.LogInformation(exception, "Central was not reachable for employee cash errors; using the branch database.");
            return null;
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, SqlTransaction? transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection, transaction);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = value;
    }
}
