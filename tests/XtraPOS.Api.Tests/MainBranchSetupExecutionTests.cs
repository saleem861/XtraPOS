using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class MainBranchSetupExecutionTests
{
    private const string CentralName = "XtraPOS_Central_Test_MainBranch";
    private const string BranchName = "XtraPOS_Branch_Test_MainSetup";
    private const string FailedBranchName = "XtraPOS_Branch_Test_MainFail";
    private const string FailedCentralName = "XtraPOS_Central_Test_MainFail";
    private const string StartupName = "XtraPOS_Branch_Test_StartupHold";
    private const string MigrateName = "XtraPOS_Branch_Test_MigrateCmd";

    [Fact]
    public async Task RealMainBranchSetup_LinksCentralIdentityOnce_AndSurvivesARepeat()
    {
        await AssertLiveFingerprintAsync();
        await DropCentralAsync(CentralName);
        await DropBranchAsync(BranchName);
        var before = await DatabaseNamesAsync();
        var factory = new XtraPosWebFactory(BranchName);
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var executor = scope.ServiceProvider.GetRequiredService<CentralSetupExecutor>();
                await executor.ExecuteAsync(
                    new CentralSetupRequest
                    {
                        Intent = "new",
                        LocationKind = "Local",
                        Server = "localhost",
                        DatabaseName = CentralName,
                        AuthenticationType = "Integrated",
                        Encrypt = true,
                        TrustServerCertificate = true,
                        BusinessName = "XtraPOS QA Business",
                        BusinessDisplayName = "QA",
                        Description = "Main Branch Setup Test",
                    },
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
            }

            Assert.Equal("Central", await TextAsync(CentralName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal(5, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal("Main Branch Setup Test", await TextAsync(CentralName, "SELECT Description FROM dbo.Businesses;"));
            Assert.Equal("", await TextAsync(CentralName, "SELECT ISNULL(Address, N'') FROM dbo.Businesses;"));
            Assert.Equal(0, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Items;"));
            Assert.Equal(0, await IntAsync(CentralName, "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'POSStations', N'Devices', N'ItemBranchInventory', N'BranchStockBalances', N'StockMovements');"));

            using var client = factory.CreateClient();
            var first = await client.PostAsJsonAsync("/api/setup/create-branch", BranchRequest());
            var firstBody = await first.Content.ReadAsStringAsync();
            Assert.True(first.IsSuccessStatusCode, firstBody);
            Assert.Contains("\"type\":\"completed\"", firstBody);
            Assert.DoesNotContain("\"alreadyConfigured\":true", firstBody);

            var centralBusinessId = await TextAsync(CentralName, "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Businesses;");
            Assert.Equal("Branch", await TextAsync(BranchName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal(15, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal("XtraPOS QA Business", await TextAsync(BranchName, "SELECT BusinessName FROM dbo.Businesses;"));
            Assert.Equal("QA", await TextAsync(BranchName, "SELECT BusinessDisplayName FROM dbo.Businesses;"));
            Assert.Equal("Main Branch Setup Test", await TextAsync(BranchName, "SELECT Description FROM dbo.Businesses;"));
            Assert.Equal("", await TextAsync(BranchName, "SELECT ISNULL(Address, N'') FROM dbo.Businesses;"));
            Assert.Equal(centralBusinessId, await TextAsync(BranchName, "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Businesses;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Businesses WHERE BusinessName = N'Manual Identity Must Not Stick';"));

            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal("فرع الاختبار", await TextAsync(CentralName, "SELECT BranchName FROM dbo.Branches;"));
            Assert.Equal("01", await TextAsync(CentralName, "SELECT BranchCode FROM dbo.Branches;"));
            Assert.Equal(centralBusinessId, await TextAsync(CentralName, "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Branches;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal("فرع الاختبار", await TextAsync(BranchName, "SELECT BranchName FROM dbo.Branches;"));
            Assert.Equal(
                await TextAsync(CentralName, "SELECT CONVERT(nvarchar(36), BranchID) FROM dbo.Branches;"),
                await TextAsync(BranchName, "SELECT CONVERT(nvarchar(36), BranchID) FROM dbo.Branches;"));

            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.POSStations;"));
            Assert.Equal("كاونتر 1", await TextAsync(BranchName, "SELECT StationName FROM dbo.POSStations;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal("QA-MAIN-DEVICE", await TextAsync(BranchName, "SELECT DeviceName FROM dbo.Devices;"));
            Assert.Equal(4, await IntAsync(BranchName, "SELECT COUNT(*) FROM (SELECT BusinessID AS Id FROM dbo.Businesses UNION SELECT BranchID FROM dbo.Branches UNION SELECT StationID FROM dbo.POSStations UNION SELECT DeviceID FROM dbo.Devices) AS ids;"));

            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Items;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.ItemBranchInventory;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.StockMovements;"));
            Assert.Equal(0, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central' AND DatabaseName = N'XtraPOS_Central_Test_MainBranch';"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.SyncEndpoints WHERE DatabaseRole = N'BranchRemote';"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.SyncCheckpoints WHERE StreamName = N'CentralMaster' AND LastAppliedVersion = 0;"));
            Assert.Equal(0, await IntAsync(MasterCatalog(), "SELECT COUNT(*) FROM sys.databases WHERE name LIKE N'XtraPOS_BranchRemote%';"));

            var second = await client.PostAsJsonAsync("/api/setup/create-branch", BranchRequest());
            var secondBody = await second.Content.ReadAsStringAsync();
            Assert.True(second.IsSuccessStatusCode, secondBody);
            Assert.Contains("\"alreadyConfigured\":true", secondBody);
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.POSStations;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.SyncEndpoints;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.SyncCheckpoints;"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Users;"));

            var after = await DatabaseNamesAsync();
            var added = after.Except(before).OrderBy(name => name).ToArray();
            Assert.Equal(new[] { BranchName, CentralName }, added);
            await AssertLiveFingerprintAsync();
        }
        finally
        {
            DeleteSecret(factory);
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropCentralAsync(CentralName);
            await DropBranchAsync(BranchName);
            Assert.False(await ExistsAsync(CentralName));
            Assert.False(await ExistsAsync(BranchName));
            await AssertLiveFingerprintAsync();
        }
    }

    [Fact]
    public async Task FailedSetup_DoesNotMarkComplete_AndDoesNotDropExistingDatabases()
    {
        await AssertLiveFingerprintAsync();
        await DropBranchAsync(FailedBranchName);
        await DropCentralAsync(FailedCentralName);
        var before = await DatabaseNamesAsync();
        var factory = new XtraPosWebFactory(FailedBranchName);
        SetupService.FailAfterTables = _ => throw new InvalidOperationException("controlled setup failure");
        try
        {
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = "XtraPOS QA Business",
                branchName = "فرع الفشل",
                branchCode = "91",
                branchType = "Retail",
                stationNumber = 1,
                stationName = "كاونتر 1",
                deviceName = "QA-FAIL-DEVICE",
                username = "qa-fail",
                displayName = "مدير الفشل",
                password = "Admin1234",
                confirmPassword = "Admin1234",
                operatingMode = "CentralManagement",
                centralAction = "create",
                centralLocation = "Local",
                centralServer = "localhost",
                centralDatabaseName = FailedCentralName,
                linkToCentral = "yes",
                branchRemoteChoice = "no",
            });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            Assert.Contains("\"type\":\"failed\"", body);
            Assert.DoesNotContain("\"type\":\"completed\"", body);
            Assert.Equal(0, await IntAsync(FailedBranchName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(0, await IntAsync(FailedBranchName, "SELECT COUNT(*) FROM dbo.SystemSettings WHERE SettingKey = N'SetupCompletedAt';"));
            Assert.False(await ExistsAsync(FailedCentralName));
            var after = await DatabaseNamesAsync();
            Assert.Contains(FailedBranchName, after);
            Assert.All(before, name => Assert.Contains(name, after));
            await AssertLiveFingerprintAsync();
        }
        finally
        {
            SetupService.FailAfterTables = null;
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropBranchAsync(FailedBranchName);
            await DropCentralAsync(FailedCentralName);
            Assert.False(await ExistsAsync(FailedBranchName));
            Assert.False(await ExistsAsync(FailedCentralName));
        }
    }

    [Fact]
    public async Task StartupReportsPendingMigrations_AndExplicitCommandRequiresTheExactDatabase()
    {
        await AssertLiveFingerprintAsync();
        await DropBranchAsync(StartupName);
        await DropBranchAsync(MigrateName);
        await ExecuteMasterAsync($"CREATE DATABASE [{TestDatabase.Require(StartupName)}] COLLATE Arabic_CI_AS;");
        await ExecuteMasterAsync($"CREATE DATABASE [{TestDatabase.Require(MigrateName)}] COLLATE Arabic_CI_AS;");
        var startup = new XtraPosWebFactory(StartupName);
        var migrate = new XtraPosWebFactory(MigrateName);
        try
        {
            using var started = startup.CreateClient();
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.Equal(0, await IntAsync(StartupName, "SELECT CASE WHEN OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U') IS NULL THEN 0 ELSE 1 END;"));

            Assert.False(BranchMigrationCommand.TryAuthorize(MigrateName, BranchDatabase.Name, out var deniedLive));
            Assert.False(string.IsNullOrWhiteSpace(deniedLive));
            Assert.False(BranchMigrationCommand.TryAuthorize(MigrateName, MigrateName + " ", out _));
            Assert.False(BranchMigrationCommand.TryAuthorize(MigrateName, "XtraPOS_Branch_Test_*", out _));
            Assert.False(BranchMigrationCommand.TryAuthorize(BranchDatabase.Name, MigrateName, out _));
            Assert.True(BranchMigrationCommand.TryAuthorize(MigrateName, MigrateName, out var allowed));
            Assert.Equal("", allowed);

            await using var scope = migrate.Services.CreateAsyncScope();
            var connections = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
            var migrations = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await BranchMigrationCommand.ExecuteAsync(connections, migrations, MigrateName, TextWriter.Null, CancellationToken.None);
            Assert.Equal(15, await IntAsync(MigrateName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.Equal("Branch", await TextAsync(MigrateName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            await BranchMigrationCommand.ExecuteAsync(connections, migrations, MigrateName, TextWriter.Null, CancellationToken.None);
            Assert.Equal(15, await IntAsync(MigrateName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.True(await ExistsAsync(MigrateName));
            Assert.Equal(0, await IntAsync(StartupName, "SELECT CASE WHEN OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U') IS NULL THEN 0 ELSE 1 END;"));
            await AssertLiveFingerprintAsync();
        }
        finally
        {
            await startup.DisposeAsync();
            await migrate.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropBranchAsync(StartupName);
            await DropBranchAsync(MigrateName);
        }
    }

    private static object BranchRequest() => new
    {
        businessName = "Manual Identity Must Not Stick",
        businessDisplayName = "NO",
        address = "This is not the description",
        branchName = "فرع الاختبار",
        branchCode = "01",
        branchType = "Retail",
        stationNumber = 1,
        stationName = "كاونتر 1",
        deviceName = "QA-MAIN-DEVICE",
        username = "qa-main",
        displayName = "مدير الاختبار",
        password = "Admin1234",
        confirmPassword = "Admin1234",
        operatingMode = "CentralManagement",
        centralAction = "existing",
        centralLocation = "Local",
        centralServer = "localhost",
        centralDatabaseName = CentralName,
        linkToCentral = "yes",
        branchRemoteChoice = "no",
    };

    private static async Task AssertLiveFingerprintAsync()
    {
        var migrations = await TextAsync(BranchDatabase.Name, "SELECT STRING_AGG(MigrationID, N',') WITHIN GROUP (ORDER BY MigrationID) FROM dbo.DatabaseSchemaHistory;");
        Assert.Equal("001_InitialSchema,002_UsersRolesPermissions", migrations);
        Assert.Equal(1, await IntAsync(BranchDatabase.Name, "SELECT COUNT(*) FROM dbo.Businesses;"));
        Assert.Equal(1, await IntAsync(BranchDatabase.Name, "SELECT COUNT(*) FROM dbo.Branches;"));
        Assert.Equal(1, await IntAsync(BranchDatabase.Name, "SELECT COUNT(*) FROM dbo.Users;"));
        Assert.Equal(0, await IntAsync(BranchDatabase.Name, "SELECT CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;"));
        Assert.False(await ExistsAsync(CentralDatabase.Name));
    }

    private static async Task<string[]> DatabaseNamesAsync()
    {
        var names = new List<string>();
        await using var connection = Master();
        await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE N'XtraPOS%' ORDER BY name;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static void DeleteSecret(XtraPosWebFactory factory)
    {
        var environment = factory.Services.GetRequiredService<IHostEnvironment>();
        var path = Path.Combine(environment.ContentRootPath, "App_Data", "central-connection.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string MasterCatalog() => "master";

    private static async Task<int> IntAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> TextAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = Master();
        await using var command = new SqlCommand("SELECT DB_ID(@name);", connection);
        command.Parameters.AddWithValue("@name", name);
        var value = await command.ExecuteScalarAsync();
        return value is not null and not DBNull;
    }

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = Master();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropBranchAsync(string name)
    {
        var safe = TestDatabase.Require(name);
        await DropNamedAsync(safe);
    }

    private static async Task DropCentralAsync(string name)
    {
        var safe = CentralDatabase.Require(name);
        if (string.Equals(safe, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(safe, CentralDatabase.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a live database.");
        }

        await DropNamedAsync(safe);
    }

    private static async Task DropNamedAsync(string safe)
    {
        SqlConnection.ClearAllPools();
        await using var connection = Master();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{safe}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{safe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{safe}];
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static SqlConnection Master()
    {
        var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        connection.Open();
        return connection;
    }

    private static string Connection(string name)
    {
        if (string.Equals(name, BranchDatabase.Name, StringComparison.Ordinal))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = BranchDatabase.Name,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                ApplicationName = "XtraPOS.Tests.Fingerprint",
            }.ConnectionString;
        }

        if (string.Equals(name, "master", StringComparison.Ordinal))
        {
            return "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";
        }

        var branch = name.StartsWith(BranchDatabase.TestNamePrefix, StringComparison.Ordinal);
        var safe = branch ? TestDatabase.Require(name) : CentralDatabase.Require(name);
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = safe,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationName = "XtraPOS.Tests",
        }.ConnectionString;
    }
}
