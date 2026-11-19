using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Sales;

public sealed class CashDrawerException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class CashDrawerService(SqlConnectionFactory connections, SyncEndpointStore endpoints, CashSessionService sessions)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await PullDenominationsAsync(cancellationToken);
        await RelayPendingAsync(cancellationToken);
    }

    public async Task<object> ContextAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await PullDenominationsAsync(cancellationToken);
        await using var connection = await OpenLocalAsync(cancellationToken);
        var denominations = await ReadDenominationsAsync(connection, null, activeOnly: true, cancellationToken);
        var open = await sessions.GetCurrentAsync(actor, cancellationToken);
        Guid? parentId = null;
        string? previousStatus = null;
        decimal? previousDifference = null;
        if (open is not null)
        {
            await using var pending = new SqlCommand(
                """
                SELECT TOP (1) CountID, Status, DifferenceAmount
                FROM dbo.CashDrawerCounts
                WHERE CashSessionID = @session AND DeviceID = @device AND IsFinal = 0
                ORDER BY CountedAt DESC;
                """,
                connection);
            pending.Parameters.AddWithValue("@session", open.CashSessionId);
            pending.Parameters.AddWithValue("@device", actor.DeviceId);
            await using var reader = await pending.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                parentId = reader.GetGuid(0);
                previousStatus = reader.GetString(1);
                previousDifference = reader.GetDecimal(2);
            }
        }

        return new
        {
            shiftOpen = open is not null,
            attempt = parentId is null ? 1 : 2,
            parentCountId = parentId,
            previousStatus,
            previousDifference,
            denominations,
        };
    }

    public async Task<object> SubmitAsync(
        PosDeviceContext actor,
        Guid? parentCountId,
        IReadOnlyList<(Guid DenominationId, int Quantity)> lines,
        string? justification,
        CancellationToken cancellationToken)
    {
        await PullDenominationsAsync(cancellationToken);
        var expected = await sessions.PreviewExpectedCashAsync(actor, cancellationToken);
        if (expected is null)
        {
            throw new CashDrawerException("افتح الوردية قبل جرد الجرار.", StatusCodes.Status409Conflict);
        }

        await using var connection = await OpenLocalAsync(cancellationToken);
        var denominations = await ReadDenominationsAsync(connection, null, activeOnly: true, cancellationToken);
        var quantities = new Dictionary<Guid, int>();
        foreach (var line in lines)
        {
            if (line.Quantity < 0)
            {
                throw new CashDrawerException("الكمية لا تكون أقل من صفر.", StatusCodes.Status400BadRequest);
            }

            if (denominations.All(item => item.DenominationId != line.DenominationId))
            {
                throw new CashDrawerException("فئة النقد غير متاحة للجرد.", StatusCodes.Status400BadRequest);
            }

            quantities[line.DenominationId] = line.Quantity;
        }

        decimal counted = 0m;
        foreach (var denomination in denominations)
        {
            quantities.TryGetValue(denomination.DenominationId, out var quantity);
            counted += decimal.Round(quantity * denomination.UnitValue, 4, MidpointRounding.AwayFromZero);
        }

        var difference = CashDrawerRules.Difference(counted, expected.Value);
        var status = CashDrawerRules.Status(difference);
        var note = string.IsNullOrWhiteSpace(justification) ? null : justification.Trim();
        if (note is { Length: > 500 })
        {
            throw new CashDrawerException("التبرير أطول من المسموح.", StatusCodes.Status400BadRequest);
        }

        var open = await sessions.GetCurrentAsync(actor, cancellationToken)
            ?? throw new CashDrawerException("افتح الوردية قبل جرد الجرار.", StatusCodes.Status409Conflict);
        var pendingParent = await PendingParentAsync(connection, open.CashSessionId, actor.DeviceId, cancellationToken);
        if (parentCountId is null)
        {
            if (pendingParent is not null)
            {
                throw new CashDrawerException("العد الأول محفوظ. أكمل العد الثاني.", StatusCodes.Status409Conflict);
            }
        }
        else if (pendingParent != parentCountId)
        {
            throw new CashDrawerException("العد الثاني لا يطابق العد الأول المحفوظ.", StatusCodes.Status409Conflict);
        }

        var finalAttempt = parentCountId is not null || status == CashDrawerRules.Matched;
        if (finalAttempt && CashDrawerRules.NeedsJustification(status) && note is null)
        {
            return new
            {
                outcome = "NeedJustification",
                attempt = 2,
                status,
                difference,
                countId = parentCountId,
                charged = false,
                message = status == CashDrawerRules.Shortage
                    ? "النقص سيُحسب على الموظف فور التأكيد."
                    : "اكتب تبرير الفارق قبل التأكيد.",
            };
        }

        var countId = Guid.NewGuid();
        var attempt = parentCountId is null ? 1 : 2;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.CashDrawerCounts
            (
                CountID, BusinessID, BranchID, POSStationID, DeviceID, CountedByUserID, CashSessionID,
                ParentCountID, AttemptNumber, CountedAmount, ExpectedAmount, DifferenceAmount, Status,
                Justification, IsFinal, CountedAt
            )
            VALUES
            (
                @id, @business, @branch, @station, @device, @user, @session,
                @parent, @attempt, @counted, @expected, @difference, @status,
                @justification, @final, SYSUTCDATETIME()
            );
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("@id", countId);
            insert.Parameters.AddWithValue("@business", actor.BusinessId);
            insert.Parameters.AddWithValue("@branch", actor.BranchId);
            insert.Parameters.AddWithValue("@station", actor.StationId);
            insert.Parameters.AddWithValue("@device", actor.DeviceId);
            insert.Parameters.AddWithValue("@user", actor.UserId);
            insert.Parameters.AddWithValue("@session", open.CashSessionId);
            insert.Parameters.AddWithValue("@parent", (object?)parentCountId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@attempt", attempt);
            AddMoney(insert, "@counted", counted);
            AddMoney(insert, "@expected", expected.Value);
            AddMoney(insert, "@difference", difference);
            insert.Parameters.AddWithValue("@status", status);
            insert.Parameters.AddWithValue("@justification", (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("@final", finalAttempt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var denomination in denominations)
        {
            quantities.TryGetValue(denomination.DenominationId, out var quantity);
            var lineAmount = decimal.Round(quantity * denomination.UnitValue, 4, MidpointRounding.AwayFromZero);
            await using var line = new SqlCommand(
                """
                INSERT INTO dbo.CashDrawerCountLines
                (
                    CountLineID, CountID, BranchID, POSStationID, DenominationID, UnitValue, DisplayName, Quantity, LineAmount
                )
                VALUES
                (
                    @line, @count, @branch, @station, @denomination, @value, @name, @quantity, @amount
                );
                """,
                connection,
                transaction);
            line.Parameters.AddWithValue("@line", Guid.NewGuid());
            line.Parameters.AddWithValue("@count", countId);
            line.Parameters.AddWithValue("@branch", actor.BranchId);
            line.Parameters.AddWithValue("@station", actor.StationId);
            line.Parameters.AddWithValue("@denomination", denomination.DenominationId);
            AddMoney(line, "@value", denomination.UnitValue);
            line.Parameters.AddWithValue("@name", denomination.DisplayName);
            line.Parameters.AddWithValue("@quantity", quantity);
            AddMoney(line, "@amount", lineAmount);
            await line.ExecuteNonQueryAsync(cancellationToken);
        }

        var charged = finalAttempt && CashDrawerRules.ChargesEmployee(status);
        if (finalAttempt && status != CashDrawerRules.Matched)
        {
            await using var error = new SqlCommand(
                """
                INSERT INTO dbo.EmployeeCashErrors
                (
                    ErrorID, BusinessID, BranchID, POSStationID, UserID, CountID, Amount, ErrorType, Justification, OccurredAt
                )
                VALUES
                (
                    @id, @business, @branch, @station, @user, @count, @amount, @type, @justification, SYSUTCDATETIME()
                );
                """,
                connection,
                transaction);
            error.Parameters.AddWithValue("@id", Guid.NewGuid());
            error.Parameters.AddWithValue("@business", actor.BusinessId);
            error.Parameters.AddWithValue("@branch", actor.BranchId);
            error.Parameters.AddWithValue("@station", actor.StationId);
            error.Parameters.AddWithValue("@user", actor.UserId);
            error.Parameters.AddWithValue("@count", countId);
            AddMoney(error, "@amount", Math.Abs(difference));
            error.Parameters.AddWithValue("@type", status);
            error.Parameters.AddWithValue("@justification", note!);
            await error.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await RelayCountAsync(connection, countId, cancellationToken);
        return new
        {
            outcome = finalAttempt ? "Saved" : "Retry",
            attempt,
            status,
            difference,
            countId,
            charged,
            message = !finalAttempt
                ? "العد الأول لم يطابق. أعد العد."
                : status == CashDrawerRules.Matched
                    ? "تم الجرد."
                    : charged
                        ? "تم الجرد. النقص محسوب على الموظف."
                        : "تم الجرد. الزيادة مسجّلة.",
        };
    }

    public async Task<IReadOnlyList<CashDenominationRow>> DenominationsAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await PullDenominationsAsync(cancellationToken);
        await using var connection = await OpenLocalAsync(cancellationToken);
        return await ReadDenominationsAsync(connection, null, activeOnly, cancellationToken);
    }

    public async Task<CashDenominationRow> SaveDenominationAsync(
        Guid? denominationId,
        decimal unitValue,
        string displayName,
        int sortOrder,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var name = displayName.Trim();
        if (unitValue <= 0m || name.Length is 0 or > 80)
        {
            throw new CashDrawerException("قيمة الفئة واسمها مطلوبان.", StatusCodes.Status400BadRequest);
        }

        await using var connection = await OpenLocalAsync(cancellationToken);
        var id = denominationId ?? Guid.NewGuid();
        if (denominationId is not null)
        {
            await using var used = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.CashDrawerCountLines WHERE DenominationID = @id;",
                connection);
            used.Parameters.AddWithValue("@id", id);
            var count = Convert.ToInt32(await used.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            await using var current = new SqlCommand(
                "SELECT UnitValue FROM dbo.CashDenominations WHERE DenominationID = @id;",
                connection);
            current.Parameters.AddWithValue("@id", id);
            var existing = await current.ExecuteScalarAsync(cancellationToken);
            if (existing is null or DBNull)
            {
                throw new CashDrawerException("الفئة غير موجودة.", StatusCodes.Status404NotFound);
            }

            var currentValue = Convert.ToDecimal(existing, System.Globalization.CultureInfo.InvariantCulture);
            if (currentValue != unitValue && count > 0)
            {
                throw new CashDrawerException("قيمة فئة مستخدمة في جرد لا تتغير. أضف فئة جديدة.", StatusCodes.Status409Conflict);
            }
        }

        await using var save = new SqlCommand(
            """
            UPDATE dbo.CashDenominations
            SET UnitValue = @value, DisplayName = @name, SortOrder = @sort, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
            WHERE DenominationID = @id;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.CashDenominations (DenominationID, UnitValue, DisplayName, SortOrder, IsActive, CreatedAt, UpdatedAt)
                VALUES (@id, @value, @name, @sort, @active, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection);
        save.Parameters.AddWithValue("@id", id);
        AddMoney(save, "@value", unitValue);
        save.Parameters.AddWithValue("@name", name);
        save.Parameters.AddWithValue("@sort", sortOrder);
        save.Parameters.AddWithValue("@active", isActive);
        try
        {
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new CashDrawerException("قيمة الفئة موجودة مسبقاً.", StatusCodes.Status409Conflict);
        }

        await PushDenominationAsync(connection, id, cancellationToken);
        var rows = await ReadDenominationsAsync(connection, null, activeOnly: false, cancellationToken);
        return rows.First(row => row.DenominationId == id);
    }

    public async Task<object> ReviewAsync(
        Guid? branchId,
        Guid? stationId,
        Guid? userId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? status,
        string? decision,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await RelayPendingAsync(cancellationToken);
        var central = await OpenCentralAsync(cancellationToken);
        var connection = central ?? await OpenLocalAsync(cancellationToken);
        await using var _ = connection;
        var where = new List<string> { "1 = 1" };
        await using var command = new SqlCommand { Connection = connection };
        if (branchId is not null)
        {
            where.Add("count.BranchID = @branch");
            command.Parameters.AddWithValue("@branch", branchId.Value);
        }

        if (stationId is not null)
        {
            where.Add("count.POSStationID = @station");
            command.Parameters.AddWithValue("@station", stationId.Value);
        }

        if (userId is not null)
        {
            where.Add("count.CountedByUserID = @user");
            command.Parameters.AddWithValue("@user", userId.Value);
        }

        if (dateFrom is not null)
        {
            where.Add("CAST(count.CountedAt AS date) >= @from");
            command.Parameters.AddWithValue("@from", dateFrom.Value.Date);
        }

        if (dateTo is not null)
        {
            where.Add("CAST(count.CountedAt AS date) <= @to");
            command.Parameters.AddWithValue("@to", dateTo.Value.Date);
        }

        if (status is CashDrawerRules.Matched or CashDrawerRules.Shortage or CashDrawerRules.Surplus)
        {
            where.Add("count.Status = @status");
            command.Parameters.AddWithValue("@status", status);
        }

        if (decision == "None")
        {
            where.Add("decision.DecisionID IS NULL");
        }
        else if (decision is CashDrawerRules.KeepShortage or CashDrawerRules.KeepSurplus or CashDrawerRules.Waive)
        {
            where.Add("decision.DecisionCode = @decision");
            command.Parameters.AddWithValue("@decision", decision);
        }

        var filter = string.Join(" AND ", where);
        command.CommandText =
            $"""
             SELECT COUNT(*),
                    COALESCE(SUM(CASE WHEN count.Status = N'Shortage' AND count.IsFinal = 1 THEN ABS(count.DifferenceAmount) ELSE 0 END), 0)
             FROM dbo.CashDrawerCounts AS count
             LEFT JOIN dbo.CashDrawerDecisions AS decision ON decision.CountID = count.CountID
             WHERE {filter};
             """;
        await using var totals = await command.ExecuteReaderAsync(cancellationToken);
        await totals.ReadAsync(cancellationToken);
        var total = totals.GetInt32(0);
        var shortage = totals.GetDecimal(1);
        await totals.CloseAsync();
        command.Parameters.AddWithValue("@skip", Math.Max(0, skip));
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 100));
        command.CommandText =
            $"""
             SELECT count.CountID, count.CountedAt, cashier.DisplayName, branch.BranchName, station.StationNumber, station.StationName,
                    count.AttemptNumber, count.CountedAmount, count.DifferenceAmount, count.Status, count.Justification, count.IsFinal,
                    decision.DecisionCode, branch.BranchID, station.StationID
             FROM dbo.CashDrawerCounts AS count
             INNER JOIN dbo.Users AS cashier ON cashier.UserID = count.CountedByUserID
             INNER JOIN dbo.Branches AS branch ON branch.BranchID = count.BranchID
             INNER JOIN dbo.POSStations AS station ON station.StationID = count.POSStationID
             LEFT JOIN dbo.CashDrawerDecisions AS decision ON decision.CountID = count.CountID
             WHERE {filter}
             ORDER BY count.CountedAt DESC
             OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
             """;
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new
            {
                countId = reader.GetGuid(0),
                countedAt = reader.GetDateTime(1),
                cashier = reader.GetString(2),
                branchName = reader.GetString(3),
                stationNumber = reader.GetInt32(4),
                stationName = reader.GetString(5),
                attempt = reader.GetByte(6),
                countedAmount = reader.GetDecimal(7),
                difference = reader.GetDecimal(8),
                status = reader.GetString(9),
                justification = reader.IsDBNull(10) ? null : reader.GetString(10),
                final = reader.GetBoolean(11),
                decision = reader.IsDBNull(12) ? null : reader.GetString(12),
                branchId = reader.GetGuid(13),
                stationId = reader.GetGuid(14),
            });
        }

        await reader.CloseAsync();
        return new
        {
            source = central is null ? "local" : "central",
            total,
            shortageTotal = shortage,
            lines = rows,
            options = await ReviewOptionsAsync(connection, cancellationToken),
        };
    }

    public async Task<object> DetailAsync(Guid countId, CancellationToken cancellationToken)
    {
        await RelayPendingAsync(cancellationToken);
        var central = await OpenCentralAsync(cancellationToken);
        var connection = central ?? await OpenLocalAsync(cancellationToken);
        await using var _ = connection;
        var header = await ReadCountHeaderAsync(connection, countId, cancellationToken)
            ?? throw new CashDrawerException("الجرد غير موجود.", StatusCodes.Status404NotFound);
        object? parent = null;
        if (header.ParentCountId is not null)
        {
            parent = await ReadCountHeaderAsync(connection, header.ParentCountId.Value, cancellationToken);
        }

        return new { count = header, firstAttempt = parent };
    }

    public async Task<object> DecideAsync(PosDeviceContext actor, Guid countId, string decisionCode, string note, CancellationToken cancellationToken)
    {
        var text = note.Trim();
        if (text.Length is 0 or > 500)
        {
            throw new CashDrawerException("ملاحظة القرار مطلوبة.", StatusCodes.Status400BadRequest);
        }

        await using var local = await OpenLocalAsync(cancellationToken);
        var central = await OpenCentralAsync(cancellationToken);
        var header = central is null ? null : await ReadCountHeaderAsync(central, countId, cancellationToken);
        header ??= await ReadCountHeaderAsync(local, countId, cancellationToken);
        if (header is null)
        {
            if (central is not null) await central.DisposeAsync();
            throw new CashDrawerException("الجرد غير موجود.", StatusCodes.Status404NotFound);
        }

        if (!header.Final || header.Status == CashDrawerRules.Matched)
        {
            if (central is not null) await central.DisposeAsync();
            throw new CashDrawerException("هذا الجرد لا يقبل قراراً.", StatusCodes.Status409Conflict);
        }

        if (header.Decision is not null)
        {
            if (central is not null) await central.DisposeAsync();
            throw new CashDrawerException("قرار هذا الجرد محفوظ ولا يتغير.", StatusCodes.Status409Conflict);
        }

        if (!CashDrawerRules.DecisionMatches(header.Status, decisionCode))
        {
            if (central is not null) await central.DisposeAsync();
            throw new CashDrawerException("القرار لا يطابق نتيجة الجرد.", StatusCodes.Status400BadRequest);
        }

        var decisionId = Guid.NewGuid();
        var wroteLocal = false;
        if (await CountExistsAsync(local, countId, cancellationToken))
        {
            await InsertDecisionAsync(local, decisionId, header, actor.UserId, decisionCode, text, cancellationToken);
            wroteLocal = true;
        }

        if (central is not null)
        {
            try
            {
                if (await CountExistsAsync(central, countId, cancellationToken))
                {
                    await InsertDecisionAsync(central, decisionId, header, actor.UserId, decisionCode, text, cancellationToken);
                    if (wroteLocal)
                    {
                        await using var mark = new SqlCommand(
                            "UPDATE dbo.CashDrawerDecisions SET RelayedAt = SYSUTCDATETIME() WHERE DecisionID = @id;",
                            local);
                        mark.Parameters.AddWithValue("@id", decisionId);
                        await mark.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
            }
            catch (SqlException)
            {
                // The local decision stays and the relay retries.
            }

            await central.DisposeAsync();
        }

        return new { decisionId, decision = decisionCode, note = text };
    }

    private static async Task<bool> CountExistsAsync(SqlConnection connection, Guid countId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT 1 FROM dbo.CashDrawerCounts WHERE CountID = @id;", connection);
        command.Parameters.AddWithValue("@id", countId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task PullDenominationsAsync(CancellationToken cancellationToken)
    {
        var central = await OpenCentralAsync(cancellationToken);
        if (central is null)
        {
            return;
        }

        await using var _ = central;
        await using var local = await OpenLocalAsync(cancellationToken);
        var remote = await ReadDenominationsAsync(central, null, activeOnly: false, cancellationToken);
        foreach (var row in remote)
        {
            await UpsertDenominationAsync(local, row, cancellationToken);
        }

        var owned = await ReadDenominationsAsync(local, null, activeOnly: false, cancellationToken);
        foreach (var row in owned)
        {
            await UpsertDenominationAsync(central, row, cancellationToken);
        }
    }

    public async Task RelayPendingAsync(CancellationToken cancellationToken)
    {
        await using var local = await OpenLocalAsync(cancellationToken);
        await PullDenominationsAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CountID, AttemptNumber AS Sort, CountedAt
            FROM dbo.CashDrawerCounts
            WHERE RelayedAt IS NULL
            UNION ALL
            SELECT CountID, 9, DecidedAt
            FROM dbo.CashDrawerDecisions
            WHERE RelayedAt IS NULL
            ORDER BY Sort, CountedAt;
            """,
            local);
        var ids = new List<Guid>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }
        catch (SqlException)
        {
            return;
        }

        foreach (var id in ids.Distinct())
        {
            await RelayCountAsync(local, id, cancellationToken);
        }
    }

    private async Task InsertDecisionAsync(
        SqlConnection connection,
        Guid decisionId,
        CashCountHeader header,
        Guid supervisorId,
        string decisionCode,
        string note,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CashDrawerDecisions WHERE CountID = @count)
                INSERT INTO dbo.CashDrawerDecisions
                (DecisionID, CountID, BranchID, POSStationID, SupervisorUserID, DecisionCode, Note, DecidedAt)
                VALUES (@id, @count, @branch, @station, @user, @code, @note, SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@id", decisionId);
        command.Parameters.AddWithValue("@count", header.CountId);
        command.Parameters.AddWithValue("@branch", header.BranchId);
        command.Parameters.AddWithValue("@station", header.StationId);
        command.Parameters.AddWithValue("@user", supervisorId);
        command.Parameters.AddWithValue("@code", decisionCode);
        command.Parameters.AddWithValue("@note", note);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CashCountHeader?> ReadCountHeaderAsync(SqlConnection connection, Guid countId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT count.CountID, count.ParentCountID, count.BranchID, count.POSStationID, count.CountedAt, cashier.DisplayName,
                   branch.BranchName, station.StationNumber, station.StationName, count.AttemptNumber, count.CountedAmount,
                   count.DifferenceAmount, count.Status, count.Justification, count.IsFinal, decision.DecisionCode,
                   decision.Note, supervisor.DisplayName, decision.DecidedAt
            FROM dbo.CashDrawerCounts AS count
            INNER JOIN dbo.Users AS cashier ON cashier.UserID = count.CountedByUserID
            INNER JOIN dbo.Branches AS branch ON branch.BranchID = count.BranchID
            INNER JOIN dbo.POSStations AS station ON station.StationID = count.POSStationID
            LEFT JOIN dbo.CashDrawerDecisions AS decision ON decision.CountID = count.CountID
            LEFT JOIN dbo.Users AS supervisor ON supervisor.UserID = decision.SupervisorUserID
            WHERE count.CountID = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@id", countId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var header = new CashCountHeader(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetDateTime(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetByte(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetBoolean(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetDateTime(18),
            []);
        await reader.CloseAsync();
        return header with { Lines = await ReadLinesAsync(connection, countId, cancellationToken) };
    }

    private static async Task<IReadOnlyList<object>> ReadLinesAsync(SqlConnection connection, Guid countId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT DisplayName, UnitValue, Quantity, LineAmount
            FROM dbo.CashDrawerCountLines
            WHERE CountID = @id
            ORDER BY UnitValue;
            """,
            connection);
        command.Parameters.AddWithValue("@id", countId);
        var lines = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new
            {
                displayName = reader.GetString(0),
                unitValue = reader.GetDecimal(1),
                quantity = reader.GetInt32(2),
                lineAmount = reader.GetDecimal(3),
            });
        }

        return lines;
    }

    private async Task RelayCountAsync(SqlConnection local, Guid countId, CancellationToken cancellationToken)
    {
        var central = await OpenCentralAsync(cancellationToken);
        if (central is null)
        {
            return;
        }

        await using var _ = central;
        try
        {
            await CopyRowAsync(
                local,
                central,
                """
                SELECT CountID, BusinessID, BranchID, POSStationID, DeviceID, CountedByUserID, CashSessionID, ParentCountID,
                       AttemptNumber, CountedAmount, ExpectedAmount, DifferenceAmount, Status, Justification, IsFinal, CountedAt
                FROM dbo.CashDrawerCounts WHERE CountID = @id;
                """,
                """
                INSERT INTO dbo.CashDrawerCounts
                (
                    CountID, BusinessID, BranchID, POSStationID, DeviceID, CountedByUserID, CashSessionID, ParentCountID,
                    AttemptNumber, CountedAmount, ExpectedAmount, DifferenceAmount, Status, Justification, IsFinal, CountedAt, RelayedAt
                )
                VALUES
                (
                    @CountID, @BusinessID, @BranchID, @POSStationID, @DeviceID, @CountedByUserID, @CashSessionID, @ParentCountID,
                    @AttemptNumber, @CountedAmount, @ExpectedAmount, @DifferenceAmount, @Status, @Justification, @IsFinal, @CountedAt, SYSUTCDATETIME()
                );
                """,
                countId,
                cancellationToken);
            await CopyChildrenAsync(local, central, countId, cancellationToken);
            await using var mark = new SqlCommand(
                """
                UPDATE dbo.CashDrawerCounts SET RelayedAt = SYSUTCDATETIME() WHERE CountID = @id;
                UPDATE dbo.CashDrawerCountLines SET RelayedAt = SYSUTCDATETIME() WHERE CountID = @id;
                UPDATE dbo.EmployeeCashErrors SET RelayedAt = SYSUTCDATETIME() WHERE CountID = @id;
                UPDATE dbo.CashDrawerDecisions SET RelayedAt = SYSUTCDATETIME() WHERE CountID = @id;
                """,
                local);
            mark.Parameters.AddWithValue("@id", countId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Leave RelayedAt empty so the next sync retries.
        }
    }

    private static async Task CopyChildrenAsync(SqlConnection local, SqlConnection central, Guid countId, CancellationToken cancellationToken)
    {
        await using var lines = new SqlCommand(
            """
            SELECT CountLineID, CountID, BranchID, POSStationID, DenominationID, UnitValue, DisplayName, Quantity, LineAmount
            FROM dbo.CashDrawerCountLines WHERE CountID = @id;
            """,
            local);
        lines.Parameters.AddWithValue("@id", countId);
        await using var reader = await lines.ExecuteReaderAsync(cancellationToken);
        var cached = new List<object[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            cached.Add(values);
        }

        await reader.CloseAsync();
        foreach (var values in cached)
        {
            await using var insert = new SqlCommand(
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.CashDrawerCountLines WHERE CountLineID = @CountLineID)
                    INSERT INTO dbo.CashDrawerCountLines
                    (CountLineID, CountID, BranchID, POSStationID, DenominationID, UnitValue, DisplayName, Quantity, LineAmount, RelayedAt)
                    VALUES
                    (@CountLineID, @CountID, @BranchID, @POSStationID, @DenominationID, @UnitValue, @DisplayName, @Quantity, @LineAmount, SYSUTCDATETIME());
                """,
                central);
            var names = new[] { "CountLineID", "CountID", "BranchID", "POSStationID", "DenominationID", "UnitValue", "DisplayName", "Quantity", "LineAmount" };
            for (var index = 0; index < names.Length; index++)
            {
                insert.Parameters.AddWithValue("@" + names[index], values[index] ?? DBNull.Value);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await CopySimpleAsync(
            local,
            central,
            """
            SELECT ErrorID, BusinessID, BranchID, POSStationID, UserID, CountID, Amount, ErrorType, Justification, OccurredAt
            FROM dbo.EmployeeCashErrors WHERE CountID = @id;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.EmployeeCashErrors WHERE ErrorID = @ErrorID)
                INSERT INTO dbo.EmployeeCashErrors
                (ErrorID, BusinessID, BranchID, POSStationID, UserID, CountID, Amount, ErrorType, Justification, OccurredAt, RelayedAt)
                VALUES
                (@ErrorID, @BusinessID, @BranchID, @POSStationID, @UserID, @CountID, @Amount, @ErrorType, @Justification, @OccurredAt, SYSUTCDATETIME());
            """,
            countId,
            cancellationToken);
        await CopySimpleAsync(
            local,
            central,
            """
            SELECT DecisionID, CountID, BranchID, POSStationID, SupervisorUserID, DecisionCode, Note, DecidedAt
            FROM dbo.CashDrawerDecisions WHERE CountID = @id;
            """,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CashDrawerDecisions WHERE DecisionID = @DecisionID)
                INSERT INTO dbo.CashDrawerDecisions
                (DecisionID, CountID, BranchID, POSStationID, SupervisorUserID, DecisionCode, Note, DecidedAt, RelayedAt)
                VALUES
                (@DecisionID, @CountID, @BranchID, @POSStationID, @SupervisorUserID, @DecisionCode, @Note, @DecidedAt, SYSUTCDATETIME());
            """,
            countId,
            cancellationToken);
    }

    private static async Task CopySimpleAsync(SqlConnection local, SqlConnection central, string readSql, string insertSql, Guid countId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(readSql, local);
        read.Parameters.AddWithValue("@id", countId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(insertSql, central);
        for (var index = 0; index < names.Length; index++)
        {
            insert.Parameters.AddWithValue("@" + names[index], values[index] ?? DBNull.Value);
        }

        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyRowAsync(SqlConnection local, SqlConnection central, string readSql, string insertSql, Guid countId, CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand("SELECT 1 FROM dbo.CashDrawerCounts WHERE CountID = @id;", central);
        exists.Parameters.AddWithValue("@id", countId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
        {
            return;
        }

        await CopySimpleAsync(local, central, readSql, insertSql, countId, cancellationToken);
    }

    private async Task PushDenominationAsync(SqlConnection local, Guid denominationId, CancellationToken cancellationToken)
    {
        var central = await OpenCentralAsync(cancellationToken);
        if (central is null)
        {
            return;
        }

        await using var _ = central;
        var rows = await ReadDenominationsAsync(local, null, activeOnly: false, cancellationToken);
        var row = rows.FirstOrDefault(item => item.DenominationId == denominationId);
        if (row is not null)
        {
            await UpsertDenominationAsync(central, row, cancellationToken);
        }
    }

    private static async Task UpsertDenominationAsync(SqlConnection connection, CashDenominationRow row, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CashDenominations
            SET UnitValue = @value, DisplayName = @name, SortOrder = @sort, IsActive = @active, UpdatedAt = @updated
            WHERE DenominationID = @id;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.CashDenominations (DenominationID, UnitValue, DisplayName, SortOrder, IsActive, CreatedAt, UpdatedAt)
                VALUES (@id, @value, @name, @sort, @active, @updated, @updated);
            """,
            connection);
        command.Parameters.AddWithValue("@id", row.DenominationId);
        AddMoney(command, "@value", row.UnitValue);
        command.Parameters.AddWithValue("@name", row.DisplayName);
        command.Parameters.AddWithValue("@sort", row.SortOrder);
        command.Parameters.AddWithValue("@active", row.IsActive);
        command.Parameters.AddWithValue("@updated", DateTime.UtcNow);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // The other side already has this value under another id.
        }
    }

    private static async Task<Guid?> PendingParentAsync(SqlConnection connection, Guid cashSessionId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) CountID
            FROM dbo.CashDrawerCounts
            WHERE CashSessionID = @session AND DeviceID = @device AND IsFinal = 0;
            """,
            connection);
        command.Parameters.AddWithValue("@session", cashSessionId);
        command.Parameters.AddWithValue("@device", deviceId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<IReadOnlyList<CashDenominationRow>> ReadDenominationsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"""
             SELECT DenominationID, UnitValue, DisplayName, SortOrder, IsActive
             FROM dbo.CashDenominations
             {(activeOnly ? "WHERE IsActive = 1" : "")}
             ORDER BY SortOrder, UnitValue;
             """,
            connection,
            transaction);
        var rows = new List<CashDenominationRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CashDenominationRow(reader.GetGuid(0), reader.GetDecimal(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4)));
        }

        return rows;
    }

    private static async Task<object> ReviewOptionsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var branches = await ReadPairsAsync(connection, "SELECT BranchID, BranchName FROM dbo.Branches ORDER BY BranchName;", cancellationToken);
        var stations = await ReadStationsAsync(connection, cancellationToken);
        var users = await ReadPairsAsync(connection, "SELECT UserID, DisplayName FROM dbo.Users WHERE IsActive = 1 ORDER BY DisplayName;", cancellationToken);
        return new { branches, stations, users };
    }

    private static async Task<List<object>> ReadPairsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<object>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new { id = reader.GetGuid(0), name = reader.GetString(1) });
        }

        return rows;
    }

    private static async Task<List<object>> ReadStationsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<object>();
        await using var command = new SqlCommand(
            "SELECT StationID, BranchID, StationNumber, StationName FROM dbo.POSStations ORDER BY StationNumber;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new
            {
                id = reader.GetGuid(0),
                branchId = reader.GetGuid(1),
                stationNumber = reader.GetInt32(2),
                name = reader.GetString(3),
            });
        }

        return rows;
    }

    private async Task<SqlConnection> OpenLocalAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<SqlConnection?> OpenCentralAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
            if (settings is null)
            {
                return null;
            }

            var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, System.Data.SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = value;
    }
}

public sealed record CashDenominationRow(Guid DenominationId, decimal UnitValue, string DisplayName, int SortOrder, bool IsActive);

public sealed record CashCountHeader(
    Guid CountId,
    Guid? ParentCountId,
    Guid BranchId,
    Guid StationId,
    DateTime CountedAt,
    string Cashier,
    string BranchName,
    int StationNumber,
    string StationName,
    byte Attempt,
    decimal CountedAmount,
    decimal Difference,
    string Status,
    string? Justification,
    bool Final,
    string? Decision,
    string? DecisionNote,
    string? SupervisorName,
    DateTime? DecidedAt,
    IReadOnlyList<object> Lines);
