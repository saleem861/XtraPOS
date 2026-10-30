using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;

namespace XtraPOS.Api.Sync;

public sealed record CentralSetupEvent(
    string Type,
    string? Stage = null,
    string? State = null,
    string? Label = null,
    string? Message = null,
    bool? AlreadyConfigured = null,
    string? BusinessName = null);

public sealed class CentralSetupRequest
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
    public string? BusinessName { get; set; }
    public string? BusinessDisplayName { get; set; }
    public string? Description { get; set; }
}

public sealed class CentralSetupExecutor(
    CentralMigrationRunner migrations,
    CentralConnectionFileStore secrets,
    IOptions<SqlServerOptions> sqlOptions,
    ILogger<CentralSetupExecutor> logger)
{
    public const string IncompleteMessage = "تعذر إكمال إعداد الإدارة المركزية. لم يتم اعتبار الإعداد مكتملاً.";

    public Func<string, CancellationToken, Task>? ApplyOverride { get; set; }

    public async Task ExecuteAsync(
        CentralSetupRequest request,
        Func<CentralSetupEvent, CancellationToken, Task> emit,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var creating = string.Equals(request.Intent, "new", StringComparison.OrdinalIgnoreCase);
        SqlEndpointSettings? settings = null;
        var createdByThisOperation = false;
        var stage = "connect";
        try
        {
            settings = ResolveSettings(request, creating);
            await Progress(emit, operationId, settings, "connect", "started", "جارٍ الاتصال بالخادم", cancellationToken);
            await using (var master = new SqlConnection(settings.BuildConnectionString("master")))
            {
                await master.OpenAsync(cancellationToken);
                var exists = await DatabaseExistsAsync(master, settings.DatabaseName, cancellationToken);
                await Progress(emit, operationId, settings, "connect", "succeeded", "تم الاتصال بالخادم", cancellationToken);

                if (!creating)
                {
                    if (!exists)
                    {
                        throw new CentralRejectedException("قاعدة البيانات غير موجودة.");
                    }

                    await FinishExistingAsync(settings, emit, operationId, cancellationToken);
                    return;
                }

                if (exists)
                {
                    if (await IsCompleteCentralAsync(settings, cancellationToken) is { } existing)
                    {
                        await secrets.SaveAsync(settings, cancellationToken);
                        await EmitAlreadyConfiguredAsync(emit, operationId, settings, existing, cancellationToken);
                        return;
                    }

                    throw new CentralRejectedException("قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.");
                }

                stage = "createDatabase";
                await Progress(emit, operationId, settings, stage, "started", "جارٍ إنشاء قاعدة البيانات", cancellationToken);
                if (!await CanCreateDatabaseAsync(master, cancellationToken))
                {
                    throw new CentralRejectedException("لا توجد صلاحية إنشاء قاعدة بيانات على هذا الخادم.");
                }

                var files = await CentralDatabaseFiles.CreateNewAsync(master, settings.DatabaseName, cancellationToken);
                createdByThisOperation = true;
                logger.LogInformation(
                    "Central setup created database {Database} data file {DataFile} log file {LogFile}",
                    settings.DatabaseName,
                    files.DataFileName,
                    files.LogFileName);
            }

            await Progress(emit, operationId, settings, "createDatabase", "succeeded", "تم إنشاء قاعدة البيانات", cancellationToken);
            stage = "migrations";
            await Progress(emit, operationId, settings, stage, "started", "جارٍ تطبيق بنية XtraPOS Central", cancellationToken);
            await (ApplyOverride ?? migrations.ApplyAsync)(settings.BuildConnectionString(settings.DatabaseName), cancellationToken);
            await Progress(emit, operationId, settings, stage, "succeeded", "تم تطبيق بنية XtraPOS Central", cancellationToken);

            stage = "business";
            await Progress(emit, operationId, settings, stage, "started", "جارٍ إنشاء هوية المنشأة", cancellationToken);
            var businessName = await EnsureBusinessAsync(settings, request, cancellationToken);
            await Progress(emit, operationId, settings, stage, "succeeded", "تم إنشاء هوية المنشأة", cancellationToken);

            stage = "verify";
            await Progress(emit, operationId, settings, stage, "started", "جارٍ التحقق من القاعدة المركزية", cancellationToken);
            await VerifyCreatedAsync(settings, cancellationToken);
            await secrets.SaveAsync(settings, cancellationToken);
            await Progress(emit, operationId, settings, stage, "succeeded", "تم التحقق من القاعدة المركزية", cancellationToken);
            Log(operationId, settings, "complete", "Succeeded");
            await emit(new CentralSetupEvent("completed", BusinessName: businessName, AlreadyConfigured: false), cancellationToken);
        }
        catch (Exception exception)
        {
            var message = Scrub(UserMessage(exception), request.Password);
            if (settings is not null && createdByThisOperation)
            {
                var removed = await TryDropCreatedDatabaseAsync(settings, cancellationToken);
                message = removed
                    ? message + " تمت إزالة القاعدة التي أُنشئت في هذه العملية."
                    : message + " بقيت قاعدة غير مكتملة ولم يتم اعتبار الإعداد مكتملاً.";
            }

            if (exception is SqlException sql && settings is not null)
            {
                logger.LogWarning(
                    "Central setup {OperationId} server {Server} database {Database} stage {Stage} sql {Number} message {Message}",
                    operationId,
                    settings.Server,
                    settings.DatabaseName,
                    stage,
                    sql.Number,
                    Scrub(sql.Message, request.Password));
            }

            if (settings is not null)
            {
                Log(operationId, settings, stage, "Failed");
            }
            else
            {
                logger.LogInformation(
                    "Central setup {OperationId} server {Server} database {Database} stage {Stage} result {Result}",
                    operationId,
                    request.Server,
                    request.DatabaseName,
                    stage,
                    "Failed");
            }

            await emit(new CentralSetupEvent("failed", Stage: stage, State: "failed", Message: message), cancellationToken);
        }
    }

    private async Task FinishExistingAsync(
        SqlEndpointSettings settings,
        Func<CentralSetupEvent, CancellationToken, Task> emit,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(settings, cancellationToken);
        if (!string.Equals(inspection.Role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            throw new CentralRejectedException(RoleMessage(inspection.Role));
        }

        if (!inspection.SchemaCompatible)
        {
            throw new CentralRejectedException("بنية القاعدة المركزية غير متوافقة مع XtraPOS Central.");
        }

        if (string.IsNullOrWhiteSpace(inspection.BusinessName))
        {
            throw new CentralRejectedException("لا توجد هوية منشأة في القاعدة المركزية.");
        }

        await Progress(emit, operationId, settings, "createDatabase", "succeeded", "القاعدة المركزية موجودة", cancellationToken);
        await Progress(emit, operationId, settings, "migrations", "succeeded", "تم التحقق من بنية XtraPOS Central", cancellationToken);
        await Progress(emit, operationId, settings, "business", "succeeded", "تم تحميل هوية المنشأة", cancellationToken);
        await Progress(emit, operationId, settings, "verify", "succeeded", "تم التحقق من القاعدة المركزية", cancellationToken);
        await secrets.SaveAsync(settings, cancellationToken);
        Log(operationId, settings, "complete", "Succeeded");
        await emit(new CentralSetupEvent("completed", BusinessName: inspection.BusinessName, AlreadyConfigured: true), cancellationToken);
    }

    private async Task EmitAlreadyConfiguredAsync(
        Func<CentralSetupEvent, CancellationToken, Task> emit,
        Guid operationId,
        SqlEndpointSettings settings,
        string businessName,
        CancellationToken cancellationToken)
    {
        await Progress(emit, operationId, settings, "createDatabase", "succeeded", "القاعدة المركزية موجودة", cancellationToken);
        await Progress(emit, operationId, settings, "migrations", "succeeded", "تم التحقق من بنية XtraPOS Central", cancellationToken);
        await Progress(emit, operationId, settings, "business", "succeeded", "هوية المنشأة موجودة مسبقاً", cancellationToken);
        await Progress(emit, operationId, settings, "verify", "succeeded", "تم التحقق من القاعدة المركزية", cancellationToken);
        Log(operationId, settings, "complete", "AlreadyConfigured");
        await emit(new CentralSetupEvent("completed", BusinessName: businessName, AlreadyConfigured: true), cancellationToken);
    }

    private SqlEndpointSettings ResolveSettings(CentralSetupRequest request, bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(request.BusinessName))
        {
            throw new CentralRejectedException("يرجى إدخال اسم المنشأة.");
        }

        var databaseName = CentralDatabase.Require(request.DatabaseName);
        var remote = string.Equals(request.LocationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal);
        if (!remote)
        {
            var configured = sqlOptions.Value;
            if (string.IsNullOrWhiteSpace(configured.Server))
            {
                throw new CentralRejectedException("لم يتم إعداد الاتصال بخادم البيانات.");
            }

            var integrated = string.IsNullOrWhiteSpace(configured.User);
            return new SqlEndpointSettings(
                DatabaseRole.Central,
                SqlEndpointSettings.Local,
                configured.Server.Trim(),
                null,
                null,
                databaseName,
                integrated ? SqlEndpointSettings.Integrated : SqlEndpointSettings.SqlLogin,
                integrated ? null : configured.User,
                integrated ? null : configured.Password,
                true,
                true);
        }

        if (string.IsNullOrWhiteSpace(request.Server))
        {
            throw new CentralRejectedException("يرجى إدخال عنوان الخادم.");
        }

        var sqlLogin = string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(request.Username);
        if (sqlLogin && (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)))
        {
            throw new CentralRejectedException("يرجى إدخال اسم المستخدم وكلمة المرور.");
        }

        return new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Remote,
            request.Server.Trim(),
            string.IsNullOrWhiteSpace(request.InstanceName) ? null : request.InstanceName.Trim(),
            request.Port is > 0 ? request.Port : null,
            databaseName,
            sqlLogin ? SqlEndpointSettings.SqlLogin : SqlEndpointSettings.Integrated,
            sqlLogin ? request.Username!.Trim() : null,
            sqlLogin ? request.Password : null,
            request.Encrypt ?? true,
            request.TrustServerCertificate ?? true);
    }

    private async Task<string> EnsureBusinessAsync(
        SqlEndpointSettings settings,
        CentralSetupRequest request,
        CancellationToken cancellationToken)
    {
        var name = (request.BusinessName ?? "").Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            throw new CentralRejectedException("يرجى إدخال اسم المنشأة.");
        }

        var display = string.IsNullOrWhiteSpace(request.BusinessDisplayName) ? null : request.BusinessDisplayName.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (display is { Length: > 200 } || description is { Length: > 500 })
        {
            throw new CentralRejectedException("هوية المنشأة أطول من المسموح.");
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var existing = new SqlCommand(
            "SELECT TOP (1) BusinessName FROM dbo.Businesses ORDER BY CreatedAt;",
            connection);
        if (await existing.ExecuteScalarAsync(cancellationToken) is string current && current.Trim().Length > 0)
        {
            return current;
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, BusinessDisplayName, Description, Address, IsActive, CreatedAt)
            VALUES (NEWID(), @name, @display, @description, NULL, 1, SYSUTCDATETIME());
            """,
            connection);
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@display", (object?)display ?? DBNull.Value);
        insert.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return name;
    }

    private async Task VerifyCreatedAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var inspection = await InspectAsync(settings, cancellationToken);
        if (!string.Equals(inspection.Role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            throw new CentralRejectedException("لم يتم إثبات أن دور القاعدة هو Central.");
        }

        if (!inspection.SchemaCompatible || inspection.BusinessCount != 1)
        {
            throw new CentralRejectedException(IncompleteMessage);
        }
    }

    private async Task<string?> IsCompleteCentralAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await InspectAsync(settings, cancellationToken);
            return string.Equals(inspection.Role, DatabaseRole.Central, StringComparison.Ordinal)
                && inspection.SchemaCompatible
                && inspection.BusinessCount == 1
                ? inspection.BusinessName
                : null;
        }
        catch (CentralRejectedException)
        {
            return null;
        }
    }

    private async Task<CentralInspection> InspectAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await connection.OpenAsync(cancellationToken);
            var role = await ReadRoleAsync(connection, cancellationToken);
            var compatible = await ReadCentralCompatibilityAsync(connection, cancellationToken);
            var businessName = await ReadBusinessNameAsync(connection, cancellationToken);
            var businessCount = await ReadBusinessCountAsync(connection, cancellationToken);
            return new CentralInspection(role, compatible, businessName, businessCount);
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

    private async Task<bool> TryDropCreatedDatabaseAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var name = settings.DatabaseName;
        if (!CentralDatabase.IsAllowedName(name) || string.Equals(name, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            await using var master = new SqlConnection(settings.BuildConnectionString("master"));
            await master.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                $"""
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """,
                master);
            command.Parameters.AddWithValue("@name", name);
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(cancellationToken);
            Log(Guid.Empty, settings, "cleanup", "DroppedNewDatabase");
            return true;
        }
        catch (Exception)
        {
            Log(Guid.Empty, settings, "cleanup", "DropFailed");
            return false;
        }
    }

    private async Task Progress(
        Func<CentralSetupEvent, CancellationToken, Task> emit,
        Guid operationId,
        SqlEndpointSettings settings,
        string stage,
        string state,
        string label,
        CancellationToken cancellationToken)
    {
        Log(operationId, settings, stage, state);
        await emit(new CentralSetupEvent("progress", stage, state, label), cancellationToken);
    }

    private void Log(Guid operationId, SqlEndpointSettings settings, string stage, string result)
    {
        logger.LogInformation(
            "Central setup {OperationId} server {Server} database {Database} stage {Stage} result {Result}",
            operationId,
            settings.Server,
            settings.DatabaseName,
            stage,
            result);
    }

    private static async Task<string?> ReadRoleAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "SystemSettings", cancellationToken))
        {
            return null;
        }

        return await ScalarAsync(
            connection,
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            cancellationToken) as string;
    }

    private static async Task<bool> ReadCentralCompatibilityAsync(SqlConnection connection, CancellationToken cancellationToken)
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

    private static async Task<string?> ReadBusinessNameAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "Businesses", cancellationToken))
        {
            return null;
        }

        return await ScalarAsync(
            connection,
            "SELECT TOP (1) BusinessName FROM dbo.Businesses ORDER BY CreatedAt;",
            cancellationToken) as string;
    }

    private static async Task<int> ReadBusinessCountAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "Businesses", cancellationToken))
        {
            return 0;
        }

        var value = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Businesses;", cancellationToken);
        return value is int count ? count : 0;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(
            connection,
            "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;",
            cancellationToken,
            ("name", "dbo." + tableName));
        return value is int flag && flag == 1;
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnection master, string databaseName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT DB_ID(@name);", master);
        command.Parameters.AddWithValue("@name", databaseName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull;
    }

    private static async Task<bool> CanCreateDatabaseAsync(SqlConnection master, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE
                WHEN IS_SRVROLEMEMBER(N'dbcreator') = 1 OR IS_SRVROLEMEMBER(N'sysadmin') = 1 THEN 1
                ELSE 0
            END;
            """,
            master);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("@" + name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    private static string RoleMessage(string? role) => role switch
    {
        DatabaseRole.Branch => "هذه قاعدة فرع ولا يمكن استخدامها كقاعدة مركزية.",
        DatabaseRole.BranchRemote => "هذه نسخة بعيدة للفرع ولا يمكن استخدامها كقاعدة مركزية.",
        _ => "قاعدة البيانات غير متوافقة مع القاعدة المركزية.",
    };

    private static string UserMessage(Exception exception)
    {
        if (exception is CentralRejectedException rejected)
        {
            return rejected.Message;
        }

        return exception is SqlException
            ? SqlEndpointSettings.DescribeConnectionFailure(exception)
            : IncompleteMessage;
    }

    private static string Scrub(string message, string? password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(message))
        {
            return message;
        }

        return message.Replace(password, "", StringComparison.Ordinal);
    }

    private sealed record CentralInspection(string? Role, bool SchemaCompatible, string? BusinessName, int BusinessCount);
}
