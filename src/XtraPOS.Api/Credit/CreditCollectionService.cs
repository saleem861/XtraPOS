using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Credit;

public enum CreditCollectionOutcome
{
    Completed,
    PendingCentral,
    Refused,
    Unauthorized,
    CentralUnavailable,
    LocalRolledBack,
    ReconciliationRequired,
}

public sealed record CreditCollectionAllocation(Guid ReceivableId, decimal Amount);

public sealed record CreditCollectionQuote(decimal Outstanding, decimal Collectible, IReadOnlyList<CreditCollectionAllocation> Allocations, string? Error);

public sealed record CreditCollectionRequest(
    Guid OperationId,
    Guid CustomerId,
    Guid BranchId,
    Guid DeviceId,
    Guid ActorUserId,
    Guid CashSessionId,
    decimal Amount,
    string PaymentMethod,
    Guid? SelectedReceivableId);

public sealed class CreditCollectionService
{
    public async Task<CreditCollectionQuote> QuoteAsync(SqlConnection central, Guid customerId, decimal amount, Guid? selectedReceivableId, CancellationToken cancellationToken)
    {
        var rows = await LoadCollectibleAsync(central, null, customerId, cancellationToken);
        return Propose(rows, amount, selectedReceivableId);
    }

    public async Task<CreditCollectionOutcome> CollectAsync(
        SqlConnection central,
        SqlConnection branch,
        CreditCollectionRequest request,
        bool centralReachable,
        bool failLocalCommit,
        bool skipFinalization,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.PaymentMethod, "Cash", StringComparison.Ordinal) || request.Amount <= 0m)
        {
            return CreditCollectionOutcome.Refused;
        }

        if (!await HasPermissionAsync(branch, request.ActorUserId, PermissionCodes.CreditCollect, cancellationToken))
        {
            return CreditCollectionOutcome.Unauthorized;
        }

        var openSession = await OpenSessionAsync(branch, request.BranchId, request.DeviceId, cancellationToken);
        if (openSession is null || (request.CashSessionId != Guid.Empty && request.CashSessionId != openSession))
        {
            return CreditCollectionOutcome.Refused;
        }

        var existing = await ReadOutboxAsync(branch, request.OperationId, cancellationToken);
        if (existing is not null)
        {
            if (!PayloadMatches(existing.Value.Payload, request))
            {
                return CreditCollectionOutcome.ReconciliationRequired;
            }

            if (existing.Value.Status == "Completed")
            {
                return CreditCollectionOutcome.Completed;
            }

            if (existing.Value.Status == "ReconciliationRequired")
            {
                return CreditCollectionOutcome.ReconciliationRequired;
            }

            return skipFinalization
                ? CreditCollectionOutcome.PendingCentral
                : await FinalizeAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
        }

        if (!centralReachable)
        {
            return CreditCollectionOutcome.CentralUnavailable;
        }

        var authorized = await AuthorizeAsync(central, request, cancellationToken);
        if (authorized != CreditCollectionOutcome.Completed)
        {
            return authorized;
        }

        try
        {
            await CommitBranchAsync(branch, request, openSession.Value, failLocalCommit, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await ReleaseAsync(central, request.OperationId, cancellationToken);
            return CreditCollectionOutcome.LocalRolledBack;
        }

        await using (var posting = new SqlCommand(
            "UPDATE dbo.CreditCollectionIntents SET Status = N'Posting' WHERE CollectionOperationID = @operation AND Status = N'Authorized';",
            central))
        {
            posting.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            await posting.ExecuteNonQueryAsync(cancellationToken);
        }

        if (skipFinalization)
        {
            return CreditCollectionOutcome.PendingCentral;
        }

        return await FinalizeAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
    }

    public async Task<CreditCollectionOutcome?> RecoverOnceAsync(SqlConnection central, SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        var operationId = await ClaimAsync(branch, workerId, cancellationToken);
        if (operationId is null)
        {
            return null;
        }

        return await FinalizeAsync(central, branch, operationId.Value, Guid.Empty, cancellationToken, workerId);
    }

    private async Task<CreditCollectionOutcome> AuthorizeAsync(SqlConnection central, CreditCollectionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        var replay = await ReadIntentAsync(central, transaction, request.OperationId, cancellationToken);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (replay.Value.CustomerId != request.CustomerId || replay.Value.Amount != request.Amount || replay.Value.BranchId != request.BranchId)
                {
                    return CreditCollectionOutcome.ReconciliationRequired;
                }

                return replay.Value.Status == "Released" ? CreditCollectionOutcome.Refused : CreditCollectionOutcome.Completed;
            }

        var rows = await LoadCollectibleAsync(central, transaction, request.CustomerId, cancellationToken);
        var quote = Propose(rows, request.Amount, request.SelectedReceivableId);
        if (quote.Error is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreditCollectionOutcome.Refused;
        }

        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.CreditCollectionIntents
                (CollectionOperationID, CustomerID, BranchID, Amount, PaymentMethodCode, Status, IdempotencyKey, ExpiresAt, CreatedBy)
            VALUES (@operation, @customer, @branch, @amount, N'Cash', N'Authorized', @key, DATEADD(minute, 15, SYSUTCDATETIME()), @actor);
            """,
            central, transaction))
        {
            insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            insert.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
            insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = request.Amount;
            insert.Parameters["@amount"].Precision = 19;
            insert.Parameters["@amount"].Scale = 4;
            insert.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = request.OperationId.ToString("N");
            insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = request.ActorUserId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var allocation in quote.Allocations)
        {
            await using var reservation = new SqlCommand(
                """
                INSERT INTO dbo.CreditCollectionReservations (ReservationID, CollectionOperationID, ReceivableID, Amount)
                VALUES (NEWID(), @operation, @receivable, @amount);
                """,
                central, transaction);
            reservation.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            reservation.Parameters.Add("@receivable", SqlDbType.UniqueIdentifier).Value = allocation.ReceivableId;
            reservation.Parameters.Add("@amount", SqlDbType.Decimal).Value = allocation.Amount;
            reservation.Parameters["@amount"].Precision = 19;
            reservation.Parameters["@amount"].Scale = 4;
            await reservation.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return CreditCollectionOutcome.Completed;
    }

    private async Task CommitBranchAsync(SqlConnection branch, CreditCollectionRequest request, Guid sessionId, bool fail, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        if (fail)
        {
            throw new InvalidOperationException("Branch collection commit failed.");
        }

        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.BranchDebtCollections
                (CollectionOperationID, CustomerID, BranchID, CashSessionID, DeviceID, CashierUserID, Amount, PaymentMethodCode, Status)
            VALUES (@operation, @customer, @branch, @session, @device, @actor, @amount, N'Cash', N'Pending');
            INSERT INTO dbo.BranchCollectionOutbox
                (OutboxEventID, CreditOperationID, BranchID, CustomerID, Payload, Status, NextRetryAt)
            VALUES (NEWID(), @operation, @branch, @customer, @payload, N'Pending', SYSUTCDATETIME());
            """,
            branch, transaction))
        {
            insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            insert.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
            insert.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
            insert.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = request.DeviceId;
            insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = request.ActorUserId;
            insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = request.Amount;
            insert.Parameters["@amount"].Precision = 19;
            insert.Parameters["@amount"].Scale = 4;
            insert.Parameters.Add("@payload", SqlDbType.NVarChar, 2000).Value = Payload(request);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CreditCollectionOutcome> FinalizeAsync(SqlConnection central, SqlConnection branch, Guid operationId, Guid actorUserId, CancellationToken cancellationToken, string? leaseOwner = null)
    {
        var box = await ReadOutboxAsync(branch, operationId, cancellationToken);
        if (box is null)
        {
            return CreditCollectionOutcome.ReconciliationRequired;
        }

        if (box.Value.Status == "Completed")
        {
            return CreditCollectionOutcome.Completed;
        }

        try
        {
            await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
            var intent = await ReadIntentAsync(central, transaction, operationId, cancellationToken);
            if (intent is null || intent.Value.Status is "Released" || intent.Value.Status == "Authorized")
            {
                await transaction.RollbackAsync(cancellationToken);
                await MarkAsync(branch, operationId, "ReconciliationRequired", "Collection authorization is not in posting.", leaseOwner, cancellationToken);
                return CreditCollectionOutcome.ReconciliationRequired;
            }

            if (intent.Value.Status == "Completed" || await LedgerExistsAsync(central, transaction, operationId, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                await MarkAsync(branch, operationId, "Completed", null, leaseOwner, cancellationToken);
                return CreditCollectionOutcome.Completed;
            }

            var ledgerId = Guid.NewGuid();
            await using (var ledger = new SqlCommand(
                """
                INSERT INTO dbo.CustomerCreditLedger
                    (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, PaymentID, CreatedBy, IdempotencyKey)
                VALUES (@entry, @customer, @operation, N'Repayment', @amount, N'ILS', @branch, @operation, @actor, @key);
                """,
                central, transaction))
            {
                ledger.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = ledgerId;
                ledger.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = intent.Value.CustomerId;
                ledger.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
                ledger.Parameters.Add("@amount", SqlDbType.Decimal).Value = intent.Value.Amount;
                ledger.Parameters["@amount"].Precision = 19;
                ledger.Parameters["@amount"].Scale = 4;
                ledger.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = intent.Value.BranchId;
                ledger.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId == Guid.Empty ? intent.Value.CreatedBy : actorUserId;
                ledger.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = "collection:" + operationId.ToString("N");
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var apply = new SqlCommand(
                """
                INSERT INTO dbo.CreditPaymentAllocations (AllocationID, LedgerEntryID, ReceivableID, Amount)
                SELECT NEWID(), @entry, reservation.ReceivableID, reservation.Amount
                FROM dbo.CreditCollectionReservations AS reservation
                WHERE reservation.CollectionOperationID = @operation;

                UPDATE receivable
                SET OutstandingAmount = receivable.OutstandingAmount - reservation.Amount,
                    Status = CASE
                        WHEN receivable.OutstandingAmount - reservation.Amount = 0 THEN N'Settled'
                        ELSE N'Partial' END
                FROM dbo.CustomerReceivables AS receivable
                INNER JOIN dbo.CreditCollectionReservations AS reservation
                    ON reservation.ReceivableID = receivable.ReceivableID
                WHERE reservation.CollectionOperationID = @operation;

                UPDATE dbo.CreditCollectionIntents
                SET Status = N'Completed'
                WHERE CollectionOperationID = @operation;
                """,
                central, transaction))
            {
                apply.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = ledgerId;
                apply.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await MarkAsync(branch, operationId, "Completed", null, leaseOwner, cancellationToken);
            return CreditCollectionOutcome.Completed;
        }
        catch (Exception)
        {
            await MarkAsync(branch, operationId, "Pending", "Central collection finalization is temporarily unavailable.", leaseOwner, cancellationToken);
            return CreditCollectionOutcome.PendingCentral;
        }
    }

    private static async Task ReleaseAsync(SqlConnection central, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CreditCollectionIntents
            SET Status = N'Released'
            WHERE CollectionOperationID = @operation AND Status = N'Authorized';
            """,
            central);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CreditCollectionQuote Propose(IReadOnlyList<CollectibleRow> rows, decimal amount, Guid? selectedReceivableId)
    {
        var outstanding = rows.Sum(row => row.Outstanding);
        var pool = selectedReceivableId is Guid selected
            ? rows.Where(row => row.ReceivableId == selected).ToArray()
            : rows.OrderBy(row => row.DueDate).ThenBy(row => row.ReceivableId).ToArray();
        if (selectedReceivableId is not null && pool.Length == 0)
        {
            return new CreditCollectionQuote(outstanding, 0m, [], "The invoice is not collectible.");
        }

        var collectible = pool.Sum(row => row.Collectible);
        if (amount <= 0m || amount > collectible)
        {
            return new CreditCollectionQuote(outstanding, collectible, [], "The payment exceeds the collectible balance.");
        }

        var remaining = amount;
        var allocations = new List<CreditCollectionAllocation>();
        foreach (var row in pool)
        {
            if (remaining == 0m)
            {
                break;
            }

            var take = Math.Min(row.Collectible, remaining);
            if (take <= 0m)
            {
                continue;
            }

            allocations.Add(new CreditCollectionAllocation(row.ReceivableId, take));
            remaining -= take;
        }

        return new CreditCollectionQuote(outstanding, collectible, allocations, null);
    }

    private static async Task<IReadOnlyList<CollectibleRow>> LoadCollectibleAsync(SqlConnection central, SqlTransaction? transaction, Guid customerId, CancellationToken cancellationToken)
    {
        var rows = new List<CollectibleRow>();
        await using var command = new SqlCommand(
            """
            SELECT receivable.ReceivableID, receivable.OutstandingAmount, receivable.DueDate,
                   COALESCE((
                       SELECT SUM(reservation.Amount)
                       FROM dbo.CreditCollectionReservations AS reservation
                       INNER JOIN dbo.CreditCollectionIntents AS intent ON intent.CollectionOperationID = reservation.CollectionOperationID
                       WHERE reservation.ReceivableID = receivable.ReceivableID
                         AND (intent.Status = N'Posting'
                              OR (intent.Status = N'Authorized' AND intent.ExpiresAt > SYSUTCDATETIME()))
                   ), 0)
            FROM dbo.CustomerReceivables AS receivable WITH (UPDLOCK, HOLDLOCK)
            WHERE receivable.CustomerID = @customer AND receivable.OutstandingAmount > 0 AND receivable.Status IN (N'Open', N'Partial')
            ORDER BY receivable.DueDate, receivable.ReceivableID;
            """,
            central, transaction);
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var outstanding = reader.GetDecimal(1);
            var reserved = reader.GetDecimal(3);
            rows.Add(new CollectibleRow(reader.GetGuid(0), outstanding, reader.GetDateTime(2), Math.Max(0m, outstanding - reserved)));
        }

        return rows;
    }

    private static async Task<(Guid CustomerId, Guid BranchId, decimal Amount, Guid CreatedBy, string Status)?> ReadIntentAsync(SqlConnection central, SqlTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CustomerID, BranchID, Amount, CreatedBy, Status FROM dbo.CreditCollectionIntents WITH (UPDLOCK, HOLDLOCK) WHERE CollectionOperationID = @operation;",
            central, transaction);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2), reader.GetGuid(3), reader.GetString(4));
    }

    private static async Task<bool> LedgerExistsAsync(SqlConnection central, SqlTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = @key;", central, transaction);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = "collection:" + operationId.ToString("N");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<(string Status, string Payload)?> ReadOutboxAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT Status, Payload FROM dbo.BranchCollectionOutbox WHERE CreditOperationID = @operation;", branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<Guid?> OpenSessionAsync(SqlConnection branch, Guid branchId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CashSessionID FROM dbo.POSCashSessions WHERE BranchID = @branch AND DeviceID = @device AND Status = N'Open';",
            branch);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = deviceId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<Guid?> ClaimAsync(SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchCollectionOutbox
            SET LeaseOwner = @owner,
                LeaseUntil = DATEADD(minute, 2, SYSUTCDATETIME()),
                AttemptCount = AttemptCount + 1,
                LastAttemptAt = SYSUTCDATETIME()
            OUTPUT inserted.CreditOperationID
            WHERE OutboxEventID = (
                SELECT TOP (1) OutboxEventID
                FROM dbo.BranchCollectionOutbox WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE Status = N'Pending'
                  AND NextRetryAt <= DATEADD(second, 1, SYSUTCDATETIME())
                  AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                ORDER BY CreatedAt);
            """,
            branch);
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = workerId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task MarkAsync(SqlConnection branch, Guid operationId, string status, string? error, string? leaseOwner, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchCollectionOutbox
            SET Status = @status,
                LastError = @error,
                CompletedAt = CASE WHEN @status = N'Completed' THEN SYSUTCDATETIME() ELSE CompletedAt END,
                NextRetryAt = DATEADD(second, 5, SYSUTCDATETIME()),
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE CreditOperationID = @operation
              AND Status <> N'Completed'
              AND (@owner IS NULL OR LeaseOwner IS NULL OR LeaseOwner = @owner);
            UPDATE dbo.BranchDebtCollections
            SET Status = @status
            WHERE CollectionOperationID = @operation AND Status <> N'Completed';
            """,
            branch, transaction);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 400).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = (object?)leaseOwner ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> HasPermissionAsync(SqlConnection branch, Guid userId, string permissionCode, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM dbo.UserRoles AS assignment
                INNER JOIN dbo.Roles AS role ON role.RoleID = assignment.RoleID AND role.IsActive = 1
                INNER JOIN dbo.RolePermissions AS grantRow ON grantRow.RoleID = role.RoleID
                INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = grantRow.PermissionID AND permission.IsActive = 1
                WHERE assignment.UserID = @user
                  AND (permission.PermissionCode = @code OR permission.PermissionCode = N'System.Administer')
            ) THEN 1 ELSE 0 END;
            """,
            branch);
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = permissionCode;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static string Payload(CreditCollectionRequest request)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{request.Amount}|{request.CustomerId:N}|{request.BranchId:N}|{request.SelectedReceivableId:N}");
    }

    private static bool PayloadMatches(string payload, CreditCollectionRequest request)
    {
        return string.Equals(payload, Payload(request), StringComparison.Ordinal);
    }

    private sealed record CollectibleRow(Guid ReceivableId, decimal Outstanding, DateTime DueDate, decimal Collectible);
}
