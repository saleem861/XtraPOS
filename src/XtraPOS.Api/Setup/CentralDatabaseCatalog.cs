using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record CentralDatabaseCandidate(string BusinessName, string DatabaseName, string Server);

public sealed class CentralDatabaseCatalog(IOptions<SqlServerOptions> options)
{
    public async Task<IReadOnlyList<CentralDatabaseCandidate>> ListCompatibleAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.Server))
        {
            throw new CentralRejectedException("لم يتم إعداد الاتصال بخادم البيانات.");
        }

        var server = configured.Server.Trim();
        var found = new List<CentralDatabaseCandidate>();
        await using (var master = new SqlConnection(ConnectionString(configured, "master")))
        {
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
                if (!CentralDatabase.IsAllowedName(name))
                {
                    continue;
                }

                if (BranchDatabase.IsTestName(configured.Database)
                    && !string.Equals(name, InitialSyncGuard.ApprovedExistingCentral, StringComparison.Ordinal))
                {
                    continue;
                }

                var businessName = await ReadCompatibleBusinessAsync(configured, name, cancellationToken);
                if (!string.IsNullOrWhiteSpace(businessName))
                {
                    found.Add(new CentralDatabaseCandidate(businessName, name, server));
                }
            }
        }

        return found;
    }

    private static async Task<string?> ReadCompatibleBusinessAsync(SqlServerOptions configured, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(configured, databaseName));
            await connection.OpenAsync(cancellationToken);
            if (!await TableExistsAsync(connection, "SystemSettings", cancellationToken))
            {
                return null;
            }

            var role = await ScalarAsync(connection, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';", cancellationToken) as string;
            if (!string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
            {
                return null;
            }

            if (!await IsCompatibleCentralAsync(connection, cancellationToken) || !await TableExistsAsync(connection, "Businesses", cancellationToken))
            {
                return null;
            }

            var businessName = await ScalarAsync(connection, "SELECT TOP (1) BusinessName FROM dbo.Businesses ORDER BY CreatedAt;", cancellationToken) as string;
            return string.IsNullOrWhiteSpace(businessName) ? null : businessName.Trim();
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static async Task<bool> IsCompatibleCentralAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var blocked = await ScalarAsync(
            connection,
            """
            SELECT CASE
                WHEN OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL THEN 1
                WHEN OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NOT NULL THEN 1
                WHEN OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL THEN 1
                WHEN OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL THEN 1
                WHEN OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL THEN 1
                WHEN OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
                 AND COL_LENGTH(N'dbo.Customers', N'CustomerID') IS NULL THEN 1
                ELSE 0
            END;
            """,
            cancellationToken);
        if (blocked is int flag && flag == 1)
        {
            return false;
        }

        if (!await TableExistsAsync(connection, "DatabaseSchemaHistory", cancellationToken))
        {
            return false;
        }

        var migrations = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory
            WHERE MigrationID IN (N'001_CentralFoundation', N'002_ItemOwnership', N'003_BranchCodeUniqueness');
            """,
            cancellationToken);
        return migrations is int count && count == 3;
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
