using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Credit;

public sealed class CreditPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CreditPolicyView(
    Guid CustomerId,
    bool CreditEnabled,
    decimal CreditLimit,
    int PaymentTermDays,
    string CreditStatus,
    string RowVersion,
    bool Configured,
    decimal Outstanding,
    decimal Available,
    bool CentralReachable);

public sealed record CreditPolicyWrite(bool CreditEnabled, decimal CreditLimit, int PaymentTermDays, string CreditStatus, string? RowVersion, string? Reason);

public sealed class CreditPolicyService(SyncEndpointStore endpoints, SqlConnectionFactory connections, BranchCardOutboxSync outbox)
{
    public static string? Denial(bool canManage, bool canSuspend, bool administer, CreditPolicyView current, CreditPolicyWrite next)
    {
        if (administer)
        {
            return null;
        }

        var suspending = next.CreditStatus == "Suspended" && current.CreditStatus != "Suspended";
        var raising = next.CreditEnabled && !current.CreditEnabled
            || next.CreditLimit > current.CreditLimit
            || next.PaymentTermDays != current.PaymentTermDays
            || (next.CreditStatus == "Active" && current.CreditStatus != "Active");
        if (suspending && !canSuspend && !canManage)
        {
            return "تعذر إيقاف الائتمان.";
        }

        if ((raising || next.CreditEnabled != current.CreditEnabled || next.CreditLimit != current.CreditLimit) && !canManage)
        {
            return "لا توجد صلاحية لإدارة حد الائتمان.";
        }

        return null;
    }

    public async Task<CreditPolicyView> ReadAsync(SqlConnection central, Guid customerId, CancellationToken cancellationToken) =>
        await ReadCoreAsync(central, customerId, cancellationToken) ?? Missing(customerId, true);

    public async Task<CreditPolicyView> ReadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var local = await ReadLocalAsync(customerId, cancellationToken);
        var outstanding = 0m;
        var reachable = false;
        CreditPolicyView? remote = null;
        try
        {
            var central = await OpenAsync(cancellationToken);
            if (central is not null)
            {
                await using (central)
                {
                    reachable = true;
                    remote = await ReadCoreAsync(central, customerId, cancellationToken);
                    outstanding = remote?.Outstanding ?? await OutstandingAsync(central, customerId, cancellationToken);
                }
            }
        }
        catch (SqlException)
        {
            reachable = false;
        }

        if (local is not null)
        {
            var available = local.CreditEnabled && local.CreditStatus == "Active" ? CreditAvailability.Available(local.CreditLimit, outstanding, 0m) : 0m;
            return local with { Outstanding = outstanding, Available = available, CentralReachable = reachable };
        }

        if (remote is not null)
        {
            return remote with { CentralReachable = reachable };
        }

