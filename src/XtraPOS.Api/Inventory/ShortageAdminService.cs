using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Security;
using static XtraPOS.Api.Inventory.ShortageService;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// المرحلة 6 على XtraPOS_Central: «تحديد جرد» (الجرد المخصص: اسم + فروع + أصناف، ينزل للفروع)،
/// «التدقيق» (طلب تأكيد استلام من 1 إلى 10 أصناف لموظف فرع، يرد عليه من داخل XtraPOS)،
/// «تقرير الكفاءة» (درجة من 10 لكل موظف؛ الكاشير يرى نفسه فقط)، و«تقرير معالجة النواقص».
/// رابط واتساب العام غير مفعّل: يحتاج عنواناً عاماً على HTTPS كرابط الفاتورة.
/// </summary>
public sealed class ShortageAdminService(ShortageService shortages)
{
    // ------------------------------------------------------------------
    // تحديد جرد (الجرد المخصص)
    // ------------------------------------------------------------------

    public async Task<object> CustomInventoriesAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        var branchNames = (await BranchesAsync(central, actor, cancellationToken)).ToDictionary(branch => branch.BranchId, branch => branch.BranchName);
        var targets = new Dictionary<Guid, List<Guid>>();
        await using (var command = new SqlCommand(
            """
            SELECT target.CustomInventoryID, target.BranchID FROM dbo.CustomInventoryBranches AS target
            INNER JOIN dbo.CustomInventories AS inventory ON inventory.CustomInventoryID = target.CustomInventoryID
            WHERE inventory.BusinessID = @business;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!targets.TryGetValue(id, out var list)) targets[id] = list = [];
                list.Add(reader.GetGuid(1));
            }
        }

        var items = new Dictionary<Guid, List<object>>();
        await using (var command = new SqlCommand(
            """
            SELECT item.CustomInventoryID, item.ItemID, item.ItemNumber, item.ItemName FROM dbo.CustomInventoryItems AS item
            INNER JOIN dbo.CustomInventories AS inventory ON inventory.CustomInventoryID = item.CustomInventoryID
            WHERE inventory.BusinessID = @business
            ORDER BY item.ItemNumber;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                if (!items.TryGetValue(id, out var list)) items[id] = list = [];
                list.Add(new { itemId = reader.GetGuid(1), itemNumber = reader.GetString(2), itemName = Str(reader, 3) });
            }
        }

