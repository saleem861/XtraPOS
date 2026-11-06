using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public static class FinancialEventStore
{
    public static async Task PublishLedgerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string idempotencyKey,
        string? payload,
        int? posNumber,
        long? saleNumber,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.CentralFinancialEvents
                (OperationID, EventType, BusinessID, CustomerID, OriginBranchID, POSNumber, ActorUserID, SourceSaleID, SourceSaleNumber, Amount, SignedEffect, Reason, ReversedOperationID, Payload)
            SELECT
                ledger.IdempotencyKey,
                CASE
                    WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN N'DebtReversal'
                    WHEN ledger.TransactionType = N'Reversal' THEN N'RepaymentReversal'
                    ELSE ledger.TransactionType
                END,
                customer.BusinessID,
                ledger.CustomerID,
                ledger.SourceBranchID,
                COALESCE(@pos, ledger.POSNumber),
                ledger.CreatedBy,
                ledger.SourceSaleID,
                COALESCE(@saleNumber, NULL),
                ledger.Amount,
                CASE
                    WHEN ledger.TransactionType = N'CreditSale' THEN ledger.Amount
                    WHEN ledger.TransactionType = N'Repayment' THEN -ledger.Amount
                    WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN -ledger.Amount
                    WHEN ledger.TransactionType = N'Reversal' THEN ledger.Amount
                    ELSE ledger.Amount
                END,
                ledger.Reason,
                ledger.ReversedOperationID,
                COALESCE(@payload, N'{}')
            FROM dbo.CustomerCreditLedger AS ledger
            INNER JOIN dbo.Customers AS customer ON customer.CustomerID = ledger.CustomerID
            WHERE ledger.IdempotencyKey = @key
              AND NOT EXISTS (
                  SELECT 1 FROM dbo.CentralFinancialEvents AS existing WHERE existing.OperationID = ledger.IdempotencyKey);
            """,
            connection,
            transaction);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = idempotencyKey;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = (object?)payload ?? DBNull.Value;
        command.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)posNumber ?? DBNull.Value;
        command.Parameters.Add("@saleNumber", SqlDbType.BigInt).Value = (object?)saleNumber ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task BackfillAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.CentralFinancialEvents
                (OperationID, EventType, BusinessID, CustomerID, OriginBranchID, POSNumber, ActorUserID, SourceSaleID, Amount, SignedEffect, Reason, ReversedOperationID, Payload)
            SELECT
                ledger.IdempotencyKey,
                CASE
                    WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN N'DebtReversal'
                    WHEN ledger.TransactionType = N'Reversal' THEN N'RepaymentReversal'
                    ELSE ledger.TransactionType
                END,
                customer.BusinessID,
                ledger.CustomerID,
                ledger.SourceBranchID,
                ledger.POSNumber,
                ledger.CreatedBy,
                ledger.SourceSaleID,
                ledger.Amount,
                CASE
                    WHEN ledger.TransactionType = N'CreditSale' THEN ledger.Amount
                    WHEN ledger.TransactionType = N'Repayment' THEN -ledger.Amount
                    WHEN ledger.TransactionType = N'Reversal' AND ledger.PaymentID IS NULL THEN -ledger.Amount
                    WHEN ledger.TransactionType = N'Reversal' THEN ledger.Amount
                    ELSE ledger.Amount
                END,
                ledger.Reason,
                ledger.ReversedOperationID,
                N'{}'
            FROM dbo.CustomerCreditLedger AS ledger
            INNER JOIN dbo.Customers AS customer ON customer.CustomerID = ledger.CustomerID
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.CentralFinancialEvents AS existing WHERE existing.OperationID = ledger.IdempotencyKey);
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
