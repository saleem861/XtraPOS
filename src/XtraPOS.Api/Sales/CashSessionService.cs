using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Sales;

public sealed class CashSessionService(SqlConnectionFactory connections)
{
    public async Task<CashSessionState?> GetCurrentAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindOpenAsync(connection, null, actor, cancellationToken);
    }

    public async Task<decimal?> PreviewExpectedCashAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var open = await FindOpenAsync(connection, null, actor, cancellationToken);
        if (open is null)
        {
            return null;
        }

        var completedCash = await SumCompletedCashAsync(connection, null, open.CashSessionId, actor.BranchId, cancellationToken);
        return open.OpeningCashAmount + completedCash;
    }

    public async Task<CashSessionOpenResult> OpenAsync(
        PosDeviceContext actor,
        decimal openingCashAmount,
        CancellationToken cancellationToken)
    {
        if (openingCashAmount < 0m || !MoneyAmount.Fits(openingCashAmount))
        {
            throw new CashSessionException(
                CashSessionCodes.InvalidOpeningAmount,
                "الرصيد الافتتاحي يجب أن يكون صفراً أو أكبر.",
                StatusCodes.Status400BadRequest);
        }

        await using var connection = await OpenAsync(cancellationToken);
        var existing = await FindOpenAsync(connection, null, actor, cancellationToken);
        if (existing is not null)
        {
            return new CashSessionAlreadyOpen(existing);
        }

        var id = Guid.NewGuid();
        try
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO dbo.POSCashSessions
                (
                    CashSessionID, BusinessID, BranchID, POSStationID, DeviceID,
                    OpenedByUserID, OpenedAt, OpeningCashAmount, Status
                )
                VALUES
                (
                    @id, @business, @branch, @station, @device,
                    @user, SYSUTCDATETIME(), @opening, N'Open'
                );
                """,
                connection);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@business", actor.BusinessId);
            command.Parameters.AddWithValue("@branch", actor.BranchId);
            command.Parameters.AddWithValue("@station", actor.StationId);
            command.Parameters.AddWithValue("@device", actor.DeviceId);
            command.Parameters.AddWithValue("@user", actor.UserId);
            AddMoney(command, "@opening", openingCashAmount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            var raced = await FindOpenAsync(connection, null, actor, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return new CashSessionAlreadyOpen(raced);
        }

        var opened = await FindOpenAsync(connection, null, actor, cancellationToken)
            ?? throw new InvalidOperationException("Opened cash session could not be read.");
        return new CashSessionOpened(opened);
    }

    public async Task<CashSessionState> CloseAsync(
        PosDeviceContext actor,
        decimal countedCashAmount,
        CancellationToken cancellationToken)
    {
        if (countedCashAmount < 0m || !MoneyAmount.Fits(countedCashAmount))
        {
            throw new CashSessionException(
                CashSessionCodes.InvalidCountedAmount,
                "المبلغ الفعلي يجب أن يكون صفراً أو أكبر.",
                StatusCodes.Status400BadRequest);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var open = await FindOpenAsync(connection, transaction, actor, cancellationToken, lockRow: true);
        if (open is null)
        {
            throw await ClosedOrMissingAsync(connection, transaction, actor, cancellationToken);
        }

        var completedCash = await SumCompletedCashAsync(connection, transaction, open.CashSessionId, actor.BranchId, cancellationToken);
        var expected = open.OpeningCashAmount + completedCash;
        var difference = countedCashAmount - expected;
        await using var update = new SqlCommand(
            """
            UPDATE dbo.POSCashSessions
            SET Status = N'Closed',
                ClosedByUserID = @user,
                ClosedAt = SYSUTCDATETIME(),
                ExpectedCashAmount = @expected,
                CountedCashAmount = @counted,
                CashDifference = @difference
            WHERE CashSessionID = @id
              AND BranchID = @branch
              AND DeviceID = @device
              AND Status = N'Open';
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("@user", actor.UserId);
        update.Parameters.AddWithValue("@id", open.CashSessionId);
        update.Parameters.AddWithValue("@branch", actor.BranchId);
        update.Parameters.AddWithValue("@device", actor.DeviceId);
        AddMoney(update, "@expected", expected);
        AddMoney(update, "@counted", countedCashAmount);
        AddMoney(update, "@difference", difference);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new CashSessionException(
                CashSessionCodes.AlreadyClosed,
                "الصندوق مغلق ولا يمكن إغلاقه مرة أخرى.",
                StatusCodes.Status409Conflict);
        }

        var closed = await ReadByIdAsync(connection, transaction, open.CashSessionId, cancellationToken)
            ?? throw new InvalidOperationException("Closed cash session could not be read.");
        await transaction.CommitAsync(cancellationToken);
        return closed;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<decimal> SumCompletedCashAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid cashSessionId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        // Opening cash + completed cash payments. Refunds and drawer adjustments are future tables.
        await using var command = new SqlCommand(
            """
            DECLARE @collections decimal(19, 4) = 0;
            DECLARE @reversals decimal(19, 4) = 0;
            IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL
                EXEC sp_executesql N'SELECT @amount = COALESCE(SUM(Amount), 0) FROM dbo.BranchDebtCollections WHERE CashSessionID = @session AND BranchID = @branch AND PaymentMethodCode = N''Cash'' AND Status IN (N''Pending'', N''Completed'', N''ReconciliationRequired'');',
                    N'@session uniqueidentifier, @branch uniqueidentifier, @amount decimal(19,4) OUTPUT', @session, @branch, @collections OUTPUT;
            IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NOT NULL
                EXEC sp_executesql N'SELECT @amount = COALESCE(SUM(Amount), 0) FROM dbo.BranchCollectionReversals WHERE CashSessionID = @session AND BranchID = @branch AND Status IN (N''Pending'', N''Completed'', N''ReconciliationRequired'');',
                    N'@session uniqueidentifier, @branch uniqueidentifier, @amount decimal(19,4) OUTPUT', @session, @branch, @reversals OUTPUT;
            SELECT CAST(COALESCE(SUM(payment.Amount), 0) AS decimal(19, 4)) + @collections - @reversals
            FROM dbo.SalePayments AS payment
            INNER JOIN dbo.Sales AS sale ON sale.SaleID = payment.SaleID
            WHERE payment.CashSessionID = @session
              AND payment.PaymentMethodCode = N'Cash'
              AND sale.Status = N'Completed'
              AND sale.BranchID = @branch;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@session", cashSessionId);
        command.Parameters.AddWithValue("@branch", branchId);
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
    }

    private static async Task<CashSessionException> ClosedOrMissingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PosDeviceContext actor,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) Status
            FROM dbo.POSCashSessions
            WHERE BranchID = @branch AND DeviceID = @device
            ORDER BY OpenedAt DESC;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@device", actor.DeviceId);
        var status = await command.ExecuteScalarAsync(cancellationToken) as string;
        return status == CashSessionStatus.Closed
            ? new CashSessionException(CashSessionCodes.AlreadyClosed, "الصندوق مغلق ولا يمكن إغلاقه مرة أخرى.", StatusCodes.Status409Conflict)
            : new CashSessionException(CashSessionCodes.NotOpen, "لا يوجد صندوق مفتوح لهذا الجهاز.", StatusCodes.Status409Conflict);
    }

    private static async Task<CashSessionState?> FindOpenAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PosDeviceContext actor,
        CancellationToken cancellationToken,
        bool lockRow = false)
    {
        var lockHint = lockRow ? "WITH (UPDLOCK, HOLDLOCK)" : "";
        await using var command = new SqlCommand(
            $"""
            SELECT
                session.CashSessionID,
                session.Status,
                opener.DisplayName,
                session.OpenedAt,
                session.OpeningCashAmount,
                session.ExpectedCashAmount,
                session.CountedCashAmount,
                session.CashDifference,
                session.ClosedAt
            FROM dbo.POSCashSessions AS session {lockHint}
            INNER JOIN dbo.Users AS opener ON opener.UserID = session.OpenedByUserID
            WHERE session.BranchID = @branch
              AND session.DeviceID = @device
              AND session.Status = N'Open';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@device", actor.DeviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    private static async Task<CashSessionState?> ReadByIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                session.CashSessionID,
                session.Status,
                opener.DisplayName,
                session.OpenedAt,
                session.OpeningCashAmount,
                session.ExpectedCashAmount,
                session.CountedCashAmount,
                session.CashDifference,
                session.ClosedAt
            FROM dbo.POSCashSessions AS session
            INNER JOIN dbo.Users AS opener ON opener.UserID = session.OpenedByUserID
            WHERE session.CashSessionID = @id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", cashSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    private static CashSessionState ReadState(SqlDataReader reader)
    {
        return new CashSessionState(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            reader.GetDecimal(4),
            reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc));
    }

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 19,
            Scale = 4,
            Value = value,
        });
    }
}
