using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class UserAdministrationTests(BranchApiFixture fixture)
{
    private const string Password = "Admin1234";
    private const string ManualPassword = "Manual-6h-pass";
    private const string CentralName = "XtraPOS_Central_Test_UserAdmin";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task StandaloneUserAdministration_EnforcesTheRules()
    {
        try
        {
            using var client = Client();
            await LoginAsync(client, fixture.Username, Password);
            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var catalog = await client.GetFromJsonAsync<CatalogBody>("/api/users/catalog", Json);
            Assert.Equal("Branch", catalog!.Authority);
            Assert.True(catalog.CanManage);
            Assert.True(catalog.CanReset);

            var created = await CreateAsync(client, "مستخدم تجربة", "900100", "user.900100", "User", branchId, "auto", null);
            Assert.False(string.IsNullOrWhiteSpace(created.TemporaryPassword));
            Assert.True(created.TemporaryPassword!.Length >= 16);
            Assert.NotEqual(created.TemporaryPassword, await HashAsync(created.User!.UserId));
            Assert.DoesNotContain(created.TemporaryPassword, await HashAsync(created.User.UserId));
            var createdAudits = await QueryStringsAsync("SELECT Action FROM dbo.UserAdministrationAudit;");
            Assert.DoesNotContain(createdAudits, action => action.Contains(created.TemporaryPassword, StringComparison.Ordinal));
            Assert.True(created.User.MustChangePassword);
            Assert.Equal("User", created.User.RoleCode);

            var page = await client.GetFromJsonAsync<PageBody>("/api/users?search=900100&active=active&role=User&branchId=" + branchId, Json);
            Assert.Equal("Branch", page!.Authority);
            Assert.Contains(page.Users, user => user.UserId == created.User.UserId);
            Assert.DoesNotContain(created.TemporaryPassword, await client.GetStringAsync("/api/users?search=900100"));

            var duplicateUser = await client.PostAsJsonAsync("/api/users", Body("شخص آخر", "900101", "user.900100", "User", branchId, "manual", ManualPassword));
            Assert.Equal(HttpStatusCode.BadRequest, duplicateUser.StatusCode);
            Assert.Contains("اسم المستخدم مستخدم بالفعل", await duplicateUser.Content.ReadAsStringAsync());

            var duplicateEmployee = await client.PostAsJsonAsync("/api/users", Body("شخص آخر", "900100", "user.900101", "User", branchId, "manual", ManualPassword));
            Assert.Equal(HttpStatusCode.BadRequest, duplicateEmployee.StatusCode);
            Assert.Contains("رقم الموظف مستخدم بالفعل", await duplicateEmployee.Content.ReadAsStringAsync());

            var shortPassword = await client.PostAsJsonAsync("/api/users", Body("شخص آخر", "900102", "user.900102", "User", branchId, "manual", "short"));
            Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

            var manual = await CreateAsync(client, "مشرف تجربة", "900200", "user.900200", "Supervisor", branchId, "manual", ManualPassword);
            Assert.Equal(ManualPassword, manual.TemporaryPassword);
            Assert.DoesNotContain(ManualPassword, await HashAsync(manual.User!.UserId));
            Assert.StartsWith("AQAAAA", await HashAsync(manual.User.UserId));

            await ExecuteAsync($"UPDATE dbo.Users SET FailedLoginCount = 4, LockoutEndAt = DATEADD(HOUR, 1, SYSUTCDATETIME()) WHERE UserID = '{manual.User.UserId}';");
            var reset = await client.PostAsJsonAsync($"/api/users/{manual.User.UserId}/reset-password", new { passwordMode = "manual", password = ManualPassword });
            var resetBody = await reset.Content.ReadFromJsonAsync<IssuedBody>(Json);
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.Equal(ManualPassword, resetBody!.TemporaryPassword);
            Assert.Equal("Supervisor", resetBody.User!.RoleCode);
            Assert.True(resetBody.User.IsActive);
            Assert.True(resetBody.User.MustChangePassword);
            Assert.Equal(0, await QueryIntAsync($"SELECT FailedLoginCount FROM dbo.Users WHERE UserID = '{manual.User.UserId}';"));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Users WHERE UserID = '{manual.User.UserId}' AND LockoutEndAt IS NOT NULL;"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '{manual.User.UserId}';"));

            using var changed = Client();
            var login = await changed.PostAsJsonAsync("/api/auth/login", new { username = "user.900200", password = ManualPassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var profile = await login.Content.ReadFromJsonAsync<CurrentBody>(Json);
            Assert.True(profile!.MustChangePassword);
            var change = await changed.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = ManualPassword, newPassword = "Changed-6h-pass", confirmPassword = "Changed-6h-pass" });
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Users WHERE UserID = '{manual.User.UserId}' AND MustChangePassword = 1;"));

            var inactive = await client.PostAsJsonAsync($"/api/users/{created.User.UserId}/activation", new { isActive = false });
            Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
            using var blocked = Client();
            var denied = await blocked.PostAsJsonAsync("/api/auth/login", new { username = "user.900100", password = created.TemporaryPassword });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("InactiveUser", await QueryStringAsync("SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/users/{created.User.UserId}/activation", new { isActive = true })).StatusCode);
            using var restored = Client();
            var restoredLogin = await restored.PostAsJsonAsync("/api/auth/login", new { username = "user.900100", password = created.TemporaryPassword });
            Assert.Equal(HttpStatusCode.OK, restoredLogin.StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{created.User.UserId}/branches", new { branchIds = Array.Empty<Guid>() })).StatusCode);
            using var noBranch = Client();
            var noAccess = await noBranch.PostAsJsonAsync("/api/auth/login", new { username = "user.900100", password = created.TemporaryPassword });
            Assert.Equal(HttpStatusCode.Unauthorized, noAccess.StatusCode);
            Assert.Equal("NoBranchAccess", await QueryStringAsync("SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));

            var hashBeforeRole = await HashAsync(created.User.UserId);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{created.User.UserId}/role", new { roleCode = "Supervisor" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{created.User.UserId}/role", new { roleCode = "Supervisor" })).StatusCode);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '{created.User.UserId}';"));
            Assert.Equal(hashBeforeRole, await HashAsync(created.User.UserId));

            var removeAdmin = await client.PostAsJsonAsync($"/api/users/{await AdminIdAsync()}/activation", new { isActive = false });
            Assert.Equal(HttpStatusCode.BadRequest, removeAdmin.StatusCode);
            Assert.Contains("آخر مدير", await removeAdmin.Content.ReadAsStringAsync());
            Assert.Equal(1, await QueryIntAsync("SELECT CAST(IsActive AS int) FROM dbo.Users WHERE Username = N'admin';"));
            var demoteAdmin = await client.PutAsJsonAsync($"/api/users/{await AdminIdAsync()}/role", new { roleCode = "User" });
            Assert.Equal(HttpStatusCode.BadRequest, demoteAdmin.StatusCode);
            Assert.Equal("Admin", await QueryStringAsync("SELECT r.RoleCode FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON r.RoleID = ur.RoleID INNER JOIN dbo.Users u ON u.UserID = ur.UserID WHERE u.Username = N'admin';"));

            using var supervisor = Client();
            await LoginAsync(supervisor, "user.900200", "Changed-6h-pass");
            Assert.Equal(HttpStatusCode.OK, (await supervisor.GetAsync("/api/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync("/api/users", Body("ممنوع", "900300", "user.900300", "User", branchId, "auto", null))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync($"/api/users/{created.User.UserId}/reset-password", new { passwordMode = "auto" })).StatusCode);

            var audits = await QueryStringsAsync("SELECT Action FROM dbo.UserAdministrationAudit;");
            Assert.Contains("UserCreated", audits);
            Assert.Contains("PasswordReset", audits);
            Assert.Contains("UserDeactivated", audits);
            Assert.Contains("UserActivated", audits);
            Assert.Contains("RoleChanged", audits);
            Assert.Contains("BranchAccessChanged", audits);
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.UserAdministrationAudit WHERE Action LIKE N'%{ManualPassword}%';"));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task CentralLinkedWrite_UpdatesCentralBeforeTheBranchCopy()
    {
        try
        {
            using var client = Client();
            await LoginAsync(client, fixture.Username, Password);
            var before = await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;");
            await SaveCentralEndpointAsync("XtraPOS_Central_Test_Missing");
            var rejected = await client.PostAsJsonAsync("/api/users", Body("لن يُحفظ", "900400", "user.900400", "User", await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;"), "manual", ManualPassword));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal(before, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.DoesNotContain(ManualPassword, await rejected.Content.ReadAsStringAsync());

            await PrepareCentralAsync();
            await SaveCentralEndpointAsync(CentralName);
            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var created = await CreateAsync(client, "مستخدم مركزي", "900500", "user.900500", "User", branchId, "auto", null);
            Assert.Equal(1, await CentralIntAsync("SELECT COUNT(*) FROM dbo.Users WHERE Username = N'user.900500';"));
            Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users WHERE Username = N'user.900500';"));
            Assert.Equal(
                await CentralStringAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'user.900500';"),
                await QueryStringAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'user.900500';"));
            Assert.DoesNotContain(created.TemporaryPassword!, await QueryStringAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'user.900500';"));
            Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '" + created.User!.UserId + "';"));
            Assert.Equal(1, await CentralIntAsync("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '" + created.User.UserId + "';"));

            var reset = await client.PostAsJsonAsync($"/api/users/{created.User.UserId}/reset-password", new { passwordMode = "manual", password = ManualPassword });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            var centralHash = await CentralStringAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'user.900500';");
            var branchHash = await QueryStringAsync("SELECT PasswordHash FROM dbo.Users WHERE Username = N'user.900500';");
            Assert.Equal(centralHash, branchHash);
            Assert.DoesNotContain(ManualPassword, centralHash);
            Assert.Equal(1, await CentralIntAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users WHERE Username = N'user.900500';"));
            Assert.Equal("User", await CentralStringAsync("SELECT r.RoleCode FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON r.RoleID = ur.RoleID INNER JOIN dbo.Users u ON u.UserID = ur.UserID WHERE u.Username = N'user.900500';"));

            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{created.User.UserId}/role", new { roleCode = "Supervisor" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/users/{created.User.UserId}/role", new { roleCode = "Supervisor" })).StatusCode);
            Assert.Equal(1, await CentralIntAsync("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '" + created.User.UserId + "';"));
            Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserID = '" + created.User.UserId + "';"));
            Assert.Equal("Central", (await client.GetFromJsonAsync<CatalogBody>("/api/users/catalog", Json))!.Authority);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Password_IsNotWrittenToLogs()
    {
        await using var host = new ProbeFactory();
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        try
        {
            await LoginAsync(client, fixture.Username, Password);
            var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
            var created = await CreateAsync(client, "سجل تجربة", "900600", "user.900600", "User", branchId, "manual", ManualPassword);
            Assert.Equal(ManualPassword, created.TemporaryPassword);
            Assert.DoesNotContain(host.Messages, message => message.Contains(ManualPassword, StringComparison.Ordinal));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    private async Task<IssuedBody> CreateAsync(HttpClient client, string fullName, string employeeNumber, string username, string role, Guid branchId, string mode, string? password)
    {
        var response = await client.PostAsJsonAsync("/api/users", Body(fullName, employeeNumber, username, role, branchId, mode, password));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonSerializer.Deserialize<IssuedBody>(text, Json)!;
    }

    private static object Body(string fullName, string employeeNumber, string username, string role, Guid branchId, string mode, string? password)
    {
        return new
        {
            fullName,
            employeeNumber,
            username,
            roleCode = role,
            branchIds = new[] { branchId },
            isActive = true,
            passwordMode = mode,
            password,
        };
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
                $"IF DB_ID(N'{CentralName}') IS NULL CREATE DATABASE [{CentralName}] COLLATE Arabic_CI_AS;",
                master);
            await create.ExecuteNonQueryAsync();
        }

        await fixture.Factory.Services.GetRequiredService<CentralMigrationRunner>()
            .ApplyAsync(settings.BuildConnectionString(CentralName), CancellationToken.None);

        await using var source = Open();
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

    private async Task SaveCentralEndpointAsync(string databaseName)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
        await store.SaveAsync(CentralSettings(databaseName), SqlEndpointSettings.Linked, CancellationToken.None);
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

    private async Task CleanupAsync()
    {
        await ExecuteAsync(
            """
            DELETE FROM dbo.UserAdministrationAudit;
            DELETE FROM dbo.UserRoles WHERE UserID IN (SELECT UserID FROM dbo.Users WHERE Username <> N'admin');
            DELETE FROM dbo.UserBranchAccess WHERE UserID IN (SELECT UserID FROM dbo.Users WHERE Username <> N'admin');
            DELETE FROM dbo.UserLoginAudit WHERE UserID IN (SELECT UserID FROM dbo.Users WHERE Username <> N'admin');
            DELETE FROM dbo.Users WHERE Username <> N'admin';
            DELETE FROM dbo.SyncCheckpoints;
            DELETE FROM dbo.SyncInbox;
            DELETE FROM dbo.SyncOutbox;
            DELETE FROM dbo.SyncEndpoints;
            """);
        if (!CentralDatabase.IsTestName(CentralName))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(CentralSettings(CentralName).BuildConnectionString("master"));
        await master.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF DB_ID(N'{CentralName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{CentralName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{CentralName}];
            END
            """,
            master);
        await drop.ExecuteNonQueryAsync();
    }

    private async Task<Guid> AdminIdAsync() => await QueryGuidAsync("SELECT UserID FROM dbo.Users WHERE Username = N'admin';");

    private async Task<string> HashAsync(Guid userId) => await QueryStringAsync($"SELECT PasswordHash FROM dbo.Users WHERE UserID = '{userId}';");

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<string> QueryStringAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<List<string>> QueryStringsAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<int> CentralIntAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> CentralStringAsync(string sql)
    {
        await using var connection = new SqlConnection(CentralSettings(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private sealed class ProbeFactory : WebApplicationFactory<Program>
    {
        public List<string> Messages { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SqlServer:Database"] = TestDatabase.Name,
                });
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new ProbeLoggerProvider(Messages)));
        }
    }

    private sealed class ProbeLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class ProbeLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class CatalogBody
    {
        public string Authority { get; set; } = "";
        public bool CanManage { get; set; }
        public bool CanReset { get; set; }
    }

    private sealed class PageBody
    {
        public string Authority { get; set; } = "";
        public int Count { get; set; }
        public List<UserBody> Users { get; set; } = [];
    }

    private sealed class IssuedBody
    {
        public UserBody? User { get; set; }
        public string? TemporaryPassword { get; set; }
    }

    private sealed class UserBody
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = "";
        public string? RoleCode { get; set; }
        public bool IsActive { get; set; }
        public bool MustChangePassword { get; set; }
    }

    private sealed class CurrentBody
    {
        public bool MustChangePassword { get; set; }
    }
}
