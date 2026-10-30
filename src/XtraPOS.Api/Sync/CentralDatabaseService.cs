using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;

namespace XtraPOS.Api.Sync;

public sealed class CentralDatabaseService(CentralMigrationRunner migrations)
{
    public async Task CreateIfMissingAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var databaseName = CentralDatabase.Require(settings.DatabaseName);
        if (!string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal))
        {
            throw new CentralRejectedException("قاعدة البيانات المطلوبة ليست قاعدة مركزية.");
        }

        await using var master = new SqlConnection(settings.BuildConnectionString("master"));
        try
        {
            await master.OpenAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new CentralRejectedException(SqlEndpointSettings.DescribeConnectionFailure(exception));
        }

        var state = await ReadStateAsync(master, databaseName, cancellationToken);
        if (state is null)
        {
            await CentralDatabaseFiles.CreateNewAsync(master, databaseName, cancellationToken);
        }
        else if (!string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralRejectedException("قاعدة البيانات غير متاحة.");
        }

        await migrations.ApplyAsync(settings.BuildConnectionString(databaseName), cancellationToken);
        await VerifyExistingAsync(settings, cancellationToken);
    }

    public async Task VerifyExistingAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var databaseName = CentralDatabase.Require(settings.DatabaseName);
        try
        {
            await using var connection = new SqlConnection(settings.BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);
            var role = await ScalarAsync(
                connection,
                "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
                cancellationToken) as string;
            if (!string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
            {
                throw new CentralRejectedException("قاعدة البيانات غير متوافقة مع القاعدة المركزية.");
            }

            var migration = await ScalarAsync(
                connection,
                "SELECT MigrationID FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'001_CentralFoundation';",
                cancellationToken) as string;
            var ownership = await ScalarAsync(
                connection,
                "SELECT MigrationID FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'002_ItemOwnership';",
                cancellationToken) as string;
            if (!string.Equals(migration, CentralMigrationRunner.MigrationId, StringComparison.Ordinal)
                || !string.Equals(ownership, CentralMigrationRunner.OwnershipMigrationId, StringComparison.Ordinal))
            {
                throw new CentralRejectedException("قاعدة البيانات غير متوافقة مع القاعدة المركزية.");
            }

            var legacyCustomers = await ScalarAsync(
                connection,
                """
                SELECT CASE
                    WHEN OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
                     AND COL_LENGTH(N'dbo.Customers', N'CustomerID') IS NULL THEN 1
                    ELSE 0
                END;
                """,
                cancellationToken);
            if (legacyCustomers is int legacy && legacy == 1)
            {
                throw new CentralRejectedException("قاعدة البيانات غير متوافقة مع القاعدة المركزية.");
            }

            var emptyNames = await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM dbo.Businesses WHERE BusinessName IS NULL OR LTRIM(RTRIM(BusinessName)) = N'';",
                cancellationToken);
            if (emptyNames is int count && count > 0)
            {
                throw new CentralRejectedException("هوية المنشأة في القاعدة المركزية غير صالحة.");
            }
        }
        catch (CentralRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CentralRejectedException(SqlEndpointSettings.DescribeConnectionFailure(exception));
        }
    }

    private static async Task<string?> ReadStateAsync(SqlConnection master, string databaseName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT state_desc FROM sys.databases WHERE name = @name;",
            master);
        command.Parameters.AddWithValue("@name", databaseName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }
}
