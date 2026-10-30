using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public sealed class CreditOutboxProcessor(CreditSaleCoordinator coordinator)
{
    public async Task<CreditSaleOutcome?> ProcessOnceAsync(SqlConnection central, SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        var operationId = await ClaimAsync(branch, workerId, cancellationToken);
        if (operationId is null)
        {
            return null;
        }

        await using var actor = new SqlCommand(
            """
            SELECT sale.CashierUserID
            FROM dbo.BranchFinancialOutbox AS box
            INNER JOIN dbo.Sales AS sale ON sale.SaleID = box.SourceSaleID
            WHERE box.CreditOperationID = @operation;
            """,
            branch);
        actor.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId.Value;
        var cashier = await actor.ExecuteScalarAsync(cancellationToken);
        if (cashier is not Guid cashierId)
        {
            await ParkAsync(branch, operationId.Value, "ReconciliationRequired", "The committed sale has no cashier.", cancellationToken);
            return CreditSaleOutcome.ReconciliationRequired;
        }

        try
        {
            var result = await coordinator.DeliverAsync(central, branch, operationId.Value, cashierId, cancellationToken, trustedRecovery: true, leaseOwner: workerId);
            return result.Outcome;
        }
        catch (SqlException exception)
        {
            await ParkAsync(branch, operationId.Value, "Pending", "Temporary database error.", cancellationToken);
            _ = exception.Number;
            return CreditSaleOutcome.PendingCentral;
        }
    }

    public async Task<CreditOperationStatus?> ReadAsync(SqlConnection branch, Guid branchId, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CreditOperationID, BranchID, SourceSaleID, CustomerID, Status, CreatedAt, LastAttemptAt, AttemptCount, LastError
            FROM dbo.BranchFinancialOutbox
            WHERE CreditOperationID = @operation AND BranchID = @branch;
            """,
            branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return Map(reader);
    }

    public async Task<IReadOnlyList<CreditOperationStatus>> ListUnresolvedAsync(SqlConnection branch, Guid branchId, CancellationToken cancellationToken)
    {
        var rows = new List<CreditOperationStatus>();
        await using var command = new SqlCommand(
            """
            SELECT CreditOperationID, BranchID, SourceSaleID, CustomerID, Status, CreatedAt, LastAttemptAt, AttemptCount, LastError
            FROM dbo.BranchFinancialOutbox
            WHERE BranchID = @branch AND Status <> N'Completed'
            ORDER BY CreatedAt;
            """,
            branch);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    private static CreditOperationStatus Map(SqlDataReader reader)
    {
        var status = reader.GetString(4);
        return new CreditOperationStatus(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            status,
            reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            status == "Completed",
            status == "ReconciliationRequired");
    }

    private static async Task<Guid?> ClaimAsync(SqlConnection branch, string workerId, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            ;WITH nextRow AS (
                SELECT TOP (1) OutboxEventID
                FROM dbo.BranchFinancialOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = N'Pending'
                  AND NextRetryAt <= DATEADD(second, 1, SYSUTCDATETIME())
                  AND (LeaseUntil IS NULL OR LeaseUntil < SYSUTCDATETIME())
                ORDER BY CreatedAt
            )
            UPDATE box
            SET LeaseOwner = @owner, LeaseUntil = DATEADD(second, 45, SYSUTCDATETIME())
            OUTPUT inserted.CreditOperationID
            FROM dbo.BranchFinancialOutbox AS box
            INNER JOIN nextRow ON nextRow.OutboxEventID = box.OutboxEventID;
            """,
            branch,
            transaction);
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 80).Value = workerId;
        var claimed = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed is Guid operationId ? operationId : null;
    }

    private static async Task ParkAsync(SqlConnection branch, Guid operationId, string status, string error, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchFinancialOutbox
            SET Status = @status,
                LastError = @error,
                LeaseOwner = NULL,
                LeaseUntil = NULL,
                NextRetryAt = DATEADD(second, 15, SYSUTCDATETIME())
            WHERE CreditOperationID = @operation AND Status <> N'Completed';
            """,
            branch);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 30).Value = status;
        command.Parameters.Add("@error", SqlDbType.NVarChar, 400).Value = error;
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record CreditOperationStatus(
    Guid CreditOperationId,
    Guid BranchId,
    Guid SourceSaleId,
    Guid CustomerId,
    string State,
    DateTime CreatedAt,
    DateTime? LastAttemptAt,
    int AttemptCount,
    string? LastError,
    bool CentrallyConfirmed,
    bool ReconciliationRequired);
