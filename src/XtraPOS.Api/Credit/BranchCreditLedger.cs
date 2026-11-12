using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public static class BranchCreditLedger
{
    public static async Task RecordCreditSaleAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        Guid operationId,
        Guid customerId,
        Guid branchId,
        Guid saleId,
        Guid actorUserId,
        decimal amount,
        int? posNumber,
        long? saleNumber,
        int paymentTermDays,
        CancellationToken cancellationToken)
    {
        if (amount <= 0 || !await TableExistsAsync(branch, transaction, cancellationToken))
        {
            return;
        }

        var key = operationId.ToString("N");
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = @key)
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.CustomerReceivables WHERE SourceSaleID = @sale)
                    INSERT INTO dbo.CustomerReceivables
                        (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status, SourceSaleNumber)
                    VALUES (NEWID(), @customer, @sale, @branch, @amount, @amount, @due, N'ILS', N'Open', @saleNumber);

                INSERT INTO dbo.CustomerCreditLedger
                    (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, CreatedBy, IdempotencyKey, POSNumber, OriginOperationID)
                VALUES (NEWID(), @customer, NEWID(), N'CreditSale', @amount, N'ILS', @branch, @sale, @actor, @key, @pos, @key);
            END;
            """,
            branch,
            transaction);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
        var money = command.Parameters.Add("@amount", SqlDbType.Decimal);
        money.Precision = 19;
        money.Scale = 4;
        money.Value = amount;
        command.Parameters.Add("@due", SqlDbType.Date).Value = DateTime.UtcNow.Date.AddDays(Math.Clamp(paymentTermDays, 0, 3650));
        command.Parameters.Add("@saleNumber", SqlDbType.BigInt).Value = (object?)saleNumber ?? DBNull.Value;
        command.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)posNumber ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task CopyCreditSaleAsync(
        SqlConnection branch,
        string operationId,
        Guid customerId,
        Guid originBranchId,
        Guid? sourceSaleId,
        long? sourceSaleNumber,
        decimal amount,
        int? posNumber,
        Guid? actorUserId,
        DateTime confirmedAt,
        CancellationToken cancellationToken)
    {
        if (amount <= 0 || sourceSaleId is null || !await TableExistsAsync(branch, null, cancellationToken))
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = @key)
                OR NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerID = @customer)
                RETURN;

            IF NOT EXISTS (SELECT 1 FROM dbo.CustomerReceivables WHERE SourceSaleID = @sale)
                INSERT INTO dbo.CustomerReceivables
                    (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status, CreatedAt, SourceSaleNumber)
                VALUES (NEWID(), @customer, @sale, @branch, @amount, @amount, @due, N'ILS', N'Open', @occurred, @saleNumber);

            INSERT INTO dbo.CustomerCreditLedger
                (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, OccurredAt, CreatedBy, IdempotencyKey, POSNumber, OriginOperationID)
            VALUES (NEWID(), @customer, NEWID(), N'CreditSale', @amount, N'ILS', @branch, @sale, @occurred, @actor, @key, @pos, @key);
            """,
            branch);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = operationId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = sourceSaleId.Value;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = originBranchId;
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId ?? Guid.Empty;
        var money = command.Parameters.Add("@amount", SqlDbType.Decimal);
        money.Precision = 19;
        money.Scale = 4;
        money.Value = amount;
        command.Parameters.Add("@due", SqlDbType.Date).Value = confirmedAt.Date.AddDays(30);
        command.Parameters.Add("@occurred", SqlDbType.DateTime2).Value = confirmedAt;
        command.Parameters.Add("@saleNumber", SqlDbType.BigInt).Value = (object?)sourceSaleNumber ?? DBNull.Value;
        command.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)posNumber ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection branch, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.CustomerCreditLedger', N'U') IS NULL THEN 0 ELSE 1 END;", branch, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
