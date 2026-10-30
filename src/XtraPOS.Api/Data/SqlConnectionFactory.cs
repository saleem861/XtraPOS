using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;

namespace XtraPOS.Api.Data;

public sealed class SqlConnectionFactory
{
    private readonly SqlServerOptions _options;
    private readonly string _databaseName;

    public SqlConnectionFactory(IOptions<SqlServerOptions> options)
    {
        _options = options.Value;
        _databaseName = _options.Database?.Trim() ?? "";
    }

    public bool IsSafeConfiguration(out string? problem)
    {
        if (!BranchDatabase.IsAllowedName(_databaseName))
        {
            problem = "اسم قاعدة بيانات الفرع غير مسموح.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.Server))
        {
            problem = "لم يتم إعداد الاتصال بخادم البيانات.";
            return false;
        }

        var hasUser = !string.IsNullOrWhiteSpace(_options.User);
        var hasPassword = !string.IsNullOrWhiteSpace(_options.Password);
        if (hasUser != hasPassword)
        {
            problem = "إعداد الاتصال بخادم البيانات غير مكتمل.";
            return false;
        }

        if (_options.ConnectTimeoutSeconds is < 1 or > 120)
        {
            problem = "إعداد الاتصال بخادم البيانات غير مكتمل.";
            return false;
        }

        problem = null;
        return true;
    }

    public string? TryCreateBranchConnectionString()
    {
        if (!IsSafeConfiguration(out _))
        {
            return null;
        }

        return CreateConnectionString(EffectiveDatabase);
    }

    public string CreateMasterConnectionString()
    {
        return CreateConnectionString("master");
    }

    public string EffectiveDatabase =>
        BranchDatabaseOverride.Database is string selected && CanOpen(selected) ? selected : _databaseName;

    public string CreateBranchConnectionString()
    {
        return CreateConnectionString(EffectiveDatabase);
    }

    public string CreateNamedBranchConnectionString(string databaseName)
    {
        if (!CanOpen(databaseName))
        {
            throw new InvalidOperationException("Refusing to open a database other than an allowed XtraPOS branch database.");
        }

        return CreateConnectionString(databaseName);
    }

    private bool CanOpen(string catalog)
    {
        return string.Equals(catalog, _databaseName, StringComparison.Ordinal) || BranchDatabase.IsTestName(catalog);
    }

    public string ConfiguredServer => _options.Server.Trim();

    public string ConfiguredDatabase => _databaseName;

    public bool UsesIntegratedSecurity => string.IsNullOrWhiteSpace(_options.User);

    public string CreateStatusConnectionString(string catalog)
    {
        var builder = new SqlConnectionStringBuilder(CreateConnectionString(catalog))
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "XtraPOS.DevelopmentDatabaseStatus",
        };
        return builder.ConnectionString;
    }

