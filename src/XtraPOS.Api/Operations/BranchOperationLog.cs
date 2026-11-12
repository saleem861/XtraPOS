using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Operations;

public sealed record OperationLogRow(
    int Serial,
    string ReferenceNumber,
    Guid OperationId,
    string OperationType,
    string Description,
    string Payload,
    string DestinationName,
    string Status,
    string ResultMessage,
    int AttemptCount,
    DateTime CreatedAt);

public sealed record OperationLogPage(int? HeadSerial, IReadOnlyList<OperationLogRow> Rows, IReadOnlyList<string> Destinations);

public sealed record BranchOperationDraft(
    Guid OperationId,
    string OperationType,
    string Payload,
    string Description,
    string SourceEntityType,
    Guid SourceEntityId,
    Guid OriginBranchId,
    Guid ActorUserId,
    Guid? StationId,
    string DestinationKind = "Central",
    Guid? DestinationBranchId = null,
    string? DestinationName = null);

public static class BranchOperationLog
{
    public const string RetentionSettingKey = "OperationLogRetentionDays";

    public static async Task EnqueueAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        BranchOperationDraft draft,
        CancellationToken cancellationToken)
    {
        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM dbo.BranchOperationLog WHERE OperationID = @operation;", branch, transaction))
        {
            exists.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = draft.OperationId;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                return;
            }
        }

        var origin = await OriginAsync(branch, transaction, draft.OriginBranchId, draft.StationId, cancellationToken);
        var destinationName = string.IsNullOrWhiteSpace(draft.DestinationName)
            ? draft.DestinationKind == "Branch" ? "فرع" : "المركز"
            : draft.DestinationName.Trim();
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.BranchOperationLog
                (OperationID, OriginBranchCode, OriginBranchID, POSNumber, StationID, ActorUserID, OperationType, PayloadVersion, Payload, Description,
                 SourceEntityType, SourceEntityID, DestinationKind, DestinationBranchCode, DestinationBranchID, DestinationName, Status)
            OUTPUT inserted.Serial
            VALUES
                (@operation, @code, @origin, @pos, @station, @actor, @type, 1, @payload, @description,
                 @entityType, @entity, @destinationKind, @destinationCode, @destination, @destinationName, N'Pending');
            """,
            branch,
            transaction);
        insert.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = draft.OperationId;
        insert.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = origin.Code;
        insert.Parameters.Add("@origin", SqlDbType.UniqueIdentifier).Value = draft.OriginBranchId;
        insert.Parameters.Add("@pos", SqlDbType.Int).Value = origin.PosNumber;
        insert.Parameters.Add("@station", SqlDbType.UniqueIdentifier).Value = (object?)draft.StationId ?? DBNull.Value;
        insert.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = draft.ActorUserId;
        insert.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = draft.OperationType;
        insert.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = draft.Payload;
        insert.Parameters.Add("@description", SqlDbType.NVarChar, 400).Value = draft.Description;
        insert.Parameters.Add("@entityType", SqlDbType.NVarChar, 64).Value = draft.SourceEntityType;
        insert.Parameters.Add("@entity", SqlDbType.UniqueIdentifier).Value = draft.SourceEntityId;
        insert.Parameters.Add("@destinationKind", SqlDbType.NVarChar, 20).Value = draft.DestinationKind;
        insert.Parameters.Add("@destinationCode", SqlDbType.NVarChar, 50).Value = (object?)origin.DestinationCode ?? DBNull.Value;
        insert.Parameters.Add("@destination", SqlDbType.UniqueIdentifier).Value = (object?)draft.DestinationBranchId ?? DBNull.Value;
        insert.Parameters.Add("@destinationName", SqlDbType.NVarChar, 100).Value = destinationName;
        var serial = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
        var reference = string.Create(CultureInfo.InvariantCulture, $"{origin.Code}-{origin.PosNumber}-{DateTime.UtcNow:yyyyMMddHHmmss}-{serial}");
        await using var referenceUpdate = new SqlCommand(
            "UPDATE dbo.BranchOperationLog SET ReferenceNumber = @reference WHERE Serial = @serial;",
            branch,
            transaction);
        referenceUpdate.Parameters.Add("@reference", SqlDbType.NVarChar, 80).Value = reference;
        referenceUpdate.Parameters.Add("@serial", SqlDbType.Int).Value = serial;
        await referenceUpdate.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<bool> HasEarlierIncompleteAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, cancellationToken))
        {
            return false;
        }

        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM dbo.BranchOperationLog AS earlier
                WHERE earlier.Status NOT IN (N'Completed', N'Cancelled')
                  AND earlier.Serial < ISNULL((SELECT currentRow.Serial FROM dbo.BranchOperationLog AS currentRow WHERE currentRow.OperationID = @operation), 2147483647)
            ) THEN 1 ELSE 0 END;
            """,
            branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public static async Task<string?> HeadTypeAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, cancellationToken))
        {
            return null;
        }

        await using var command = new SqlCommand(
            """
            SELECT TOP (1) OperationType
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled')
            ORDER BY Serial;
            """,
            branch);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public static async Task<bool> IsExecutableHeadAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, cancellationToken))
        {
            return true;
        }

        await using var command = new SqlCommand(
            """
            SELECT TOP (1) OperationID
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled')
            ORDER BY Serial;
            """,
            branch);
        var head = await command.ExecuteScalarAsync(cancellationToken);
        return head is not Guid headId || headId == operationId;
    }

    public static async Task MarkAsync(
        SqlConnection branch,
        SqlTransaction? transaction,
        Guid operationId,
        string status,
        string? message,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, cancellationToken, transaction))
        {
            return;
        }

        var logStatus = status switch
        {
            "Completed" => "Completed",
            "Cancelled" => "Cancelled",
            "Executing" => "Executing",
            _ => "Blocked",
        };
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchOperationLog
            SET Status = @status,
                ResultMessage = @message,
                AttemptCount = AttemptCount + 1,
                LastAttemptAt = SYSUTCDATETIME(),
                StartedAt = CASE WHEN @status = N'Executing' AND StartedAt IS NULL THEN SYSUTCDATETIME() ELSE StartedAt END,
                CompletedAt = CASE WHEN @status = N'Completed' THEN SYSUTCDATETIME() ELSE CompletedAt END,
                LeaseOwner = NULL,
                LeaseUntil = NULL
            WHERE OperationID = @operation AND Status <> N'Completed';
            """,
            branch,
            transaction);
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = logStatus;
        command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = (object?)message ?? DBNull.Value;
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<OperationLogPage> ListAsync(SqlConnection branch, DateTime? from, DateTime? to, string? destination, CancellationToken cancellationToken)
    {
        var rows = new List<OperationLogRow>();
        var destinations = new List<string>();
        int? head = null;
        await using var command = new SqlCommand(
            """
            SELECT Serial, ReferenceNumber, OperationID, OperationType, Description, Payload, DestinationName, Status, ResultMessage, AttemptCount, CreatedAt
            FROM dbo.BranchOperationLog
            WHERE (@from IS NULL OR CreatedAt >= @from)
              AND (@to IS NULL OR CreatedAt < @to)
              AND (@destination IS NULL OR DestinationName = @destination)
            ORDER BY Serial;
            SELECT MIN(Serial) FROM dbo.BranchOperationLog WHERE Status IN (N'Pending', N'Executing', N'Blocked');
            SELECT DISTINCT DestinationName FROM dbo.BranchOperationLog ORDER BY DestinationName;
            """,
            branch);
        command.Parameters.Add("@from", SqlDbType.DateTime2).Value = (object?)from ?? DBNull.Value;
        command.Parameters.Add("@to", SqlDbType.DateTime2).Value = (object?)to ?? DBNull.Value;
        command.Parameters.Add("@destination", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(destination) ? DBNull.Value : destination.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OperationLogRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? "" : reader.GetString(8),
                reader.GetInt32(9),
                reader.GetDateTime(10)));
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0))
        {
            head = reader.GetInt32(0);
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    destinations.Add(reader.GetString(0));
                }
            }
        }

        return new OperationLogPage(head, rows, destinations);
    }

    public static async Task<bool> RetryAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchOperationLog
            SET Status = N'Pending', ResultMessage = N'بانتظار إعادة المحاولة.', LeaseOwner = NULL, LeaseUntil = NULL
            WHERE OperationID = @operation AND Status IN (N'Pending', N'Executing', N'Blocked');
            UPDATE dbo.BranchFinancialOutbox
            SET Status = N'Pending', NextRetryAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status = N'ReconciliationRequired';
            UPDATE dbo.BranchCollectionOutbox
            SET Status = N'Pending', NextRetryAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status = N'ReconciliationRequired';
            UPDATE dbo.BranchReversalOutbox
            SET Status = N'Pending', NextRetryAt = SYSUTCDATETIME(), LeaseOwner = NULL, LeaseUntil = NULL
            WHERE ReversalOperationID = @operation AND Status = N'ReconciliationRequired';
            """,
            branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static async Task<bool> RemoveAsync(SqlConnection branch, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BranchFinancialOutbox
            SET Status = N'ReconciliationRequired', LastError = N'حُذف أمر الترحيل من السجل.', LeaseOwner = NULL, LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status <> N'Completed';
            UPDATE dbo.BranchCollectionOutbox
            SET Status = N'ReconciliationRequired', LastError = N'حُذف أمر الترحيل من السجل.', LeaseOwner = NULL, LeaseUntil = NULL
            WHERE CreditOperationID = @operation AND Status <> N'Completed';
            UPDATE dbo.BranchReversalOutbox
            SET Status = N'ReconciliationRequired', LastError = N'حُذف أمر الترحيل من السجل.', LeaseOwner = NULL, LeaseUntil = NULL
            WHERE ReversalOperationID = @operation AND Status <> N'Completed';
            DELETE FROM dbo.BranchOperationLog WHERE OperationID = @operation;
            """,
            branch);
        command.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId;
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static async Task PurgeAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(branch, cancellationToken))
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            DECLARE @days int = TRY_CONVERT(int, (SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'OperationLogRetentionDays'));
            IF @days IS NULL OR @days < 1 SET @days = 30;
            DELETE FROM dbo.BranchOperationLog
            WHERE Status = N'Completed'
              AND CompletedAt < DATEADD(day, -@days, SYSUTCDATETIME());
            """,
            branch);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(string Code, int PosNumber, string? DestinationCode)> OriginAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        Guid branchId,
        Guid? stationId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT ISNULL(branch.BranchCode, N''),
                   ISNULL((SELECT TOP (1) station.StationNumber FROM dbo.POSStations AS station WHERE station.StationID = @station), 0)
            FROM dbo.Branches AS branch
            WHERE branch.BranchID = @branch;
            """,
            branch,
            transaction);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@station", SqlDbType.UniqueIdentifier).Value = (object?)stationId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ("0", 0, null);
        }

        var code = reader.GetString(0);
        return (code.Length == 0 ? "0" : code, reader.GetInt32(1), null);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection branch, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(N'dbo.BranchOperationLog', N'U');", branch, transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is not null and not DBNull;
    }
}
