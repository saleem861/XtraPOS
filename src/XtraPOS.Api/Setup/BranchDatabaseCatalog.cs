using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record BranchDatabaseCandidate(
    string BranchName,
    string BranchCode,
    string DatabaseName,
    string BusinessName,
    string Server);

public sealed class BranchDatabaseCatalog(IOptions<SqlServerOptions> options)
{
    public async Task<IReadOnlyList<BranchDatabaseCandidate>> ListCompatibleAsync(string? centralBusinessName, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.Server))
        {
            throw new CentralRejectedException("لم يتم إعداد الاتصال بخادم البيانات.");
        }

        var server = configured.Server.Trim();
        var expectedBusiness = centralBusinessName?.Trim() ?? "";
        var found = new List<BranchDatabaseCandidate>();
        await using var master = new SqlConnection(ConnectionString(configured, "master"));
        await master.OpenAsync(cancellationToken);
        await using var list = new SqlCommand(
            """
            SELECT name
            FROM sys.databases
            WHERE state = 0
              AND name NOT IN (N'master', N'model', N'msdb', N'tempdb')
            ORDER BY name;
            """,
            master);
        await using var reader = await list.ExecuteReaderAsync(cancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        await reader.CloseAsync();
        foreach (var name in names)
        {
            if (!BranchDatabase.IsAllowedName(name) || CentralDatabase.IsAllowedName(name))
            {
                continue;
            }

            var candidate = await ReadCompatibleBranchAsync(configured, name, server, expectedBusiness, cancellationToken);
            if (candidate is not null)
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    private static async Task<BranchDatabaseCandidate?> ReadCompatibleBranchAsync(
        SqlServerOptions configured,
        string databaseName,
        string server,
        string expectedBusiness,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(configured, databaseName));
            await connection.OpenAsync(cancellationToken);
            if (!await TableExistsAsync(connection, "SystemSettings", cancellationToken))
            {
                return null;
            }

            var role = await ScalarAsync(
                connection,
                "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
                cancellationToken) as string;
            if (!string.Equals(role, DatabaseRole.Branch, StringComparison.Ordinal))
            {
                return null;
            }

            if (!await IsCompatibleBranchAsync(connection, cancellationToken))
            {
                return null;
            }

            var identity = await ReadIdentityAsync(connection, cancellationToken);
            if (identity is null)
            {
                return null;
            }

            if (expectedBusiness.Length > 0 && !string.Equals(identity.Value.BusinessName, expectedBusiness, StringComparison.Ordinal))
            {
                return null;
            }

            return new BranchDatabaseCandidate(
                identity.Value.BranchName,
                identity.Value.BranchCode,
                databaseName,
                identity.Value.BusinessName,
                server);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static async Task<bool> IsCompatibleBranchAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "Businesses", cancellationToken)
            || !await TableExistsAsync(connection, "Branches", cancellationToken)
            || !await TableExistsAsync(connection, "DatabaseSchemaHistory", cancellationToken))
        {
            return false;
        }

        var compatible = await ScalarAsync(
            connection,
            """
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'001_CentralFoundation') THEN 0
                WHEN NOT EXISTS (SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'001_InitialSchema') THEN 0
                WHEN NOT EXISTS (SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'010_SyncFoundation') THEN 0
                ELSE 1
            END;
            """,
            cancellationToken);
        return compatible is int flag && flag == 1;
    }

    private static async Task<(string BranchName, string BranchCode, string BusinessName)?> ReadIdentityAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) b.BranchName, b.BranchCode, bus.BusinessName
            FROM dbo.Branches AS b
            INNER JOIN dbo.Businesses AS bus ON bus.BusinessID = b.BusinessID
            WHERE LTRIM(RTRIM(b.BranchName)) <> N''
              AND LTRIM(RTRIM(bus.BusinessName)) <> N''
            ORDER BY b.CreatedAt;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? "" : reader.GetString(1).Trim(),
            reader.GetString(2).Trim());
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", cancellationToken, tableName);
        return value is int flag && flag == 1;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken, string? objectName = null)
    {
        await using var command = new SqlCommand(sql, connection);
        if (objectName is not null)
        {
            command.Parameters.AddWithValue("@name", "dbo." + objectName);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    private static string ConnectionString(SqlServerOptions configured, string catalog)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = configured.Server.Trim(),
            InitialCatalog = catalog,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = Math.Clamp(configured.ConnectTimeoutSeconds, 1, 15),
            ApplicationName = "XtraPOS.Api",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            IntegratedSecurity = string.IsNullOrWhiteSpace(configured.User),
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = configured.User;
            builder.Password = configured.Password;
        }

        return builder.ConnectionString;
    }
}
