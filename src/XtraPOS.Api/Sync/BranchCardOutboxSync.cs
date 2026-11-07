using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sync;

public sealed class BranchCardOutboxSync(
    SqlConnectionFactory branchConnections,
    SyncEndpointStore endpoints,
    ILogger<BranchCardOutboxSync> logger)
{
    public const string Customers = "Customers";
    public const string CreditPolicies = "CustomerCreditPolicies";

    public async Task EnqueueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            DECLARE @endpoint uniqueidentifier =
                (SELECT TOP (1) EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central' ORDER BY CreatedAt);
            IF @endpoint IS NULL OR OBJECT_ID(N'dbo.SyncOutbox', N'U') IS NULL
                RETURN;
            IF NOT EXISTS (
                SELECT 1 FROM dbo.SyncOutbox
                WHERE EndpointID = @endpoint AND EntityType = @type AND EntityId = @id AND Status = N'Pending')
                INSERT INTO dbo.SyncOutbox (OutboxID, EndpointID, EntityType, EntityId, Status)
                VALUES (NEWID(), @endpoint, @type, @id, N'Pending');
            """,
            connection,
            transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 64).Value = entityType;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = entityId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        SyncEndpointView? endpoint;
        SqlEndpointSettings? settings;
        try
        {
            endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
            settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Customer card outbox was not read.");
            return;
        }

        if (endpoint is null || settings is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return;
        }

        var pending = await ReadPendingAsync(endpoint.EndpointId, cancellationToken);
        foreach (var row in pending)
        {
            try
            {
                if (row.EntityType == Customers)
                {
                    await FlushCustomerAsync(settings, endpoint.EndpointId, row.EntityId, cancellationToken);
                }
                else if (row.EntityType == CreditPolicies)
                {
                    await FlushPolicyAsync(settings, endpoint.EndpointId, row.EntityId, cancellationToken);
                }
            }
            catch (Exception exception) when (IsConnectivity(exception))
            {
                logger.LogWarning(exception, "Customer card upload paused because central is unreachable.");
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Customer card outbox row {EntityType} {EntityId} stayed pending.", row.EntityType, row.EntityId);
            }
        }
    }

    private async Task FlushCustomerAsync(SqlEndpointSettings central, Guid endpointId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var branch = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        var local = await CustomerMasterService.ReadPayloadAsync(branch, null, customerId, cancellationToken);
        if (local is null)
        {
            await DeletePendingAsync(branch, endpointId, Customers, customerId, cancellationToken);
            return;
        }

        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        var remoteUpdated = await ReadUpdatedAtAsync(remote, "dbo.Customers", "CustomerID", customerId, cancellationToken);
        var payload = CentralChangeApply.Serialize(local);
        if (remoteUpdated is null || local.UpdatedAt >= remoteUpdated.Value)
        {
            await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
            await CentralChangeApply.ApplyAsync(remote, transaction, Customers, customerId, payload, cancellationToken, authoritative: true);
            var after = await ReadUpdatedAtAsync(remote, transaction, "dbo.Customers", "CustomerID", customerId, cancellationToken);
            if (after is not null && after.Value >= local.UpdatedAt)
            {
                await InsertChangeLogAsync(remote, transaction, Customers, customerId, payload, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            var remotePayload = await CustomerMasterService.ReadPayloadAsync(remote, null, customerId, cancellationToken);
            if (remotePayload is not null)
            {
                await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
                await CentralChangeApply.ApplyAsync(
                    branch,
                    transaction,
                    Customers,
                    customerId,
                    CentralChangeApply.Serialize(remotePayload),
                    cancellationToken,
                    authoritative: true);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        await DeletePendingAsync(branch, endpointId, Customers, customerId, cancellationToken);
    }

    private async Task FlushPolicyAsync(SqlEndpointSettings central, Guid endpointId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var branch = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        var local = await ReadPolicyAsync(branch, null, customerId, cancellationToken);
        if (local is null)
        {
            await DeletePendingAsync(branch, endpointId, CreditPolicies, customerId, cancellationToken);
            return;
        }

        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        if (await ReadUpdatedAtAsync(remote, "dbo.Customers", "CustomerID", customerId, cancellationToken) is null)
        {
            await FlushCustomerAsync(central, endpointId, customerId, cancellationToken);
        }

        var remotePolicy = await ReadPolicyAsync(remote, null, customerId, cancellationToken);
        var payload = CentralChangeApply.Serialize(local);
        if (remotePolicy is null || local.UpdatedAt >= remotePolicy.UpdatedAt)
        {
            await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
            await CentralChangeApply.ApplyAsync(remote, transaction, CreditPolicies, customerId, payload, cancellationToken, authoritative: true);
            await InsertPolicyAuditAsync(remote, transaction, customerId, local, remotePolicy, cancellationToken);
            await InsertChangeLogAsync(remote, transaction, CreditPolicies, customerId, payload, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
            await CentralChangeApply.ApplyAsync(
                branch,
                transaction,
                CreditPolicies,
                customerId,
                CentralChangeApply.Serialize(remotePolicy),
                cancellationToken,
                authoritative: true);
            await transaction.CommitAsync(cancellationToken);
        }

        await DeletePendingAsync(branch, endpointId, CreditPolicies, customerId, cancellationToken);
    }

    private async Task<List<(string EntityType, Guid EntityId)>> ReadPendingAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var rows = new List<(string, Guid)>();
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT EntityType, EntityId
            FROM dbo.SyncOutbox
            WHERE EndpointID = @endpoint AND Status = N'Pending'
              AND EntityType IN (N'Customers', N'CustomerCreditPolicies')
            ORDER BY CASE WHEN EntityType = N'Customers' THEN 0 ELSE 1 END, CreatedAt;
            """,
            connection);
        command.Parameters.Add("@endpoint", SqlDbType.UniqueIdentifier).Value = endpointId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetGuid(1)));
        }

        return rows;
    }

    private static async Task<CreditPolicySyncPayload?> ReadPolicyAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.CustomerCreditPolicies', N'U');", connection, transaction);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return null;
        }

        await using var command = new SqlCommand(
            """
            SELECT CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus, CreatedAt, UpdatedAt, UpdatedBy
            FROM dbo.CustomerCreditPolicies
            WHERE CustomerID = @id;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CreditPolicySyncPayload(
            customerId,
            reader.GetBoolean(0),
            reader.GetDecimal(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetDateTime(4),
            reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6));
    }

    private static async Task<DateTime?> ReadUpdatedAtAsync(
        SqlConnection connection,
        string table,
        string key,
        Guid id,
        CancellationToken cancellationToken) =>
        await ReadUpdatedAtAsync(connection, null, table, key, id, cancellationToken);

    private static async Task<DateTime?> ReadUpdatedAtAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string table,
        string key,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT UpdatedAt FROM {table} WHERE {key} = @id;", connection, transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime updated ? updated : null;
    }

    private static async Task InsertChangeLogAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string entityType,
        Guid entityId,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload)
            VALUES (@type, @id, N'Upsert', @payload);
            """,
            connection,
            transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 64).Value = entityType;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = entityId;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPolicyAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid customerId,
        CreditPolicySyncPayload next,
        CreditPolicySyncPayload? previous,
        CancellationToken cancellationToken)
    {
        if (next.UpdatedBy is null)
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.CreditAuditLog (AuditID, CustomerID, ActorUserID, Action, OldValue, NewValue, Reason, Result)
            VALUES (NEWID(), @customer, @actor, N'PolicyUpdate', @old, @new, N'تحديث من الفرع', N'Completed');
            """,
            connection,
            transaction);
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = next.UpdatedBy.Value;
        command.Parameters.Add("@old", SqlDbType.NVarChar, 500).Value = previous is null
            ? "غير معد"
            : $"{previous.CreditEnabled}|{previous.CreditLimit}|{previous.PaymentTermDays}|{previous.CreditStatus}";
        command.Parameters.Add("@new", SqlDbType.NVarChar, 500).Value = $"{next.CreditEnabled}|{next.CreditLimit}|{next.PaymentTermDays}|{next.CreditStatus}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeletePendingAsync(
        SqlConnection connection,
        Guid endpointId,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            DELETE FROM dbo.SyncOutbox
            WHERE EndpointID = @endpoint AND EntityType = @type AND EntityId = @id AND Status = N'Pending';
            """,
            connection);
        command.Parameters.Add("@endpoint", SqlDbType.UniqueIdentifier).Value = endpointId;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 64).Value = entityType;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = entityId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsConnectivity(Exception exception)
    {
        if (exception is InvalidOperationException or TimeoutException or System.Security.Cryptography.CryptographicException)
        {
            return true;
        }

        if (exception is SqlException sql)
        {
            return sql.Number is -2 or 40 or 53 or 64 or 121 or 233 or 258 or 10053 or 10054 or 10060 or 11001;
        }

        return exception.InnerException is not null && IsConnectivity(exception.InnerException);
    }
}