        return Missing(customerId, reachable);
    }

    public async Task<CreditPolicyView> SaveAsync(Guid customerId, Guid actorUserId, Guid? branchId, CreditPolicyWrite write, CancellationToken cancellationToken)
    {
        _ = branchId;
        Validate(write);
        try
        {
            await SaveLocalAsync(customerId, actorUserId, write, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new CreditPolicyException("CREDIT_CONFLICT", "تم تغيير إعدادات الائتمان من مستخدم آخر.");
        }

        try
        {
            await outbox.FlushAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // The local card is saved. The pending outbox row is uploaded on the next cycle.
        }

        return await ReadAsync(customerId, cancellationToken);
    }

    public async Task<CreditPolicyView> SaveAsync(SqlConnection central, Guid customerId, Guid actorUserId, Guid? branchId, CreditPolicyWrite write, CancellationToken cancellationToken)
    {
        Validate(write);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
            if (!await CustomerExistsAsync(central, transaction, customerId, cancellationToken))
            {
                throw new CreditPolicyException("CUSTOMER_NOT_FOUND", "الزبون غير موجود في المركز.");
            }

            var current = await ReadCoreAsync(central, transaction, customerId, cancellationToken) ?? Missing(customerId, true);
            var version = string.IsNullOrWhiteSpace(write.RowVersion) ? current.RowVersion : write.RowVersion;
            if (current.Configured && !string.Equals(version, current.RowVersion, StringComparison.Ordinal))
            {
                throw new CreditPolicyException("CREDIT_CONFLICT", "تم تغيير إعدادات الائتمان من مستخدم آخر.");
            }

            var status = write.CreditEnabled
                ? (write.CreditStatus == "Suspended" ? "Suspended" : "Active")
                : (write.CreditStatus == "Suspended" ? "Suspended" : "Inactive");
            var enabled = write.CreditEnabled && status == "Active";

            if (current.Configured)
            {
                await using var update = new SqlCommand(
                    """
                    UPDATE dbo.CustomerCreditPolicies
                    SET CreditEnabled = @enabled, CreditLimit = @limit, PaymentTermDays = @terms, CreditStatus = @status,
                        UpdatedBy = @actor, UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerID = @customer AND RowVersion = @version;
                    """,
                    central,
                    transaction);
                Bind(update, customerId, enabled, write.CreditLimit, write.PaymentTermDays, status, actorUserId);
                update.Parameters.Add("@version", SqlDbType.VarBinary, 8).Value = Convert.FromBase64String(current.RowVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new CreditPolicyException("CREDIT_CONFLICT", "تم تغيير إعدادات الائتمان من مستخدم آخر.");
                }
            }
            else
            {
                await using var insert = new SqlCommand(
                    """
                    INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus, UpdatedBy)
                    VALUES (@customer, @enabled, @limit, @terms, @status, @actor);
                    """,
                    central,
                    transaction);
                Bind(insert, customerId, enabled, write.CreditLimit, write.PaymentTermDays, status, actorUserId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var audit = new SqlCommand(
                """
                INSERT INTO dbo.CreditAuditLog (AuditID, CustomerID, ActorUserID, BranchID, Action, OldValue, NewValue, Reason, Result)
                VALUES (NEWID(), @customer, @actor, @branch, N'PolicyUpdate', @old, @new, @reason, N'Completed');
                """,
                central,
                transaction);
            audit.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
            audit.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
            audit.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = (object?)branchId ?? DBNull.Value;
            audit.Parameters.Add("@old", SqlDbType.NVarChar, 500).Value = Describe(current);
            audit.Parameters.Add("@new", SqlDbType.NVarChar, 500).Value = Describe(enabled, write.CreditLimit, write.PaymentTermDays, status);
            audit.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = (object?)write.Reason ?? DBNull.Value;
            await audit.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        return await ReadCoreAsync(central, customerId, cancellationToken) ?? Missing(customerId, true);
    }

    private static void Validate(CreditPolicyWrite write)
    {
        if (write.CreditLimit < 0m || decimal.Round(write.CreditLimit, 4) != write.CreditLimit)
        {
            throw new CreditPolicyException("INVALID_LIMIT", "حد الائتمان غير صالح.");
        }

        if (write.PaymentTermDays is < 0 or > 3650)
        {
            throw new CreditPolicyException("INVALID_TERMS", "مدة السداد غير صالحة.");
        }

        if (write.CreditStatus is not ("Inactive" or "Active" or "Suspended"))
        {
            throw new CreditPolicyException("INVALID_STATUS", "حالة الائتمان غير صالحة.");
        }
    }

    private static void Bind(SqlCommand command, Guid customerId, bool enabled, decimal limit, int terms, string status, Guid actorUserId)
    {
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@enabled", SqlDbType.Bit).Value = enabled;
        command.Parameters.Add("@limit", SqlDbType.Decimal).Value = limit;
        command.Parameters.Add("@terms", SqlDbType.Int).Value = terms;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = status;
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorUserId;
    }

    private async Task<CreditPolicyView?> ReadLocalAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.CustomerCreditPolicies', N'U');", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return null;
        }

        await using var command = new SqlCommand(
            """
            SELECT CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus, RowVersion
            FROM dbo.CustomerCreditPolicies
            WHERE CustomerID = @id;
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var enabled = reader.GetBoolean(0);
        var limit = reader.GetDecimal(1);
        var status = reader.GetString(3);
        var version = new byte[8];
        reader.GetBytes(4, 0, version, 0, 8);
        return new CreditPolicyView(customerId, enabled, limit, reader.GetInt32(2), status, Convert.ToBase64String(version), true, 0m, 0m, false);
    }

    private async Task SaveLocalAsync(Guid customerId, Guid actorUserId, CreditPolicyWrite write, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await CustomerExistsAsync(connection, transaction, customerId, cancellationToken))
        {
            throw new CreditPolicyException("CUSTOMER_NOT_FOUND", "الزبون غير موجود.");
        }

        var current = await ReadLocalCoreAsync(connection, transaction, customerId, cancellationToken) ?? Missing(customerId, false);
        var version = string.IsNullOrWhiteSpace(write.RowVersion) ? current.RowVersion : write.RowVersion;
        if (current.Configured && !string.Equals(version, current.RowVersion, StringComparison.Ordinal))
        {
            throw new CreditPolicyException("CREDIT_CONFLICT", "تم تغيير إعدادات الائتمان من مستخدم آخر.");
        }

        var status = write.CreditEnabled
            ? (write.CreditStatus == "Suspended" ? "Suspended" : "Active")
            : (write.CreditStatus == "Suspended" ? "Suspended" : "Inactive");
        var enabled = write.CreditEnabled && status == "Active";
        if (current.Configured)
        {
            await using var update = new SqlCommand(
                """
                UPDATE dbo.CustomerCreditPolicies
                SET CreditEnabled = @enabled, CreditLimit = @limit, PaymentTermDays = @terms, CreditStatus = @status,
                    UpdatedBy = @actor, UpdatedAt = SYSUTCDATETIME()
                WHERE CustomerID = @customer AND RowVersion = @version;
                """,
                connection,
                transaction);
            Bind(update, customerId, enabled, write.CreditLimit, write.PaymentTermDays, status, actorUserId);
            update.Parameters.Add("@version", SqlDbType.VarBinary, 8).Value = Convert.FromBase64String(current.RowVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new CreditPolicyException("CREDIT_CONFLICT", "تم تغيير إعدادات الائتمان من مستخدم آخر.");
            }
        }
        else
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus, UpdatedBy)
                VALUES (@customer, @enabled, @limit, @terms, @status, @actor);
                """,
                connection,
                transaction);
            Bind(insert, customerId, enabled, write.CreditLimit, write.PaymentTermDays, status, actorUserId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await outbox.EnqueueAsync(connection, transaction, BranchCardOutboxSync.CreditPolicies, customerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<CreditPolicyView?> ReadLocalCoreAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus, RowVersion
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

        var version = new byte[8];
        reader.GetBytes(4, 0, version, 0, 8);
        return new CreditPolicyView(customerId, reader.GetBoolean(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetString(3), Convert.ToBase64String(version), true, 0m, 0m, false);
    }

    private static async Task<decimal> OutstandingAsync(SqlConnection connection, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT ISNULL((SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = @id AND Status IN (N'Open', N'Partial')), 0);
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken) ?? 0m);
    }

    private async Task<SqlConnection?> OpenAsync(CancellationToken cancellationToken)
    {
        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null)
        {
            return null;
        }

        var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<bool> CustomerExistsAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = @id;", connection, transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static Task<CreditPolicyView?> ReadCoreAsync(SqlConnection connection, Guid customerId, CancellationToken cancellationToken) =>
        ReadCoreAsync(connection, null, customerId, cancellationToken);

    private static async Task<CreditPolicyView?> ReadCoreAsync(SqlConnection connection, SqlTransaction? transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT policy.CreditEnabled, policy.CreditLimit, policy.PaymentTermDays, policy.CreditStatus, policy.RowVersion,
                   ISNULL((SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = @id AND Status IN (N'Open', N'Partial')), 0)
            FROM dbo.CustomerCreditPolicies AS policy
            WHERE policy.CustomerID = @id;
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var enabled = reader.GetBoolean(0);
        var limit = reader.GetDecimal(1);
        var status = reader.GetString(3);
        var outstanding = reader.GetDecimal(5);
        var available = enabled && status == "Active" ? CreditAvailability.Available(limit, outstanding, 0m) : 0m;
        var version = new byte[8];
        reader.GetBytes(4, 0, version, 0, 8);
        return new CreditPolicyView(customerId, enabled, limit, reader.GetInt32(2), status, Convert.ToBase64String(version), true, outstanding, available, true);
    }

    private static CreditPolicyView Missing(Guid customerId, bool reachable) =>
        new(customerId, false, 0m, 30, "Inactive", "", false, 0m, 0m, reachable);

    private static string Describe(CreditPolicyView policy) =>
        Describe(policy.CreditEnabled, policy.CreditLimit, policy.PaymentTermDays, policy.CreditStatus);

    private static string Describe(bool enabled, decimal limit, int terms, string status) =>
        string.Create(CultureInfo.InvariantCulture, $"enabled={enabled};limit={limit};terms={terms};status={status}");
}
