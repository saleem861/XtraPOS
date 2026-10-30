using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CentralSetupExecutionTests(BranchApiFixture fixture)
{
    private const string Password = "LeakMe-Central-9";
    private const string LocalName = "XtraPOS_Central_Test_SetupLocal";
    private const string RemoteName = "XtraPOS_Central_Test_SetupRemote";
    private const string BranchRoleName = "XtraPOS_Central_Test_SetupBranchRole";
    private const string RemoteRoleName = "XtraPOS_Central_Test_SetupRemoteRole";
    private const string PreexistingName = "XtraPOS_Central_Test_SetupKeep";
    private const string FailedName = "XtraPOS_Central_Test_SetupFailed";

    [Fact]
    public async Task LocalCentral_IsCreatedOnce_AndASecondRunDoesNotDuplicateTheBusiness()
    {
        await DropAsync(LocalName);
        try
        {
            using var client = await AdminClientAsync();
            var first = await client.PostAsJsonAsync("/api/central/setup", Request(LocalName, "new", "Local", "منشأة الاختبار"));
            var firstBody = await first.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Contains("\"type\":\"completed\"", firstBody);
            Assert.Contains("تم التحقق من القاعدة المركزية", firstBody);
            Assert.DoesNotContain(Password, firstBody, StringComparison.Ordinal);
            Assert.DoesNotContain("connectionString", firstBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password=", firstBody, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Central", await TextAsync(LocalName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal(3, await IntAsync(LocalName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID IN (N'001_CentralFoundation', N'002_ItemOwnership', N'003_BranchCodeUniqueness');"));
            Assert.Equal(1, await IntAsync(LocalName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal("منشأة الاختبار", await TextAsync(LocalName, "SELECT BusinessName FROM dbo.Businesses;"));
            Assert.Equal(0, await IntAsync(LocalName, "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'ItemBranchInventory', N'BranchStockBalances', N'StockMovements', N'POSStations', N'Devices', N'Sales');"));
            Assert.Equal(1, await IntAsync(LocalName, "SELECT CASE WHEN COL_LENGTH(N'dbo.Customers', N'CustomerID') IS NULL THEN 0 ELSE 1 END;"));
            Assert.Equal(0, await IntAsync(LocalName, "SELECT COUNT(*) FROM dbo.Customers;"));
            Assert.Equal(0, await IntAsync(LocalName, "SELECT COUNT(*) FROM dbo.Items;"));

            var second = await client.PostAsJsonAsync("/api/central/setup", Request(LocalName, "new", "Local", "منشأة أخرى"));
            var secondBody = await second.Content.ReadAsStringAsync();
            Assert.Contains("\"alreadyConfigured\":true", secondBody);
            Assert.Equal(1, await IntAsync(LocalName, "SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal("منشأة الاختبار", await TextAsync(LocalName, "SELECT BusinessName FROM dbo.Businesses;"));

            var stored = await File.ReadAllTextAsync(await SecretPathAsync());
            Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", stored, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropAsync(LocalName);
            await DeleteSecretAsync();
        }
    }

    [Fact]
    public async Task RemoteCentral_UsesTheRequestedServer_AndExistingCentralIsAcceptedWithoutAnotherBusiness()
    {
        await DropAsync(RemoteName);
        try
        {
            var unreachable = await RunAsync(new CentralSetupRequest
            {
                Intent = "new",
                LocationKind = "Remote",
                Server = "127.0.0.1",
                DatabaseName = RemoteName,
                AuthenticationType = "Integrated",
                Encrypt = true,
                TrustServerCertificate = true,
                BusinessName = "منشأة بعيدة",
                Password = Password,
            });
            Assert.Contains(unreachable, item => item.Type == "failed" && item.Message == "تعذر الوصول إلى خادم البيانات.");
            Assert.DoesNotContain(unreachable, item => (item.Message ?? "").Contains(Password, StringComparison.Ordinal));
            Assert.False(await ExistsAsync(RemoteName));

            var events = await RunAsync(new CentralSetupRequest
            {
                Intent = "new",
                LocationKind = "Remote",
                Server = "localhost",
                DatabaseName = RemoteName,
                AuthenticationType = "Integrated",
                Encrypt = true,
                TrustServerCertificate = true,
                BusinessName = "منشأة بعيدة",
                BusinessDisplayName = "بعيد",
                Description = "وصف تجريبي",
                Password = Password,
            });
            Assert.Contains(events, item => item.Type == "completed" && item.AlreadyConfigured == false);
            Assert.DoesNotContain(events, item => (item.Message ?? "").Contains(Password, StringComparison.Ordinal));
            Assert.Equal("Central", await TextAsync(RemoteName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal("وصف تجريبي", await TextAsync(RemoteName, "SELECT Description FROM dbo.Businesses;"));
            Assert.Equal("", await TextAsync(RemoteName, "SELECT ISNULL(Address, N'') FROM dbo.Businesses;"));
            Assert.Equal(1, await IntAsync(RemoteName, "SELECT COUNT(*) FROM dbo.Businesses;"));

            var again = await RunAsync(new CentralSetupRequest
            {
                Intent = "existing",
                LocationKind = "Remote",
                Server = "localhost",
                DatabaseName = RemoteName,
                AuthenticationType = "Integrated",
                Encrypt = true,
                TrustServerCertificate = true,
            });
            Assert.Contains(again, item => item.Type == "completed" && item.BusinessName == "منشأة بعيدة");
            Assert.Equal(1, await IntAsync(RemoteName, "SELECT COUNT(*) FROM dbo.Businesses;"));
        }
        finally
        {
            await DropAsync(RemoteName);
            await DeleteSecretAsync();
        }
    }

    [Fact]
    public async Task BranchAndBranchRemoteDatabases_AreRejectedAndLeftUntouched()
    {
        await DropAsync(BranchRoleName);
        await DropAsync(RemoteRoleName);
        try
        {
            await CreateRoleDatabaseAsync(BranchRoleName, "Branch");
            await CreateRoleDatabaseAsync(RemoteRoleName, "BranchRemote");
            var branch = await RunAsync(Existing(BranchRoleName));
            var remote = await RunAsync(Existing(RemoteRoleName));
            Assert.Contains(branch, item => item.Type == "failed" && item.Message == "هذه قاعدة فرع ولا يمكن استخدامها كقاعدة مركزية.");
            Assert.Contains(remote, item => item.Type == "failed" && item.Message == "هذه نسخة بعيدة للفرع ولا يمكن استخدامها كقاعدة مركزية.");
            Assert.Equal("Branch", await TextAsync(BranchRoleName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal("BranchRemote", await TextAsync(RemoteRoleName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';"));
            Assert.Equal(0, await IntAsync(BranchRoleName, "SELECT COUNT(*) FROM sys.tables WHERE name = N'DatabaseSchemaHistory';"));
            Assert.Equal(0, await IntAsync(RemoteRoleName, "SELECT COUNT(*) FROM sys.tables WHERE name = N'DatabaseSchemaHistory';"));
        }
        finally
        {
            await DropAsync(BranchRoleName);
            await DropAsync(RemoteRoleName);
        }
    }

    [Fact]
    public async Task PreexistingDatabase_IsNotDropped_AndMigrationFailure_DoesNotMarkSetupComplete()
    {
        await DropAsync(PreexistingName);
        await DropAsync(FailedName);
        try
        {
            await ExecuteMasterAsync($"CREATE DATABASE [{CentralDatabase.Require(PreexistingName)}] COLLATE Arabic_CI_AS;");
            var refused = await RunAsync(new CentralSetupRequest
            {
                Intent = "new",
                LocationKind = "Local",
                DatabaseName = PreexistingName,
                BusinessName = "لا تُحذف",
            });
            Assert.Contains(refused, item => item.Type == "failed" && item.Message == "قاعدة البيانات موجودة مسبقاً ولم يتم حذفها.");
            Assert.True(await ExistsAsync(PreexistingName));
            Assert.Equal(0, await IntAsync(PreexistingName, "SELECT COUNT(*) FROM sys.tables WHERE name = N'SystemSettings';"));

            var executor = await ExecutorAsync();
            executor.ApplyOverride = (_, _) => throw new InvalidOperationException("migration failed");
            var failed = new List<CentralSetupEvent>();
            await executor.ExecuteAsync(new CentralSetupRequest
            {
                Intent = "new",
                LocationKind = "Local",
                DatabaseName = FailedName,
                BusinessName = "فشل",
                Password = Password,
            }, (item, _) =>
            {
                failed.Add(item);
                return Task.CompletedTask;
            }, CancellationToken.None);
            var failure = Assert.Single(failed, item => item.Type == "failed");
            Assert.Contains(CentralSetupExecutor.IncompleteMessage, failure.Message);
            Assert.Contains("تمت إزالة القاعدة التي أُنشئت في هذه العملية.", failure.Message);
            Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(failed, item => item.Type == "completed");
            Assert.False(await ExistsAsync(FailedName));
        }
        finally
        {
            await DropAsync(PreexistingName);
            await DropAsync(FailedName);
            await DeleteSecretAsync();
        }
    }

    [Fact]
    public async Task ProtectedPassword_RoundTripsWithoutPlaintext_AndAnonymousSetupIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtrapos-central-secret-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            var store = new CentralConnectionFileStore(protection, directory);
            await store.SaveAsync(new SqlEndpointSettings(
                DatabaseRole.Central,
                SqlEndpointSettings.Remote,
                "10.0.0.8",
                null,
                1433,
                "XtraPOS_Central_Test_Secret",
                SqlEndpointSettings.SqlLogin,
                "central-user",
                Password,
                true,
                true), CancellationToken.None);
            var stored = await File.ReadAllTextAsync(store.FilePath);
            Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
            var read = await store.ReadAsync(CancellationToken.None);
            Assert.Equal(Password, read!.Password);

            using var anonymous = fixture.CreateClient();
            var response = await anonymous.PostAsJsonAsync("/api/central/setup", Request("XtraPOS_Central_Test_NoAuth", "new", "Local", "لا"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(await ExistsAsync("XtraPOS_Central_Test_NoAuth"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private async Task<List<CentralSetupEvent>> RunAsync(CentralSetupRequest request)
    {
        var executor = await ExecutorAsync();
        var events = new List<CentralSetupEvent>();
        await executor.ExecuteAsync(request, (item, _) =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return events;
    }

    private Task<CentralSetupExecutor> ExecutorAsync()
    {
        var scope = fixture.Factory.Services.CreateAsyncScope();
        return Task.FromResult(scope.ServiceProvider.GetRequiredService<CentralSetupExecutor>());
    }

    private static CentralSetupRequest Existing(string name) => new()
    {
        Intent = "existing",
        LocationKind = "Local",
        DatabaseName = name,
    };

    private static object Request(string name, string intent, string location, string businessName) => new
    {
        intent,
        locationKind = location,
        databaseName = name,
        businessName,
        password = Password,
        encrypt = true,
        trustServerCertificate = true,
    };

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "Admin1234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<string> SecretPathAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        return Path.Combine(environment.ContentRootPath, "App_Data", "central-connection.json");
    }

    private async Task DeleteSecretAsync()
    {
        var path = await SecretPathAsync();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task CreateRoleDatabaseAsync(string name, string role)
    {
        var safe = CentralDatabase.Require(name);
        await ExecuteMasterAsync($"IF DB_ID(N'{safe}') IS NULL CREATE DATABASE [{safe}] COLLATE Arabic_CI_AS;");
        await using var connection = new SqlConnection(Connection(safe));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            CREATE TABLE dbo.SystemSettings
            (
                SettingKey nvarchar(100) NOT NULL PRIMARY KEY,
                SettingValue nvarchar(max) NULL,
                UpdatedAt datetime2 NOT NULL CONSTRAINT DF_{safe}_Updated DEFAULT (SYSUTCDATETIME())
            );
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'DatabaseRole', N'{role}');
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = Master();
        await using var command = new SqlCommand("SELECT DB_ID(@name);", connection);
        command.Parameters.AddWithValue("@name", CentralDatabase.Require(name));
        var value = await command.ExecuteScalarAsync();
        return value is not null and not DBNull;
    }

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

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = Master();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        var safe = CentralDatabase.Require(name);
        if (string.Equals(safe, CentralDatabase.Name, StringComparison.Ordinal)
            || string.Equals(safe, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to drop a live database.");
        }

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

    private static string Connection(string name) =>
        $"Server=localhost;Database={CentralDatabase.Require(name)};Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";
}
