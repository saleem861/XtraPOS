using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("MigrationGate")]
public sealed class BranchMigrationEmptyDatabaseTests
{
    private const string Name = "XtraPOS_Branch_Test_MigrationGate";

    [Fact]
    public void CommandRejectsRealAndCentralDatabases()
    {
        Assert.False(BranchMigrationCommand.TryAuthorize("saleemPOSDB", "saleemPOSDB", out _));
        Assert.False(BranchMigrationCommand.TryAuthorize("XtraPOS_Central", "XtraPOS_Central", out _));
        Assert.False(BranchMigrationCommand.TryAuthorize("XtraPOS_Branch_01", "XtraPOS_Branch_Test_Cloud", out _));
        Assert.False(BranchMigrationCommand.TryAuthorize("XtraPOS_Branch_Test_Cloud", "XtraPOS_Branch_01", out _));
        Assert.False(BranchMigrationCommand.TryAuthorize(Name, Name + "*", out _));
        Assert.True(BranchMigrationCommand.TryAuthorize(Name, Name, out var allowed));
        Assert.Equal("", allowed);
    }

    [Fact]
    public async Task EmptyDatabase_ReceivesEveryBranchMigration_WithoutCreatingABusiness()
    {
        await RecreateAsync();
        try
        {
            var before = new StringWriter();
            await UserAdministrationLiveCommand.PrintCountsAsync(ConnectionString(), "BranchBefore", true, before, CancellationToken.None);
            Assert.Contains("Items=0", before.ToString());
            Assert.Contains("Migrations=0", before.ToString());

            await ApplyAsync();

            Assert.Equal(32, await ScalarAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.Businesses;"));
            var after = new StringWriter();
            await UserAdministrationLiveCommand.PrintCountsAsync(ConnectionString(), "BranchAfter", true, after, CancellationToken.None);
            Assert.Contains("Migrations=32", after.ToString());

            await ApplyAsync();
            Assert.Equal(32, await ScalarAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.Businesses;"));
        }
        finally
        {
            await DropAsync();
        }
    }

    [Fact]
    public async Task PartialSchema_KeepsExistingRows_AndFinishesTheRemainingMigrations()
    {
        await RecreateAsync();
        try
        {
            var initialSql = await File.ReadAllTextAsync(MigrationPath("001_InitialSchema.sql"));
            await ExecuteAsync(initialSql);
            await ExecuteAsync(
                """
                INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion)
                VALUES (N'001_InitialSchema', N'Initial branch schema', N'test');
                INSERT INTO dbo.Businesses (BusinessName) VALUES (N'partial marker');
                """);

            var before = new StringWriter();
            await UserAdministrationLiveCommand.PrintCountsAsync(ConnectionString(), "BranchBefore", true, before, CancellationToken.None);
            Assert.Contains("Items=0", before.ToString());
            Assert.Contains("Migrations=1", before.ToString());

            await ApplyAsync();

            Assert.Equal(32, await ScalarAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM dbo.Businesses WHERE BusinessName = N'partial marker';"));
            Assert.Equal(1, await ScalarAsync("SELECT CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;"));
        }
        finally
        {
            await DropAsync();
        }
    }

    [Fact]
    public async Task FullyMigratedDatabase_KeepsHistoryAndInsertedRows()
    {
        await RecreateAsync();
        try
        {
            await ApplyAsync();
            await ExecuteAsync(
                """
                INSERT INTO dbo.Items (ItemNumber, ItemName, IsActive, IsProhibited)
                VALUES (N'MIGRATION-GATE', N'migration gate marker', 1, 0);
                """);
            var before = await ScalarAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;");

            var counts = new StringWriter();
            await UserAdministrationLiveCommand.PrintCountsAsync(ConnectionString(), "BranchBefore", true, counts, CancellationToken.None);
            Assert.Contains("Items=1", counts.ToString());
            Assert.Contains("Migrations=32", counts.ToString());

            await ApplyAsync();

            Assert.Equal(before, await ScalarAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'MIGRATION-GATE';"));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task ApplyAsync()
    {
        var options = Options.Create(new SqlServerOptions
        {
            Server = Server(),
            Database = TestDatabase.Require(Name),
            User = LoginUser(),
            Password = LoginPassword(),
            ConnectTimeoutSeconds = 30,
        });
        var connections = new SqlConnectionFactory(options);
        var migrations = new MigrationRunner(connections, NullLogger<MigrationRunner>.Instance);
        await BranchMigrationCommand.ExecuteAsync(connections, migrations, Name, TextWriter.Null, CancellationToken.None);
    }

    private static async Task RecreateAsync()
    {
        var name = TestDatabase.Require(Name);
        if (string.Equals(name, "XtraPOS_Branch_Test_Cloud", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to recreate the cloud development database.");
        }

        await DropAsync();
        await ExecuteMasterAsync($"CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;");
    }

    private static async Task DropAsync()
    {
        var name = TestDatabase.Require(Name);
        SqlConnection.ClearAllPools();
        await ExecuteMasterAsync(
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """);
        SqlConnection.ClearAllPools();
    }

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(MasterConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static string ConnectionString() => Login(Name).ConnectionString;

    private static string MasterConnectionString() => Login("master").ConnectionString;

    private static SqlConnectionStringBuilder Login(string catalog)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server(),
            InitialCatalog = catalog,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            ApplicationName = "XtraPOS.Tests",
        };
        var user = LoginUser();
        if (string.IsNullOrWhiteSpace(user))
        {
            builder.IntegratedSecurity = true;
            return builder;
        }

        builder.UserID = user;
        builder.Password = LoginPassword();
        return builder;
    }

    private static string Server() => Environment.GetEnvironmentVariable("SqlServer__Server")?.Trim() is { Length: > 0 } server ? server : "localhost";

    private static string? LoginUser()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")))
        {
            return "sa";
        }

        var user = Environment.GetEnvironmentVariable("SqlServer__User")?.Trim();
        return string.IsNullOrWhiteSpace(user) ? null : user;
    }

    private static string? LoginPassword()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")))
        {
            return Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        }

        return Environment.GetEnvironmentVariable("SqlServer__Password");
    }

    private static string MigrationPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XtraPOS.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not find the XtraPOS repository root.");
        }

        return Path.Combine(directory.FullName, "src", "XtraPOS.Api", "Data", "Migrations", fileName);
    }
}

[CollectionDefinition("MigrationGate", DisableParallelization = true)]
public sealed class MigrationGateCollection;
