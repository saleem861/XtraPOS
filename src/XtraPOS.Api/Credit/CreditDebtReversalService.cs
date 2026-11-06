using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Credit;

public enum CreditDebtReversalOutcome
{
    Completed,
    Refused,
    Unauthorized,
    AlreadyReversed,
    HasPayments,
    CentralUnavailable,
    NotFound,
}

public sealed record CreditDebtReversalResult(CreditDebtReversalOutcome Outcome, Guid? LedgerEntryId);

public sealed class CreditDebtReversalService
{
    public async Task<CreditDebtReversalResult> ReverseAsync(
        SqlConnection central,
        SqlConnection branch,
        Guid operationId,
        Guid saleId,
        Guid actorUserId,
        Guid businessId,
        string reason,
        bool centralReachable,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(branch, actorUserId, cancellationToken))
        {
            return new CreditDebtReversalResult(CreditDebtReversalOutcome.Unauthorized, null);
        }

        if (!centralReachable)
        {
            return new CreditDebtReversalResult(CreditDebtReversalOutcome.CentralUnavailable, null);
        }

        var key = "debt-reversal:" + operationId.ToString("N");
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        await using (var existing = new SqlCommand(
            "SELECT LedgerEntryID FROM dbo.CustomerCreditLedger WITH (UPDLOCK, HOLDLOCK) WHERE IdempotencyKey = @key;",
            central,
            transaction))
        {
            existing.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
            if (await existing.ExecuteScalarAsync(cancellationToken) is Guid existingId)
            {
                await transaction.CommitAsync(cancellationToken);
                return new CreditDebtReversalResult(CreditDebtReversalOutcome.Completed, existingId);
            }
        }

        await using var original = new SqlCommand(
            """
            SELECT ledger.LedgerEntryID, ledger.CustomerID, ledger.Amount, ledger.SourceBranchID, ledger.IdempotencyKey,
                   receivable.ReceivableID, receivable.OriginalAmount, receivable.OutstandingAmount, receivable.Status
            FROM dbo.CustomerCreditLedger AS ledger WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN dbo.CustomerReceivables AS receivable WITH (UPDLOCK, HOLDLOCK)
                ON receivable.SourceSaleID = ledger.SourceSaleID AND receivable.CustomerID = ledger.CustomerID
            INNER JOIN dbo.Customers AS customer ON customer.CustomerID = ledger.CustomerID
            WHERE ledger.SourceSaleID = @sale
              AND ledger.TransactionType = N'CreditSale'
              AND customer.BusinessID = @business;
            """,
            central,
            transaction);
        original.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        original.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        await using var reader = await original.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.CloseAsync();
            await transaction.RollbackAsync(cancellationToken);
            return new CreditDebtReversalResult(CreditDebtReversalOutcome.NotFound, null);
        }

        var originalLedgerId = reader.GetGuid(0);
        var customerId = reader.GetGuid(1);
        var amount = reader.GetDecimal(2);
        var branchId = reader.GetGuid(3);
        var originalKey = reader.GetString(4);
        var receivableId = reader.GetGuid(5);
        var originalAmount = reader.GetDecimal(6);
        var outstanding = reader.GetDecimal(7);
        var status = reader.GetString(8);
        await reader.CloseAsync();

        await using (var reversed = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM dbo.CustomerCreditLedger
            WHERE ReversalReference = @original OR ReversedOperationID = @originalKey;
            """,
            central,
            transaction))
        {
            reversed.Parameters.Add("@original", SqlDbType.UniqueIdentifier).Value = originalLedgerId;
            reversed.Parameters.Add("@originalKey", SqlDbType.NVarChar, 80).Value = originalKey;
            if (Convert.ToInt32(await reversed.ExecuteScalarAsync(cancellationToken)) > 0 || status == "Reversed")
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreditDebtReversalResult(CreditDebtReversalOutcome.AlreadyReversed, null);
            }
        }

        if (outstanding != originalAmount || status is not ("Open" or "Partial"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreditDebtReversalResult(CreditDebtReversalOutcome.HasPayments, null);
        }

        var ledgerId = Guid.NewGuid();
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerCreditLedger
                (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, CreatedBy, ReversalReference, IdempotencyKey, BusinessID, OriginOperationID, ReversedOperationID, Reason)
            VALUES
                (@id, @customer, @operation, N'Reversal', @amount, N'ILS', @branch, @sale, @actor, @original, @key,
                 (SELECT BusinessID FROM dbo.Customers WHERE CustomerID = @customer), @key, @originalKey, @reason);
            UPDATE dbo.CustomerReceivables
            SET OutstandingAmount = 0, Status = N'Reversed'
            WHERE ReceivableID = @receivable AND OutstandingAmount = OriginalAmount AND Status IN (N'Open', N'Partial');
            """,
            central,
            transaction))
        {
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = ledgerId;
            insert.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
            insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
            var money = insert.Parameters.Add("@amount", SqlDbType.Decimal);
            money.Precision = 19;
            money.Scale = 4;
            money.Value = amount;
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            insert.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
            insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
            insert.Parameters.Add("@original", SqlDbType.UniqueIdentifier).Value = originalLedgerId;
            insert.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
            insert.Parameters.Add("@originalKey", SqlDbType.NVarChar, 80).Value = originalKey;
            insert.Parameters.Add("@reason", SqlDbType.NVarChar, 400).Value = reason;
            insert.Parameters.Add("@receivable", SqlDbType.UniqueIdentifier).Value = receivableId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = new SqlCommand(
            """
            INSERT INTO dbo.CreditAuditLog (AuditID, CustomerID, ActorUserID, BranchID, Action, OldValue, NewValue, Reason, Result)
            VALUES (NEWID(), @customer, @actor, @branch, N'DebtReversal', @old, N'0', @reason, N'Completed');
            """,
            central,
            transaction))
        {
            audit.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
            audit.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
            audit.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            audit.Parameters.Add("@old", SqlDbType.NVarChar, 40).Value = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            audit.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = reason;
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await FinancialEventStore.PublishLedgerAsync(central, transaction, key, "{\"notes\":\"" + Escape(reason) + "\"}", null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreditDebtReversalResult(CreditDebtReversalOutcome.Completed, ledgerId);
    }

    private static string Escape(string reason) => reason.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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
                  AND (permission.PermissionCode = @code OR permission.PermissionCode = N'System.Administer')
            ) THEN 1 ELSE 0 END;
            """,
            branch);
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = PermissionCodes.CreditReverse;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