        var rows = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT inventory.CustomInventoryID, inventory.InventoryName, inventory.IsActive, inventory.CreatedByName, inventory.CreatedAt,
                   CASE WHEN EXISTS (SELECT 1 FROM dbo.InventorySessions AS used WHERE used.CustomInventoryID = inventory.CustomInventoryID)
                        THEN 1 ELSE 0 END
            FROM dbo.CustomInventories AS inventory
            WHERE inventory.BusinessID = @business
              -- «حذف» جرد مستعمل = إيقافه وفك فروعه، فيختفي من القائمة وتبقى تقاريره.
              AND EXISTS (SELECT 1 FROM dbo.CustomInventoryBranches AS target WHERE target.CustomInventoryID = inventory.CustomInventoryID)
            ORDER BY inventory.IsActive DESC, inventory.InventoryName;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var branches = targets.GetValueOrDefault(id) ?? new List<Guid>();
                rows.Add(new
                {
                    customInventoryId = id,
                    name = reader.GetString(1),
                    isActive = reader.GetBoolean(2),
                    createdByName = Str(reader, 3),
                    createdAt = Utc(reader.GetDateTime(4)),
                    used = reader.GetInt32(5) == 1,
                    branchIds = branches,
                    branchNames = branches.Select(branch => branchNames.GetValueOrDefault(branch) ?? "—").ToArray(),
                    items = items.GetValueOrDefault(id) ?? new List<object>(),
                });
            }
        }

        var options = (await BranchesAsync(central, actor, cancellationToken)).Select(branch => new { branchId = branch.BranchId, branchName = branch.BranchName, current = branch.Current });
        return new { customInventories = rows, branches = options };
    }

    /// <summary>«تحديد جرد»: بحث في أصناف المركز باسم أو رقم الصنف (مالكو المخزون فقط).</summary>
    public async Task<object> SearchItemsAsync(PosDeviceContext actor, string? query, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return new { items = Array.Empty<object>(), truncated = false };
        if (text.Length > 100) text = text[..100];
        const int limit = 500;
        await using var central = await shortages.CentralAsync(cancellationToken);
        // ملاحظة المالك: تظهر الأصناف التي «تحتوي» النص في الاسم أو الرقم (تشابه لا تطابق)،
        // والعبوة تظهر بصنف وحدتها الداخلية (مالك المخزون). المحرف «!» للهروب من % و_ و[.
        await using var command = new SqlCommand(
            $"""
            WITH matched AS
            (
                SELECT TOP (3000) item.ItemID
                FROM dbo.Items AS item
                WHERE item.IsActive = 1
                  AND (item.ItemNumber LIKE @contains ESCAPE N'!' OR item.ItemName LIKE @contains ESCAPE N'!'
                       OR item.ItemNumber LIKE @digits ESCAPE N'!')
            ),
            owners AS
            (
                SELECT DISTINCT COALESCE(unit.InternalUnitItemID, matched.ItemID) AS ItemID
                FROM matched
                OUTER APPLY (SELECT TOP (1) link.InternalUnitItemID
                             FROM dbo.ItemUnits AS link
                             WHERE link.ItemID = matched.ItemID AND link.IsActive = 1
                               AND link.InternalUnitItemID IS NOT NULL AND link.InternalUnitItemID <> matched.ItemID) AS unit
            )
            SELECT TOP ({limit + 1}) item.ItemID, item.ItemNumber, item.ItemName
            FROM owners
            INNER JOIN dbo.Items AS item ON item.ItemID = owners.ItemID
            WHERE item.IsActive = 1
              AND {InventoryRelayRules.StockOwnerSql("item")}
            ORDER BY CASE WHEN item.ItemNumber = @exact THEN 0
                          WHEN item.ItemNumber LIKE @prefix ESCAPE N'!' THEN 1
                          WHEN item.ItemName LIKE @prefix ESCAPE N'!' THEN 2 ELSE 3 END,
                     item.ItemNumber;
            """,
            central);
        var escaped = ShortageRules.EscapeLike(text);
        var digits = ShortageRules.EscapeLike(ArabicSearchNormalizer.NormalizeDigits(text));
        Text(command, "@exact", text, 100);
        Text(command, "@prefix", escaped + "%", 210);
        Text(command, "@contains", "%" + escaped + "%", 220);
        Text(command, "@digits", "%" + digits + "%", 220);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == limit) return new { items = rows, truncated = true };
            rows.Add(new { itemId = reader.GetGuid(0), itemNumber = reader.GetString(1), itemName = Str(reader, 2) });
        }

        return new { items = rows, truncated = false };
    }

    /// <summary>تفعيل أو توقيف جرد مخصص. الفروع تسحب الحالة خلال دقيقة.</summary>
    public async Task<object> SetCustomInventoryActiveAsync(PosDeviceContext actor, Guid id, bool active, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CustomInventories SET IsActive = @active, UpdatedAt = SYSUTCDATETIME()
            WHERE CustomInventoryID = @id AND BusinessID = @business;
            SELECT @@ROWCOUNT;
            """,
            central);
        Id(command, "@id", id);
        Id(command, "@business", actor.BusinessId);
        command.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            throw new InventoryException("الجرد المخصص غير موجود.", StatusCodes.Status404NotFound);
        }

        return await CustomInventoriesAsync(actor, cancellationToken);
    }

    /// <summary>
    /// قرار المالك: جرد استُعمل في جلسات سابقة يُوقف وتُفك فروعه فيختفي من القائمة وتبقى تقاريره؛
    /// غير المستعمل يُحذف نهائياً. في الحالتين لا يعود مخصصاً لأي فرع، فيوقفه الفرع في السحب التالي.
    /// </summary>
    public async Task<object> DeleteCustomInventoryAsync(PosDeviceContext actor, Guid id, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CustomInventories WHERE CustomInventoryID = @id AND BusinessID = @business)
            BEGIN
                SELECT N'Missing';
                RETURN;
            END;
            DELETE FROM dbo.CustomInventoryBranches WHERE CustomInventoryID = @id;
            IF EXISTS (SELECT 1 FROM dbo.InventorySessions WHERE CustomInventoryID = @id)
            BEGIN
                UPDATE dbo.CustomInventories SET IsActive = 0, UpdatedAt = SYSUTCDATETIME() WHERE CustomInventoryID = @id;
                SELECT N'Stopped';
            END
            ELSE
            BEGIN
                DELETE FROM dbo.CustomInventoryItems WHERE CustomInventoryID = @id;
                DELETE FROM dbo.CustomInventories WHERE CustomInventoryID = @id;
                SELECT N'Deleted';
            END;
            """,
            central);
        Id(command, "@id", id);
        Id(command, "@business", actor.BusinessId);
        var outcome = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (outcome == "Missing") throw new InventoryException("الجرد المخصص غير موجود.", StatusCodes.Status404NotFound);
        var view = await CustomInventoriesAsync(actor, cancellationToken);
        return new { deleted = outcome == "Deleted", stopped = outcome == "Stopped", view };
    }

    public sealed class CustomInventoryRequest
    {
        public Guid? CustomInventoryId { get; set; }
        public string? Name { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Guid>? BranchIds { get; set; }
        public List<string>? ItemCodes { get; set; }
    }

    /// <summary>إنشاء أو تعديل جرد مخصص. الأصناف برقمها أو باركودها، وتُحوَّل العبوة لصنف الوحدة الأساسية.</summary>
    public async Task<object> SaveCustomInventoryAsync(PosDeviceContext actor, CustomInventoryRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 200) throw new InventoryException("اسم الجرد المخصص مطلوب (حتى 200 حرف).");
        var branchIds = (request.BranchIds ?? []).Distinct().ToList();
        if (branchIds.Count == 0) throw new InventoryException("يرجى اختيار فرع واحد على الأقل.");
        var codes = (request.ItemCodes ?? []).Select(code => code.Trim()).Where(code => code.Length > 0).Distinct().ToList();
        if (codes.Count == 0) throw new InventoryException("يرجى إضافة صنف واحد على الأقل.");
        if (codes.Count > 500) throw new InventoryException("أقصى عدد للأصناف في جرد مخصص 500.");

        await using var central = await shortages.CentralAsync(cancellationToken);
        var known = (await BranchesAsync(central, actor, cancellationToken)).Select(branch => branch.BranchId).ToHashSet();
        if (branchIds.Any(branch => !known.Contains(branch))) throw new InventoryException("فرع غير موجود أو غير نشط.");

        var resolved = new Dictionary<Guid, (string Number, string? Name)>();
        var missing = new List<string>();
        foreach (var code in codes)
        {
            var owner = await InventoryCountService.ResolveOwnerAsync(central, null, code, cancellationToken);
            if (owner is null)
            {
                missing.Add(code);
                continue;
            }

            await using var read = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @item;", central);
            Id(read, "@item", owner.OwnerItemId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) resolved[owner.OwnerItemId] = (reader.GetString(0), Str(reader, 1));
        }

        if (missing.Count > 0) throw new InventoryException($"أصناف غير موجودة: {string.Join("، ", missing.Take(10))}");

        var id = request.CustomInventoryId ?? Guid.NewGuid();
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(central, transaction,
                """
                UPDATE dbo.CustomInventories SET InventoryName = @name, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
                WHERE CustomInventoryID = @id AND BusinessID = @business;
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.CustomInventories (CustomInventoryID, BusinessID, InventoryName, IsActive, CreatedByName)
                    VALUES (@id, @business, @name, @active, @by);
                DELETE FROM dbo.CustomInventoryBranches WHERE CustomInventoryID = @id;
                DELETE FROM dbo.CustomInventoryItems WHERE CustomInventoryID = @id;
                """,
                command =>
                {
                    Id(command, "@id", id);
                    Id(command, "@business", actor.BusinessId);
                    Text(command, "@name", name, 200);
                    command.Parameters.Add("@active", SqlDbType.Bit).Value = request.IsActive;
                    Text(command, "@by", actor.DisplayName, 200);
                },
                cancellationToken);
            foreach (var branch in branchIds)
            {
                await ExecuteAsync(central, transaction, "INSERT INTO dbo.CustomInventoryBranches (CustomInventoryID, BranchID) VALUES (@id, @branch);",
                    command =>
                    {
                        Id(command, "@id", id);
                        Id(command, "@branch", branch);
                    },
                    cancellationToken);
            }

            foreach (var (itemId, item) in resolved)
            {
                await ExecuteAsync(central, transaction,
                    "INSERT INTO dbo.CustomInventoryItems (CustomInventoryID, ItemID, ItemNumber, ItemName) VALUES (@id, @item, @number, @name);",
                    command =>
                    {
                        Id(command, "@id", id);
                        Id(command, "@item", itemId);
                        Text(command, "@number", item.Number, 100);
                        Text(command, "@name", item.Name, 500);
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await CustomInventoriesAsync(actor, cancellationToken);
    }

    // ------------------------------------------------------------------
    // التدقيق: طلبات تأكيد الاستلام
    // ------------------------------------------------------------------

    public async Task<object> AuditOptionsAsync(PosDeviceContext actor, Guid? branchId, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        var branches = (await BranchesAsync(central, actor, cancellationToken)).Select(branch => new { branchId = branch.BranchId, branchName = branch.BranchName, current = branch.Current });
        var users = branchId is Guid branch ? await UsersAsync(central, branch, cancellationToken) : new List<object>();
        return new { branches, users };
    }

    public sealed class AuditRequestBody
    {
        public Guid? BranchId { get; set; }
        public Guid? RecipientUserId { get; set; }
        public string? Note { get; set; }
        public List<AuditRequestItem>? Items { get; set; }

        /// <summary>تدقيق معالجة النواقص (كما في النظام القديم): يوم المعالجة، والموظف، والمهام المختارة.</summary>
        public DateOnly? ProcessedDate { get; set; }
        public string? ProcessedByName { get; set; }
        public List<Guid>? TaskIds { get; set; }
    }

    private sealed record AuditTask(Guid TaskId, Guid ItemId, string ItemNumber, string? ItemName, string TaskType, decimal? Quantity, string ProcessedBy);

    /// <summary>
    /// المهام المنجزة في يوم (شراء، مخزن، موازنة) مجمّعة بالموظف الذي أنجزها، لاختيار أصناف التدقيق.
    /// المهمة التي دخلت تدقيقاً غير ملغى لا تظهر ثانية.
    /// </summary>
    private static async Task<List<AuditTask>> AuditTasksAsync(SqlConnection central, SqlTransaction? transaction, Guid business, DateOnly day, CancellationToken cancellationToken)
    {
        var tasks = new List<AuditTask>();
        await using var command = new SqlCommand(
            """
            SELECT task.TaskID, task.ItemID, task.ItemNumber, task.ItemName, task.TaskType, task.Quantity, task.CompletedByName
            FROM dbo.ShortageTasks AS task
            WHERE task.BusinessID = @business AND task.Status = N'Done'
              AND task.TaskType IN (N'Purchase', N'Warehouse', N'Balance')
              AND task.CompletedAt >= @from AND task.CompletedAt < @to
              AND NULLIF(LTRIM(RTRIM(task.CompletedByName)), N'') IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.ShortageAuditRequestItems AS line
                              INNER JOIN dbo.ShortageAuditRequests AS request ON request.RequestID = line.RequestID
                              WHERE line.TaskID = task.TaskID AND request.Status <> N'Cancelled')
            ORDER BY task.CompletedByName, task.TaskType, task.ItemNumber;
            """,
            central,
            transaction);
        Id(command, "@business", business);
        command.Parameters.Add("@from", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(day);
        command.Parameters.Add("@to", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(day.AddDays(1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new AuditTask(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Str(reader, 3), reader.GetString(4), Dec(reader, 5), reader.GetString(6).Trim()));
        }

        return tasks;
    }

    /// <summary>«التدقيق» كما في النظام القديم: التاريخ ← الموظف الذي تتم مراجعة معالجته ← أصنافه المنجزة.</summary>
    public async Task<object> AuditCandidatesAsync(PosDeviceContext actor, DateOnly? date, CancellationToken cancellationToken)
    {
        var day = date ?? InventoryCalendar.Today();
        await using var central = await shortages.CentralAsync(cancellationToken);
        var tasks = await AuditTasksAsync(central, null, actor.BusinessId, day, cancellationToken);
        return new
        {
            date = day.ToString("yyyy-MM-dd"),
            employees = tasks
                .GroupBy(task => task.ProcessedBy, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    name = group.First().ProcessedBy,
                    items = group.Select(task => new
                    {
                        taskId = task.TaskId,
                        itemId = task.ItemId,
                        itemNumber = task.ItemNumber,
                        itemName = task.ItemName,
                        taskType = task.TaskType,
                        quantity = task.Quantity,
                    }),
                }),
        };
    }

    public sealed class AuditRequestItem
    {
        public string? Code { get; set; }
        public decimal? ExpectedQuantity { get; set; }
    }

    public async Task<object> CreateAuditRequestAsync(PosDeviceContext actor, AuditRequestBody body, CancellationToken cancellationToken)
    {
        var branch = body.BranchId ?? throw new InventoryException("يرجى اختيار الفرع.");
        var recipient = body.RecipientUserId ?? throw new InventoryException("يرجى اختيار المستلم.");
        if (body.TaskIds is { Count: > 0 })
        {
            return await CreateTaskAuditAsync(actor, body, branch, recipient, cancellationToken);
        }

        var lines = (body.Items ?? []).Where(line => !string.IsNullOrWhiteSpace(line.Code)).ToList();
        if (lines.Count is < ShortageRules.AuditMinItems or > ShortageRules.AuditMaxItems)
        {
            throw new InventoryException("طلب التدقيق من صنف واحد إلى 10 أصناف.");
        }

        await using var central = await shortages.CentralAsync(cancellationToken);
        var branchName = (await BranchesAsync(central, actor, cancellationToken)).FirstOrDefault(row => row.BranchId == branch)?.BranchName
            ?? throw new InventoryException("الفرع غير موجود أو غير نشط.");
        string recipientName;
        await using (var read = new SqlCommand("SELECT DisplayName FROM dbo.Users WHERE UserID = @user AND IsActive = 1;", central))
        {
            Id(read, "@user", recipient);
            recipientName = await read.ExecuteScalarAsync(cancellationToken) as string ?? throw new InventoryException("الموظف غير موجود أو موقوف.");
        }

        var items = new List<(Guid ItemId, string Number, string? Name, decimal Expected)>();
        foreach (var line in lines)
        {
            var expected = InventoryCountRules.ValidateCountedQuantity(line.ExpectedQuantity);
            var owner = await InventoryCountService.ResolveOwnerAsync(central, null, line.Code!.Trim(), cancellationToken)
                ?? throw new InventoryException($"الصنف {line.Code} غير موجود.");
            await using var read = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @item;", central);
            Id(read, "@item", owner.OwnerItemId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            items.Add((owner.OwnerItemId, reader.GetString(0), Str(reader, 1), expected));
        }

        var requestId = Guid.NewGuid();
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(central, transaction,
                """
                INSERT INTO dbo.ShortageAuditRequests
                    (RequestID, BusinessID, BranchID, BranchName, RecipientUserID, RecipientName, Note, CreatedByName, ExpiresAt)
                VALUES (@id, @business, @branch, @branchName, @recipient, @recipientName, @note, @by, DATEADD(DAY, @days, SYSUTCDATETIME()));
                """,
                command =>
                {
                    Id(command, "@id", requestId);
                    Id(command, "@business", actor.BusinessId);
                    Id(command, "@branch", branch);
                    Text(command, "@branchName", branchName, 200);
                    Id(command, "@recipient", recipient);
                    Text(command, "@recipientName", recipientName, 200);
                    Text(command, "@note", body.Note, 500);
                    Text(command, "@by", actor.DisplayName, 200);
                    command.Parameters.Add("@days", SqlDbType.Int).Value = ShortageRules.AuditValidDays;
                },
                cancellationToken);
            foreach (var item in items)
            {
                await ExecuteAsync(central, transaction,
                    """
                    INSERT INTO dbo.ShortageAuditRequestItems (RequestItemID, RequestID, ItemID, ItemNumber, ItemName, ExpectedQuantity)
                    VALUES (NEWID(), @id, @item, @number, @name, @expected);
                    """,
                    command =>
                    {
                        Id(command, "@id", requestId);
                        Id(command, "@item", item.ItemId);
                        Text(command, "@number", item.Number, 100);
                        Text(command, "@name", item.Name, 500);
                        Num(command, "@expected", item.Expected);
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new { requestId };
    }

    /// <summary>
    /// «إنشاء طلب التدقيق والمتابعة» كما في النظام القديم: من صنف إلى 10 من المهام المنجزة للموظف في ذلك اليوم،
    /// وصنف على الأقل من كل نوع معالجة متاح، والمستلم يجيب «هل استلمت هذا الصنف؟» نعم/لا.
    /// </summary>
    private async Task<object> CreateTaskAuditAsync(PosDeviceContext actor, AuditRequestBody body, Guid branch, Guid recipient, CancellationToken cancellationToken)
    {
        var day = body.ProcessedDate ?? throw new InventoryException("يرجى اختيار التاريخ.");
        var processedBy = body.ProcessedByName?.Trim();
        if (string.IsNullOrEmpty(processedBy)) throw new InventoryException("يرجى اختيار الموظف الذي تتم مراجعة معالجته.");
        var wanted = body.TaskIds!.Distinct().ToList();

        await using var central = await shortages.CentralAsync(cancellationToken);
        var branchName = (await BranchesAsync(central, actor, cancellationToken)).FirstOrDefault(row => row.BranchId == branch)?.BranchName
            ?? throw new InventoryException("الفرع غير موجود أو غير نشط.");
        string recipientName;
        await using (var read = new SqlCommand("SELECT DisplayName FROM dbo.Users WHERE UserID = @user AND IsActive = 1;", central))
        {
            Id(read, "@user", recipient);
            recipientName = await read.ExecuteScalarAsync(cancellationToken) as string ?? throw new InventoryException("المستلم غير موجود أو موقوف.");
        }

        var requestId = Guid.NewGuid();
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var mine = (await AuditTasksAsync(central, transaction, actor.BusinessId, day, cancellationToken))
                .Where(task => string.Equals(task.ProcessedBy, processedBy, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var chosen = mine.Where(task => wanted.Contains(task.TaskId)).ToList();
            if (chosen.Count != wanted.Count) throw new InventoryException("بعض الأصناف المختارة ليست من معالجة هذا الموظف في هذا اليوم، أو دخلت تدقيقاً سابقاً.");
            ShortageRules.RequireAuditSelection(
                chosen.Select(task => task.TaskType).ToList(),
                mine.Select(task => task.TaskType).Distinct().ToList());

            await ExecuteAsync(central, transaction,
                """
                INSERT INTO dbo.ShortageAuditRequests
                    (RequestID, BusinessID, BranchID, BranchName, RecipientUserID, RecipientName, Note, CreatedByName, ExpiresAt,
                     ProcessedByName, ProcessedDate)
                VALUES (@id, @business, @branch, @branchName, @recipient, @recipientName, @note, @by, DATEADD(DAY, @days, SYSUTCDATETIME()),
                        @processedBy, @day);
                """,
                command =>
                {
                    Id(command, "@id", requestId);
                    Id(command, "@business", actor.BusinessId);
                    Id(command, "@branch", branch);
                    Text(command, "@branchName", branchName, 200);
                    Id(command, "@recipient", recipient);
                    Text(command, "@recipientName", recipientName, 200);
                    Text(command, "@note", body.Note, 500);
                    Text(command, "@by", actor.DisplayName, 200);
                    command.Parameters.Add("@days", SqlDbType.Int).Value = ShortageRules.AuditValidDays;
                    Text(command, "@processedBy", processedBy, 200);
                    command.Parameters.Add("@day", SqlDbType.Date).Value = day.ToDateTime(TimeOnly.MinValue);
                },
                cancellationToken);
            foreach (var task in chosen)
            {
                await ExecuteAsync(central, transaction,
                    """
                    INSERT INTO dbo.ShortageAuditRequestItems (RequestItemID, RequestID, ItemID, ItemNumber, ItemName, ExpectedQuantity, TaskID, TaskType)
                    VALUES (NEWID(), @id, @item, @number, @name, @expected, @task, @type);
                    """,
                    command =>
                    {
                        Id(command, "@id", requestId);
                        Id(command, "@item", task.ItemId);
                        Text(command, "@number", task.ItemNumber, 100);
                        Text(command, "@name", task.ItemName, 500);
                        Num(command, "@expected", task.Quantity ?? 0m);
                        Id(command, "@task", task.TaskId);
                        Text(command, "@type", task.TaskType, 20);
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new { requestId };
    }

    /// <summary>
    /// طلبات التدقيق. mineOnly: طلبات هذا الموظف فقط (ليرد عليها، أو ليرى درجاته).
    /// التوقعات تبقى مخفية عن المستلم حتى يرد، فالرد أعمى.
    /// </summary>
    public async Task<object> AuditRequestsAsync(PosDeviceContext actor, bool mineOnly, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = ShortageRules.Range(from, to, InventoryCalendar.Today(), 30);
        await using var central = await shortages.CentralAsync(cancellationToken);
        var lines = new Dictionary<Guid, List<object>>();
        var requests = new List<(Guid Id, Guid Recipient, string Status, object Row)>();
        await using (var command = new SqlCommand(
            """
            SELECT RequestID, BranchName, RecipientUserID, RecipientName, Note, Status, CreatedByName, CreatedAt, ExpiresAt, AnsweredAt, Score,
                   ProcessedByName, ProcessedDate
            FROM dbo.ShortageAuditRequests
            WHERE BusinessID = @business AND (@user IS NULL OR RecipientUserID = @user)
              AND (Status = N'Pending' OR (CreatedAt >= @from AND CreatedAt < @to))
            ORDER BY CASE WHEN Status = N'Pending' THEN 0 ELSE 1 END, CreatedAt DESC;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            NullableGuid(command, "@user", mineOnly ? actor.UserId : null);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(start);
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(end.AddDays(1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var now = DateTime.UtcNow;
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var status = reader.GetString(5);
                var expires = Utc(reader.GetDateTime(8));
                requests.Add((id, reader.GetGuid(2), status, new
                {
                    requestId = id,
                    branchName = Str(reader, 1),
                    recipientUserId = reader.GetGuid(2),
                    recipientName = Str(reader, 3),
                    note = Str(reader, 4),
                    status,
                    expired = status == "Pending" && expires < now,
                    createdByName = Str(reader, 6),
                    createdAt = Utc(reader.GetDateTime(7)),
                    expiresAt = expires,
                    answeredAt = UtcOrNull(reader, 9),
                    score = Dec(reader, 10),
                    processedByName = Str(reader, 11),
                    processedDate = reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)).ToString("yyyy-MM-dd"),
                }));
            }
        }

        await using (var command = new SqlCommand(
            """
            SELECT line.RequestID, line.RequestItemID, line.ItemNumber, line.ItemName, line.ExpectedQuantity, line.ReceivedQuantity, request.Status,
                   line.TaskType, line.Received
            FROM dbo.ShortageAuditRequestItems AS line
            INNER JOIN dbo.ShortageAuditRequests AS request ON request.RequestID = line.RequestID
            WHERE request.BusinessID = @business AND (@user IS NULL OR request.RecipientUserID = @user)
            ORDER BY line.ItemNumber;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            NullableGuid(command, "@user", mineOnly ? actor.UserId : null);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var answered = reader.GetString(6) == "Answered";
                if (!lines.TryGetValue(id, out var list)) lines[id] = list = [];
                list.Add(new
                {
                    requestItemId = reader.GetGuid(1),
                    itemNumber = reader.GetString(2),
                    itemName = Str(reader, 3),
                    // المستلم لا يرى المتوقع قبل أن يرد.
                    expectedQuantity = answered || !mineOnly ? reader.GetDecimal(4) : (decimal?)null,
                    receivedQuantity = Dec(reader, 5),
                    taskType = Str(reader, 7),
                    received = reader.IsDBNull(8) ? (bool?)null : reader.GetBoolean(8),
                });
            }
        }

        return new
        {
            from = start.ToString("yyyy-MM-dd"),
            to = end.ToString("yyyy-MM-dd"),
            requests = requests.Select(request => new { request.Row, items = lines.GetValueOrDefault(request.Id) ?? new List<object>() })
                .Select(entry => MergeItems(entry.Row, entry.items)),
        };
    }

    private static Dictionary<string, object?> MergeItems(object row, List<object> items)
    {
        var merged = row.GetType().GetProperties().ToDictionary(property => property.Name, property => property.GetValue(row));
        merged["items"] = items;
        return merged;
    }

    public sealed class AuditAnswer
    {
        public Guid? RequestItemId { get; set; }
        public decimal? ReceivedQuantity { get; set; }

        /// <summary>كما في النظام القديم: «هل استلمت هذا الصنف؟» نعم/لا.</summary>
        public bool? Received { get; set; }
    }

    public async Task<object> AnswerAuditAsync(PosDeviceContext actor, Guid requestId, List<AuditAnswer>? answers, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using (var check = new SqlCommand(
                "SELECT Status, RecipientUserID, ExpiresAt FROM dbo.ShortageAuditRequests WITH (UPDLOCK) WHERE RequestID = @id AND BusinessID = @business;",
                central,
                transaction))
            {
                Id(check, "@id", requestId);
                Id(check, "@business", actor.BusinessId);
                await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new InventoryException("طلب التدقيق غير موجود.", StatusCodes.Status404NotFound);
                if (reader.GetGuid(1) != actor.UserId) throw new InventoryException("هذا الطلب لموظف آخر.", StatusCodes.Status403Forbidden);
                if (reader.GetString(0) != "Pending") throw new InventoryException("تم الرد على هذا الطلب.", StatusCodes.Status409Conflict);
                if (Utc(reader.GetDateTime(2)) < DateTime.UtcNow) throw new InventoryException("انتهت صلاحية هذا الطلب (7 أيام).", StatusCodes.Status409Conflict);
            }

            var expected = new Dictionary<Guid, decimal>();
            await using (var read = new SqlCommand("SELECT RequestItemID, ExpectedQuantity FROM dbo.ShortageAuditRequestItems WHERE RequestID = @id;", central, transaction))
            {
                Id(read, "@id", requestId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) expected[reader.GetGuid(0)] = reader.GetDecimal(1);
            }

            var all = (answers ?? []).Where(answer => answer.RequestItemId is not null).ToList();
            if (all.Count > 0 && all.All(answer => answer.Received is not null))
            {
                // نعم/لا: الإجابة على كل الأصناف، والدرجة نسبة ما أكّد استلامه.
                var yesNo = all.ToDictionary(answer => answer.RequestItemId!.Value, answer => answer.Received!.Value);
                if (expected.Keys.Any(key => !yesNo.ContainsKey(key))) throw new InventoryException("أجب عن كل الأصناف بنعم أو لا.");
                foreach (var (lineId, quantity) in expected)
                {
                    await ExecuteAsync(central, transaction,
                        "UPDATE dbo.ShortageAuditRequestItems SET Received = @received, ReceivedQuantity = @quantity WHERE RequestItemID = @line;",
                        command =>
                        {
                            Id(command, "@line", lineId);
                            command.Parameters.Add("@received", SqlDbType.Bit).Value = yesNo[lineId];
                            Num(command, "@quantity", yesNo[lineId] ? quantity : 0m);
                        },
                        cancellationToken);
                }

                var yesScore = ShortageRules.AuditYesNoScore(yesNo.Values.Count(value => value), expected.Count);
                await ExecuteAsync(central, transaction,
                    "UPDATE dbo.ShortageAuditRequests SET Status = N'Answered', AnsweredAt = SYSUTCDATETIME(), Score = @score WHERE RequestID = @id;",
                    command =>
                    {
                        Id(command, "@id", requestId);
                        var parameter = command.Parameters.Add("@score", SqlDbType.Decimal);
                        parameter.Precision = 4;
                        parameter.Scale = 1;
                        parameter.Value = yesScore;
                    },
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new { score = yesScore };
            }

            var given = all.ToDictionary(answer => answer.RequestItemId!.Value, answer => answer.ReceivedQuantity);
            if (expected.Keys.Any(key => !given.ContainsKey(key))) throw new InventoryException("أدخل الكمية المستلمة لكل الأصناف.");
            var scored = new List<(decimal, decimal)>();
            foreach (var (lineId, quantity) in expected)
            {
                var received = InventoryCountRules.ValidateCountedQuantity(given[lineId]);
                scored.Add((quantity, received));
                await ExecuteAsync(central, transaction, "UPDATE dbo.ShortageAuditRequestItems SET ReceivedQuantity = @received WHERE RequestItemID = @line;",
                    command =>
                    {
                        Id(command, "@line", lineId);
                        Num(command, "@received", received);
                    },
                    cancellationToken);
            }

            var score = ShortageRules.AuditScore(scored);
            await ExecuteAsync(central, transaction,
                "UPDATE dbo.ShortageAuditRequests SET Status = N'Answered', AnsweredAt = SYSUTCDATETIME(), Score = @score WHERE RequestID = @id;",
                command =>
                {
                    Id(command, "@id", requestId);
                    var parameter = command.Parameters.Add("@score", SqlDbType.Decimal);
                    parameter.Precision = 4;
                    parameter.Scale = 1;
                    parameter.Value = score;
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new { score };
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<object> CancelAuditAsync(PosDeviceContext actor, Guid requestId, CancellationToken cancellationToken)
    {
        await using var central = await shortages.CentralAsync(cancellationToken);
        await ExecuteAsync(central, null,
            "UPDATE dbo.ShortageAuditRequests SET Status = N'Cancelled' WHERE RequestID = @id AND BusinessID = @business AND Status = N'Pending';",
            command =>
            {
                Id(command, "@id", requestId);
                Id(command, "@business", actor.BusinessId);
            },
            cancellationToken);
        return new { cancelled = true };
    }

    // ------------------------------------------------------------------
    // تقرير الكفاءة وتقرير معالجة النواقص
    // ------------------------------------------------------------------

    /// <summary>درجة كل موظف من 10 في الفترة. ownOnly: الكاشير يرى نفسه فقط.</summary>
    public async Task<object> EfficiencyAsync(PosDeviceContext actor, bool ownOnly, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = ShortageRules.Range(from, to, InventoryCalendar.Today(), 30);
        await using var central = await shortages.CentralAsync(cancellationToken);
        var rows = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT RecipientUserID, MAX(RecipientName), COUNT(*),
                   SUM(CASE WHEN Status = N'Answered' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Status = N'Pending' THEN 1 ELSE 0 END),
                   AVG(CASE WHEN Status = N'Answered' THEN Score END)
            FROM dbo.ShortageAuditRequests
            WHERE BusinessID = @business AND Status <> N'Cancelled' AND CreatedAt >= @from AND CreatedAt < @to
              AND (@user IS NULL OR RecipientUserID = @user)
            GROUP BY RecipientUserID
            ORDER BY AVG(CASE WHEN Status = N'Answered' THEN Score END) DESC;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            NullableGuid(command, "@user", ownOnly ? actor.UserId : null);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(start);
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(end.AddDays(1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new
                {
                    userId = reader.GetGuid(0),
                    userName = Str(reader, 1),
                    requests = reader.GetInt32(2),
                    answered = reader.GetInt32(3),
                    pending = reader.GetInt32(4),
                    averageScore = reader.IsDBNull(5) ? (decimal?)null : Math.Round(reader.GetDecimal(5), 1),
                });
            }
        }

        return new { from = start.ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd"), ownOnly, employees = rows };
    }

    public async Task<object> ProcessingReportAsync(PosDeviceContext actor, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = ShortageRules.Range(from, to, InventoryCalendar.Today(), 30);
        await using var central = await shortages.CentralAsync(cancellationToken);
        var runs = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT run.RunID, run.ShortageType, run.SupplyDays, run.Status, run.CreatedByName, run.CreatedAt, run.CompletedAt, run.ItemCount,
                   (SELECT COUNT(*) FROM dbo.ShortageRunItems WHERE RunID = run.RunID AND IsProcessed = 1),
                   (SELECT COUNT(*) FROM dbo.ShortageTasks WHERE RunID = run.RunID),
                   STUFF((SELECT N'، ' + target.BranchName FROM dbo.ShortageRunBranches AS target WHERE target.RunID = run.RunID
                          ORDER BY target.BranchName FOR XML PATH(N'')), 1, 2, N'')
            FROM dbo.ShortageRuns AS run
            WHERE run.BusinessID = @business AND run.Status <> N'Discarded' AND run.CreatedAt >= @from AND run.CreatedAt < @to
            ORDER BY run.CreatedAt DESC;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            command.Parameters.Add("@from", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(start);
            command.Parameters.Add("@to", SqlDbType.DateTime2).Value = InventoryCalendar.StartOfDayUtc(end.AddDays(1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetString(1);
                runs.Add(new
                {
                    runId = reader.GetGuid(0),
                    shortageTypeLabel = ShortageTypes.Label(type),
                    supplyDays = reader.GetInt32(2),
                    status = reader.GetString(3),
                    createdByName = Str(reader, 4),
                    createdAt = Utc(reader.GetDateTime(5)),
                    completedAt = UtcOrNull(reader, 6),
                    itemCount = reader.GetInt32(7),
                    processedCount = reader.GetInt32(8),
                    tasks = reader.GetInt32(9),
                    branches = Str(reader, 10),
                });
            }
        }

        var overdue = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT task.ItemNumber, task.ItemName, task.Quantity, task.AssignedAt, task.AssignedByName, supplier.SupplierName
            FROM dbo.ShortageTasks AS task
            LEFT JOIN dbo.SupplyRequests AS request ON request.RequestID = task.SupplyRequestID
            LEFT JOIN dbo.Suppliers AS supplier ON supplier.SupplierID = request.SupplierID
            WHERE task.BusinessID = @business AND task.TaskType = N'Purchase' AND task.Status = N'Open'
              AND task.AssignedAt < DATEADD(DAY, -@days, SYSUTCDATETIME())
            ORDER BY task.AssignedAt;
            """,
            central))
        {
            Id(command, "@business", actor.BusinessId);
            command.Parameters.Add("@days", SqlDbType.Int).Value = ShortageRules.OverduePurchaseDays;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                overdue.Add(new
                {
                    itemNumber = reader.GetString(0),
                    itemName = Str(reader, 1),
                    quantity = Dec(reader, 2),
                    assignedAt = Utc(reader.GetDateTime(3)),
                    assignedByName = Str(reader, 4),
                    supplierName = Str(reader, 5),
                });
            }
        }

        return new { from = start.ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd"), runs, overduePurchases = overdue, overdueDays = ShortageRules.OverduePurchaseDays };
    }
}
