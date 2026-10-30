using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Credit;

public enum CreditSaleOutcome
{
    Completed,
    PendingCentral,
    Refused,
    Unauthorized,
    CentralUnavailable,
    LocalRolledBack,
    ReconciliationRequired,
}

public sealed record CreditSaleOutcomeResult(CreditSaleOutcome Outcome, Guid? SaleId, Guid? AuthorizationId);

public sealed class CreditSaleCoordinator(CreditReservationService credit, CreditSaleDocumentWriter documents)
{
    public async Task<CreditSaleOutcomeResult> StageAsync(
        SqlConnection central,
        SqlConnection branch,
        CreditSaleRequest request,
        bool centralReachable,
        bool failLocalCommit,
        bool skipFinalization,
        Func<Task>? afterLocalCommit,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(branch, request.ActorUserId, PermissionCodes.CreditCreate, cancellationToken))
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.Unauthorized, null, null);
        }

        var replayExists = false;
        await using (var existing = new SqlCommand("SELECT COUNT(*) FROM dbo.BranchFinancialOutbox WHERE CreditOperationID = @operation;", branch))
        {
            existing.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            replayExists = Convert.ToInt32(await existing.ExecuteScalarAsync(cancellationToken)) > 0;
        }

        CreditQuote quote;
        try
        {
            quote = await documents.QuoteAsync(branch, request, cancellationToken);
        }
        catch (CreditConflictException)
        {
            if (replayExists)
            {
                await MarkOutboxAsync(branch, request.OperationId, "ReconciliationRequired", "Replay payload does not match the priced sale.", cancellationToken);
                return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, request.SaleId, null);
            }

            return new CreditSaleOutcomeResult(CreditSaleOutcome.Refused, null, null);
        }

        if (quote.Deferred <= 0m || request.DeferredAmount != quote.Deferred)
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.Refused, null, null);
        }

        if (!centralReachable)
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.CentralUnavailable, null, null);
        }

        await using (var existing = new SqlCommand("SELECT Payload FROM dbo.BranchFinancialOutbox WHERE CreditOperationID = @operation;", branch))
        {
            existing.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
            if (await existing.ExecuteScalarAsync(cancellationToken) is string stored)
            {
                if (!TryReadPayload(stored, out var storedDeferred, out var storedCustomer, out var storedBranch, out _)
                    || storedDeferred != quote.Deferred
                    || storedCustomer != request.CustomerId
                    || storedBranch != request.BranchId)
                {
                    await MarkOutboxAsync(branch, request.OperationId, "ReconciliationRequired", "Replay payload does not match the priced sale.", cancellationToken);
                    return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, request.SaleId, null);
                }

                return await DeliverAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
            }
        }

        var reserved = await credit.ReserveAsync(
            central,
            request.CustomerId,
            request.BranchId,
            quote.Deferred,
            request.OperationId.ToString("N"),
            DateTime.UtcNow.AddMinutes(10),
            cancellationToken);
        if (reserved.Decision != CreditDecision.Approved || reserved.Status != "Authorized" && !reserved.Replay)
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.Refused, null, reserved.AuthorizationId);
        }

        var saleId = request.SaleId;
        try
        {
            await InsertPendingSaleAsync(branch, request, quote, saleId, reserved.AuthorizationId, failLocalCommit, cancellationToken);
        }
        catch
        {
            await credit.ReleaseAsync(central, reserved.AuthorizationId, cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.LocalRolledBack, null, reserved.AuthorizationId);
        }

        if (afterLocalCommit is not null)
        {
            await afterLocalCommit();
        }

        if (!await credit.BeginPostingAsync(central, reserved.AuthorizationId, saleId, cancellationToken))
        {
            await MarkOutboxAsync(branch, request.OperationId, "ReconciliationRequired", "Reservation was not available after the local sale committed.", cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, saleId, reserved.AuthorizationId);
        }

        if (skipFinalization)
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.PendingCentral, saleId, reserved.AuthorizationId);
        }

        return await DeliverAsync(central, branch, request.OperationId, request.ActorUserId, cancellationToken);
    }

    public async Task<CreditSaleOutcomeResult> DeliverAsync(
        SqlConnection central,
        SqlConnection branch,
        Guid operationId,
        Guid actorUserId,
        CancellationToken cancellationToken,
        bool trustedRecovery = false,
        string? leaseOwner = null)
    {
        await using var read = new SqlCommand(
            """
            SELECT SourceSaleID, BranchID, CustomerID, AuthorizationID, Status, Payload
            FROM dbo.BranchFinancialOutbox
            WHERE CreditOperationID = @operation;
            """,
            branch);
        read.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Credit outbox operation was not found.");
        }

        var saleId = reader.GetGuid(0);
        var branchId = reader.GetGuid(1);
        var customerId = reader.GetGuid(2);
        var authorizationId = reader.GetGuid(3);
        var status = reader.GetString(4);
        var payload = reader.GetString(5);
        await reader.CloseAsync();
        if (status == "Completed")
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.Completed, saleId, authorizationId);
        }

        if (status == "ReconciliationRequired")
        {
            return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, saleId, authorizationId);
        }

        if (!trustedRecovery && !await HasPermissionAsync(branch, actorUserId, PermissionCodes.CreditCreate, cancellationToken))
        {
            await MarkOutboxAsync(branch, operationId, "ReconciliationRequired", "Actor lost credit permission.", cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, saleId, authorizationId);
        }

        if (!TryReadPayload(payload, out var deferred, out var payloadCustomer, out var payloadBranch, out var payloadSale)
            || payloadCustomer != customerId
            || payloadBranch != branchId
            || payloadSale != saleId)
        {
            await MarkOutboxAsync(branch, operationId, "ReconciliationRequired", "Stored credit payload does not match the sale.", cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, saleId, authorizationId);
        }

        try
        {
            await credit.FinalizeAsync(
                central,
                authorizationId,
                saleId,
                actorUserId,
                operationId.ToString("N"),
                DateTime.UtcNow.Date.AddDays(await PaymentTermDaysAsync(central, customerId, cancellationToken)),
                cancellationToken,
                customerId,
                deferred,
                branchId,
                await SaleNumberAsync(branch, saleId, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            await MarkOutboxAsync(branch, operationId, "ReconciliationRequired", "Credit finalization conflict requires reconciliation.", cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.ReconciliationRequired, saleId, authorizationId);
        }
        catch (Exception)
        {
            await MarkOutboxAsync(branch, operationId, "Pending", "Central finalization is temporarily unavailable.", cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.PendingCentral, saleId, authorizationId);
        }

        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using (var sale = new SqlCommand(
            """
            UPDATE dbo.Sales SET Status = N'Completed', CompletedAt = SYSUTCDATETIME()
            WHERE SaleID = @sale AND BranchID = @branch AND Status = N'CreditPending';
            """,
            branch,
            transaction))
        {
            sale.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
            sale.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            await sale.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var complete = new SqlCommand(
            """
            UPDATE dbo.BranchFinancialOutbox
            SET Status = N'Completed', CompletedAt = SYSUTCDATETIME(), AttemptCount = AttemptCount + 1, LastAttemptAt = SYSUTCDATETIME(), LastError = NULL, LeaseOwner = NULL, LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status = N'Pending'
              AND (@owner IS NULL OR LeaseOwner IS NULL OR LeaseOwner = @owner);
            """,
            branch,
            transaction);
        complete.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        complete.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = (object?)leaseOwner ?? DBNull.Value;
        var completed = await complete.ExecuteNonQueryAsync(cancellationToken);
        if (completed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreditSaleOutcomeResult(CreditSaleOutcome.PendingCentral, saleId, authorizationId);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreditSaleOutcomeResult(CreditSaleOutcome.Completed, saleId, authorizationId);
    }

    private async Task InsertPendingSaleAsync(
        SqlConnection branch,
        CreditSaleRequest request,
        CreditQuote quote,
        Guid saleId,
        Guid authorizationId,
        bool failLocalCommit,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await documents.PersistAsync(branch, transaction, request, quote, saleId, cancellationToken);

        await using var outbox = new SqlCommand(
            """
            INSERT INTO dbo.BranchFinancialOutbox
                (OutboxEventID, CreditOperationID, SourceSaleID, BranchID, CustomerID, AuthorizationID, EventType, Payload, Status, NextRetryAt)
            VALUES (NEWID(), @operation, @sale, @branch, @customer, @authorization, N'FinalizeCredit', @payload, N'Pending', SYSUTCDATETIME());
            """,
            branch,
            transaction);
        outbox.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = request.OperationId;
        outbox.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        outbox.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = request.BranchId;
        outbox.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
        outbox.Parameters.Add("@authorization", SqlDbType.UniqueIdentifier).Value = authorizationId;
        outbox.Parameters.Add("@payload", SqlDbType.NVarChar, 2000).Value = Payload(request, quote);
        await outbox.ExecuteNonQueryAsync(cancellationToken);
        if (failLocalCommit)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Local credit sale commit failed.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MarkOutboxAsync(SqlConnection branch, Guid operationId, string status, string error, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchFinancialOutbox
            SET Status = @status,
                LastError = @error,
                AttemptCount = AttemptCount + 1,
                LastAttemptAt = SYSUTCDATETIME(),
                NextRetryAt = DATEADD(second, CASE WHEN AttemptCount + 1 >= 5 THEN 60 ELSE POWER(2, AttemptCount + 1) END, SYSUTCDATETIME()),
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status <> N'Completed';
            """,
            branch);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 400).Value = error;
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<long?> SaleNumberAsync(SqlConnection branch, Guid saleId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT SaleNumber FROM dbo.Sales WHERE SaleID = @sale;", branch);
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long number ? number : null;
    }

    private static async Task<int> PaymentTermDaysAsync(SqlConnection central, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT PaymentTermDays FROM dbo.CustomerCreditPolicies WHERE CustomerID = @customer;", central);
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int days && days is >= 0 and <= 3650 ? days : 30;
    }

    internal static string Payload(CreditSaleRequest request, CreditQuote quote)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{quote.Deferred}|{request.CustomerId:N}|{request.BranchId:N}|{request.SaleId:N}");
    }

    private static bool TryReadPayload(string payload, out decimal deferred, out Guid customerId, out Guid branchId, out Guid saleId)
    {
        deferred = 0m;
        customerId = Guid.Empty;
        branchId = Guid.Empty;
        saleId = Guid.Empty;
        var parts = payload.Split('|');
        return parts.Length == 4
            && decimal.TryParse(parts[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out deferred)
            && Guid.TryParse(parts[1], out customerId)
            && Guid.TryParse(parts[2], out branchId)
            && Guid.TryParse(parts[3], out saleId);
    }
}

public sealed record CreditSaleLine(
    Guid ItemId,
    decimal Quantity,
    string LineType = "Fixed",
    decimal? ExpectedUnitPrice = null,
    string? PriceOverrideMode = null,
    decimal? ManualUnitPrice = null,
    decimal? ManualLineTotal = null,
    decimal? EnteredScaleValue = null,
    decimal? ExpectedFinalSellingPrice = null,
    decimal? ExpectedReferenceBasePrice = null,
    decimal? ExpectedWeightKg = null,
    decimal? ExpectedStockOwnerQuantity = null);

public sealed record CreditSaleRequest(
    Guid OperationId,
    Guid SaleId,
    Guid CartId,
    Guid BusinessId,
    Guid BranchId,
    Guid StationId,
    Guid DeviceId,
    Guid CashSessionId,
    Guid CustomerId,
    Guid ActorUserId,
    Guid AuthorizationId,
    decimal InvoiceTotal,
    decimal ImmediatePayment,
    decimal DeferredAmount,
    IReadOnlyList<CreditSaleLine> Lines);