    public async Task<DatabaseState?> GetDatabaseStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CreateMasterConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT collation_name, state_desc
            FROM sys.databases
            WHERE name = @name;
            """,
            connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = _databaseName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DatabaseState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1));
    }

    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var state = await GetDatabaseStateAsync(cancellationToken);
        return state is not null;
    }

    public async Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken)
    {
        var state = await GetDatabaseStateAsync(cancellationToken);
        if (state is null || !string.Equals(state.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await using var connection = new SqlConnection(CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.SystemSettings', N'U') IS NULL
                SELECT CAST(0 AS int);
            ELSE
                SELECT COUNT(*) FROM dbo.SystemSettings WHERE SettingKey = N'SetupCompletedAt';
            """,
            connection);
        var count = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count > 0;
    }

    public async Task<InstallationPhase> GetInstallationPhaseAsync(CancellationToken cancellationToken)
    {
        var state = await GetDatabaseStateAsync(cancellationToken);
        if (state is null || !string.Equals(state.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            return InstallationPhase.NotConfigured;
        }

        await using var connection = new SqlConnection(CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.Businesses', N'U') IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.Businesses)
                SELECT CAST(0 AS int);
            ELSE IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
                OR OBJECT_ID(N'dbo.Roles', N'U') IS NULL
                OR OBJECT_ID(N'dbo.UserRoles', N'U') IS NULL
                OR OBJECT_ID(N'dbo.UserBranchAccess', N'U') IS NULL
                SELECT CAST(1 AS int);
            ELSE IF EXISTS (
                SELECT 1
                FROM dbo.Users AS u
                INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
                INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
                INNER JOIN dbo.UserBranchAccess AS a ON a.UserID = u.UserID AND a.IsActive = 1
                INNER JOIN dbo.Branches AS b ON b.BranchID = a.BranchID)
                SELECT CAST(2 AS int);
            ELSE
                SELECT CAST(1 AS int);
            """,
            connection);
        var phase = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return phase switch
        {
            2 => InstallationPhase.Configured,
            1 => InstallationPhase.AdminMissing,
            _ => InstallationPhase.NotConfigured,
        };
    }

    public async Task EnsureBranchDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(CreateMasterConnectionString());
        await connection.OpenAsync(cancellationToken);

        var state = await ReadStateAsync(connection, cancellationToken);
        if (state is null)
        {
            await using var create = new SqlCommand(
                $"CREATE DATABASE [{RequireBranchName()}] COLLATE {BranchDatabase.Collation};",
                connection);
            create.CommandTimeout = 60;
            await create.ExecuteNonQueryAsync(cancellationToken);
            SqlConnection.ClearAllPools();
            state = await WaitUntilOnlineAsync(connection, cancellationToken);
        }

        if (!string.Equals(state.Collation, BranchDatabase.Collation, StringComparison.Ordinal))
        {
            throw new SetupRejectedException("قاعدة البيانات الحالية غير متوافقة مع إعداد إكسترا.");
        }

        if (!string.Equals(state.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupRejectedException("قاعدة البيانات غير جاهزة. أعد المحاولة.");
        }
    }

    public async Task DropBranchDatabaseAsync(
        DevelopmentResetAuthorization authorization,
        bool isDevelopment,
        CancellationToken cancellationToken)
    {
        if (!DevelopmentDatabaseReset.TryAuthorize(isDevelopment, _databaseName, authorization.ConfirmedDatabaseName, out var denial))
        {
            throw new InvalidOperationException(denial);
        }

        var databaseName = RequireBranchName();
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(CreateMasterConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
        SqlConnection.ClearAllPools();
    }

    private async Task<DatabaseState> WaitUntilOnlineAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var state = await ReadStateAsync(connection, cancellationToken);
            if (state is not null && string.Equals(state.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new SetupRejectedException("قاعدة البيانات غير جاهزة. أعد المحاولة.");
    }

    private async Task<DatabaseState?> ReadStateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT collation_name, state_desc
            FROM sys.databases
            WHERE name = @name;
            """,
            connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = _databaseName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DatabaseState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1));
    }

    private string RequireBranchName()
    {
        if (!BranchDatabase.IsAllowedName(_databaseName))
        {
            throw new InvalidOperationException("Refusing to create or drop a database other than the configured XtraPOS branch database.");
        }

        return _databaseName;
    }

    private string CreateConnectionString(string catalog)
    {
        if (!IsSafeConfiguration(out _))
        {
            throw new InvalidOperationException("SQL Server configuration is not safe to use.");
        }

        if (catalog != "master" && !CanOpen(catalog))
        {
            throw new InvalidOperationException("Refusing to open a database other than an allowed XtraPOS branch database.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.Server.Trim(),
            InitialCatalog = catalog,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = _options.ConnectTimeoutSeconds,
            ApplicationName = "XtraPOS.Api",
            IntegratedSecurity = string.IsNullOrWhiteSpace(_options.User),
        };

        if (!builder.IntegratedSecurity)
        {
            builder.UserID = _options.User;
            builder.Password = _options.Password;
        }

        return builder.ConnectionString;
    }
}

public sealed record DatabaseState(string? Collation, string State);

public enum InstallationPhase
{
    NotConfigured = 0,
    AdminMissing = 1,
    Configured = 2,
}

public sealed class SetupRejectedException(string message) : Exception(message);
