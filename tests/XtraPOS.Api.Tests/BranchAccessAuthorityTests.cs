using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class BranchAccessAuthorityTests(BranchApiFixture fixture)
{
    private const string Password = "Admin1234";
    private const string CentralName = "XtraPOS_Central_Test_AccessAuth";
    private const string SetupCentralName = "XtraPOS_Central_Test_AccessSetup";
    private const string SetupBranchName = "XtraPOS_Branch_Test_AccessSetup";

    [Fact]
    public async Task LinkedAdminAccess_FollowsExplicitCentralState()
    {
        var hash = await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';");
        var role = await BranchTextAsync(
            """
            SELECT r.RoleCode
            FROM dbo.UserRoles AS ur
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            INNER JOIN dbo.Users AS u ON u.UserID = ur.UserID
            WHERE u.Username = N'admin';
            """);
        Assert.Equal("Admin", role);
        try
        {
            await PrepareCentralAsync();
            await SaveCentralEndpointAsync();
            var userId = await BranchGuidAsync("SELECT UserID FROM dbo.Users WHERE Username = N'admin';");
            var branchId = await BranchGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            await using var adminScope = fixture.Factory.Services.CreateAsyncScope();
            var users = adminScope.ServiceProvider.GetRequiredService<UserAdministrationService>();
            await BranchExecuteAsync("UPDATE dbo.UserBranchAccess SET IsActive = 0 WHERE UserID = '" + userId + "';");
            await users.SetRoleAsync(userId, userId, "Admin", CancellationToken.None);
            Assert.Equal(1, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            Assert.Equal(1, await CentralIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            Assert.Equal(hash, await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';"));
            Assert.Equal("Admin", await RoleAsync());
            using var restored = Client();
            await LoginAsync(restored, fixture.Username, Password);

            await CentralExecuteAsync($"UPDATE dbo.UserBranchAccess SET IsActive = 0 WHERE UserID = '{userId}' AND BranchID = '{branchId}';");
            await users.SetRoleAsync(userId, userId, "Admin", CancellationToken.None);
            Assert.Equal(0, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            using var revoked = Client();
            var denied = await revoked.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("NoBranchAccess", await BranchTextAsync("SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
            Assert.Equal(hash, await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';"));
            Assert.Equal("Admin", await RoleAsync());

            await CentralExecuteAsync($"UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = '{userId}' AND BranchID = '{branchId}';");
            await users.SetRoleAsync(userId, userId, "Admin", CancellationToken.None);
            Assert.Equal(1, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));

            await CentralExecuteAsync($"DELETE FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';");
            await users.SetRoleAsync(userId, userId, "Admin", CancellationToken.None);
            Assert.Equal(0, await CentralIntAsync($"SELECT COUNT(*) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            Assert.Equal(1, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            using var stillAllowed = Client();
            await LoginAsync(stillAllowed, fixture.Username, Password);
            Assert.Equal(hash, await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';"));
            Assert.Equal("Admin", await RoleAsync());

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
            var settings = CentralSettings(CentralName);
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal(1, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            Assert.Equal(hash, await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';"));
            Assert.Equal("Admin", await RoleAsync());

            await CentralExecuteAsync(
                $"INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt) VALUES ('{userId}', '{branchId}', 0, SYSUTCDATETIME());");
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal(0, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            using var revokedByBootstrap = Client();
            var bootstrapDenied = await revokedByBootstrap.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, bootstrapDenied.StatusCode);
            Assert.Equal("NoBranchAccess", await BranchTextAsync("SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));

            await CentralExecuteAsync($"UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = '{userId}' AND BranchID = '{branchId}';");
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal(1, await BranchIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            Assert.Equal(1, await CentralIntAsync($"SELECT CAST(IsActive AS int) FROM dbo.UserBranchAccess WHERE UserID = '{userId}' AND BranchID = '{branchId}';"));
            using var afterBootstrap = Client();
            await LoginAsync(afterBootstrap, fixture.Username, Password);
            Assert.Equal(hash, await BranchTextAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'admin';"));
            Assert.Equal("Admin", await RoleAsync());
            Assert.Equal(1, await BranchIntAsync("SELECT CAST(IsActive AS int) FROM dbo.Users WHERE Username = N'admin';"));
        }
        finally
        {
            await BranchExecuteAsync(
                """
                UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndAt = NULL WHERE Username = N'admin';
                UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID IN (SELECT UserID FROM dbo.Users WHERE Username = N'admin');
                DELETE FROM dbo.SyncCheckpoints;
                DELETE FROM dbo.SyncInbox;
                DELETE FROM dbo.SyncOutbox;
                DELETE FROM dbo.SyncEndpoints;
                """);
            await DropCentralAsync(CentralName);
        }
    }

    [Fact]
    public async Task NewLinkedBranch_GivesTheSetupAdminActiveCentralAccess()
    {
        await DropCentralAsync(SetupCentralName);
        await DropBranchAsync(SetupBranchName);
        var factory = new XtraPosWebFactory(SetupBranchName);
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
                        DatabaseName = SetupCentralName,
                        AuthenticationType = "Integrated",
                        Encrypt = true,
                        TrustServerCertificate = true,
                        BusinessName = "Access Authority Business",
                        BusinessDisplayName = "Access",
                        Description = "Setup admin access",
                    },
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
            }

            using var client = factory.CreateClient();
            var created = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = "Manual Identity Must Not Stick",
                branchName = "فرع الصلاحية",
                branchCode = "01",
                branchType = "Retail",
                stationNumber = 1,
                stationName = "كاونتر 1",
                deviceName = "QA-ACCESS-DEVICE",
                username = "setup.admin",
                displayName = "مدير الإعداد",
                password = Password,
                confirmPassword = Password,
                operatingMode = "CentralManagement",
                centralAction = "existing",
                centralLocation = "Local",
                centralServer = "localhost",
                centralDatabaseName = SetupCentralName,
                linkToCentral = "yes",
                branchRemoteChoice = "no",
            });
            var body = await created.Content.ReadAsStringAsync();
            Assert.True(created.IsSuccessStatusCode, body);
            var (deviceId, deviceToken) = ReadDevice(body);
            client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
            client.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
            var admin = await client.PostAsJsonAsync("/api/setup/create-admin", new
            {
                username = "setup.admin",
                displayName = "مدير الإعداد",
                fullName = "مدير الإعداد",
                employeeNumber = "905017083",
                password = Password,
                confirmPassword = Password,
            });
            Assert.True(admin.IsSuccessStatusCode, await admin.Content.ReadAsStringAsync());

            var branchHash = await TextAsync(SetupBranchName, "SELECT PasswordHash FROM dbo.Users WHERE Username = N'setup.admin';");
            var centralHash = await TextAsync(SetupCentralName, "SELECT PasswordHash FROM dbo.Users WHERE Username = N'setup.admin';");
            Assert.Equal(branchHash, centralHash);
            Assert.Equal("Admin", await TextAsync(SetupBranchName, "SELECT r.RoleCode FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON r.RoleID = ur.RoleID INNER JOIN dbo.Users u ON u.UserID = ur.UserID WHERE u.Username = N'setup.admin';"));
            Assert.Equal("Admin", await TextAsync(SetupCentralName, "SELECT r.RoleCode FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON r.RoleID = ur.RoleID INNER JOIN dbo.Users u ON u.UserID = ur.UserID WHERE u.Username = N'setup.admin';"));
            Assert.Equal(1, await IntAsync(SetupCentralName, "SELECT CAST(a.IsActive AS int) FROM dbo.UserBranchAccess a INNER JOIN dbo.Users u ON u.UserID = a.UserID INNER JOIN dbo.Branches b ON b.BranchID = a.BranchID WHERE u.Username = N'setup.admin' AND b.BranchCode = N'01';"));
            Assert.Equal(1, await IntAsync(SetupBranchName, "SELECT CAST(a.IsActive AS int) FROM dbo.UserBranchAccess a INNER JOIN dbo.Users u ON u.UserID = a.UserID INNER JOIN dbo.Branches b ON b.BranchID = a.BranchID WHERE u.Username = N'setup.admin' AND b.BranchCode = N'01';"));
            Assert.Equal(1, await IntAsync(SetupBranchName, "SELECT CAST(IsActive AS int) FROM dbo.Users WHERE Username = N'setup.admin';"));
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropCentralAsync(SetupCentralName);
            await DropBranchAsync(SetupBranchName);
        }
    }

    private HttpClient Client()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        return client;
    }

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PrepareCentralAsync()
    {
        var settings = CentralSettings(CentralName);
        await using (var master = new SqlConnection(settings.BuildConnectionString("master")))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand(
                $"IF DB_ID(N'{CentralName}') IS NULL CREATE DATABASE [{CentralDatabase.Require(CentralName)}] COLLATE Arabic_CI_AS;",
                master);
            await create.ExecuteNonQueryAsync();
        }

        await fixture.Factory.Services.GetRequiredService<CentralMigrationRunner>()
            .ApplyAsync(settings.BuildConnectionString(CentralName), CancellationToken.None);

        await using var source = OpenBranch();
        await using var read = new SqlCommand(
            """
            SELECT u.UserID, u.Username, u.NormalizedUsername, u.DisplayName, u.FullName, u.EmployeeNumber, u.PasswordHash, u.IsActive, u.MustChangePassword,
                   b.BusinessID, bus.BusinessName, b.BranchID, b.BranchCode, b.BranchName, b.BranchType
            FROM dbo.Users AS u
            CROSS JOIN dbo.Branches AS b
            INNER JOIN dbo.Businesses AS bus ON bus.BusinessID = b.BusinessID
            WHERE u.Username = N'admin';
            """,
            source);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var userId = reader.GetGuid(0);
        var username = reader.GetString(1);
        var normalized = reader.GetString(2);
        var displayName = reader.GetString(3);
        var fullName = reader.IsDBNull(4) ? null : reader.GetString(4);
        var employeeNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
        var hash = reader.GetString(6);
        var isActive = reader.GetBoolean(7);
        var mustChange = reader.GetBoolean(8);
        var businessId = reader.GetGuid(9);
        var businessName = reader.GetString(10);
        var branchId = reader.GetGuid(11);
        var branchCode = reader.IsDBNull(12) ? null : reader.GetString(12);
        var branchName = reader.GetString(13);
        var branchType = reader.GetString(14);
        await reader.CloseAsync();

        await using var central = new SqlConnection(settings.BuildConnectionString(CentralName));
        await central.OpenAsync();
        await using var seed = new SqlCommand(
            """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
            VALUES (@businessId, @businessName, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
            VALUES (@branchId, @businessId, @branchCode, @branchName, @branchType, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
            VALUES (NEWID(), N'Admin', N'مدير النظام', 1, 1, SYSUTCDATETIME()),
                   (NEWID(), N'Supervisor', N'مشرف', 1, 1, SYSUTCDATETIME()),
                   (NEWID(), N'User', N'مستخدم', 1, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES (@userId, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, @active, @mustChange, SYSUTCDATETIME());
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @userId, RoleID, SYSUTCDATETIME() FROM dbo.Roles WHERE RoleCode = N'Admin';
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
            """,
            central);
        seed.Parameters.AddWithValue("@businessId", businessId);
        seed.Parameters.AddWithValue("@businessName", businessName);
        seed.Parameters.AddWithValue("@branchId", branchId);
        seed.Parameters.AddWithValue("@branchCode", (object?)branchCode ?? DBNull.Value);
        seed.Parameters.AddWithValue("@branchName", branchName);
        seed.Parameters.AddWithValue("@branchType", branchType);
        seed.Parameters.AddWithValue("@userId", userId);
        seed.Parameters.AddWithValue("@username", username);
        seed.Parameters.AddWithValue("@normalized", normalized);
        seed.Parameters.AddWithValue("@displayName", displayName);
        seed.Parameters.AddWithValue("@fullName", (object?)fullName ?? DBNull.Value);
        seed.Parameters.AddWithValue("@employeeNumber", (object?)employeeNumber ?? DBNull.Value);
        seed.Parameters.AddWithValue("@hash", hash);
        seed.Parameters.AddWithValue("@active", isActive);
        seed.Parameters.AddWithValue("@mustChange", mustChange);
        await seed.ExecuteNonQueryAsync();
    }

    private async Task SaveCentralEndpointAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
        await store.SaveAsync(CentralSettings(CentralName), SqlEndpointSettings.Linked, CancellationToken.None);
    }

    private static SqlEndpointSettings CentralSettings(string databaseName)
    {
        return new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            databaseName,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);
    }

    private static (string DeviceId, string DeviceToken) ReadDevice(string body)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == "completed"
                && document.RootElement.TryGetProperty("device", out var device))
            {
                return (device.GetProperty("deviceId").GetString()!, device.GetProperty("deviceToken").GetString()!);
            }
        }

        throw new InvalidOperationException("Setup did not return a device token.");
    }

    private static Task<string> RoleAsync() =>
        BranchTextAsync(
            """
            SELECT r.RoleCode
            FROM dbo.UserRoles AS ur
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            INNER JOIN dbo.Users AS u ON u.UserID = ur.UserID
            WHERE u.Username = N'admin';
            """);

    private static async Task BranchExecuteAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> BranchIntAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<Guid> BranchGuidAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<string> BranchTextAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task CentralExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CentralIntAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static SqlConnection OpenBranch()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private static async Task<int> IntAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> TextAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static string Connection(string databaseName)
    {
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;
    }

    private static async Task DropCentralAsync(string name)
    {
        var databaseName = CentralDatabase.Require(name);
        if (!CentralDatabase.IsTestName(databaseName))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(CentralSettings(databaseName).BuildConnectionString("master"));
        await master.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """,
            master);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task DropBranchAsync(string name)
    {
        var databaseName = TestDatabase.Require(name);
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(Connection("master"));
        await master.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """,
            master);
        await drop.ExecuteNonQueryAsync();
    }
}
