using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// المرحلتان 4 و5 من وحدة الجرد والنواقص، كلها على XtraPOS_Central:
/// «معالجة النواقص» (الحساب من نسخ أرصدة الفروع وسرعاتها، المسودة، القرار لكل صنف، التثبيت النهائي)،
/// و«متابعة النواقص» (الشراء، المخزن، الموازنة، الحذف بموافقة المدير، التجار وطلبات التزويد).
/// المركز لا يكتب مخزون أي فرع: تصحيح كمية فرع آخر طلب ينفّذه الفرع نفسه (InventoryRelayService).
/// </summary>
public sealed class ShortageService(InventoryCountService counting, InventoryReviewService review)
{
    private const string CentralUnavailable = "المركز غير متاح الآن. النواقص ومتابعتها تعمل مع المركز فقط.";
    private const string CentralNotUpdated = "المركز لم يُحدَّث بعد للنواقص (الترحيل 041).";

    // ------------------------------------------------------------------
    // معالجة النواقص
    // ------------------------------------------------------------------

    public async Task<object> OptionsAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        var branches = await BranchesAsync(central, actor, cancellationToken);
        var customs = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT inventory.CustomInventoryID, inventory.InventoryName,
                   (SELECT COUNT(*) FROM dbo.CustomInventoryItems AS item WHERE item.CustomInventoryID = inventory.CustomInventoryID)
            FROM dbo.CustomInventories AS inventory
            WHERE inventory.BusinessID = @business AND inventory.IsActive = 1
            ORDER BY inventory.InventoryName;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                customs.Add(new { customInventoryId = reader.GetGuid(0), name = reader.GetString(1), items = reader.GetInt32(2) });
            }
        }

        var users = await UsersAsync(central, null, cancellationToken);
        var draft = await DraftAsync(central, actor, cancellationToken);
        return new { branches, customInventories = customs, users, draft, pageCount = InventoryCountRules.PeriodicPageCount };
    }

    /// <summary>
    /// ملاحظة المالك: في «الجرد اليدوي» تظهر فقط أسماء من نفّذ جرداً يدوياً مكتملاً في الفترة (وفي الفروع المختارة)،
    /// ويُعاد حسابها عند كل تغيير للفترة. الافتراضي: البارحة إلى اليوم.
    /// </summary>
    public async Task<object> ManualCountersAsync(PosDeviceContext actor, DateOnly? from, DateOnly? to, IReadOnlyCollection<Guid>? branchIds, CancellationToken cancellationToken)
    {
        var today = InventoryCalendar.Today();
        var (start, end) = ShortageRules.Range(from ?? today.AddDays(-1), to ?? today, today, 1);
        await using var central = await CentralAsync(cancellationToken);
        var branches = string.Join(",", (branchIds ?? []).Distinct().Select(id => id.ToString("D")));
        await using var command = new SqlCommand(
            """
            SELECT countSession.UserID, MAX(countSession.UserName), MAX(users.EmployeeNumber), COUNT(*)
            FROM dbo.InventorySessions AS countSession
            LEFT JOIN dbo.Users AS users ON users.UserID = countSession.UserID
            WHERE countSession.BusinessID = @business
              AND countSession.InventoryType = N'Manual' AND countSession.Status = N'Completed'
              AND countSession.BusinessDate BETWEEN @from AND @to
              AND (@branches = N'' OR countSession.BranchID IN (SELECT TRY_CONVERT(uniqueidentifier, value) FROM STRING_SPLIT(@branches, N',')))
            GROUP BY countSession.UserID
            ORDER BY MAX(countSession.UserName);
            """,
            central);
        Id(command, "@business", actor.BusinessId);
        command.Parameters.Add("@from", SqlDbType.Date).Value = start.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@to", SqlDbType.Date).Value = end.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@branches", SqlDbType.NVarChar, -1).Value = branches;
        var users = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new
            {
                userId = reader.GetGuid(0),
                displayName = Str(reader, 1) ?? "موظف",
                employeeNumber = Str(reader, 2),
                sessions = reader.GetInt32(3),
            });
        }

        return new { from = start.ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd"), users };
    }

    public sealed class StartRequest
    {
        public string? ShortageType { get; set; }
        public List<Guid>? BranchIds { get; set; }
        public List<int>? Pages { get; set; }
        public int? SupplyDays { get; set; }
        public Guid? CustomInventoryId { get; set; }
        public DateOnly? ManualFrom { get; set; }
        public DateOnly? ManualTo { get; set; }
        public List<Guid>? UserIds { get; set; }
        public bool ReplaceDraft { get; set; }
    }

    /// <summary>«تثبيت ومتابعة»: مسودة هذا المستخدم تُتابع، أو تُلغى ويُحسب من جديد إن طلب «جديد».</summary>
    public async Task<object> StartAsync(PosDeviceContext actor, StartRequest request, CancellationToken cancellationToken)
    {
        var type = ShortageTypes.Normalize(request.ShortageType) ?? throw new InventoryException("نوع النواقص غير صالح.");
        var days = ShortageRules.RequireSupplyDays(request.SupplyDays);
        var pages = InventoryCountRules.NormalizePages(type, request.Pages);
        var branchIds = (request.BranchIds ?? []).Distinct().ToList();
        if (branchIds.Count == 0) throw new InventoryException("يرجى اختيار فرع واحد على الأقل.");
        if (type == InventoryTypes.Custom && request.CustomInventoryId is null) throw new InventoryException("يرجى اختيار الجرد المخصص.");
        DateOnly? manualFrom = null, manualTo = null;
        if (type == InventoryTypes.Manual)
        {
            (manualFrom, manualTo) = ShortageRules.Range(request.ManualFrom, request.ManualTo, InventoryCalendar.Today(), 2);
        }

        await using var central = await CentralAsync(cancellationToken);
        if (!request.ReplaceDraft && await DraftAsync(central, actor, cancellationToken) is { } existing)
        {
            return new { resumed = true, draft = existing };
        }

        var branches = (await BranchesAsync(central, actor, cancellationToken))
            .Where(branch => branchIds.Contains(branch.BranchId))
            .ToList();
        if (branches.Count != branchIds.Count) throw new InventoryException("فرع غير موجود أو غير نشط.");

        var runId = Guid.NewGuid();
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var discard = new SqlCommand(
                "UPDATE dbo.ShortageRuns SET Status = N'Discarded' WHERE CreatedByUserID = @user AND BusinessID = @business AND Status = N'Draft';",
                central,
                transaction))
            {
                Id(discard, "@user", actor.UserId);
                Id(discard, "@business", actor.BusinessId);
                await discard.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = new SqlCommand(
                """
                INSERT INTO dbo.ShortageRuns
                    (RunID, BusinessID, ShortageType, SupplyDays, Pages, CustomInventoryID, ManualFrom, ManualTo, CreatedByUserID, CreatedByName)
                VALUES (@run, @business, @type, @days, @pages, @custom, @from, @to, @user, @name);
                """,
                central,
                transaction))
            {
                Id(insert, "@run", runId);
                Id(insert, "@business", actor.BusinessId);
                Text(insert, "@type", type, 20);
                insert.Parameters.Add("@days", SqlDbType.Int).Value = days;
                Text(insert, "@pages", pages.Length == 0 ? null : string.Join(",", pages), 60);
                NullableGuid(insert, "@custom", type == InventoryTypes.Custom ? request.CustomInventoryId : null);
                insert.Parameters.Add("@from", SqlDbType.Date).Value = (object?)manualFrom?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value;
                insert.Parameters.Add("@to", SqlDbType.Date).Value = (object?)manualTo?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value;
                Id(insert, "@user", actor.UserId);
                Text(insert, "@name", actor.DisplayName, 200);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var branch in branches)
            {
                await using var insert = new SqlCommand(
                    "INSERT INTO dbo.ShortageRunBranches (RunID, BranchID, BranchName, SnapshotAt) VALUES (@run, @branch, @name, @snapshot);",
                    central,
                    transaction);
                Id(insert, "@run", runId);
                Id(insert, "@branch", branch.BranchId);
                Text(insert, "@name", branch.BranchName, 200);
                insert.Parameters.Add("@snapshot", SqlDbType.DateTime2).Value = (object?)branch.LastPushAt ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var userId in (request.UserIds ?? []).Distinct())
            {
                await using var insert = new SqlCommand("INSERT INTO dbo.ShortageRunUsers (RunID, UserID) VALUES (@run, @user);", central, transaction);
                Id(insert, "@run", runId);
                Id(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var count = await CalculateAsync(central, transaction, runId, type, pages, request.CustomInventoryId, days, cancellationToken);
            await using (var update = new SqlCommand("UPDATE dbo.ShortageRuns SET ItemCount = @count WHERE RunID = @run;", central, transaction))
            {
                Id(update, "@run", runId);
                update.Parameters.Add("@count", SqlDbType.Int).Value = count;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new { resumed = false, draft = await DraftAsync(central, actor, cancellationToken) };
    }

    private static async Task<int> CalculateAsync(
        SqlConnection central,
        SqlTransaction transaction,
        Guid runId,
        string type,
        int[] pages,
        Guid? customId,
        int days,
        CancellationToken cancellationToken)
    {
        var owner = InventoryRelayRules.StockOwnerSql("item");
        var scope = type switch
        {
            InventoryTypes.Important => $"""
                SELECT item.ItemID FROM dbo.ItemInventorySettings AS setting
                INNER JOIN dbo.Items AS item ON item.ItemNumber = setting.ItemNumber AND item.IsActive = 1
                WHERE (setting.IsImportant = 1 OR setting.InventoryPage = 0) AND {owner}
                """,
            InventoryTypes.Periodic => $"""
                SELECT item.ItemID FROM dbo.ItemInventorySettings AS setting
                INNER JOIN dbo.Items AS item ON item.ItemNumber = setting.ItemNumber AND item.IsActive = 1
                WHERE setting.InventoryPage IN ({string.Join(", ", pages.Select((_, index) => $"@page{index}"))}) AND {owner}
                """,
            InventoryTypes.Custom => $"""
                SELECT item.ItemID FROM dbo.CustomInventoryItems AS custom
                INNER JOIN dbo.Items AS item ON item.ItemID = custom.ItemID AND item.IsActive = 1
                WHERE custom.CustomInventoryID = @custom AND {owner}
                """,
            _ => $"""
                SELECT line.ItemID AS ItemID FROM dbo.InventorySessionItems AS line
                INNER JOIN dbo.InventorySessions AS countSession ON countSession.SessionID = line.SessionID
                INNER JOIN dbo.Items AS item ON item.ItemID = line.ItemID AND item.IsActive = 1
                WHERE {ManualFilter} AND {owner}
                """,
        };

        var rows = new List<(Guid ItemId, string Number, string? Name, Guid BranchId, decimal? Quantity, decimal? Rate)>();
        await using (var command = new SqlCommand(
            $"""
             SELECT item.ItemID, item.ItemNumber, item.ItemName, target.BranchID,
                    CASE WHEN snapshot.IsQuantityInitialized = 1 THEN snapshot.OnHandQuantity END, snapshot.DailySalesRate
             FROM (SELECT DISTINCT ItemID FROM ({scope}) AS candidates) AS scope
             INNER JOIN dbo.Items AS item ON item.ItemID = scope.ItemID
             CROSS JOIN dbo.ShortageRunBranches AS target
             LEFT JOIN dbo.BranchStockSnapshots AS snapshot ON snapshot.BranchID = target.BranchID AND snapshot.ItemID = item.ItemID
             WHERE target.RunID = @run;
             """,
            central,
            transaction) { CommandTimeout = 300 })
        {
            Id(command, "@run", runId);
            NullableGuid(command, "@custom", customId);
            for (var index = 0; index < pages.Length; index++) command.Parameters.Add($"@page{index}", SqlDbType.TinyInt).Value = (byte)pages[index];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5)));
            }
        }

        // اليدوي: آخر عدّ مكتمل لكل فرع وصنف بدل الرصيد، كما في التطبيق القديم.
        var counted = new Dictionary<(Guid, Guid), decimal>();
        if (type == InventoryTypes.Manual)
        {
            await using var command = new SqlCommand(
                $"""
                 SELECT BranchID, ItemID, CountedQuantity FROM (
                     SELECT countSession.BranchID, line.ItemID, line.CountedQuantity,
                            ROW_NUMBER() OVER (PARTITION BY countSession.BranchID, line.ItemID ORDER BY line.CountedAt DESC) AS Position
                     FROM dbo.InventorySessionItems AS line
                     INNER JOIN dbo.InventorySessions AS countSession ON countSession.SessionID = line.SessionID
                     WHERE {ManualFilter} AND line.CountedQuantity IS NOT NULL) AS latest
                 WHERE Position = 1;
                 """,
                central,
                transaction);
            Id(command, "@run", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) counted[(reader.GetGuid(0), reader.GetGuid(1))] = reader.GetDecimal(2);
        }

        var items = new DataTable();
        foreach (var (name, kind) in new (string, Type)[]
                 {
                     ("RunItemID", typeof(Guid)), ("RunID", typeof(Guid)), ("ItemID", typeof(Guid)), ("ItemNumber", typeof(string)),
                     ("ItemName", typeof(string)), ("TotalNeed", typeof(decimal)), ("TotalSurplus", typeof(decimal)),
                     ("RequiredQuantity", typeof(decimal)), ("BalanceQuantity", typeof(decimal)), ("MissingRate", typeof(bool)),
                     ("MissingQuantity", typeof(bool)),
                 })
        {
            items.Columns.Add(name, kind);
        }

        var branchRows = new DataTable();
        foreach (var (name, kind) in new (string, Type)[]
                 {
                     ("RunID", typeof(Guid)), ("ItemID", typeof(Guid)), ("BranchID", typeof(Guid)), ("CurrentQuantity", typeof(decimal)),
                     ("DailySalesRate", typeof(decimal)), ("TargetQuantity", typeof(decimal)), ("NeedQuantity", typeof(decimal)),
                     ("SurplusQuantity", typeof(decimal)),
                 })
        {
            branchRows.Columns.Add(name, kind);
        }

        foreach (var group in rows.GroupBy(row => row.ItemId))
        {
            var first = group.First();
            var result = ShortageRules.Calculate(
                group.Select(row => new ShortageBranchInput(
                    row.BranchId,
                    counted.TryGetValue((row.BranchId, row.ItemId), out var count) ? count : row.Quantity,
                    row.Rate)),
                days);
            if (!result.IsShort) continue;
            items.Rows.Add(Guid.NewGuid(), runId, first.ItemId, first.Number, (object?)first.Name ?? DBNull.Value,
                result.TotalNeed, result.TotalSurplus, result.Required, result.Balance, result.MissingRate, result.MissingQuantity);
            foreach (var branch in result.Branches)
            {
                branchRows.Rows.Add(runId, first.ItemId, branch.BranchId, (object?)branch.Quantity ?? DBNull.Value,
                    (object?)branch.DailySalesRate ?? DBNull.Value, branch.Target, branch.Need, branch.Surplus);
            }
        }

        await BulkAsync(central, transaction, "dbo.ShortageRunItems", items, cancellationToken);
        await BulkAsync(central, transaction, "dbo.ShortageRunItemBranches", branchRows, cancellationToken);
        return items.Rows.Count;
    }

    private const string ManualFilter = """
        countSession.InventoryType = N'Manual' AND countSession.Status = N'Completed' AND line.IsCounted = 1
        AND countSession.BranchID IN (SELECT BranchID FROM dbo.ShortageRunBranches WHERE RunID = @run)
        AND countSession.BusinessDate BETWEEN (SELECT ManualFrom FROM dbo.ShortageRuns WHERE RunID = @run)
                                          AND (SELECT ManualTo FROM dbo.ShortageRuns WHERE RunID = @run)
        AND (NOT EXISTS (SELECT 1 FROM dbo.ShortageRunUsers WHERE RunID = @run)
             OR countSession.UserID IN (SELECT UserID FROM dbo.ShortageRunUsers WHERE RunID = @run))
        """;

    public async Task<object> RunAsync(PosDeviceContext actor, Guid runId, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        var header = await RunHeaderAsync(central, actor, runId, cancellationToken)
            ?? throw new InventoryException("عملية معالجة النواقص غير موجودة.", StatusCodes.Status404NotFound);
        var branchRows = new Dictionary<Guid, List<object>>();
        await using (var command = new SqlCommand(
            """
            SELECT row.ItemID, row.BranchID, target.BranchName, row.CurrentQuantity, row.DailySalesRate, row.TargetQuantity, row.NeedQuantity, row.SurplusQuantity
            FROM dbo.ShortageRunItemBranches AS row
            INNER JOIN dbo.ShortageRunBranches AS target ON target.RunID = row.RunID AND target.BranchID = row.BranchID
            WHERE row.RunID = @run
            ORDER BY target.BranchName;
            """,
            central))
        {
            Id(command, "@run", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemId = reader.GetGuid(0);
                if (!branchRows.TryGetValue(itemId, out var list)) branchRows[itemId] = list = [];
                list.Add(new
                {
                    branchId = reader.GetGuid(1),
                    branchName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    currentQuantity = Dec(reader, 3),
                    dailySalesRate = Dec(reader, 4),
                    target = reader.GetDecimal(5),
                    need = reader.GetDecimal(6),
                    surplus = reader.GetDecimal(7),
                });
            }
        }

        var items = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT ItemID, ItemNumber, ItemName, TotalNeed, TotalSurplus, RequiredQuantity, BalanceQuantity, MissingRate, MissingQuantity,
                   IsProcessed, PurchaseQuantity, WarehouseQuantity, BalanceDecision, DeleteRequested, AlreadySupplied, Note, ProcessedByName,
                   SubstituteItemNumber
            FROM dbo.ShortageRunItems WHERE RunID = @run ORDER BY IsProcessed, ItemNumber;
            """,
            central))
        {
            Id(command, "@run", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemId = reader.GetGuid(0);
                items.Add(new
                {
                    itemId,
                    itemNumber = reader.GetString(1),
                    itemName = Str(reader, 2),
                    totalNeed = reader.GetDecimal(3),
                    totalSurplus = reader.GetDecimal(4),
                    requiredQuantity = reader.GetDecimal(5),
                    balanceQuantity = reader.GetDecimal(6),
                    missingRate = reader.GetBoolean(7),
                    missingQuantity = reader.GetBoolean(8),
                    isProcessed = reader.GetBoolean(9),
                    purchaseQuantity = Dec(reader, 10),
                    warehouseQuantity = Dec(reader, 11),
                    balanceDecision = Dec(reader, 12),
                    deleteRequested = reader.GetBoolean(13),
                    alreadySupplied = reader.GetBoolean(14),
                    note = Str(reader, 15),
                    processedByName = Str(reader, 16),
                    substituteItemNumber = Str(reader, 17),
                    branches = branchRows.GetValueOrDefault(itemId) ?? new List<object>(),
                });
            }
        }

        return new { run = header, items };
    }

    public async Task<object> SaveDecisionAsync(PosDeviceContext actor, Guid runId, Guid itemId, ShortageDecision? decision, CancellationToken cancellationToken)
    {
        var clean = decision is null ? null : ShortageRules.ValidateDecision(decision);
        await using var central = await CentralAsync(cancellationToken);
        await RequireOwnDraftAsync(central, null, actor, runId, cancellationToken);
        // البديل في طلب الحذف: يُربط بصنف مالك المخزون الفعلي، ولا يكون نفس الصنف.
        InventoryCountService.OwnerResolution? substitute = null;
        if (clean?.Substitute is string code)
        {
            substitute = await InventoryCountService.ResolveOwnerAsync(central, null, code, cancellationToken)
                ?? throw new InventoryException($"الصنف البديل {code} غير موجود.", StatusCodes.Status404NotFound);
            if (substitute.OwnerItemId == itemId) throw new InventoryException("الصنف البديل لا يكون نفس الصنف المطلوب حذفه.");
        }

        await using var command = new SqlCommand(
            """
            UPDATE dbo.ShortageRunItems
            SET IsProcessed = @processed, PurchaseQuantity = @purchase, WarehouseQuantity = @warehouse, BalanceDecision = @balance,
                DeleteRequested = @delete, AlreadySupplied = @supplied, Note = @note,
                SubstituteItemID = @substitute, SubstituteItemNumber = @substituteNumber,
                ProcessedAt = CASE WHEN @processed = 1 THEN SYSUTCDATETIME() END,
                ProcessedByName = CASE WHEN @processed = 1 THEN @by END
            WHERE RunID = @run AND ItemID = @item;
            UPDATE dbo.ShortageRuns SET LastSavedAt = SYSUTCDATETIME() WHERE RunID = @run;
            """,
            central);
        Id(command, "@run", runId);
        Id(command, "@item", itemId);
        command.Parameters.Add("@processed", SqlDbType.Bit).Value = clean is not null;
        Num(command, "@purchase", clean?.Purchase);
        Num(command, "@warehouse", clean?.Warehouse);
        Num(command, "@balance", clean?.Balance);
        command.Parameters.Add("@delete", SqlDbType.Bit).Value = clean?.Delete ?? false;
        command.Parameters.Add("@supplied", SqlDbType.Bit).Value = clean?.AlreadySupplied ?? false;
        Text(command, "@note", clean?.Note, 500);
        NullableGuid(command, "@substitute", substitute?.OwnerItemId);
        Text(command, "@substituteNumber", substitute?.OwnerItemNumber, 100);
        Text(command, "@by", actor.DisplayName, 200);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new { saved = true, substituteItemNumber = substitute?.OwnerItemNumber, substituteItemName = substitute?.OwnerItemName };
    }

    /// <summary>التثبيت النهائي: كل صنف ناقص يجب أن يُعالج، ثم تتحول القرارات إلى مهام «متابعة النواقص».</summary>
    public async Task<object> FinalizeAsync(PosDeviceContext actor, Guid runId, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await RequireOwnDraftAsync(central, transaction, actor, runId, cancellationToken);
            var decisions = new List<(Guid ItemId, string Number, string? Name, ShortageDecision Decision, Guid? SubstituteId, string? SubstituteNumber)>();
            var unprocessed = 0;
            await using (var read = new SqlCommand(
                """
                SELECT ItemID, ItemNumber, ItemName, IsProcessed, PurchaseQuantity, WarehouseQuantity, BalanceDecision, DeleteRequested, AlreadySupplied, Note,
                       SubstituteItemID, SubstituteItemNumber
                FROM dbo.ShortageRunItems WHERE RunID = @run;
                """,
                central,
                transaction))
            {
                Id(read, "@run", runId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.GetBoolean(3))
                    {
                        unprocessed++;
                        continue;
                    }

                    decisions.Add((reader.GetGuid(0), reader.GetString(1), Str(reader, 2),
                        new ShortageDecision(Dec(reader, 4), Dec(reader, 5), Dec(reader, 6), reader.GetBoolean(7), reader.GetBoolean(8), Str(reader, 9)),
                        reader.IsDBNull(10) ? null : reader.GetGuid(10), Str(reader, 11)));
                }
            }

            if (unprocessed > 0)
            {
                throw new InventoryException($"لم تتم معالجة {unprocessed} صنف بعد. عالج كل الأصناف قبل التثبيت النهائي.", StatusCodes.Status409Conflict);
            }

            var tasks = new DataTable();
            foreach (var (name, kind) in new (string, Type)[]
                     {
                         ("TaskID", typeof(Guid)), ("BusinessID", typeof(Guid)), ("RunID", typeof(Guid)), ("ItemID", typeof(Guid)),
                         ("ItemNumber", typeof(string)), ("ItemName", typeof(string)), ("TaskType", typeof(string)), ("Quantity", typeof(decimal)),
                         ("Note", typeof(string)), ("Status", typeof(string)), ("AssignedByName", typeof(string)), ("ApprovalStatus", typeof(string)),
                         ("SubstituteItemID", typeof(Guid)), ("SubstituteItemNumber", typeof(string)),
                     })
            {
                tasks.Columns.Add(name, kind);
            }

            foreach (var entry in decisions)
            {
                foreach (var (taskType, quantity) in ShortageRules.TasksFor(entry.Decision))
                {
                    tasks.Rows.Add(Guid.NewGuid(), actor.BusinessId, runId, entry.ItemId, entry.Number, (object?)entry.Name ?? DBNull.Value,
                        taskType, (object?)quantity ?? DBNull.Value, (object?)entry.Decision.Note ?? DBNull.Value, "Open",
                        (object?)actor.DisplayName ?? DBNull.Value, taskType == ShortageTaskTypes.Delete ? "Pending" : DBNull.Value,
                        taskType == ShortageTaskTypes.Delete && entry.SubstituteId is Guid substituteId ? substituteId : DBNull.Value,
                        taskType == ShortageTaskTypes.Delete ? (object?)entry.SubstituteNumber ?? DBNull.Value : DBNull.Value);
                }
            }

            await BulkAsync(central, transaction, "dbo.ShortageTasks", tasks, cancellationToken);
            await using (var complete = new SqlCommand(
                "UPDATE dbo.ShortageRuns SET Status = N'Completed', CompletedAt = SYSUTCDATETIME(), LastSavedAt = SYSUTCDATETIME() WHERE RunID = @run;",
                central,
                transaction))
            {
                Id(complete, "@run", runId);
                await complete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new { completed = true, tasks = tasks.Rows.Count, items = decisions.Count };
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<object> DiscardAsync(PosDeviceContext actor, Guid runId, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        await RequireOwnDraftAsync(central, null, actor, runId, cancellationToken);
        await using var command = new SqlCommand("UPDATE dbo.ShortageRuns SET Status = N'Discarded' WHERE RunID = @run AND Status = N'Draft';", central);
        Id(command, "@run", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new { discarded = true };
    }

    /// <summary>«الكميات» و«الجرد» لصنف في كل فرع: الرصيد على المركز ووقته، والسرعة، وآخر عدّ، وطلبات التصحيح.</summary>
    public async Task<object> ItemBranchesAsync(PosDeviceContext actor, Guid itemId, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        var rows = new List<object>();
        await using var command = new SqlCommand(
            """
            SELECT branch.BranchID, branch.BranchName,
                   CASE WHEN snapshot.IsQuantityInitialized = 1 THEN snapshot.OnHandQuantity END, snapshot.DailySalesRate, snapshot.ReceivedAt,
                   lastCount.CountedQuantity, lastCount.CountedAt, lastCount.CountedByName,
                   pending.NewQuantity, pending.RequestedAt, pending.ExpectedQuantity, pending.DeltaQuantity
            FROM dbo.Branches AS branch
            LEFT JOIN dbo.BranchStockSnapshots AS snapshot ON snapshot.BranchID = branch.BranchID AND snapshot.ItemID = @item
            OUTER APPLY (
                SELECT TOP (1) line.CountedQuantity, line.CountedAt, line.CountedByName
                FROM dbo.InventorySessionItems AS line
                INNER JOIN dbo.InventorySessions AS countSession ON countSession.SessionID = line.SessionID
                WHERE countSession.BranchID = branch.BranchID AND line.ItemID = @item AND line.IsCounted = 1
                ORDER BY line.CountedAt DESC) AS lastCount
            OUTER APPLY (
                SELECT TOP (1) request.NewQuantity, request.RequestedAt, request.ExpectedQuantity, request.DeltaQuantity
                FROM dbo.InventoryCorrectionRequests AS request
                WHERE request.BranchID = branch.BranchID AND request.ItemID = @item AND request.Status = N'Pending'
                ORDER BY request.RequestedAt DESC) AS pending
            WHERE branch.BusinessID = @business AND branch.IsActive = 1
            ORDER BY branch.BranchName;
            """,
            central);
        Id(command, "@item", itemId);
        Id(command, "@business", actor.BusinessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var branchId = reader.GetGuid(0);
            rows.Add(new
            {
                branchId,
                branchName = reader.GetString(1),
                current = branchId == actor.BranchId,
                quantity = Dec(reader, 2),
                dailySalesRate = Dec(reader, 3),
                snapshotAt = UtcOrNull(reader, 4),
                lastCountedQuantity = Dec(reader, 5),
                lastCountedAt = UtcOrNull(reader, 6),
                lastCountedBy = Str(reader, 7),
                pendingCorrection = Dec(reader, 8),
                pendingSince = UtcOrNull(reader, 9),
                pendingExpected = Dec(reader, 10),
                pendingDelta = Dec(reader, 11),
            });
        }

        return new { itemId, branches = rows };
    }

    /// <summary>«تصحيح»: فرع الجهاز يُرحَّل فوراً، وأي فرع آخر طلب ينفّذه ذلك الفرع ويظهر «قيد التنفيذ في الفرع».</summary>
    public async Task<object> CorrectAsync(
        PosDeviceContext actor,
        Guid branchId,
        Guid itemId,
        Guid requestId,
        decimal? quantity,
        string? reason,
        CancellationToken cancellationToken,
        decimal? expectedQuantity = null)
    {
        if (branchId == actor.BranchId)
        {
            await review.CorrectQuantityAsync(actor, itemId, requestId, quantity, reason, cancellationToken);
            return new { applied = true, pending = false };
        }

        if (requestId == Guid.Empty) throw new InventoryException("طلب التصحيح غير صالح.");
        var value = InventoryCountRules.ValidateCountedQuantity(quantity);
        var why = InventoryReviewRules.RequireReason(reason);
        await using var central = await CentralAsync(cancellationToken);
        // قرار المالك: الطلب يحمل المفترضة (رصيد الفرع الذي رآه المصحّح) والمعدودة والفارق،
        // والفرع يضيف الفارق إلى رصيده الحالي، فلا يضيع بيع حدث قبل المزامنة.
        // بلا رصيد معروف للفرع لا يوجد فارق: يضع الفرع المعدودة كما هي.
        await using var command = new SqlCommand(
            """
            DECLARE @expected decimal(18, 4) = @seen;
            IF @expected IS NULL
                SELECT @expected = CASE WHEN snapshot.IsQuantityInitialized = 1 THEN snapshot.OnHandQuantity END
                FROM dbo.BranchStockSnapshots AS snapshot
                WHERE snapshot.BranchID = @branch AND snapshot.ItemID = @item;
            IF NOT EXISTS (SELECT 1 FROM dbo.InventoryCorrectionRequests WHERE RequestID = @id)
                INSERT INTO dbo.InventoryCorrectionRequests
                    (RequestID, BranchID, ItemID, ItemNumber, ItemName, NewQuantity, ExpectedQuantity, DeltaQuantity,
                     Reason, RequestedByUserID, RequestedByName)
                SELECT @id, branch.BranchID, item.ItemID, item.ItemNumber, item.ItemName, @quantity, @expected, @quantity - @expected,
                       @reason, @user, @name
                FROM dbo.Items AS item
                CROSS JOIN dbo.Branches AS branch
                WHERE item.ItemID = @item AND branch.BranchID = @branch AND branch.BusinessID = @business;
            SELECT COUNT(*) FROM dbo.InventoryCorrectionRequests WHERE RequestID = @id;
            """,
            central);
        Num(command, "@seen", expectedQuantity);
        Id(command, "@id", requestId);
        Id(command, "@item", itemId);
        Id(command, "@branch", branchId);
        Id(command, "@business", actor.BusinessId);
        Num(command, "@quantity", value);
        Text(command, "@reason", why, 500);
        Id(command, "@user", actor.UserId);
        Text(command, "@name", actor.DisplayName, 200);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
        {
            throw new InventoryException("الصنف أو الفرع غير موجود في المركز.", StatusCodes.Status404NotFound);
        }

        return new { applied = false, pending = true };
    }

    // ------------------------------------------------------------------
    // متابعة النواقص
    // ------------------------------------------------------------------

    public async Task<object> TasksAsync(PosDeviceContext actor, string? tab, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = ShortageRules.Range(from, to, InventoryCalendar.Today(), ShortageRules.FollowUpDefaultDays);
        var fromUtc = InventoryCalendar.StartOfDayUtc(start);
        var toUtc = InventoryCalendar.StartOfDayUtc(end.AddDays(1));
        await using var central = await CentralAsync(cancellationToken);
        var rows = new List<object>();
        var counts = new Dictionary<string, int>();
        await using (var command = new SqlCommand(
            """
            SELECT CASE WHEN Status = N'Open' THEN TaskType ELSE N'Done' END, COUNT(*)
            FROM dbo.ShortageTasks
            WHERE BusinessID = @business
              AND ((Status = N'Open' AND AssignedAt >= @from AND AssignedAt < @to)
                OR (Status <> N'Open' AND CompletedAt >= @from AND CompletedAt < @to))
            GROUP BY CASE WHEN Status = N'Open' THEN TaskType ELSE N'Done' END;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = fromUtc;
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = toUtc;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        var selected = tab?.Trim() ?? ShortageTaskTypes.Purchase;
        if (selected == ShortageTaskTypes.Purchase)
        {
            await using var command = new SqlCommand(
                """
                SELECT task.ItemID, MAX(task.ItemNumber), MAX(task.ItemName), SUM(task.Quantity), COUNT(*), MIN(task.AssignedAt),
                       task.SupplyRequestID, MAX(request.RequestNumber), MAX(supplier.SupplierName)
                FROM dbo.ShortageTasks AS task
                LEFT JOIN dbo.SupplyRequests AS request ON request.RequestID = task.SupplyRequestID
                LEFT JOIN dbo.Suppliers AS supplier ON supplier.SupplierID = request.SupplierID
                WHERE task.BusinessID = @business AND task.TaskType = N'Purchase' AND task.Status = N'Open'
                  AND task.AssignedAt >= @from AND task.AssignedAt < @to
                GROUP BY task.ItemID, task.SupplyRequestID
                ORDER BY MIN(task.AssignedAt);
                """,
                central);
            Id(command, "@business", actor.BusinessId);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = fromUtc;
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = toUtc;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new
                {
                    itemId = reader.GetGuid(0),
                    itemNumber = reader.GetString(1),
                    itemName = Str(reader, 2),
                    quantity = Dec(reader, 3),
                    tasks = reader.GetInt32(4),
                    assignedAt = Utc(reader.GetDateTime(5)),
                    supplyRequestId = reader.IsDBNull(6) ? (Guid?)null : reader.GetGuid(6),
                    requestNumber = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
                    supplierName = Str(reader, 8),
                });
            }
        }
        else
        {
            var done = selected == "Done";
            await using var command = new SqlCommand(
                $"""
                 SELECT task.TaskID, task.ItemID, task.ItemNumber, task.ItemName, task.TaskType, task.Quantity, task.Note, task.Status,
                        task.AssignedAt, task.AssignedByName, task.ApprovalStatus, task.DecidedByName, task.CompletedAt, task.CompletedByName,
                        task.SubstituteItemNumber, substitute.ItemName
                 FROM dbo.ShortageTasks AS task
                 LEFT JOIN dbo.Items AS substitute ON substitute.ItemID = task.SubstituteItemID
                 WHERE task.BusinessID = @business
                   AND {(done
                       ? "task.Status <> N'Open' AND task.CompletedAt >= @from AND task.CompletedAt < @to"
                       : "task.Status = N'Open' AND task.TaskType = @type AND task.AssignedAt >= @from AND task.AssignedAt < @to")}
                 ORDER BY {(done ? "task.CompletedAt DESC" : "task.AssignedAt")};
                 """,
                central);
            Id(command, "@business", actor.BusinessId);
            Text(command, "@type", selected, 20);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = fromUtc;
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = toUtc;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new
                {
                    taskId = reader.GetGuid(0),
                    itemId = reader.GetGuid(1),
                    itemNumber = reader.GetString(2),
                    itemName = Str(reader, 3),
                    taskType = reader.GetString(4),
                    quantity = Dec(reader, 5),
                    note = Str(reader, 6),
                    status = reader.GetString(7),
                    assignedAt = Utc(reader.GetDateTime(8)),
                    assignedByName = Str(reader, 9),
                    approvalStatus = Str(reader, 10),
                    decidedByName = Str(reader, 11),
                    completedAt = UtcOrNull(reader, 12),
                    completedByName = Str(reader, 13),
                    substituteItemNumber = Str(reader, 14),
                    substituteItemName = Str(reader, 15),
                });
            }
        }

        return new
        {
            tab = selected,
            from = start.ToString("yyyy-MM-dd"),
            to = end.ToString("yyyy-MM-dd"),
            counts = new
            {
                purchase = counts.GetValueOrDefault(ShortageTaskTypes.Purchase),
                warehouse = counts.GetValueOrDefault(ShortageTaskTypes.Warehouse),
                balance = counts.GetValueOrDefault(ShortageTaskTypes.Balance),
                delete = counts.GetValueOrDefault(ShortageTaskTypes.Delete),
                done = counts.GetValueOrDefault("Done"),
            },
            rows,
        };
    }

    public sealed class TaskActionRequest
    {
        public string? Action { get; set; }
        public Guid? TaskId { get; set; }
        public Guid? ItemId { get; set; }
        public Guid? SupplyRequestId { get; set; }
        public Guid? SupplierId { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>
    /// إجراءات المتابعة: purchased (تم شراء الصنف)، toWarehouse (جزء للمخزن)، refer (إحالة لتاجر)، done (تم المخزن/الموازنة)،
    /// approveDelete / rejectDelete (بصلاحية Shortages.Approve فقط).
    /// </summary>
    public async Task<object> TaskActionAsync(PosDeviceContext actor, TaskActionRequest request, bool canApprove, CancellationToken cancellationToken)
    {
        var action = request.Action?.Trim() ?? "";
        await using var central = await CentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            switch (action)
            {
                case "purchased":
                {
                    var item = request.ItemId ?? throw new InventoryException("يرجى اختيار الصنف.");
                    await ExecuteAsync(central, transaction,
                        """
                        UPDATE dbo.ShortageTasks SET Status = N'Done', CompletedAt = SYSUTCDATETIME(), CompletedByName = @by
                        WHERE BusinessID = @business AND ItemID = @item AND TaskType = N'Purchase' AND Status = N'Open'
                          AND ((@request IS NULL AND SupplyRequestID IS NULL) OR SupplyRequestID = @request);
                        """,
                        command =>
                        {
                            Id(command, "@business", actor.BusinessId);
                            Id(command, "@item", item);
                            NullableGuid(command, "@request", request.SupplyRequestId);
                            Text(command, "@by", actor.DisplayName, 200);
                        },
                        cancellationToken);
                    break;
                }

                case "toWarehouse":
                {
                    var item = request.ItemId ?? throw new InventoryException("يرجى اختيار الصنف.");
                    var moved = request.Quantity ?? 0m;
                    var open = new List<(Guid, decimal)>();
                    string? number = null, name = null;
                    await using (var read = new SqlCommand(
                        """
                        SELECT TaskID, ISNULL(Quantity, 0), ItemNumber, ItemName FROM dbo.ShortageTasks
                        WHERE BusinessID = @business AND ItemID = @item AND TaskType = N'Purchase' AND Status = N'Open' AND SupplyRequestID IS NULL
                        ORDER BY AssignedAt;
                        """,
                        central,
                        transaction))
                    {
                        Id(read, "@business", actor.BusinessId);
                        Id(read, "@item", item);
                        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            open.Add((reader.GetGuid(0), reader.GetDecimal(1)));
                            number = reader.GetString(2);
                            name = Str(reader, 3);
                        }
                    }

                    foreach (var (taskId, remaining) in ShortageRules.ReducePurchases(open, moved))
                    {
                        await ExecuteAsync(central, transaction,
                            """
                            UPDATE dbo.ShortageTasks
                            SET Quantity = @remaining,
                                Status = CASE WHEN @remaining <= 0 THEN N'Cancelled' ELSE Status END,
                                CompletedAt = CASE WHEN @remaining <= 0 THEN SYSUTCDATETIME() END,
                                CompletedByName = CASE WHEN @remaining <= 0 THEN @by END
                            WHERE TaskID = @task;
                            """,
                            command =>
                            {
                                Id(command, "@task", taskId);
                                Num(command, "@remaining", remaining);
                                Text(command, "@by", actor.DisplayName, 200);
                            },
                            cancellationToken);
                    }

                    await ExecuteAsync(central, transaction,
                        """
                        INSERT INTO dbo.ShortageTasks (TaskID, BusinessID, ItemID, ItemNumber, ItemName, TaskType, Quantity, Note, AssignedByName)
                        VALUES (NEWID(), @business, @item, @number, @name, N'Warehouse', @quantity, N'محوّل من الشراء', @by);
                        """,
                        command =>
                        {
                            Id(command, "@business", actor.BusinessId);
                            Id(command, "@item", item);
                            Text(command, "@number", number, 100);
                            Text(command, "@name", name, 500);
                            Num(command, "@quantity", moved);
                            Text(command, "@by", actor.DisplayName, 200);
                        },
                        cancellationToken);
                    break;
                }

                case "refer":
                {
                    var item = request.ItemId ?? throw new InventoryException("يرجى اختيار الصنف.");
                    var supplier = request.SupplierId ?? throw new InventoryException("يرجى اختيار التاجر.");
                    var requestId = await OpenSupplyRequestAsync(central, transaction, actor, supplier, cancellationToken);
                    await ExecuteAsync(central, transaction,
                        """
                        UPDATE dbo.ShortageTasks SET SupplyRequestID = @request
                        WHERE BusinessID = @business AND ItemID = @item AND TaskType = N'Purchase' AND Status = N'Open' AND SupplyRequestID IS NULL;
                        """,
                        command =>
                        {
                            Id(command, "@business", actor.BusinessId);
                            Id(command, "@item", item);
                            Id(command, "@request", requestId);
                        },
                        cancellationToken);
                    break;
                }

                case "done":
                {
                    var task = request.TaskId ?? throw new InventoryException("يرجى اختيار المهمة.");
                    await ExecuteAsync(central, transaction,
                        """
                        UPDATE dbo.ShortageTasks SET Status = N'Done', CompletedAt = SYSUTCDATETIME(), CompletedByName = @by
                        WHERE TaskID = @task AND BusinessID = @business AND Status = N'Open' AND TaskType IN (N'Warehouse', N'Balance');
                        """,
                        command =>
                        {
                            Id(command, "@task", task);
                            Id(command, "@business", actor.BusinessId);
                            Text(command, "@by", actor.DisplayName, 200);
                        },
                        cancellationToken);
                    break;
                }

                case "approveDelete":
                case "rejectDelete":
                {
                    if (!canApprove) throw new InventoryException("اعتماد الحذف للمدير فقط.", StatusCodes.Status403Forbidden);
                    var task = request.TaskId ?? throw new InventoryException("يرجى اختيار المهمة.");
                    Guid? itemToStop = null;
                    await using (var read = new SqlCommand(
                        "SELECT ItemID FROM dbo.ShortageTasks WHERE TaskID = @task AND BusinessID = @business AND TaskType = N'Delete' AND Status = N'Open';",
                        central,
                        transaction))
                    {
                        Id(read, "@task", task);
                        Id(read, "@business", actor.BusinessId);
                        itemToStop = (await read.ExecuteScalarAsync(cancellationToken) as Guid?)
                            ?? throw new InventoryException("طلب الحذف غير موجود أو تمت معالجته.", StatusCodes.Status404NotFound);
                    }

                    var approve = action == "approveDelete";
                    if (approve) await DeactivateItemAsync(central, transaction, itemToStop.Value, actor.UserId, cancellationToken);
                    await ExecuteAsync(central, transaction,
                        """
                        UPDATE dbo.ShortageTasks
                        SET Status = N'Done', ApprovalStatus = @approval, DecidedByName = @by, DecidedAt = SYSUTCDATETIME(),
                            CompletedAt = SYSUTCDATETIME(), CompletedByName = @by
                        WHERE TaskID = @task;
                        """,
                        command =>
                        {
                            Id(command, "@task", task);
                            Text(command, "@approval", approve ? "Approved" : "Rejected", 20);
                            Text(command, "@by", actor.DisplayName, 200);
                        },
                        cancellationToken);
                    break;
                }

                default:
                    throw new InventoryException("الإجراء غير صالح.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new { done = true };
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// اعتماد الحذف: الصنف يُوقف (IsActive = 0) على المركز ولا يُمحى، وينزل للفروع بمزامنة الأصناف العادية
    /// (SyncChangeLog بنفس شكل تعديل الصنف). يُعاد تفعيله من شاشة الأصناف.
    /// </summary>
    private static async Task DeactivateItemAsync(SqlConnection central, SqlTransaction transaction, Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        object? payload = null;
        await using (var read = new SqlCommand(
            """
            SELECT ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification, CountryOfOrigin,
                   ProductType, ParentItemID, IsProhibited, IsPublishedOnline, IsActive
            FROM dbo.Items WITH (UPDLOCK) WHERE ItemID = @item;
            """,
            central,
            transaction))
        {
            Id(read, "@item", itemId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InventoryException("الصنف غير موجود في المركز.", StatusCodes.Status404NotFound);
            if (!reader.GetBoolean(14)) return;
            payload = new
            {
                itemNumber = reader.GetString(0),
                itemName = reader.GetString(1),
                itemType = Str(reader, 2),
                manufacturer = Str(reader, 3),
                color = Str(reader, 4),
                taste = Str(reader, 5),
                size = Str(reader, 6),
                packing = Str(reader, 7),
                classification = Str(reader, 8),
                countryOfOrigin = Str(reader, 9),
                productType = Str(reader, 10),
                parentItemId = reader.IsDBNull(11) ? (Guid?)null : reader.GetGuid(11),
                isActive = false,
                isProhibited = reader.GetBoolean(12),
                isPublishedOnline = reader.GetBoolean(13),
            };
        }

        await ExecuteAsync(central, transaction,
            """
            UPDATE dbo.Items SET IsActive = 0, UpdatedAt = SYSUTCDATETIME() WHERE ItemID = @item;
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload, ChangedByUserID)
            VALUES (N'Items', @item, N'Update', @payload, @user);
            """,
            command =>
            {
                Id(command, "@item", itemId);
                command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = CentralChangeApply.Serialize(payload!);
                Id(command, "@user", userId);
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------
    // التجار وطلبات التزويد
    // ------------------------------------------------------------------

    public async Task<object> SuppliersAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        var rows = new List<object>();
        await using var command = new SqlCommand(
            "SELECT SupplierID, SupplierName, Phone, Notes, IsActive FROM dbo.Suppliers WHERE BusinessID = @business ORDER BY IsActive DESC, SupplierName;",
            central);
        Id(command, "@business", actor.BusinessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new { supplierId = reader.GetGuid(0), name = reader.GetString(1), phone = Str(reader, 2), notes = Str(reader, 3), isActive = reader.GetBoolean(4) });
        }

        return new { suppliers = rows };
    }

    public sealed class SupplierRequest
    {
        public Guid? SupplierId { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public async Task<object> SaveSupplierAsync(PosDeviceContext actor, SupplierRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 200) throw new InventoryException("اسم التاجر مطلوب (حتى 200 حرف).");
        await using var central = await CentralAsync(cancellationToken);
        var id = request.SupplierId ?? Guid.NewGuid();
        await using var command = new SqlCommand(
            """
            UPDATE dbo.Suppliers SET SupplierName = @name, Phone = @phone, Notes = @notes, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
            WHERE SupplierID = @id AND BusinessID = @business;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.Suppliers (SupplierID, BusinessID, SupplierName, Phone, Notes, IsActive)
                VALUES (@id, @business, @name, @phone, @notes, @active);
            """,
            central);
        Id(command, "@id", id);
        Id(command, "@business", actor.BusinessId);
        Text(command, "@name", name, 200);
        Text(command, "@phone", request.Phone?.Trim(), 50);
        Text(command, "@notes", request.Notes?.Trim(), 500);
        command.Parameters.Add("@active", SqlDbType.Bit).Value = request.IsActive;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await SuppliersAsync(actor, cancellationToken);
    }

    public async Task<object> SupplyRequestsAsync(PosDeviceContext actor, string? status, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        var requests = new List<object>();
        var lines = new Dictionary<Guid, List<object>>();
        await using (var command = new SqlCommand(
            """
            SELECT task.SupplyRequestID, task.ItemID, MAX(task.ItemNumber), MAX(task.ItemName), SUM(task.Quantity)
            FROM dbo.ShortageTasks AS task
            INNER JOIN dbo.SupplyRequests AS request ON request.RequestID = task.SupplyRequestID
            WHERE request.BusinessID = @business AND (@status IS NULL OR request.Status = @status)
            GROUP BY task.SupplyRequestID, task.ItemID;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            Text(command, "@status", string.IsNullOrWhiteSpace(status) ? null : status.Trim(), 20);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new { itemId = reader.GetGuid(1), itemNumber = reader.GetString(2), itemName = Str(reader, 3), quantity = Dec(reader, 4) });
            }
        }

        await using (var command = new SqlCommand(
            """
            SELECT TOP (200) request.RequestID, request.RequestNumber, request.Status, supplier.SupplierID, supplier.SupplierName, supplier.Phone,
                   request.CreatedByName, request.CreatedAt, request.ClosedByName, request.ClosedAt
            FROM dbo.SupplyRequests AS request
            INNER JOIN dbo.Suppliers AS supplier ON supplier.SupplierID = request.SupplierID
            WHERE request.BusinessID = @business AND (@status IS NULL OR request.Status = @status)
            ORDER BY request.CreatedAt DESC;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            Text(command, "@status", string.IsNullOrWhiteSpace(status) ? null : status.Trim(), 20);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                requests.Add(new
                {
                    requestId = id,
                    requestNumber = reader.GetInt32(1),
                    status = reader.GetString(2),
                    supplierId = reader.GetGuid(3),
                    supplierName = reader.GetString(4),
                    supplierPhone = Str(reader, 5),
                    createdByName = Str(reader, 6),
                    createdAt = Utc(reader.GetDateTime(7)),
                    closedByName = Str(reader, 8),
                    closedAt = UtcOrNull(reader, 9),
                    items = lines.GetValueOrDefault(id) ?? new List<object>(),
                });
            }
        }

        return new { requests };
    }

    /// <summary>طلب التزويد: purchased (تم الشراء)، revert (تراجع / إعادة لمسؤول الشراء)، reassign (إحالة لتاجر آخر).</summary>
    public async Task<object> SupplyRequestActionAsync(PosDeviceContext actor, Guid requestId, string? action, Guid? supplierId, CancellationToken cancellationToken)
    {
        await using var central = await CentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        try
        {
            var sql = action?.Trim() switch
            {
                "purchased" => """
                    UPDATE dbo.ShortageTasks SET Status = N'Done', CompletedAt = SYSUTCDATETIME(), CompletedByName = @by
                    WHERE SupplyRequestID = @request AND Status = N'Open';
                    UPDATE dbo.SupplyRequests SET Status = N'Purchased', ClosedAt = SYSUTCDATETIME(), ClosedByName = @by
                    WHERE RequestID = @request AND BusinessID = @business AND Status = N'Open';
                    """,
                "revert" => """
                    UPDATE dbo.ShortageTasks SET SupplyRequestID = NULL WHERE SupplyRequestID = @request AND Status = N'Open';
                    UPDATE dbo.SupplyRequests SET Status = N'Reverted', ClosedAt = SYSUTCDATETIME(), ClosedByName = @by
                    WHERE RequestID = @request AND BusinessID = @business AND Status = N'Open';
                    """,
                "reassign" => """
                    UPDATE dbo.SupplyRequests SET SupplierID = @supplier
                    WHERE RequestID = @request AND BusinessID = @business AND Status = N'Open'
                      AND EXISTS (SELECT 1 FROM dbo.Suppliers WHERE SupplierID = @supplier AND BusinessID = @business AND IsActive = 1);
                    """,
                _ => throw new InventoryException("الإجراء غير صالح."),
            };
            if (action == "reassign" && supplierId is null) throw new InventoryException("يرجى اختيار التاجر.");
            await ExecuteAsync(central, transaction, sql, command =>
            {
                Id(command, "@request", requestId);
                Id(command, "@business", actor.BusinessId);
                NullableGuid(command, "@supplier", supplierId);
                Text(command, "@by", actor.DisplayName, 200);
            }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await SupplyRequestsAsync(actor, "Open", cancellationToken);
    }

    private static async Task<Guid> OpenSupplyRequestAsync(SqlConnection central, SqlTransaction transaction, PosDeviceContext actor, Guid supplierId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Suppliers WHERE SupplierID = @supplier AND BusinessID = @business AND IsActive = 1)
            BEGIN
                SELECT CAST(NULL AS uniqueidentifier);
                RETURN;
            END;
            DECLARE @id uniqueidentifier = (SELECT TOP (1) RequestID FROM dbo.SupplyRequests WITH (UPDLOCK, HOLDLOCK)
                                            WHERE SupplierID = @supplier AND BusinessID = @business AND Status = N'Open' ORDER BY CreatedAt DESC);
            IF @id IS NULL
            BEGIN
                SET @id = NEWID();
                INSERT INTO dbo.SupplyRequests (RequestID, BusinessID, SupplierID, CreatedByName) VALUES (@id, @business, @supplier, @by);
            END;
            SELECT @id;
            """,
            central,
            transaction);
        Id(command, "@supplier", supplierId);
        Id(command, "@business", actor.BusinessId);
        Text(command, "@by", actor.DisplayName, 200);
        return (await command.ExecuteScalarAsync(cancellationToken) as Guid?)
            ?? throw new InventoryException("التاجر غير موجود أو موقوف.", StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------
    // مساعدات
    // ------------------------------------------------------------------

    public sealed record BranchOption(Guid BranchId, string BranchName, bool Current, DateTime? LastPushAt, bool Stale);

    internal async Task<SqlConnection> CentralAsync(CancellationToken cancellationToken)
    {
        var central = await counting.OpenCentralAsync(cancellationToken)
            ?? throw new InventoryException(CentralUnavailable, StatusCodes.Status503ServiceUnavailable);
        await using (var check = new SqlCommand("SELECT OBJECT_ID(N'dbo.ShortageRuns', N'U');", central))
        {
            if (await check.ExecuteScalarAsync(cancellationToken) is null or DBNull)
            {
                await central.DisposeAsync();
                throw new InventoryException(CentralNotUpdated, StatusCodes.Status503ServiceUnavailable);
            }
        }

        return central;
    }

    internal static async Task<List<BranchOption>> BranchesAsync(SqlConnection central, PosDeviceContext actor, CancellationToken cancellationToken)
    {
        var rows = new List<BranchOption>();
        await using var command = new SqlCommand(
            """
            SELECT branch.BranchID, branch.BranchName, status.LastPushAt
            FROM dbo.Branches AS branch
            LEFT JOIN dbo.BranchSnapshotStatus AS status ON status.BranchID = branch.BranchID
            WHERE branch.BusinessID = @business AND branch.IsActive = 1
            ORDER BY branch.BranchName;
            """,
            central);
        Id(command, "@business", actor.BusinessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var now = DateTime.UtcNow;
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var last = UtcOrNull(reader, 2);
            rows.Add(new BranchOption(id, reader.GetString(1), id == actor.BranchId, last, InventoryRelayRules.IsStale(last, now)));
        }

        return rows;
    }

    /// <summary>مستخدمو المركز النشطون، أو من لهم وصول لفرع معيّن.</summary>
    internal static async Task<List<object>> UsersAsync(SqlConnection central, Guid? branchId, CancellationToken cancellationToken)
    {
        var rows = new List<object>();
        await using var command = new SqlCommand(
            """
            SELECT users.UserID, users.DisplayName, users.EmployeeNumber
            FROM dbo.Users AS users
            WHERE users.IsActive = 1
              AND (@branch IS NULL OR EXISTS (SELECT 1 FROM dbo.UserBranchAccess AS access
                                              WHERE access.UserID = users.UserID AND access.BranchID = @branch AND access.IsActive = 1))
            ORDER BY users.DisplayName;
            """,
            central);
        NullableGuid(command, "@branch", branchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new { userId = reader.GetGuid(0), displayName = reader.GetString(1), employeeNumber = Str(reader, 2) });
        }

        return rows;
    }

    private static async Task<object?> DraftAsync(SqlConnection central, PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT TOP (1) RunID FROM dbo.ShortageRuns WHERE CreatedByUserID = @user AND BusinessID = @business AND Status = N'Draft' ORDER BY CreatedAt DESC;",
            central);
        Id(command, "@user", actor.UserId);
        Id(command, "@business", actor.BusinessId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid runId
            ? await RunHeaderAsync(central, actor, runId, cancellationToken)
            : null;
    }

    internal static async Task<object?> RunHeaderAsync(SqlConnection central, PosDeviceContext actor, Guid runId, CancellationToken cancellationToken)
    {
        var branches = new List<object>();
        await using (var command = new SqlCommand(
            "SELECT BranchID, BranchName, SnapshotAt FROM dbo.ShortageRunBranches WHERE RunID = @run ORDER BY BranchName;",
            central))
        {
            Id(command, "@run", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var now = DateTime.UtcNow;
            while (await reader.ReadAsync(cancellationToken))
            {
                var snapshot = UtcOrNull(reader, 2);
                branches.Add(new { branchId = reader.GetGuid(0), branchName = Str(reader, 1), snapshotAt = snapshot, stale = InventoryRelayRules.IsStale(snapshot, now) });
            }
        }

        await using var header = new SqlCommand(
            """
            SELECT run.ShortageType, run.SupplyDays, run.Pages, run.Status, run.CreatedByName, run.CreatedAt, run.LastSavedAt, run.CompletedAt,
                   run.ItemCount, (SELECT COUNT(*) FROM dbo.ShortageRunItems WHERE RunID = run.RunID AND IsProcessed = 1),
                   run.CreatedByUserID, custom.InventoryName, run.ManualFrom, run.ManualTo
            FROM dbo.ShortageRuns AS run
            LEFT JOIN dbo.CustomInventories AS custom ON custom.CustomInventoryID = run.CustomInventoryID
            WHERE run.RunID = @run AND run.BusinessID = @business;
            """,
            central);
        Id(header, "@run", runId);
        Id(header, "@business", actor.BusinessId);
        await using var row = await header.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken)) return null;
        var type = row.GetString(0);
        return new
        {
            runId,
            shortageType = type,
            shortageTypeLabel = ShortageTypes.Label(type),
            supplyDays = row.GetInt32(1),
            pages = row.IsDBNull(2) ? Array.Empty<int>() : row.GetString(2).Split(',').Select(int.Parse).ToArray(),
            status = row.GetString(3),
            createdByName = Str(row, 4),
            createdAt = Utc(row.GetDateTime(5)),
            lastSavedAt = Utc(row.GetDateTime(6)),
            completedAt = UtcOrNull(row, 7),
            itemCount = row.GetInt32(8),
            processedCount = row.GetInt32(9),
            mine = row.GetGuid(10) == actor.UserId,
            customInventoryName = Str(row, 11),
            manualFrom = row.IsDBNull(12) ? null : DateOnly.FromDateTime(row.GetDateTime(12)).ToString("yyyy-MM-dd"),
            manualTo = row.IsDBNull(13) ? null : DateOnly.FromDateTime(row.GetDateTime(13)).ToString("yyyy-MM-dd"),
            branches,
        };
    }

    private static async Task RequireOwnDraftAsync(SqlConnection central, SqlTransaction? transaction, PosDeviceContext actor, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT Status, CreatedByUserID FROM dbo.ShortageRuns WITH (UPDLOCK) WHERE RunID = @run AND BusinessID = @business;",
            central,
            transaction);
        Id(command, "@run", runId);
        Id(command, "@business", actor.BusinessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InventoryException("عملية معالجة النواقص غير موجودة.", StatusCodes.Status404NotFound);
        if (reader.GetGuid(1) != actor.UserId) throw new InventoryException("هذه المعالجة لمستخدم آخر.", StatusCodes.Status403Forbidden);
        if (reader.GetString(0) != "Draft") throw new InventoryException("هذه المعالجة مثبتة أو ملغاة.", StatusCodes.Status409Conflict);
    }

    internal static async Task BulkAsync(SqlConnection central, SqlTransaction transaction, string table, DataTable rows, CancellationToken cancellationToken)
    {
        if (rows.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(central, SqlBulkCopyOptions.CheckConstraints, transaction) { DestinationTableName = table, BulkCopyTimeout = 300 };
        foreach (DataColumn column in rows.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(rows, cancellationToken);
    }

    internal static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, Action<SqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void Id(SqlCommand command, string name, Guid value) => command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = value;

    internal static void NullableGuid(SqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = (object?)value ?? DBNull.Value;

    internal static void Text(SqlCommand command, string name, string? value, int size)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is not null && text.Length > size) text = text[..size];
        command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = (object?)text ?? DBNull.Value;
    }

    internal static void Num(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 4;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    internal static decimal? Dec(SqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);

    internal static string? Str(SqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    internal static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime? UtcOrNull(SqlDataReader reader, int index) => reader.IsDBNull(index) ? null : Utc(reader.GetDateTime(index));
}
