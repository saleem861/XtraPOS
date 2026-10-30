using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Credit;

public enum CreditReversalOutcome
{
    Completed,
    PendingCentral,
    Rejected,
    Unauthorized,
    ReconciliationRequired,
    CentralUnavailable,
}

public sealed record CreditReversalRequest(
    Guid OperationId,
    Guid CollectionOperationId,
    Guid CustomerId,
    Guid BranchId,
    Guid DeviceId,
    Guid ActorUserId,
    Guid CashSessionId,
    string ReasonCode,
    string Notes);

public sealed class CreditReversalService
{
    public async Task<CreditReversalOutcome> ReverseAsync(
        SqlConnection central,
        SqlConnection branch,
        CreditReversalRequest request,
        bool centralReachable,
        bool skipFinalization,
        CancellationToken cancellationToken)
    {
        var effect = CashEffect(request.ReasonCode);
        if (effect is null || request.OperationId == Guid.Empty || request.CollectionOperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.Notes) || request.Notes.Trim().Length > 400)
        {
            return CreditReversalOutcome.Rejected;
        }

        if (!await HasPermissionAsync(branch, request.ActorUserId, cancellationToken))
        {
            return CreditReversalOutcome.Unauthorized;
        }

        var openSession = await OpenSessionAsync(branch, request.BranchId, request.DeviceId, cancellationToken);
        if (openSession is null || (request.CashSessionId != Guid.Empty && request.CashSessionId != openSession))
        {
            return CreditReversalOutcome.Rejected;
        }

        var collection = await ReadCollectionAsync(branch, request, cancellationToken);
        if (collection is null)
        {
            return CreditReversalOutcome.Rejected;
        }

        var existing = await ReadLocalAsync(branch, request.OperationId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(central, branch, request, existing.Value.Payload, skipFinalization, cancellationToken);
        }

        if (await CollectionAlreadyReversedAsync(branch, request.CollectionOperationId, cancellationToken))
        {
            return CreditReversalOutcome.Rejected;
        }

        if (!centralReachable)
        {
            return CreditReversalOutcome.CentralUnavailable;
        }

        var authorized = await AuthorizeAsync(central, request, collection.Value, cancellationToken);
        if (authorized != CreditReversalOutcome.Completed)
        {
            return authorized;
        }

        try
        {
            await CommitBranchAsync(branch, request, openSession.Value, effect, cancellationToken);
        }
        catch (SqlException)
        {
            return CreditReversalOutcome.Rejected;
        }

        await MarkPostingAsync(central, request.OperationId, cancellationToken);
        if (skipFinalization)
        {
            return CreditReversalOutcome.PendingCentral;
        }

