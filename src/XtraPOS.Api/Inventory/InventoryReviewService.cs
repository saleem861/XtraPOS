using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Inventory;

/// <summary>
/// المرحلة 2 من وحدة الجرد والنواقص، على فرع الجهاز:
/// - «تقرير الجرد»: جلسات الفرع وفروقاتها، ومنه «تدقيق المشرف» الأعمى (لا يغيّر المخزون).
/// - «معالجة الأصناف»: تغيير التصنيف (يُكتب في المركز ثم نسخة الفرع)، و«كمية صنف» مع التصحيح
///   (حركة InventoryReconciliation واحدة عبر StockPostingService، ومعرّف التصحيح هو سطر المصدر).
/// قرار المالك (25 نوفمبر 2026): في النظام المركزي الجلسات والتدقيق والرصيد على المركز (المرجع)، و«تصحيح» الكمية
/// يُثبَّت على المركز أولاً بالفارق ويأخذه الفرع في أول مزامنة. الجهاز المنفرد (بلا مركز) يعمل على قاعدته.
/// </summary>
public sealed class InventoryReviewService(
    InventoryCountService counting,
    StockPostingService stock)
{
    private const string MigrationMissing = "قاعدة هذا الفرع لم تُحدَّث بعد لمراجعة الجرد (الترحيل 055).";
    private const string CentralUnavailable = "المركز غير متاح الآن. لم يُحفظ التصنيف، حاول لاحقاً.";
    private const string AuthorityUnavailable = "المركز غير متاح الآن. الجرد والكميات تعمل على المركز فقط، حاول عند عودة الاتصال.";

    // ------------------------------------------------------------------
    // تقرير الجرد
    // ------------------------------------------------------------------

    public async Task<object> ReportAsync(
        PosDeviceContext actor,
        Guid? branchId,
        bool allBranches,
        DateOnly? from,
        DateOnly? to,
        string? type,
        Guid? userId,
        string? status,
        CancellationToken cancellationToken)
    {
        var (start, end) = InventoryReviewRules.ReportRange(from, to, InventoryCalendar.Today());
        var normalizedType = string.IsNullOrWhiteSpace(type) ? null : InventoryTypes.Normalize(type)
            ?? throw new InventoryException("نوع الجرد غير صالح.");
        var normalizedStatus = status?.Trim() switch
        {
            null or "" => null,
            InventorySessionStatuses.Open => InventorySessionStatuses.Open,
            InventorySessionStatuses.Finalizing => InventorySessionStatuses.Finalizing,
            InventorySessionStatuses.Completed => InventorySessionStatuses.Completed,
            InventorySessionStatuses.Discarded => InventorySessionStatuses.Discarded,
            _ => throw new InventoryException("حالة الجرد غير صالحة."),
        };

        var branch = branchId ?? actor.BranchId;
        var remote = allBranches || branch != actor.BranchId;
        await using var connection = await OpenSessionsAsync(remote, cancellationToken);
        var branchScope = allBranches
            ? "SELECT listed.BranchID FROM dbo.Branches AS listed WHERE listed.IsActive = 1 AND listed.BusinessID = @business"
            : CustomInventoryScope.SameNameBranches("@branch");
        var sessions = new List<object>();
        await using (var command = new SqlCommand(
            $"""
            SELECT session.SessionID, session.BranchID, branch.BranchName, session.InventoryType, session.Status, session.UserID, session.UserName,
                   session.BusinessDate, session.StartedAt, session.LastSavedAt, session.CompletedAt,
                   session.TotalItems, session.CountedItems, session.PostedItems,
                   (SELECT COUNT(*) FROM dbo.InventorySessionItems AS line
                     WHERE line.SessionID = session.SessionID AND line.IsCounted = 1
                       AND (line.SystemQuantityKnown = 0 OR line.CountedQuantity <> line.SystemQuantityAtCount)) AS Differences,
                   (SELECT COUNT(*) FROM dbo.InventoryAudits AS audit
                     WHERE audit.SessionID = session.SessionID AND audit.Status = N'Completed') AS Audits,
                   STUFF((SELECT N',' + CONVERT(nvarchar(3), page.InventoryPage)
                          FROM dbo.InventorySessionPages AS page
                          WHERE page.SessionID = session.SessionID
                          ORDER BY page.InventoryPage
                          FOR XML PATH(N'')), 1, 1, N'') AS Pages
            FROM dbo.InventorySessions AS session
            INNER JOIN dbo.Branches AS branch ON branch.BranchID = session.BranchID
            WHERE session.BranchID IN ({branchScope})
              AND session.BusinessDate BETWEEN @from AND @to
              AND (@type IS NULL OR session.InventoryType = @type)
              AND (@user IS NULL OR session.UserID = @user)
              AND (@status IS NULL OR session.Status = @status)
              AND (@status IS NOT NULL OR session.Status <> N'Discarded')
            ORDER BY session.StartedAt DESC;
            """,
            connection))
        {
            command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.BusinessId;
            command.Parameters.Add("@from", SqlDbType.Date).Value = start.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@to", SqlDbType.Date).Value = end.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = (object?)normalizedType ?? DBNull.Value;
            command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = (object?)userId ?? DBNull.Value;
            command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = (object?)normalizedStatus ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var total = reader.GetInt32(11);
                var counted = reader.GetInt32(12);
                var sessionType = reader.GetString(3);
                sessions.Add(new
                {
                    sessionId = reader.GetGuid(0),
                    branchId = reader.GetGuid(1),
                    branchName = reader.GetString(2),
                    inventoryType = sessionType,
                    inventoryTypeLabel = InventoryTypes.Label(sessionType),
                    status = reader.GetString(4),
                    userId = reader.GetGuid(5),
                    userName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    businessDate = DateOnly.FromDateTime(reader.GetDateTime(7)).ToString("yyyy-MM-dd"),
                    startedAt = Utc(reader.GetDateTime(8)),
                    lastSavedAt = Utc(reader.GetDateTime(9)),
                    completedAt = reader.IsDBNull(10) ? (DateTime?)null : Utc(reader.GetDateTime(10)),
                    totalItems = total,
                    countedItems = counted,
                    postedItems = reader.GetInt32(13),
                    completionPercent = total > 0 ? Math.Round(counted * 100m / total, 0) : (counted > 0 ? 100m : 0m),
                    differences = reader.GetInt32(14),
                    audits = reader.GetInt32(15),
                    pages = reader.IsDBNull(16) ? Array.Empty<int>() : reader.GetString(16).Split(',').Select(int.Parse).ToArray(),
                });
            }
        }

        var employees = new List<object>();
        await using (var command = new SqlCommand(
            $"""
            SELECT UserID, MAX(UserName) AS UserName
            FROM dbo.InventorySessions
            WHERE BranchID IN ({branchScope}) AND BusinessDate BETWEEN @from AND @to
            GROUP BY UserID
            ORDER BY MAX(UserName);
            """,
            connection))
        {
            command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
            command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.BusinessId;
            command.Parameters.Add("@from", SqlDbType.Date).Value = start.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add("@to", SqlDbType.Date).Value = end.ToDateTime(TimeOnly.MinValue);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                employees.Add(new { userId = reader.GetGuid(0), userName = reader.IsDBNull(1) ? null : reader.GetString(1) });
            }
        }

        return new
        {
            branchId = allBranches ? (Guid?)null : branch,
            allBranches,
            remote,
            from = start.ToString("yyyy-MM-dd"),
            to = end.ToString("yyyy-MM-dd"),
            sessions,
            employees,
        };
    }

    public async Task<object> SessionDetailAsync(PosDeviceContext actor, Guid sessionId, Guid? branchId, CancellationToken cancellationToken)
    {
        var branch = branchId ?? actor.BranchId;
        var remote = branch != actor.BranchId;
        await using var connection = await OpenSessionsAsync(remote, cancellationToken);
        var header = await ReadSessionHeaderAsync(connection, null, branch, sessionId, cancellationToken);

        var items = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT SessionItemID, ItemID, ItemNumber, ItemName, IsCounted, CountedQuantity, SystemQuantityAtCount,
                   SystemQuantityKnown, CountedAt, CountedByName, StockMovementID, QuantityAfterPosting
            FROM dbo.InventorySessionItems
            WHERE SessionID = @id
            ORDER BY IsCounted DESC, ItemNumber;
            """,
            connection))
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var counted = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
                var known = reader.GetBoolean(7);
                var system = known && !reader.IsDBNull(6) ? reader.GetDecimal(6) : (decimal?)null;
                items.Add(new
                {
                    sessionItemId = reader.GetGuid(0),
                    itemId = reader.GetGuid(1),
                    itemNumber = reader.GetString(2),
                    itemName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    isCounted = reader.GetBoolean(4),
                    countedQuantity = counted,
                    expectedQuantity = system,
                    difference = counted is decimal c && system is decimal s ? c - s : (decimal?)null,
                    countedAt = reader.IsDBNull(8) ? (DateTime?)null : Utc(reader.GetDateTime(8)),
                    countedByName = reader.IsDBNull(9) ? null : reader.GetString(9),
                    posted = !reader.IsDBNull(10),
                    quantityAfterPosting = reader.IsDBNull(11) ? (decimal?)null : reader.GetDecimal(11),
                });
            }
        }

        var audits = await ReadAuditsAsync(connection, sessionId, cancellationToken);
        return new { session = header.View, items, audits, remote };
    }

    /// <summary>فروع التقرير: فرع الجهاز أولاً، ثم باقي فروع المركز مع وقت آخر نسخة وصلت منها.</summary>
    public async Task<object> BranchesAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        var branches = new List<object>();
        var central = await counting.OpenCentralAsync(cancellationToken);
        if (central is not null)
        {
            await using (central)
            {
                var hasStatus = await TableExistsAsync(central, "dbo.BranchSnapshotStatus", cancellationToken);
                await using var command = new SqlCommand(
                    hasStatus
                        ? """
                          SELECT branch.BranchID, branch.BranchName, status.LastPushAt
                          FROM dbo.Branches AS branch
                          LEFT JOIN dbo.BranchSnapshotStatus AS status ON status.BranchID = branch.BranchID
                          WHERE branch.IsActive = 1 AND branch.BusinessID = @business
                          ORDER BY branch.BranchName;
                          """
                        : """
                          SELECT branch.BranchID, branch.BranchName, CAST(NULL AS datetime2(0))
                          FROM dbo.Branches AS branch
                          WHERE branch.IsActive = 1 AND branch.BusinessID = @business
                          ORDER BY branch.BranchName;
                          """,
                    central);
                command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.BusinessId;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var lastPush = reader.IsDBNull(2) ? (DateTime?)null : Utc(reader.GetDateTime(2));
                    branches.Add(new
                    {
                        branchId = id,
                        branchName = reader.GetString(1),
                        current = id == actor.BranchId,
                        lastPushAt = lastPush,
                        stale = false,
                    });
                }
            }
        }

        return new { centralReachable = central is not null, branches };
    }

    /// <summary>
    /// مصدر الجلسات والتدقيق: في النظام المركزي المركز لكل الفروع (الجرد يجري عليه، والجلسات القديمة رُحّلت إليه)،
    /// وفي الجهاز المنفرد قاعدة الفرع (لفرع الجهاز فقط).
    /// </summary>
    private async Task<SqlConnection> OpenSessionsAsync(bool otherBranch, CancellationToken cancellationToken)
    {
        if (!await counting.CentralConfiguredAsync(cancellationToken))
        {
            if (otherBranch) throw new InventoryException("تقرير فرع آخر يحتاج نظاماً مركزياً.", StatusCodes.Status409Conflict);
            var local = await counting.OpenLocalAsync(cancellationToken);
            try
            {
                await RequireSchemaAsync(local, cancellationToken);
                return local;
            }
            catch
            {
                await local.DisposeAsync();
                throw;
            }
        }

        var central = await counting.OpenCentralAsync(cancellationToken)
            ?? throw new InventoryException(AuthorityUnavailable, StatusCodes.Status503ServiceUnavailable);
        if (!await TableExistsAsync(central, "dbo.InventorySessionItems", cancellationToken))
        {
            await central.DisposeAsync();
            throw new InventoryException("المركز لم يُحدَّث بعد لجرد الفروع (الترحيل 040).", StatusCodes.Status503ServiceUnavailable);
        }

        return central;
    }

    /// <summary>المركز في النظام المركزي (مرجع الكمية)، أو null للجهاز المنفرد.</summary>
    private async Task<SqlConnection?> OpenAuthorityAsync(CancellationToken cancellationToken)
    {
        if (!await counting.CentralConfiguredAsync(cancellationToken)) return null;
        var central = await counting.OpenCentralAsync(cancellationToken)
            ?? throw new InventoryException(AuthorityUnavailable, StatusCodes.Status503ServiceUnavailable);
        try
        {
            await CentralStock.RequireReadyAsync(central, null, cancellationToken);
            return central;
        }
        catch
        {
            await central.DisposeAsync();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // تدقيق المشرف
    // ------------------------------------------------------------------

    public async Task<object> StartAuditAsync(
        PosDeviceContext actor,
        Guid sessionId,
        string? mode,
        IReadOnlyCollection<Guid>? sessionItemIds,
        CancellationToken cancellationToken)
    {
        var normalized = InventoryAuditModes.Normalize(mode) ?? throw new InventoryException("نوع التدقيق غير صالح.");
        await using var connection = await OpenSessionsAsync(false, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var header = await ReadSessionHeaderAsync(connection, transaction, actor.BranchId, sessionId, cancellationToken);
            if (header.Status != InventorySessionStatuses.Completed)
            {
                throw new InventoryException("يُدقق الجرد بعد تثبيته فقط.", StatusCodes.Status409Conflict);
            }

            // تدقيق مفتوح لنفس المشرف على نفس الجرد يُستأنف بدل إنشاء آخر.
            await using (var open = new SqlCommand(
                "SELECT TOP (1) AuditID FROM dbo.InventoryAudits WHERE SessionID = @id AND UserID = @user AND Status = N'Open' ORDER BY StartedAt DESC;",
                connection,
                transaction))
            {
                open.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                open.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
                if (await open.ExecuteScalarAsync(cancellationToken) is Guid existing)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return await AuditAsync(actor, existing, cancellationToken);
                }
            }

            var candidates = new List<AuditCandidate>();
            await using (var read = new SqlCommand(
                "SELECT SessionItemID, CountedQuantity, SystemQuantityAtCount, SystemQuantityKnown, IsCounted FROM dbo.InventorySessionItems WHERE SessionID = @id;",
                connection,
                transaction))
            {
                read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    candidates.Add(new AuditCandidate(
                        reader.GetGuid(0),
                        reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                        reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4)));
                }
            }

            var chosen = InventoryReviewRules.SelectAuditLines(normalized, candidates, sessionItemIds);
            var auditId = Guid.NewGuid();
            await using (var insert = new SqlCommand(
                """
                INSERT INTO dbo.InventoryAudits
                    (AuditID, SessionID, BranchID, UserID, UserName, AuditMode, Status, StartedAt, TotalItems, CountedItems, MatchedItems)
                VALUES (@audit, @session, @branch, @user, @name, @mode, N'Open', SYSUTCDATETIME(), @total, 0, 0);
                """,
                connection,
                transaction))
            {
                insert.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
                insert.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
                insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
                insert.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
                insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = (object?)Limit(actor.DisplayName, 200) ?? DBNull.Value;
                insert.Parameters.Add("@mode", SqlDbType.NVarChar, 20).Value = normalized;
                insert.Parameters.Add("@total", SqlDbType.Int).Value = chosen.Count;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var lineId in chosen)
            {
                await using var line = new SqlCommand(
                    """
                    INSERT INTO dbo.InventoryAuditItems (AuditItemID, AuditID, SessionItemID, ItemID, ItemNumber, ItemName, IsCounted)
                    SELECT NEWID(), @audit, SessionItemID, ItemID, ItemNumber, ItemName, 0
                    FROM dbo.InventorySessionItems WHERE SessionItemID = @line;
                    """,
                    connection,
                    transaction);
                line.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
                line.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = lineId;
                await line.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return await AuditAsync(actor, auditId, cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>أثناء التدقيق: عدّ المشرف فقط (أعمى). بعد الإنهاء: النظام، الموظف، المشرف، والفارق.</summary>
    public async Task<object> AuditAsync(PosDeviceContext actor, Guid auditId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenSessionsAsync(false, cancellationToken);
        var audit = await ReadAuditHeaderAsync(connection, null, actor, auditId, cancellationToken);
        var revealed = audit.Status == "Completed";
        var lines = new List<object>();
        await using (var command = new SqlCommand(
            """
            SELECT auditLine.AuditItemID, auditLine.ItemID, auditLine.ItemNumber, auditLine.ItemName,
                   auditLine.IsCounted, auditLine.AuditQuantity, auditLine.CountedAt,
                   line.CountedQuantity, line.SystemQuantityAtCount, line.SystemQuantityKnown, line.CountedByName
            FROM dbo.InventoryAuditItems AS auditLine
            INNER JOIN dbo.InventorySessionItems AS line ON line.SessionItemID = auditLine.SessionItemID
            WHERE auditLine.AuditID = @audit
            ORDER BY auditLine.IsCounted, auditLine.ItemNumber;
            """,
            connection))
        {
            command.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var auditQuantity = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
                var employee = reader.IsDBNull(7) ? (decimal?)null : reader.GetDecimal(7);
                var system = reader.GetBoolean(9) && !reader.IsDBNull(8) ? reader.GetDecimal(8) : (decimal?)null;
                lines.Add(new
                {
                    auditItemId = reader.GetGuid(0),
                    itemId = reader.GetGuid(1),
                    itemNumber = reader.GetString(2),
                    itemName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    isCounted = reader.GetBoolean(4),
                    auditQuantity,
                    countedAt = reader.IsDBNull(6) ? (DateTime?)null : Utc(reader.GetDateTime(6)),
                    employeeQuantity = revealed ? employee : null,
                    employeeName = revealed && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                    systemQuantity = revealed ? system : null,
                    difference = revealed ? InventoryReviewRules.AuditDifference(auditQuantity, employee) : null,
                    matched = revealed && auditQuantity is not null ? auditQuantity == employee : (bool?)null,
                });
            }
        }

        return new { audit = audit.View, lines };
    }

    public async Task<object> SaveAuditCountAsync(
        PosDeviceContext actor,
        Guid auditId,
        Guid auditItemId,
        decimal? quantity,
        CancellationToken cancellationToken)
    {
        var counted = InventoryCountRules.ValidateCountedQuantity(quantity);
        await using var connection = await OpenSessionsAsync(false, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var audit = await ReadAuditHeaderAsync(connection, transaction, actor, auditId, cancellationToken);
            RequireOwnOpenAudit(audit, actor);
            await using (var update = new SqlCommand(
                """
                UPDATE dbo.InventoryAuditItems
                SET IsCounted = 1, AuditQuantity = @quantity, CountedAt = SYSUTCDATETIME()
                WHERE AuditID = @audit AND AuditItemID = @line;
                """,
                connection,
                transaction))
            {
                update.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
                update.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = auditItemId;
                var parameter = update.Parameters.Add("@quantity", SqlDbType.Decimal);
                parameter.Precision = 18;
                parameter.Scale = 4;
                parameter.Value = counted;
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new InventoryException("هذا الصنف ليس ضمن هذا التدقيق.", StatusCodes.Status404NotFound);
                }
            }

            await UpdateAuditCountersAsync(connection, transaction, auditId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return await AuditAsync(actor, auditId, cancellationToken);
    }

    public async Task<object> CompleteAuditAsync(PosDeviceContext actor, Guid auditId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenSessionsAsync(false, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var audit = await ReadAuditHeaderAsync(connection, transaction, actor, auditId, cancellationToken);
            RequireOwnOpenAudit(audit, actor);
            var counted = await UpdateAuditCountersAsync(connection, transaction, auditId, cancellationToken);
            if (counted == 0)
            {
                throw new InventoryException("يرجى تدقيق صنف واحد على الأقل قبل الإنهاء.");
            }

            await using var complete = new SqlCommand(
                "UPDATE dbo.InventoryAudits SET Status = N'Completed', CompletedAt = SYSUTCDATETIME() WHERE AuditID = @audit AND Status = N'Open';",
                connection,
                transaction);
            complete.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
            await complete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return await AuditAsync(actor, auditId, cancellationToken);
    }

    // ------------------------------------------------------------------
    // معالجة الأصناف: التصنيف و«كمية صنف»
    // ------------------------------------------------------------------

    public async Task<object> ItemAsync(PosDeviceContext actor, string? code, CancellationToken cancellationToken)
    {
        var text = code?.Trim() ?? "";
        if (text.Length == 0) throw new InventoryException("يرجى إدخال رقم الصنف أو مسح الباركود.");
        await using var connection = await counting.OpenLocalAsync(cancellationToken);
        await RequireSchemaAsync(connection, cancellationToken);
        var resolved = await InventoryCountService.ResolveOwnerAsync(connection, null, text, cancellationToken)
            ?? throw new InventoryException("الصنف غير موجود.", StatusCodes.Status404NotFound);
        await RequireCountableAsync(connection, null, resolved.OwnerItemId, cancellationToken);
        await using var authority = await OpenAuthorityAsync(cancellationToken);
        return await ItemViewAsync(connection, authority, actor, resolved.OwnerItemId, resolved.ScannedItemNumber, cancellationToken);
    }

    public async Task<object> SaveClassificationAsync(
        PosDeviceContext actor,
        Guid itemId,
        bool isImportant,
        int? page,
        int? supplyDays,
        CancellationToken cancellationToken)
    {
        var (important, normalizedPage, days) = InventoryReviewRules.NormalizeClassification(isImportant, page, supplyDays);
        await using var connection = await counting.OpenLocalAsync(cancellationToken);
        await RequireSchemaAsync(connection, cancellationToken);
        await RequireCountableAsync(connection, null, itemId, cancellationToken);
        var itemNumber = await ReadItemNumberAsync(connection, null, itemId, cancellationToken);

        // المركز هو المرجع: الفروع الأخرى تسحب منه. بدونه لا يُحفظ، حتى لا يختلف الفرع عن المركز.
        var central = await counting.OpenCentralAsync(cancellationToken)
            ?? throw new InventoryException(CentralUnavailable, StatusCodes.Status503ServiceUnavailable);
        DateTime updatedAt;
        await using (central)
        {
            await using var write = new SqlCommand(
                """
                IF OBJECT_ID(N'dbo.ItemInventorySettings', N'U') IS NULL
                BEGIN
                    SELECT CAST(NULL AS datetime2(3));
                    RETURN;
                END;
                DECLARE @now datetime2(3) = SYSUTCDATETIME();
                UPDATE dbo.ItemInventorySettings
                SET IsImportant = @important, InventoryPage = @page, SupplyDays = @days, UpdatedAt = @now, UpdatedByName = @by
                WHERE ItemNumber = @number;
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.ItemInventorySettings (ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName)
                    VALUES (@number, @important, @page, @days, @now, @by);
                SELECT @now;
                """,
                central);
            AddClassification(write, itemNumber, important, normalizedPage, days, actor.DisplayName);
            updatedAt = await write.ExecuteScalarAsync(cancellationToken) is DateTime stamp
                ? stamp
                : throw new InventoryException(CentralUnavailable, StatusCodes.Status503ServiceUnavailable);
        }

        // نفس القيم ونفس الوقت في نسخة الفرع، فالسحب التالي لا يعيد كتابتها.
        await using (var local = new SqlCommand(
            """
            UPDATE dbo.ItemInventorySettings
            SET IsImportant = @important, InventoryPage = @page, SupplyDays = @days, UpdatedAt = @updated, UpdatedByName = @by
            WHERE ItemNumber = @number;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.ItemInventorySettings (ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName)
                VALUES (@number, @important, @page, @days, @updated, @by);
            """,
            connection))
        {
            AddClassification(local, itemNumber, important, normalizedPage, days, actor.DisplayName);
            local.Parameters.Add("@updated", SqlDbType.DateTime2).Value = updatedAt;
            await local.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var authority = await OpenAuthorityAsync(cancellationToken);
        return await ItemViewAsync(connection, authority, actor, itemId, itemNumber, cancellationToken);
    }

    public async Task<object> CorrectQuantityAsync(
        PosDeviceContext actor,
        Guid itemId,
        Guid correctionId,
        decimal? quantity,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (correctionId == Guid.Empty) throw new InventoryException("طلب التصحيح غير صالح.");
        var newQuantity = InventoryCountRules.ValidateCountedQuantity(quantity);
        var why = InventoryReviewRules.RequireReason(reason);
        await using (var check = await counting.OpenLocalAsync(cancellationToken))
        {
            await RequireSchemaAsync(check, cancellationToken);
            await RequireCountableAsync(check, null, itemId, cancellationToken);
        }

        // قرار المالك (25 نوفمبر 2026): في النظام المركزي يُثبَّت التصحيح على المركز أولاً بالفارق
        // (المعدودة − رصيد المركز الحالي)، والفرع يأخذه في أول مزامنة. بلا مركز متاح لا يُصحَّح.
        await using (var authority = await OpenAuthorityAsync(cancellationToken))
        {
            if (authority is not null)
            {
                await using (var transaction = (SqlTransaction)await authority.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
                {
                    try
                    {
                        await CentralStock.PostCorrectionAsync(
                            authority,
                            transaction,
                            new CentralStock.CorrectionInput(
                                actor.BranchId,
                                itemId,
                                correctionId,
                                StockSourceTypes.InventoryReconciliation,
                                correctionId,
                                newQuantity,
                                null,
                                why,
                                actor.UserId,
                                actor.DisplayName),
                            cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
                        throw;
                    }
                }

                await using var view = await counting.OpenLocalAsync(cancellationToken);
                return await ItemViewAsync(view, authority, actor, itemId, null, cancellationToken);
            }
        }

        await stock.InTransactionAsync(async (session, token) =>
        {
            await using (var existing = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.InventoryQuantityCorrections WHERE CorrectionID = @id;",
                session.Connection,
                session.Transaction))
            {
                existing.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = correctionId;
                if ((int)(await existing.ExecuteScalarAsync(token) ?? 0) > 0) return 0;
            }

            var result = await stock.SetAbsoluteQuantityAsync(
                new StockAbsoluteRequest(
                    actor.BranchId,
                    itemId,
                    StockSourceTypes.InventoryReconciliation,
                    correctionId,
                    correctionId,
                    newQuantity,
                    null,
                    DateTime.UtcNow,
                    actor.UserId),
                session,
                token);

            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.InventoryQuantityCorrections
                    (CorrectionID, BranchID, ItemID, ItemNumber, ItemName, PreviousQuantity, NewQuantity,
                     ExpectedQuantity, CountedQuantity, DeltaQuantity, Reason, UserID, UserName, StockMovementID)
                SELECT @id, @branch, item.ItemID, item.ItemNumber, item.ItemName, @previous, @new,
                       @previous, @new, @delta, @reason, @user, @userName, @movement
                FROM dbo.Items AS item WHERE item.ItemID = @item;
                """,
                session.Connection,
                session.Transaction);
            AddNullableDecimal(insert, "@delta", result.QuantityDelta);
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = correctionId;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            insert.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            AddNullableDecimal(insert, "@previous", result.PreviousQuantity);
            AddNullableDecimal(insert, "@new", newQuantity);
            insert.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = why;
            insert.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = actor.UserId;
            insert.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = (object?)Limit(actor.DisplayName, 200) ?? DBNull.Value;
            insert.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = result.StockMovementId;
            await insert.ExecuteNonQueryAsync(token);
            return 1;
        }, cancellationToken);

        await using var connection = await counting.OpenLocalAsync(cancellationToken);
        return await ItemViewAsync(connection, null, actor, itemId, null, cancellationToken);
    }

    // ------------------------------------------------------------------
    // مساعدات
    // ------------------------------------------------------------------

    private sealed record SessionHeader(Guid Id, string Status, object View);

    private sealed record AuditHeader(Guid Id, Guid SessionId, Guid UserId, string Status, object View);

    private static async Task<SessionHeader> ReadSessionHeaderAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid branch,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT InventoryType, Status, UserName, BusinessDate, StartedAt, CompletedAt, TotalItems, CountedItems, PostedItems
            FROM dbo.InventorySessions WHERE SessionID = @id AND BranchID = @branch;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InventoryException("جلسة الجرد غير موجودة في هذا الفرع.", StatusCodes.Status404NotFound);
        }

        var type = reader.GetString(0);
        var status = reader.GetString(1);
        return new SessionHeader(sessionId, status, new
        {
            sessionId,
            inventoryType = type,
            inventoryTypeLabel = InventoryTypes.Label(type),
            status,
            userName = reader.IsDBNull(2) ? null : reader.GetString(2),
            businessDate = DateOnly.FromDateTime(reader.GetDateTime(3)).ToString("yyyy-MM-dd"),
            startedAt = Utc(reader.GetDateTime(4)),
            completedAt = reader.IsDBNull(5) ? (DateTime?)null : Utc(reader.GetDateTime(5)),
            totalItems = reader.GetInt32(6),
            countedItems = reader.GetInt32(7),
            postedItems = reader.GetInt32(8),
        });
    }

    private static async Task<AuditHeader> ReadAuditHeaderAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PosDeviceContext actor,
        Guid auditId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT audit.SessionID, audit.UserID, audit.UserName, audit.AuditMode, audit.Status, audit.StartedAt,
                   audit.CompletedAt, audit.TotalItems, audit.CountedItems, audit.MatchedItems,
                   session.InventoryType, session.UserName, session.BusinessDate
            FROM dbo.InventoryAudits AS audit
            INNER JOIN dbo.InventorySessions AS session ON session.SessionID = audit.SessionID
            WHERE audit.AuditID = @audit AND audit.BranchID = @branch;
            """,
            connection,
            transaction);
        command.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InventoryException("التدقيق غير موجود في هذا الفرع.", StatusCodes.Status404NotFound);
        }

        var sessionId = reader.GetGuid(0);
        var userId = reader.GetGuid(1);
        var mode = reader.GetString(3);
        var status = reader.GetString(4);
        var sessionType = reader.GetString(10);
        return new AuditHeader(auditId, sessionId, userId, status, new
        {
            auditId,
            sessionId,
            userName = reader.IsDBNull(2) ? null : reader.GetString(2),
            mine = userId == actor.UserId,
            auditMode = mode,
            auditModeLabel = InventoryAuditModes.Label(mode),
            status,
            startedAt = Utc(reader.GetDateTime(5)),
            completedAt = reader.IsDBNull(6) ? (DateTime?)null : Utc(reader.GetDateTime(6)),
            totalItems = reader.GetInt32(7),
            countedItems = reader.GetInt32(8),
            matchedItems = reader.GetInt32(9),
            inventoryTypeLabel = InventoryTypes.Label(sessionType),
            employeeName = reader.IsDBNull(11) ? null : reader.GetString(11),
            businessDate = DateOnly.FromDateTime(reader.GetDateTime(12)).ToString("yyyy-MM-dd"),
        });
    }

    private static async Task<List<object>> ReadAuditsAsync(SqlConnection connection, Guid sessionId, CancellationToken cancellationToken)
    {
        var audits = new List<object>();
        await using var command = new SqlCommand(
            """
            SELECT AuditID, UserName, AuditMode, Status, StartedAt, CompletedAt, TotalItems, CountedItems, MatchedItems
            FROM dbo.InventoryAudits
            WHERE SessionID = @id AND Status <> N'Discarded'
            ORDER BY StartedAt DESC;
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = sessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mode = reader.GetString(2);
            audits.Add(new
            {
                auditId = reader.GetGuid(0),
                userName = reader.IsDBNull(1) ? null : reader.GetString(1),
                auditMode = mode,
                auditModeLabel = InventoryAuditModes.Label(mode),
                status = reader.GetString(3),
                startedAt = Utc(reader.GetDateTime(4)),
                completedAt = reader.IsDBNull(5) ? (DateTime?)null : Utc(reader.GetDateTime(5)),
                totalItems = reader.GetInt32(6),
                countedItems = reader.GetInt32(7),
                matchedItems = reader.GetInt32(8),
            });
        }

        return audits;
    }

    private static void RequireOwnOpenAudit(AuditHeader audit, PosDeviceContext actor)
    {
        if (audit.UserId != actor.UserId)
        {
            throw new InventoryException("هذا التدقيق لمشرف آخر.", StatusCodes.Status403Forbidden);
        }

        if (audit.Status != "Open")
        {
            throw new InventoryException("هذا التدقيق منتهٍ.", StatusCodes.Status409Conflict);
        }
    }

    private static async Task<int> UpdateAuditCountersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid auditId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE audit
            SET CountedItems = counts.Counted, MatchedItems = counts.Matched
            OUTPUT inserted.CountedItems
            FROM dbo.InventoryAudits AS audit
            CROSS APPLY (
                SELECT COUNT(CASE WHEN auditLine.IsCounted = 1 THEN 1 END) AS Counted,
                       COUNT(CASE WHEN auditLine.IsCounted = 1 AND auditLine.AuditQuantity = line.CountedQuantity THEN 1 END) AS Matched
                FROM dbo.InventoryAuditItems AS auditLine
                INNER JOIN dbo.InventorySessionItems AS line ON line.SessionItemID = auditLine.SessionItemID
                WHERE auditLine.AuditID = audit.AuditID
            ) AS counts
            WHERE audit.AuditID = @audit;
            """,
            connection,
            transaction);
        command.Parameters.Add("@audit", SqlDbType.UniqueIdentifier).Value = auditId;
        return await command.ExecuteScalarAsync(cancellationToken) is int counted ? counted : 0;
    }

    private static async Task RequireCountableAsync(SqlConnection connection, SqlTransaction? transaction, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM dbo.Items WHERE ItemID = @item AND IsActive = 1) THEN 0
                WHEN EXISTS (SELECT 1 FROM dbo.Items WHERE ParentItemID = @item) THEN 1
                WHEN EXISTS (SELECT 1 FROM dbo.ItemUnits WHERE ItemID = @item AND IsActive = 1
                             AND InternalUnitItemID IS NOT NULL AND InternalUnitItemID <> @item) THEN 1
                ELSE 2 END;
            """,
            connection,
            transaction);
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        var state = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (state == 0) throw new InventoryException("الصنف غير موجود أو موقوف.", StatusCodes.Status404NotFound);
        if (state == 1) throw new InventoryException("هذا الصنف لا يُجرد مباشرة. اختر صنف الوحدة الأساسية.", StatusCodes.Status409Conflict);
    }

    private static async Task<string> ReadItemNumberAsync(SqlConnection connection, SqlTransaction? transaction, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT ItemNumber FROM dbo.Items WHERE ItemID = @item;", connection, transaction);
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InventoryException("الصنف غير موجود.", StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// الصنف وتصنيفه من نسخة الفرع. الرصيد والسرعة وآخر التصحيحات: من المركز (المرجع) في النظام المركزي،
    /// ومن قاعدة الفرع في الجهاز المنفرد.
    /// </summary>
    private static async Task<object> ItemViewAsync(
        SqlConnection connection,
        SqlConnection? central,
        PosDeviceContext actor,
        Guid itemId,
        string? scannedItemNumber,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT item.ItemNumber, item.ItemName,
                   setting.IsImportant, setting.InventoryPage, setting.SupplyDays, setting.UpdatedAt, setting.UpdatedByName,
                   balance.OnHandQuantity, balance.IsQuantityInitialized, card.DailySalesRate
            FROM dbo.Items AS item
            LEFT JOIN dbo.ItemInventorySettings AS setting ON setting.ItemNumber = item.ItemNumber
            LEFT JOIN dbo.BranchStockBalances AS balance ON balance.BranchID = @branch AND balance.ItemID = item.ItemID
            LEFT JOIN dbo.ItemBranchInventory AS card ON card.BranchID = @branch AND card.ItemID = item.ItemID
            WHERE item.ItemID = @item;
            """,
            connection);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
        string itemNumber;
        string? itemName;
        bool important;
        byte? page;
        int? supplyDays;
        DateTime? classifiedAt;
        string? classifiedBy;
        decimal? onHand;
        decimal? dailyRate;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InventoryException("الصنف غير موجود.", StatusCodes.Status404NotFound);
            itemNumber = reader.GetString(0);
            itemName = reader.IsDBNull(1) ? null : reader.GetString(1);
            important = !reader.IsDBNull(2) && reader.GetBoolean(2);
            page = reader.IsDBNull(3) ? null : reader.GetByte(3);
            supplyDays = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            classifiedAt = reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5));
            classifiedBy = reader.IsDBNull(6) ? null : reader.GetString(6);
            var known = !reader.IsDBNull(8) && reader.GetBoolean(8) && !reader.IsDBNull(7);
            onHand = known ? reader.GetDecimal(7) : null;
            dailyRate = reader.IsDBNull(9) ? null : reader.GetDecimal(9);
        }

        if (central is not null)
        {
            onHand = null;
            dailyRate = null;
            await using var balance = new SqlCommand(
                """
                SELECT CASE WHEN IsQuantityInitialized = 1 THEN OnHandQuantity END, DailySalesRate
                FROM dbo.BranchStockSnapshots WHERE BranchID = @branch AND ItemID = @item;
                """,
                central);
            balance.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            balance.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            await using var reader = await balance.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                onHand = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
                dailyRate = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
            }
        }

        var corrections = new List<object>();
        await using (var history = central is null
            ? new SqlCommand(
                """
                SELECT TOP (5) PreviousQuantity, NewQuantity, Reason, UserName, CorrectedAt, ExpectedQuantity, CountedQuantity, DeltaQuantity
                FROM dbo.InventoryQuantityCorrections
                WHERE BranchID = @branch AND ItemID = @item
                ORDER BY CorrectedAt DESC;
                """,
                connection)
            : new SqlCommand(
                """
                SELECT TOP (5) CentralPreviousQuantity, COALESCE(CentralResultingQuantity, NewQuantity), Reason, RequestedByName,
                       COALESCE(CentralAppliedAt, RequestedAt), ExpectedQuantity, NewQuantity, DeltaQuantity
                FROM dbo.InventoryCorrectionRequests
                WHERE BranchID = @branch AND ItemID = @item
                ORDER BY RequestedAt DESC;
                """,
                central))
        {
            history.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.BranchId;
            history.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            await using var reader = await history.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                corrections.Add(new
                {
                    previousQuantity = reader.IsDBNull(0) ? (decimal?)null : reader.GetDecimal(0),
                    newQuantity = reader.GetDecimal(1),
                    reason = reader.GetString(2),
                    userName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    correctedAt = Utc(reader.GetDateTime(4)),
                    expectedQuantity = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5),
                    countedQuantity = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
                    deltaQuantity = reader.IsDBNull(7) ? (decimal?)null : reader.GetDecimal(7),
                });
            }
        }

        return new
        {
            itemId,
            itemNumber,
            itemName,
            scannedItemNumber,
            convertedFromPackage = scannedItemNumber is not null && !string.Equals(scannedItemNumber, itemNumber, StringComparison.Ordinal),
            isImportant = important || page == 0,
            inventoryPage = page is > 0 ? (int?)page : null,
            supplyDays,
            classifiedAt,
            classifiedBy,
            onHandQuantity = onHand,
            quantityKnown = onHand is not null,
            dailySalesRate = dailyRate,
            corrections,
        };
    }

    private static void AddClassification(SqlCommand command, string itemNumber, bool important, byte? page, int? days, string? by)
    {
        command.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = itemNumber;
        command.Parameters.Add("@important", SqlDbType.Bit).Value = important;
        command.Parameters.Add("@page", SqlDbType.TinyInt).Value = (object?)page ?? DBNull.Value;
        command.Parameters.Add("@days", SqlDbType.Int).Value = (object?)days ?? DBNull.Value;
        command.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = (object?)Limit(by, 200) ?? DBNull.Value;
    }

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = 4;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static async Task RequireSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(N'dbo.InventoryQuantityCorrections', N'U');", connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            throw new InventoryException(MigrationMissing, StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = table;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static string? Limit(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
