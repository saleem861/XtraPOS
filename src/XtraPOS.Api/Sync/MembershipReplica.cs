using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Operations;

namespace XtraPOS.Api.Sync;

public static class MembershipReplica
{
    public const string Identities = "CustomerMembershipIdentities";
    public const string EnrollmentOperation = "MembershipEnrollment";
    public const string IdentityOperation = "MembershipUpsert";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task EnqueueAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        string operationType,
        Guid entityId,
        Guid actorUserId,
        string description,
        CancellationToken cancellationToken)
    {
        await using var table = new SqlCommand("SELECT OBJECT_ID(N'dbo.BranchOperationLog', N'U');", branch, transaction);
        if (await table.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        await using var branchId = new SqlCommand("SELECT TOP (1) BranchID FROM dbo.Branches;", branch, transaction);
        var origin = await branchId.ExecuteScalarAsync(cancellationToken) is Guid id ? id : Guid.Empty;
        await BranchOperationLog.EnqueueAsync(branch, transaction, new BranchOperationDraft(
            Guid.NewGuid(),
            operationType,
            entityId.ToString("N"),
            description,
            operationType == EnrollmentOperation ? "MembershipEnrollmentRequests" : Identities,
            entityId,
            origin,
            actorUserId,
            null), cancellationToken);
    }

    public static async Task<string?> RelayAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID, OperationType, SourceEntityID
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled')
              AND OperationType IN (N'MembershipEnrollment', N'MembershipUpsert')
            ORDER BY Serial;
            """,
            branch);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var operationId = reader.GetGuid(0);
        var operationType = reader.GetString(1);
        var entityId = reader.GetGuid(2);
        await reader.CloseAsync();
        try
        {
            if (operationType == EnrollmentOperation)
            {
                await CopyEnrollmentAsync(branch, central, entityId, cancellationToken);
            }
            else
            {
                await CopyIdentityToCentralAsync(branch, central, entityId, cancellationToken);
            }

            await BranchOperationLog.MarkAsync(branch, null, operationId, "Completed", "تم نقل العضوية إلى المركز.", cancellationToken);
            return "Completed";
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            await BranchOperationLog.MarkAsync(branch, null, operationId, "Blocked", "جدول العضوية غير موجود على المركز بعد.", cancellationToken);
            return "Blocked";
        }
    }

    public static async Task CopyIdentitiesFromCentralAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        var rows = new List<IdentityRow>();
        await using (var read = new SqlCommand(
            """
            SELECT IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail,
                   LinkedAt, LastAuthenticatedAt, Status, RevokedAt, CreatedAt, UpdatedAt
            FROM dbo.CustomerMembershipIdentities;
            """,
            central))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadIdentity(reader));
            }
        }

        foreach (var row in rows)
        {
            await UpsertIdentityAsync(branch, null, row, cancellationToken);
        }
    }

    public static async Task ApplyIdentityAsync(SqlConnection connection, SqlTransaction? transaction, string payload, CancellationToken cancellationToken)
    {
        var row = JsonSerializer.Deserialize<IdentityRow>(payload, Json);
        if (row is null || row.IdentityId == Guid.Empty)
        {
            return;
        }

        await UpsertIdentityAsync(connection, transaction, row, cancellationToken);
    }

    private static async Task CopyEnrollmentAsync(SqlConnection branch, SqlConnection central, Guid enrollmentId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT EnrollmentID, BusinessID, CustomerID, TokenHash, Status, ExpiresAt, CreatedAt, CashierUserID,
                   CounterID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail
            FROM dbo.MembershipEnrollmentRequests
            WHERE EnrollmentID = @id;
            """,
            branch);
        read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = enrollmentId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var values = new object[13];
        reader.GetValues(values);
        await reader.CloseAsync();
        await using var write = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.MembershipEnrollmentRequests WHERE EnrollmentID = @id)
                UPDATE dbo.MembershipEnrollmentRequests
                SET CustomerID = @customer, Status = @status, Provider = @provider, ProviderSubject = @subject,
                    ProviderAccountDisplay = @display, ProviderEmail = @email
                WHERE EnrollmentID = @id;
            ELSE
                INSERT INTO dbo.MembershipEnrollmentRequests
                    (EnrollmentID, BusinessID, CustomerID, TokenHash, Status, ExpiresAt, CreatedAt, CashierUserID,
                     CounterID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail)
                VALUES
                    (@id, @business, @customer, @hash, @status, @expires, @created, @cashier,
                     @counter, @provider, @subject, @display, @email);
            """,
            central);
        write.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = values[0];
        write.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = values[1];
        write.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = values[2] is DBNull ? DBNull.Value : values[2];
        write.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = values[3];
        write.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = values[4];
        write.Parameters.Add("@expires", SqlDbType.DateTime2).Value = values[5];
        write.Parameters.Add("@created", SqlDbType.DateTime2).Value = values[6];
        write.Parameters.Add("@cashier", SqlDbType.UniqueIdentifier).Value = values[7] is DBNull ? DBNull.Value : values[7];
        write.Parameters.Add("@counter", SqlDbType.UniqueIdentifier).Value = values[8] is DBNull ? DBNull.Value : values[8];
        write.Parameters.Add("@provider", SqlDbType.NVarChar, 20).Value = values[9] is DBNull ? DBNull.Value : values[9];
        write.Parameters.Add("@subject", SqlDbType.NVarChar, 256).Value = values[10] is DBNull ? DBNull.Value : values[10];
        write.Parameters.Add("@display", SqlDbType.NVarChar, 256).Value = values[11] is DBNull ? DBNull.Value : values[11];
        write.Parameters.Add("@email", SqlDbType.NVarChar, 256).Value = values[12] is DBNull ? DBNull.Value : values[12];
        await write.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyIdentityToCentralAsync(SqlConnection branch, SqlConnection central, Guid identityId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail,
                   LinkedAt, LastAuthenticatedAt, Status, RevokedAt, CreatedAt, UpdatedAt
            FROM dbo.CustomerMembershipIdentities
            WHERE IdentityID = @id;
            """,
            branch);
        read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = identityId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var row = ReadIdentity(reader);
        await reader.CloseAsync();
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        await UpsertIdentityAsync(central, transaction, row, cancellationToken);
        await using var log = new SqlCommand(
            """
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload)
            VALUES (@type, @id, N'Upsert', @payload);
            """,
            central,
            transaction);
        log.Parameters.Add("@type", SqlDbType.NVarChar, 64).Value = Identities;
        log.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = row.IdentityId;
        log.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(row, Json);
        await log.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertIdentityAsync(SqlConnection connection, SqlTransaction? transaction, IdentityRow row, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.CustomerMembershipIdentities WHERE IdentityID = @id)
                UPDATE dbo.CustomerMembershipIdentities
                SET Status = @status, RevokedAt = @revoked, LastAuthenticatedAt = @last, UpdatedAt = @updated,
                    ProviderAccountDisplay = @display, ProviderEmail = @email
                WHERE IdentityID = @id;
            ELSE
                INSERT INTO dbo.CustomerMembershipIdentities
                    (IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail,
                     LinkedAt, LastAuthenticatedAt, Status, RevokedAt, CreatedAt, UpdatedAt)
                VALUES
                    (@id, @business, @customer, @provider, @subject, @display, @email,
                     @linked, @last, @status, @revoked, @created, @updated);
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = row.IdentityId;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = row.BusinessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = row.CustomerId;
        command.Parameters.Add("@provider", SqlDbType.NVarChar, 20).Value = row.Provider;
        command.Parameters.Add("@subject", SqlDbType.NVarChar, 256).Value = row.ProviderSubject;
        command.Parameters.Add("@display", SqlDbType.NVarChar, 256).Value = (object?)row.ProviderAccountDisplay ?? DBNull.Value;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 256).Value = (object?)row.ProviderEmail ?? DBNull.Value;
        command.Parameters.Add("@linked", SqlDbType.DateTime2).Value = row.LinkedAt;
        command.Parameters.Add("@last", SqlDbType.DateTime2).Value = (object?)row.LastAuthenticatedAt ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = row.Status;
        command.Parameters.Add("@revoked", SqlDbType.DateTime2).Value = (object?)row.RevokedAt ?? DBNull.Value;
        command.Parameters.Add("@created", SqlDbType.DateTime2).Value = row.CreatedAt;
        command.Parameters.Add("@updated", SqlDbType.DateTime2).Value = row.UpdatedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IdentityRow ReadIdentity(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetDateTime(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetDateTime(10),
        reader.GetDateTime(11),
        reader.GetDateTime(12));

    public sealed record IdentityRow(
        Guid IdentityId,
        Guid BusinessId,
        Guid CustomerId,
        string Provider,
        string ProviderSubject,
        string? ProviderAccountDisplay,
        string? ProviderEmail,
        DateTime LinkedAt,
        DateTime? LastAuthenticatedAt,
        string Status,
        DateTime? RevokedAt,
        DateTime CreatedAt,
        DateTime UpdatedAt);
}
