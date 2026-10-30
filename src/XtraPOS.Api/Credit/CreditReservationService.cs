using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public sealed record CreditReserveResult(Guid AuthorizationId, string Status, CreditDecision Decision, bool Replay);

public sealed class CreditReservationService
{
    public async Task<CreditReserveResult> ReserveAsync(
        SqlConnection connection,
        Guid customerId,
        Guid branchId,
        decimal amount,
        string idempotencyKey,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var result = await ReserveCoreAsync(connection, transaction, customerId, branchId, amount, idempotencyKey, expiresAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task FinalizeAsync(
        SqlConnection connection,
        Guid authorizationId,
        Guid saleId,
        Guid actorUserId,
        string ledgerIdempotencyKey,
        DateTime dueDate,
        CancellationToken cancellationToken,
        Guid expectedCustomer = default,
        decimal? expectedAmount = null,
        Guid expectedBranch = default,
        long? sourceSaleNumber = null)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var existing = new SqlCommand(
            """
            SELECT SourceSaleID, CustomerID, Amount, SourceBranchID
            FROM dbo.CustomerCreditLedger WITH (UPDLOCK, HOLDLOCK)
            WHERE IdempotencyKey = @key;
            """,
            connection,
            transaction);
        existing.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = ledgerIdempotencyKey;
        await using (var existingReader = await existing.ExecuteReaderAsync(cancellationToken))
        {
            if (await existingReader.ReadAsync(cancellationToken))
            {
                var storedSale = existingReader.IsDBNull(0) ? Guid.Empty : existingReader.GetGuid(0);
                var storedCustomer = existingReader.GetGuid(1);
                var storedAmount = existingReader.GetDecimal(2);
                var storedBranch = existingReader.IsDBNull(3) ? Guid.Empty : existingReader.GetGuid(3);
                await existingReader.CloseAsync();
                if (storedSale != saleId
                    || (expectedCustomer != Guid.Empty && storedCustomer != expectedCustomer)
                    || (expectedAmount is decimal expected && storedAmount != expected)
                    || (expectedBranch != Guid.Empty && storedBranch != expectedBranch))
                {
                    await InsertAuditAsync(connection, transaction, storedCustomer, actorUserId, "FinalizeConflict", "Rejected", cancellationToken);
                    throw new InvalidOperationException("Credit finalization conflicted with an existing ledger entry.");
                }

                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using var auth = new SqlCommand(
            """
            SELECT CustomerID, BranchID, RequestedAmount, Status
            FROM dbo.CreditAuthorizations WITH (UPDLOCK, HOLDLOCK)
            WHERE AuthorizationID = @id;
            """,
            connection,
            transaction);
        auth.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        await using var reader = await auth.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Credit authorization was not found.");
        }

        var customerId = reader.GetGuid(0);
        var branchId = reader.GetGuid(1);
        var amount = reader.GetDecimal(2);
        var status = reader.GetString(3);
        await reader.CloseAsync();
        if (status is "Completed")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (status is not ("Authorized" or "Posting")
            || (expectedCustomer != Guid.Empty && customerId != expectedCustomer)
            || (expectedAmount is decimal authorizedAmount && amount != authorizedAmount)
            || (expectedBranch != Guid.Empty && branchId != expectedBranch))
        {
            throw new InvalidOperationException("Credit authorization cannot be finalized.");
        }

        var receivableId = Guid.NewGuid();
        var ledgerId = Guid.NewGuid();
        await using var insertReceivable = new SqlCommand(
            """
            INSERT INTO dbo.CustomerReceivables
                (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, SourceSaleNumber, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status)
            VALUES (@id, @customer, @sale, @branch, @saleNumber, @amount, @amount, @due, N'ILS', N'Open');
            """,
            connection,
            transaction);
        insertReceivable.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = receivableId;
        insertReceivable.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        insertReceivable.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        insertReceivable.Parameters.Add("@saleNumber", SqlDbType.BigInt).Value = (object?)sourceSaleNumber ?? DBNull.Value;
        insertReceivable.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insertReceivable.Parameters.Add("@amount", SqlDbType.Decimal).Value = amount;
        insertReceivable.Parameters.Add("@due", SqlDbType.Date).Value = dueDate;
        await insertReceivable.ExecuteNonQueryAsync(cancellationToken);

        await using var insertLedger = new SqlCommand(
            """
            INSERT INTO dbo.CustomerCreditLedger
                (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, CreatedBy, IdempotencyKey)
            VALUES (@id, @customer, @tx, N'CreditSale', @amount, N'ILS', @branch, @sale, @actor, @key);
            """,
            connection,
            transaction);
        insertLedger.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = ledgerId;
        insertLedger.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        insertLedger.Parameters.Add("@tx", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        insertLedger.Parameters.Add("@amount", SqlDbType.Decimal).Value = amount;
        insertLedger.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insertLedger.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        insertLedger.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
        insertLedger.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = ledgerIdempotencyKey;
        await insertLedger.ExecuteNonQueryAsync(cancellationToken);

        await using var complete = new SqlCommand(
            """
            UPDATE dbo.CreditAuthorizations
            SET Status = N'Completed', SourceSaleID = @sale
            WHERE AuthorizationID = @id AND Status IN (N'Authorized', N'Posting');
            """,
            connection,
            transaction);
        complete.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        complete.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        await complete.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(connection, transaction, customerId, actorUserId, "Finalize", "Completed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> BeginPostingAsync(SqlConnection connection, Guid authorizationId, Guid saleId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CreditAuthorizations
            SET Status = N'Posting',
                SourceSaleID = @sale,
                ExpiresAt = DATEADD(day, 7, SYSUTCDATETIME())
            WHERE AuthorizationID = @id
              AND Status = N'Authorized'
              AND (SourceSaleID IS NULL OR SourceSaleID = @sale);
            """,
            connection);
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return true;
        }

        await using var read = new SqlCommand(
            "SELECT Status, SourceSaleID FROM dbo.CreditAuthorizations WHERE AuthorizationID = @id;",
            connection);
        read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var status = reader.GetString(0);
        var linked = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        return status is "Posting" or "Completed" && linked == saleId;
    }

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, Guid actorUserId, string action, string result, CancellationToken cancellationToken)
    {
        if (customerId == Guid.Empty)
        {
            return;
        }

        await using var audit = new SqlCommand(
            """
            INSERT INTO dbo.CreditAuditLog (AuditID, CustomerID, ActorUserID, Action, Result)
            VALUES (NEWID(), @customer, @actor, @action, @result);
            """,
            connection,
            transaction);
        audit.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        audit.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
        audit.Parameters.Add("@action", SqlDbType.NVarChar, 50).Value = action;
        audit.Parameters.Add("@result", SqlDbType.NVarChar, 30).Value = result;
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseAsync(SqlConnection connection, Guid authorizationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CreditAuthorizations
            SET Status = N'Expired'
            WHERE AuthorizationID = @id AND Status = N'Authorized';
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CreditReserveResult> ReserveCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid customerId,
        Guid branchId,
        decimal amount,
        string idempotencyKey,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var replay = new SqlCommand(
            """
            SELECT AuthorizationID, Status
            FROM dbo.CreditAuthorizations WITH (UPDLOCK, HOLDLOCK)
            WHERE IdempotencyKey = @key;
            """,
            connection,
            transaction))
        {
            replay.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = idempotencyKey;
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var stored = reader.GetString(1);
                var replayDecision = stored is "Authorized" or "Completed" or "Posting"
                    ? CreditDecision.Approved
                    : CreditDecision.LimitExceeded;
                return new CreditReserveResult(reader.GetGuid(0), stored, replayDecision, true);
            }
        }

        await using var policy = new SqlCommand(
            """
            SELECT CreditEnabled, CreditStatus, CreditLimit
            FROM dbo.CustomerCreditPolicies WITH (UPDLOCK, HOLDLOCK)
            WHERE CustomerID = @customer;
            """,
            connection,
            transaction);
        policy.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var policyReader = await policy.ExecuteReaderAsync(cancellationToken);
        if (!await policyReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Customer credit policy was not found.");
        }

        var enabled = policyReader.GetBoolean(0);
        var status = policyReader.GetString(1);
        var limit = policyReader.GetDecimal(2);
        await policyReader.CloseAsync();

        var outstanding = await MoneyAsync(connection, transaction, """
            SELECT ISNULL(SUM(OutstandingAmount), 0)
            FROM dbo.CustomerReceivables WITH (UPDLOCK, HOLDLOCK)
            WHERE CustomerID = @customer AND Status IN (N'Open', N'Partial');
            """, customerId, cancellationToken);
        var reserved = await MoneyAsync(connection, transaction, """
            SELECT ISNULL(SUM(RequestedAmount), 0)
            FROM dbo.CreditAuthorizations WITH (UPDLOCK, HOLDLOCK)
            WHERE CustomerID = @customer
              AND (
                    Status = N'Posting'
                    OR (Status IN (N'Pending', N'Authorized') AND ExpiresAt > SYSUTCDATETIME())
                  );
            """, customerId, cancellationToken);
        var decision = CreditAvailability.Decide(enabled, status, limit, outstanding, reserved, amount);
        var authorizationId = Guid.NewGuid();
        var storedStatus = decision == CreditDecision.Approved ? "Authorized" : "Rejected";
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CreditAuthorizations
                (AuthorizationID, CustomerID, BranchID, RequestedAmount, CurrencyCode, Status, IdempotencyKey, ExpiresAt)
            VALUES (@id, @customer, @branch, @amount, N'ILS', @status, @key, @expires);
            """,
            connection,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = authorizationId;
        insert.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = amount;
        insert.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = storedStatus;
        insert.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = idempotencyKey;
        insert.Parameters.Add("@expires", SqlDbType.DateTime2).Value = expiresAtUtc;
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new CreditReserveResult(authorizationId, storedStatus, decision, false);
    }

    private static async Task<decimal> MoneyAsync(SqlConnection connection, SqlTransaction transaction, string sql, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is decimal money ? money : 0m;
    }
}
