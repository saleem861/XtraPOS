using System.Data;
using Microsoft.Data.SqlClient;
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
            SELECT CustomInventoryID, InventoryName, IsActive, CreatedByName, CreatedAt
            FROM dbo.CustomInventories WHERE BusinessID = @business ORDER BY IsActive DESC, InventoryName;
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
                    branchIds = branches,
                    branchNames = branches.Select(branch => branchNames.GetValueOrDefault(branch) ?? "—").ToArray(),
                    items = items.GetValueOrDefault(id) ?? new List<object>(),
                });
            }
        }

        var options = (await BranchesAsync(central, actor, cancellationToken)).Select(branch => new { branchId = branch.BranchId, branchName = branch.BranchName, current = branch.Current });
        return new { customInventories = rows, branches = options };
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
    }

    public sealed class AuditRequestItem
    {
        public string? Code { get; set; }
        public decimal? ExpectedQuantity { get; set; }
    }

    public async Task<object> CreateAuditRequestAsync(PosDeviceContext actor, AuditRequestBody body, CancellationToken cancellationToken)
    {
        var branch = body.BranchId ?? throw new InventoryException("يرجى اختيار الفرع.");
        var recipient = body.RecipientUserId ?? throw new InventoryException("يرجى اختيار الموظف.");
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
            SELECT RequestID, BranchName, RecipientUserID, RecipientName, Note, Status, CreatedByName, CreatedAt, ExpiresAt, AnsweredAt, Score
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
                }));
            }
        }

        await using (var command = new SqlCommand(
            """
            SELECT line.RequestID, line.RequestItemID, line.ItemNumber, line.ItemName, line.ExpectedQuantity, line.ReceivedQuantity, request.Status
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

            var given = (answers ?? []).Where(answer => answer.RequestItemId is not null).ToDictionary(answer => answer.RequestItemId!.Value, answer => answer.ReceivedQuantity);
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
