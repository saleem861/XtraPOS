using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public sealed record FinancialPullResult(int Applied, long Checkpoint, long CentralCount, long InboxCount);

public static class FinancialInboxSync
{
    public const string StreamName = "CentralFinancial";

    public static async Task<FinancialPullResult> PullAsync(
        SqlConnection central,
        SqlConnection branch,
        CancellationToken cancellationToken)
    {
        try
        {
            await FinancialEventStore.BackfillAsync(central, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
        }

        var businessId = await BusinessIdAsync(branch, cancellationToken);
        if (businessId == Guid.Empty)
        {
            return new FinancialPullResult(0, 0, 0, 0);
        }

        var checkpoint = await CheckpointAsync(branch, cancellationToken);
        var applied = 0;
        long maxSeen = checkpoint;
        await using var read = new SqlCommand(
            """
            SELECT EventSequence, OperationID, EventType, BusinessID, CustomerID, OriginBranchID, POSNumber, ActorUserID,
                   SourceSaleID, SourceSaleNumber, Amount, SignedEffect, Reason, ReversedOperationID, Payload, ConfirmedAt
            FROM dbo.CentralFinancialEvents
            WHERE EventSequence > @cursor AND BusinessID = @business
            ORDER BY EventSequence;
            """,
            central);
        read.Parameters.Add("@cursor", SqlDbType.BigInt).Value = checkpoint;
        read.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var rows = new List<EventRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.GetDateTime(15)));
        }

        await reader.CloseAsync();
        var localBranchId = await LocalBranchIdAsync(branch, cancellationToken);
        foreach (var row in rows)
        {
            maxSeen = row.Sequence;
            if (row.EventType == "CreditSale")
            {
                await BranchCreditLedger.CopyCreditSaleAsync(
                    branch,
                    row.OperationId,
                    row.CustomerId,
                    row.OriginBranchId,
                    row.SourceSaleId,
                    row.SourceSaleNumber,
                    row.Amount,
                    row.PosNumber,
                    row.ActorUserId,
                    row.ConfirmedAt,
                    cancellationToken);
            }

            if (localBranchId != Guid.Empty && row.OriginBranchId == localBranchId)
            {
                continue;
            }

            if (await InsertInboxAsync(branch, row, cancellationToken))
            {
                applied++;
            }
        }

        if (maxSeen > checkpoint)
        {
            await SaveCheckpointAsync(branch, maxSeen, cancellationToken);
        }

        var centralCount = await CountAsync(central, "SELECT COUNT(*) FROM dbo.CentralFinancialEvents WHERE BusinessID = @business;", businessId, cancellationToken);
        var inboxCount = await CountAsync(branch, "SELECT COUNT(*) FROM dbo.BranchFinancialInbox WHERE BusinessID = @business;", businessId, cancellationToken);
        return new FinancialPullResult(applied, maxSeen, centralCount, inboxCount);
    }

    private static async Task<bool> InsertInboxAsync(SqlConnection branch, EventRow row, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.BranchFinancialInbox WHERE OperationID = @operation)
            INSERT INTO dbo.BranchFinancialInbox
                (OperationID, EventSequence, EventType, BusinessID, CustomerID, OriginBranchID, POSNumber, ActorUserID,
                 SourceSaleID, SourceSaleNumber, Amount, SignedEffect, Reason, ReversedOperationID, Payload, ConfirmedAt)
            VALUES
                (@operation, @sequence, @type, @business, @customer, @branch, @pos, @actor,
                 @sale, @saleNumber, @amount, @effect, @reason, @reversed, @payload, @confirmed);
            """,
            branch);
        command.Parameters.Add("@operation", SqlDbType.NVarChar, 80).Value = row.OperationId;
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = row.Sequence;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = row.EventType;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = row.BusinessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = row.CustomerId;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = row.OriginBranchId;
        command.Parameters.Add("@pos", SqlDbType.Int).Value = (object?)row.PosNumber ?? DBNull.Value;
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = (object?)row.ActorUserId ?? DBNull.Value;
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = (object?)row.SourceSaleId ?? DBNull.Value;
        command.Parameters.Add("@saleNumber", SqlDbType.BigInt).Value = (object?)row.SourceSaleNumber ?? DBNull.Value;
        var amount = command.Parameters.Add("@amount", SqlDbType.Decimal);
        amount.Precision = 19;
        amount.Scale = 4;
        amount.Value = row.Amount;
        var effect = command.Parameters.Add("@effect", SqlDbType.Decimal);
        effect.Precision = 19;
        effect.Scale = 4;
        effect.Value = row.SignedEffect;
        command.Parameters.Add("@reason", SqlDbType.NVarChar, 400).Value = (object?)row.Reason ?? DBNull.Value;
        command.Parameters.Add("@reversed", SqlDbType.NVarChar, 80).Value = (object?)row.ReversedOperationId ?? DBNull.Value;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = row.Payload;
        command.Parameters.Add("@confirmed", SqlDbType.DateTime2).Value = row.ConfirmedAt;
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<Guid> LocalBranchIdAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var setting = new SqlCommand(
            "SELECT TRY_CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BranchID';",
            branch);
        if (await setting.ExecuteScalarAsync(cancellationToken) is Guid configured && configured != Guid.Empty)
        {
            return configured;
        }

        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN COUNT(*) = 1 THEN CONVERT(nvarchar(36), MIN(BranchID)) ELSE NULL END
            FROM dbo.Branches;
            """,
            branch);
        return Guid.TryParse(await command.ExecuteScalarAsync(cancellationToken) as string, out var only) ? only : Guid.Empty;
    }

    private static async Task<Guid> BusinessIdAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT TRY_CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';",
            branch);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid business ? business : Guid.Empty;
    }

    private static async Task<long> CheckpointAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT LastEventSequence FROM dbo.FinancialSyncCheckpoints WHERE StreamName = @stream;",
            branch);
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 40).Value = StreamName;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long sequence ? sequence : 0L;
    }

    private static async Task SaveCheckpointAsync(SqlConnection branch, long sequence, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.FinancialSyncCheckpoints
            SET LastEventSequence = @sequence, UpdatedAt = SYSUTCDATETIME()
            WHERE StreamName = @stream;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.FinancialSyncCheckpoints (StreamName, LastEventSequence)
                VALUES (@stream, @sequence);
            """,
            branch);
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 40).Value = StreamName;
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(SqlConnection connection, string sql, Guid businessId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record EventRow(
        long Sequence,
        string OperationId,
        string EventType,
        Guid BusinessId,
        Guid CustomerId,
        Guid OriginBranchId,
        int? PosNumber,
        Guid? ActorUserId,
        Guid? SourceSaleId,
        long? SourceSaleNumber,
        decimal Amount,
        decimal SignedEffect,
        string? Reason,
        string? ReversedOperationId,
        string Payload,
        DateTime ConfirmedAt);
}
