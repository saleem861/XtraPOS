using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class SqlProbeRequest
{
    public string? Intent { get; set; }
    public string? LocationKind { get; set; }
    public string? Server { get; set; }
    public string? InstanceName { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? AuthenticationType { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool? Encrypt { get; set; }
    public bool? TrustServerCertificate { get; set; }
    public string? ExpectedRole { get; set; }
}

public sealed record SqlProbeResponse(
    bool Ok,
    string? Message,
    string? DatabaseRole,
    string? BusinessName,
    bool? NameAvailable,
    bool? DatabaseExists = null,
    bool? SchemaCompatible = null);

public sealed class SetupConnectionProbe(IHostEnvironment? environment = null, SqlServerOptions? applicationSql = null, IConfiguration? configuration = null)
{
    public async Task<SqlProbeResponse> ProbeAsync(SqlProbeRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            return Fail("يرجى إدخال اسم قاعدة البيانات.");
        }

        var databaseName = request.DatabaseName.Trim();
        if (!BranchDatabase.IsSafeIdentifier(databaseName))
        {
            return Fail("اسم قاعدة البيانات غير مسموح.");
        }

        var expectedRole = request.ExpectedRole?.Trim();
        if (!string.IsNullOrWhiteSpace(expectedRole) && !DatabaseRole.IsKnown(expectedRole))
        {
            return Fail("دور قاعدة البيانات غير معروف.");
        }

        if (string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal)
            && !CentralDatabase.IsAllowedName(databaseName))
        {
            return Fail("اسم القاعدة المركزية غير مسموح.");
        }

        var creating = string.Equals(request.Intent, "new", StringComparison.OrdinalIgnoreCase);
        var staging = environment?.IsStaging() == true;
        var centralProbe = staging && string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal);
        var creationRejection = InitialSyncGuard.RejectCentralCreation(centralProbe, creating, databaseName);
        if (creationRejection is not null)
        {
            return Fail(creationRejection);
        }

        var stagingRejection = InitialSyncGuard.StagingCentralRejection(centralProbe, databaseName);
        if (stagingRejection is not null)
        {
            return Fail(stagingRejection);
        }

        var server = string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim();
        var settings = new SqlEndpointSettings(
            expectedRole ?? DatabaseRole.Branch,
            string.Equals(request.LocationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal)
                ? SqlEndpointSettings.Remote
                : SqlEndpointSettings.Local,
            server,
            request.InstanceName,
            request.Port,
            databaseName,
            string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
                ? SqlEndpointSettings.SqlLogin
                : SqlEndpointSettings.Integrated,
            request.Username,
            request.Password,
            request.Encrypt ?? true,
            request.TrustServerCertificate ?? true);
        if (configuration is not null)
        {
            try
            {
                settings = StagingSetupSql.Apply(environment, configuration, settings);
            }
            catch (SetupRejectedException exception)
            {
                return Fail(exception.Message);
            }
        }
        else if (staging
            && !string.IsNullOrWhiteSpace(applicationSql?.User)
            && !string.IsNullOrWhiteSpace(applicationSql.Password)
            && (centralProbe && string.Equals(databaseName, InitialSyncGuard.ApprovedExistingCentral, StringComparison.Ordinal)
                || !centralProbe && string.Equals(expectedRole, DatabaseRole.Branch, StringComparison.Ordinal)))
        {
            settings = settings with
            {
                Server = string.IsNullOrWhiteSpace(applicationSql.Server) ? settings.Server : applicationSql.Server.Trim(),
                InstanceName = null,
                Port = null,
                AuthenticationType = SqlEndpointSettings.SqlLogin,
                Username = applicationSql.User,
                Password = applicationSql.Password,
            };
        }
        try
        {
            await using var master = new SqlConnection(settings.BuildConnectionString("master"));
            await master.OpenAsync(cancellationToken);
            await using var exists = new SqlCommand("SELECT DB_ID(@name);", master);
            exists.Parameters.AddWithValue("@name", databaseName);
            var databaseId = await exists.ExecuteScalarAsync(cancellationToken);
            var nameTaken = databaseId is not null and not DBNull;
            if (creating)
            {
                if (nameTaken)
                {
                    var userTables = await CountUserTablesAsync(settings, databaseName, cancellationToken);
                    if (InitialSyncGuard.CanInitializeExistingEmpty(true, true, userTables, CentralDatabase.IsTestName(databaseName)))
                    {
                        return new SqlProbeResponse(true, null, expectedRole, null, true, true, null);
                    }

                    return Fail("اسم قاعدة البيانات مستخدم بالفعل.", nameAvailable: false);
                }

                await using var permission = new SqlCommand(
                    """
                    SELECT CASE
                        WHEN IS_SRVROLEMEMBER(N'dbcreator') = 1 OR IS_SRVROLEMEMBER(N'sysadmin') = 1 THEN 1
                        ELSE 0
                    END;
                    """,
                    master);
                var allowed = (int)(await permission.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
                return allowed
                    ? new SqlProbeResponse(true, null, expectedRole, null, true, false, null)
                    : Fail("لا توجد صلاحية إنشاء قاعدة بيانات على هذا الخادم.", nameAvailable: true);
            }

            if (!nameTaken)
            {
                return Fail("قاعدة البيانات غير موجودة.", nameAvailable: true);
            }
        }
        catch (Exception exception)
        {
            return Fail(environment is null
                ? Scrub(SqlEndpointSettings.DescribeConnectionFailure(exception), request.Password)
                : SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "connection-test", exception, request.Password));
        }

        try
        {
            await using var connection = new SqlConnection(settings.BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);
            string? role = null;
            if (await TableExistsAsync(connection, "SystemSettings", cancellationToken))
            {
                await using var roleCommand = new SqlCommand(
                    "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
                    connection);
                role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
            }

            if (string.IsNullOrWhiteSpace(role) || !DatabaseRole.IsKnown(role))
            {
                return Fail("قاعدة البيانات لا تثبت دورها.");
            }

            if (!string.IsNullOrWhiteSpace(expectedRole) && !string.Equals(role, expectedRole, StringComparison.Ordinal))
            {
                if (string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal)
                    && string.Equals(role, DatabaseRole.Branch, StringComparison.Ordinal))
                {
                    return Fail("هذه قاعدة فرع ولا يمكن استخدامها كقاعدة مركزية.", databaseExists: true, schemaCompatible: false);
                }

                if (string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal)
                    && string.Equals(role, DatabaseRole.BranchRemote, StringComparison.Ordinal))
                {
                    return Fail("هذه نسخة بعيدة للفرع ولا يمكن استخدامها كقاعدة مركزية.", databaseExists: true, schemaCompatible: false);
                }

                return Fail("دور قاعدة البيانات لا يطابق الاختيار.", databaseExists: true, schemaCompatible: false);
            }

            if (string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal))
            {
                var compatible = await IsCompatibleCentralAsync(connection, cancellationToken);
                if (!compatible)
                {
                    return Fail("بنية القاعدة المركزية غير متوافقة مع XtraPOS Central.", databaseExists: true, schemaCompatible: false);
                }
            }

            string? businessName = null;
            if (await TableExistsAsync(connection, "Businesses", cancellationToken))
            {
                await using var business = new SqlCommand(
                    "SELECT TOP (1) BusinessName FROM dbo.Businesses ORDER BY CreatedAt;",
                    connection);
                businessName = await business.ExecuteScalarAsync(cancellationToken) as string;
            }

            var central = string.Equals(expectedRole, DatabaseRole.Central, StringComparison.Ordinal);
            if (central && string.IsNullOrWhiteSpace(businessName))
            {
                return Fail("لا توجد هوية منشأة في القاعدة المركزية.", databaseExists: true, schemaCompatible: true);
            }

            return new SqlProbeResponse(true, null, role, businessName, false, true, central ? true : null);
        }
        catch (Exception exception)
        {
            return Fail(environment is null
                ? Scrub(SqlEndpointSettings.DescribeConnectionFailure(exception), request.Password)
                : SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "connection-test", exception, request.Password));
        }
    }

    private static async Task<int> CountUserTablesAsync(SqlEndpointSettings settings, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.BuildConnectionString(databaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;", connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static SqlProbeResponse Fail(string message, bool? nameAvailable = null, bool? databaseExists = null, bool? schemaCompatible = null) =>
        new(false, message, null, null, nameAvailable, databaseExists, schemaCompatible);

    private static async Task<bool> IsCompatibleCentralAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var blocked = new SqlCommand(
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
            connection);
        if ((int)(await blocked.ExecuteScalarAsync(cancellationToken) ?? 0) == 1)
        {
            return false;
        }

        if (!await TableExistsAsync(connection, "DatabaseSchemaHistory", cancellationToken))
        {
            return false;
        }

        await using var migrations = new SqlCommand(
            """
            SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory
            WHERE MigrationID IN (N'001_CentralFoundation', N'002_ItemOwnership', N'003_BranchCodeUniqueness');
            """,
            connection);
        return (int)(await migrations.ExecuteScalarAsync(cancellationToken) ?? 0) == 3;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.AddWithValue("@name", "dbo." + tableName);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    private static string Scrub(string message, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return message;
        }

        return message.Replace(password, "", StringComparison.Ordinal);
    }
}