        return await FinalizeAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
    }

    public async Task<CreditReversalOutcome?> RecoverOnceAsync(SqlConnection central, SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        var operationId = await ClaimAsync(branch, workerId, cancellationToken);
        if (operationId is null)
        {
            return null;
        }

        return await FinalizeAsync(central, branch, operationId.Value, Guid.Empty, cancellationToken, workerId);
    }

    private async Task<CreditReversalOutcome> ReplayAsync(
        SqlConnection central,
        SqlConnection branch,
        CreditReversalRequest request,
        string payload,
        bool skipFinalization,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payload, Payload(request), StringComparison.Ordinal))
        {
            return CreditReversalOutcome.ReconciliationRequired;
        }

        return skipFinalization
            ? CreditReversalOutcome.PendingCentral
            : await FinalizeAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
    }

    private async Task<CreditReversalOutcome> AuthorizeAsync(
        SqlConnection central,
        CreditReversalRequest request,
        CollectionRow collection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT Status, CustomerID, BranchID, Amount
            FROM dbo.CreditCollectionIntents WITH (UPDLOCK, HOLDLOCK)
            WHERE CollectionOperationID = @collection;
            """,
            central, transaction);
        command.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = request.CollectionOperationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetString(0) != "Completed"
            || reader.GetGuid(1) != request.CustomerId
            || reader.GetGuid(2) != request.BranchId
            || reader.GetDecimal(3) != collection.Amount)
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            return CreditReversalOutcome.Rejected;
        }

        await reader.DisposeAsync();
        try
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.CreditRepaymentReversals
                    (ReversalOperationID, CollectionOperationID, CustomerID, BranchID, Amount, ReasonCode, Status, IdempotencyKey, CreatedBy, ExpiresAt)
                VALUES (@operation, @collection, @customer, @branch, @amount, @reason, N'Authorized', @key, @actor, DATEADD(minute, 15, SYSUTCDATETIME()));
                """,
                central, transaction);
            insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            insert.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = request.CollectionOperationId;
            insert.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
            insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = collection.Amount;
            insert.Parameters["@amount"].Precision = 19;
            insert.Parameters["@amount"].Scale = 4;
            insert.Parameters.Add("@reason", SqlDbType.NVarChar, 40).Value = request.ReasonCode;
            insert.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = request.OperationId.ToString("N");
            insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = request.ActorUserId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreditReversalOutcome.Completed;
        }
        catch (SqlException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreditReversalOutcome.Rejected;
        }
    }

    private static async Task CommitBranchAsync(SqlConnection branch, CreditReversalRequest request, Guid sessionId, string effect, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.BranchCollectionReversals
                (ReversalOperationID, CollectionOperationID, BranchID, CashSessionID, DeviceID, ActorUserID, Amount, ReasonCode, CashEffect, Status)
            VALUES (@operation, @collection, @branch, @session, @device, @actor, @amount, @reason, @effect, N'Pending');
            INSERT INTO dbo.BranchReversalOutbox
                (OutboxEventID, ReversalOperationID, BranchID, Payload, Status, NextRetryAt)
            VALUES (NEWID(), @operation, @branch, @payload, N'Pending', SYSUTCDATETIME());
            """,
            branch, transaction);
        insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
        insert.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = request.CollectionOperationId;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
        insert.Parameters.Add("@session", SqlDbType.UniqueIdentifier).Value = sessionId;
        insert.Parameters.Add("@device", SqlDbType.UniqueIdentifier).Value = request.DeviceId;
        insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = request.ActorUserId;
        insert.Parameters.Add("@amount", SqlDbType.Decimal).Value = await CollectionAmountAsync(branch, transaction, request.CollectionOperationId, cancellationToken);
        insert.Parameters["@amount"].Precision = 19;
        insert.Parameters["@amount"].Scale = 4;
        insert.Parameters.Add("@reason", SqlDbType.NVarChar, 40).Value = request.ReasonCode;
        insert.Parameters.Add("@effect", SqlDbType.NVarChar, 30).Value = effect;
        insert.Parameters.Add("@payload", SqlDbType.NVarChar, 2000).Value = Payload(request);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CreditReversalOutcome> FinalizeAsync(
        SqlConnection central,
        SqlConnection branch,
        Guid operationId,
        Guid actorUserId,
        CancellationToken cancellationToken,
        string? leaseOwner = null)
    {
        var box = await ReadLocalAsync(branch, operationId, cancellationToken);
        if (box is null)
        {
            return CreditReversalOutcome.ReconciliationRequired;
        }

        try
        {
            await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
            await using var read = new SqlCommand(
                """
                SELECT Status, CustomerID, BranchID, Amount, CollectionOperationID, ReasonCode, CreatedBy
                FROM dbo.CreditRepaymentReversals WITH (UPDLOCK, HOLDLOCK)
                WHERE ReversalOperationID = @operation;
                """,
                central, transaction);
            read.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                await MarkAsync(branch, operationId, "ReconciliationRequired", "Reversal authorization is missing.", leaseOwner, cancellationToken);
                return CreditReversalOutcome.ReconciliationRequired;
            }

            var status = reader.GetString(0);
            var customerId = reader.GetGuid(1);
            var branchId = reader.GetGuid(2);
            var amount = reader.GetDecimal(3);
            var collectionId = reader.GetGuid(4);
            _ = reader.GetString(5);
            var createdBy = reader.GetGuid(6);
            await reader.DisposeAsync();
            if (status == "Completed")
            {
                await transaction.CommitAsync(cancellationToken);
                await MarkAsync(branch, operationId, "Completed", null, leaseOwner, cancellationToken);
                return CreditReversalOutcome.Completed;
            }

            if (status is not "Posting")
            {
                await transaction.RollbackAsync(cancellationToken);
                await MarkAsync(branch, operationId, "ReconciliationRequired", "Reversal authorization is not posting.", leaseOwner, cancellationToken);
                return CreditReversalOutcome.ReconciliationRequired;
            }

            var actor = actorUserId == Guid.Empty ? createdBy : actorUserId;
            var ledgerId = Guid.NewGuid();
            await using (var ledger = new SqlCommand(
                """
                INSERT INTO dbo.CustomerCreditLedger
                    (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, PaymentID, CreatedBy, ReversalReference, IdempotencyKey)
                SELECT @entry, @customer, @operation, N'Reversal', @amount, N'ILS', @branch, @collection, @actor, original.LedgerEntryID, @key
                FROM dbo.CustomerCreditLedger AS original
                WHERE original.IdempotencyKey = @originalKey;
                """,
                central, transaction))
            {
                ledger.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = ledgerId;
                ledger.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
                ledger.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
                ledger.Parameters.Add("@amount", SqlDbType.Decimal).Value = amount;
                ledger.Parameters["@amount"].Precision = 19;
                ledger.Parameters["@amount"].Scale = 4;
                ledger.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
                ledger.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = collectionId;
                ledger.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actor;
                ledger.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = "reversal:" + operationId.ToString("N");
                ledger.Parameters.Add("@originalKey", SqlDbType.NVarChar, 80).Value = "collection:" + collectionId.ToString("N");
                if (await ledger.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await MarkAsync(branch, operationId, "ReconciliationRequired", "Original repayment ledger is missing.", leaseOwner, cancellationToken);
                    return CreditReversalOutcome.ReconciliationRequired;
                }
            }

            await using (var apply = new SqlCommand(
                """
                INSERT INTO dbo.CreditAllocationReversals
                    (ReversalAllocationID, ReversalOperationID, OriginalAllocationID, ReceivableID, Amount, LedgerEntryID)
                SELECT NEWID(), @operation, allocation.AllocationID, allocation.ReceivableID, allocation.Amount, @entry
                FROM dbo.CreditPaymentAllocations AS allocation
                INNER JOIN dbo.CustomerCreditLedger AS original ON original.LedgerEntryID = allocation.LedgerEntryID
                WHERE original.IdempotencyKey = @originalKey;

                UPDATE receivable
                SET OutstandingAmount = receivable.OutstandingAmount + allocation.Amount,
                    Status = CASE
                        WHEN receivable.OutstandingAmount + allocation.Amount = receivable.OriginalAmount THEN N'Open'
                        ELSE N'Partial' END
                FROM dbo.CustomerReceivables AS receivable
                INNER JOIN dbo.CreditPaymentAllocations AS allocation ON allocation.ReceivableID = receivable.ReceivableID
                INNER JOIN dbo.CustomerCreditLedger AS original ON original.LedgerEntryID = allocation.LedgerEntryID
                WHERE original.IdempotencyKey = @originalKey;

                UPDATE dbo.CreditRepaymentReversals
                SET Status = N'Completed', LedgerEntryID = @entry
                WHERE ReversalOperationID = @operation;

                INSERT INTO dbo.CreditAuditLog
                    (AuditID, CustomerID, ActorUserID, BranchID, Action, OldValue, NewValue, Reason, Result)
                SELECT NEWID(), @customer, @actor, @branch, N'RepaymentReversal',
                    CONVERT(nvarchar(40), SUM(receivable.OutstandingAmount - allocation.Amount)),
                    CONVERT(nvarchar(40), SUM(receivable.OutstandingAmount)),
                    @reason, N'Completed'
                FROM dbo.CustomerReceivables AS receivable
                INNER JOIN dbo.CreditPaymentAllocations AS allocation ON allocation.ReceivableID = receivable.ReceivableID
                INNER JOIN dbo.CustomerCreditLedger AS original ON original.LedgerEntryID = allocation.LedgerEntryID
                WHERE original.IdempotencyKey = @originalKey;
                """,
                central, transaction))
            {
                apply.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
                apply.Parameters.Add("@entry", SqlDbType.UniqueIdentifier).Value = ledgerId;
                apply.Parameters.Add("@originalKey", SqlDbType.NVarChar, 80).Value = "collection:" + collectionId.ToString("N");
                apply.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
                apply.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actor;
                apply.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
                apply.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = await NotesAsync(branch, operationId, cancellationToken);
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await MarkAsync(branch, operationId, "Completed", null, leaseOwner, cancellationToken);
            return CreditReversalOutcome.Completed;
        }
        catch (Exception)
        {
            await MarkAsync(branch, operationId, "Pending", "Central reversal finalization is temporarily unavailable.", leaseOwner, cancellationToken);
            return CreditReversalOutcome.PendingCentral;
        }
    }

    private static async Task MarkPostingAsync(SqlConnection central, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "UPDATE dbo.CreditRepaymentReversals SET Status = N'Posting' WHERE ReversalOperationID = @operation AND Status = N'Authorized';",
            central);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkAsync(SqlConnection branch, Guid operationId, string status, string? error, string? leaseOwner, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchCollectionReversals SET Status = @status WHERE ReversalOperationID = @operation;
            UPDATE dbo.BranchReversalOutbox
            SET Status = @status, LastError = @error, LeaseOwner = NULL, LeaseUntil = NULL
            WHERE ReversalOperationID = @operation
              AND (@owner IS NULL OR LeaseOwner = @owner OR LeaseOwner IS NULL);
            """,
            branch);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status == "PendingCentral" ? "Pending" : status;
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 400).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = (object?)leaseOwner ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> ClaimAsync(SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchReversalOutbox
            SET LeaseOwner = @owner, LeaseUntil = DATEADD(minute, 2, SYSUTCDATETIME()), AttemptCount = AttemptCount + 1
            OUTPUT inserted.ReversalOperationID
            WHERE OutboxEventID = (
                SELECT TOP (1) OutboxEventID
                FROM dbo.BranchReversalOutbox WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE Status = N'Pending'
                  AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                  AND NextRetryAt <= DATEADD(second, 1, SYSUTCDATETIME())
                ORDER BY NextRetryAt);
            """,
            branch);
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = workerId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<(string Payload, string Status)?> ReadLocalAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT Payload, Status FROM dbo.BranchReversalOutbox WHERE ReversalOperationID = @operation;",
            branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<bool> CollectionAlreadyReversedAsync(SqlConnection branch, Guid collectionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.BranchCollectionReversals WHERE CollectionOperationID = @collection) THEN 1 ELSE 0 END;",
            branch);
        command.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = collectionId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private readonly record struct CollectionRow(decimal Amount);

    private static async Task<CollectionRow?> ReadCollectionAsync(SqlConnection branch, CreditReversalRequest request, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT Amount
            FROM dbo.BranchDebtCollections
            WHERE CollectionOperationID = @collection
              AND BranchID = @branch
              AND CustomerID = @customer
              AND Status = N'Completed'
              AND PaymentMethodCode = N'Cash';
            """,
            branch);
        command.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = request.CollectionOperationId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is decimal amount ? new CollectionRow(amount) : null;
    }

    private static async Task<decimal> CollectionAmountAsync(SqlConnection branch, SqlTransaction transaction, Guid collectionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT Amount FROM dbo.BranchDebtCollections WHERE CollectionOperationID = @collection;",
            branch, transaction);
        command.Parameters.Add("@collection", SqlDbType.UniqueIdentifier).Value = collectionId;
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
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

    private static async Task<bool> HasPermissionAsync(SqlConnection branch, Guid userId, CancellationToken cancellationToken)
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
                  AND permission.PermissionCode IN (N'Credit.Reverse', N'System.Administer')
            ) THEN 1 ELSE 0 END;
            """,
            branch);
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = userId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static string? CashEffect(string reason) => reason switch
    {
        "RefundToCustomer" => "RefundOutflow",
        "IncorrectRecording" => "RecordingCorrection",
        _ => null,
    };

    private static async Task<string> NotesAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT Payload FROM dbo.BranchReversalOutbox WHERE ReversalOperationID = @operation;", branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        var payload = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "";
        var parts = payload.Split('|');
        return parts.Length > 4 ? parts[4] : parts[0];
    }

    private static string Payload(CreditReversalRequest request) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{request.ReasonCode}|{request.CollectionOperationId:N}|{request.CustomerId:N}|{request.BranchId:N}|{request.Notes.Trim()}");
}
