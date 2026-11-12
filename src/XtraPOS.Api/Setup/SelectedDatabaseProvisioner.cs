using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class ProductionDatabaseNames
{
    public static bool IsForbidden(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length == 0)
        {
            return true;
        }

        return value.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Demo", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Integration", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Temporary", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Staging", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SelectedDatabaseProvisioner(MigrationRunner migrations)
{
    public async Task ProbeAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        RequireCatalog(settings);
        if (string.Equals(settings.AuthenticationType, SqlEndpointSettings.Integrated, StringComparison.Ordinal)
            && !OperatingSystem.IsWindows())
        {
            throw new SetupRejectedException("مصادقة ويندوز غير متاحة من خادم التطبيق. استخدم حساب SQL.");
        }

        try
        {
            await using var master = new SqlConnection(settings.BuildConnectionString("master"));
            await master.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", master);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (SetupRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var target = settings.Server.Trim();
            if (!string.IsNullOrWhiteSpace(settings.InstanceName) && !target.Contains('\\', StringComparison.Ordinal))
            {
                target += "\\" + settings.InstanceName.Trim();
            }

            if (settings.Port is > 0 && !target.Contains(',', StringComparison.Ordinal))
            {
                target += "," + settings.Port.Value;
            }

            throw new SetupRejectedException(SqlEndpointSettings.DescribeConnectionFailure(exception) + " (" + target + ")");
        }
    }

    public async Task ProvisionSchemaAsync(
        SqlEndpointSettings settings,
        CancellationToken cancellationToken,
        string? dataDirectory = null)
    {
        await EnsureCatalogAsync(settings, cancellationToken, dataDirectory);
        try
        {
            await migrations.ApplyPendingToAsync(settings.BuildConnectionString(settings.DatabaseName), cancellationToken);
        }
        catch (SetupRejectedException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupRejectedException("تعذر تطبيق تحديثات قاعدة البيانات. لم يتم حذف القاعدة.");
        }
    }

    public async Task EnsureCatalogAsync(
        SqlEndpointSettings settings,
        CancellationToken cancellationToken,
        string? dataDirectory = null)
    {
        RequireCatalog(settings);

        var database = settings.DatabaseName.Replace("]", "]]", StringComparison.Ordinal);
        try
        {
            await using (var master = new SqlConnection(settings.BuildConnectionString("master")))
            {
                await master.OpenAsync(cancellationToken);
                await using var exists = new SqlCommand(
                    """
                    SELECT DB_ID(@name), collation_name
                    FROM sys.databases
                    WHERE name = @name;
                    """,
                    master);
                exists.Parameters.AddWithValue("@name", settings.DatabaseName);
                await using var reader = await exists.ExecuteReaderAsync(cancellationToken);
                var found = await reader.ReadAsync(cancellationToken);
                if (!found)
                {
                    await reader.DisposeAsync();
                    await using var permission = new SqlCommand("SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE DATABASE');", master);
                    var allowed = await permission.ExecuteScalarAsync(cancellationToken);
                    if (allowed is int flag && flag == 0)
                    {
                        throw new SetupRejectedException("تم الاتصال بالخادم، لكن حساب SQL لا يملك صلاحية إنشاء قاعدة بيانات.");
                    }

                    await using var create = new SqlCommand(CreateDatabaseSql(database, dataDirectory), master);
                    create.CommandTimeout = 60;
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    var collation = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(collation, "Arabic_CI_AS", StringComparison.Ordinal))
                    {
                        throw new SetupRejectedException("قاعدة البيانات الحالية غير متوافقة مع إعداد إكسترا. لم يتم تعديلها.");
                    }
                }
            }

            await using (var databaseConnection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName)))
            {
                await databaseConnection.OpenAsync(cancellationToken);
                await using var owner = new SqlCommand("SELECT IS_ROLEMEMBER(N'db_owner');", databaseConnection);
                var isOwner = await owner.ExecuteScalarAsync(cancellationToken);
                if (isOwner is int member && member != 1)
                {
                    throw new SetupRejectedException("تم الاتصال بالخادم، لكن الحساب ليس مالكاً لقاعدة البيانات. لم يتم تعديلها.");
                }

                await using var history = new SqlCommand("SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');", databaseConnection);
                var historyId = await history.ExecuteScalarAsync(cancellationToken);
                await using var tables = new SqlCommand(
                    "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;",
                    databaseConnection);
                var tableCount = (int)(await tables.ExecuteScalarAsync(cancellationToken) ?? 0);
                if (historyId is null or DBNull && tableCount > 0)
                {
                    throw new SetupRejectedException("قاعدة بهذا الاسم موجودة وليست قاعدة إكسترا. لم يتم تعديلها.");
                }
            }
        }
        catch (SetupRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SetupRejectedException(SqlEndpointSettings.DescribeConnectionFailure(exception));
        }

    }

    private static void RequireCatalog(SqlEndpointSettings settings)
    {
        if (!OperationalDatabaseGuard.IsProductionCatalog(settings.DatabaseName))
        {
            throw new SetupRejectedException("اسم قاعدة البيانات غير صالح.");
        }
    }

    private static string CreateDatabaseSql(string database, string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return $"CREATE DATABASE [{database}] COLLATE Arabic_CI_AS;";
        }

        var directory = Path.GetFullPath(dataDirectory.Trim());
        if (directory.IndexOfAny(['\'', ';', '\r', '\n']) >= 0)
        {
            throw new SetupRejectedException("مسار ملفات قاعدة البيانات غير صالح.");
        }

        var file = Path.Combine(directory, database + ".mdf").Replace("'", "''");
        var log = Path.Combine(directory, database + "_log.ldf").Replace("'", "''");
        return $"""
            CREATE DATABASE [{database}]
            ON (NAME = N'{database}', FILENAME = N'{file}')
            LOG ON (NAME = N'{database}_log', FILENAME = N'{log}')
            COLLATE Arabic_CI_AS;
            """;
    }
}
