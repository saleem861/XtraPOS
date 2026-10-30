using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class BranchAuthenticationTests(BranchApiFixture fixture)
{
    private const string Password = "Admin1234";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Migration002_IsAppliedOnce()
    {
        var ids = await QueryStringsAsync(
            "SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY MigrationID;");
        Assert.Equal(
            ["001_InitialSchema", "002_UsersRolesPermissions", "003_NormalizeSystemRoles", "004_ItemsInventoryFoundation", "005_CatalogModelRefinement", "006_InventoryOwnership", "007_RemoveSkuOnHandQuantity", "008_ItemsViewPermission", "009_DataImportHistory", "010_SyncFoundation", "011_ItemEditPermissions", "012_NullableDailySalesRate", "013_UserAdministration", "014_StockMovementSource", "015_CustomerMaster", "016_SalesFoundation", "017_POSPaymentsAndCashSessions", "018_ManualLinePriceOverride"],
            ids);

        Assert.Equal(TestDatabase.Name, await QueryStringAsync("SELECT DB_NAME();"));
        var collation = await QueryStringAsync(
            $"SELECT collation_name FROM sys.databases WHERE name = N'{TestDatabase.Require(TestDatabase.Name)}';");
        Assert.Equal("Arabic_CI_AS", collation);

        var conflicts = await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo')
              AND c.collation_name IS NOT NULL
              AND c.collation_name <> N'Arabic_CI_AS';
            """);
        Assert.Equal(0, conflicts);
    }

    [Fact]
    public async Task SeededRoles_AreUnique()
    {
        var codes = await QueryStringsAsync("SELECT RoleCode FROM dbo.Roles ORDER BY RoleCode;");
        Assert.Equal(["Admin", "Supervisor", "User"], codes);
        var duplicates = await QueryIntAsync(
            "SELECT COUNT(*) FROM (SELECT RoleCode FROM dbo.Roles GROUP BY RoleCode HAVING COUNT(*) > 1) AS d;");
        Assert.Equal(0, duplicates);
        Assert.Equal("مدير النظام", await QueryStringAsync("SELECT RoleName FROM dbo.Roles WHERE RoleCode = N'Admin';"));
        Assert.Equal("مشرف", await QueryStringAsync("SELECT RoleName FROM dbo.Roles WHERE RoleCode = N'Supervisor';"));
        Assert.Equal("مستخدم", await QueryStringAsync("SELECT RoleName FROM dbo.Roles WHERE RoleCode = N'User';"));
        Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Roles WHERE RoleCode IN (N'Manager', N'Cashier');"));
    }

    [Fact]
    public async Task SeededPermissions_AreUnique()
    {
        var codes = await QueryStringsAsync("SELECT PermissionCode FROM dbo.Permissions ORDER BY PermissionCode;");
        Assert.Equal(
            ["Customers.Manage", "Customers.View", "Device.View", "Items.EditBranchPrice", "Items.EditBranchState", "Items.EditMaster", "Items.View", "Roles.Manage", "Roles.View", "Setup.View", "System.Administer", "Users.Manage", "Users.ResetPassword", "Users.View"],
            codes);
        var duplicates = await QueryIntAsync(
            "SELECT COUNT(*) FROM (SELECT PermissionCode FROM dbo.Permissions GROUP BY PermissionCode HAVING COUNT(*) > 1) AS d;");
        Assert.Equal(0, duplicates);
    }

    [Fact]
    public async Task AdminRole_HasEveryCurrentPermission()
    {
        var assigned = await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS r ON r.RoleID = rp.RoleID
            WHERE r.RoleCode = N'Admin';
            """);
        var total = await QueryIntAsync("SELECT COUNT(*) FROM dbo.Permissions;");
        Assert.Equal(total, assigned);
        Assert.Equal(
            ["Items.View"],
            await QueryStringsAsync(
                """
                SELECT p.PermissionCode
                FROM dbo.RolePermissions AS rp
                INNER JOIN dbo.Roles AS r ON r.RoleID = rp.RoleID
                INNER JOIN dbo.Permissions AS p ON p.PermissionID = rp.PermissionID
                WHERE r.RoleCode = N'User'
                ORDER BY p.PermissionCode;
                """));
        var supervisorPermissions = await QueryStringsAsync(
            """
            SELECT p.PermissionCode
            FROM dbo.RolePermissions AS rp
            INNER JOIN dbo.Roles AS r ON r.RoleID = rp.RoleID
            INNER JOIN dbo.Permissions AS p ON p.PermissionID = rp.PermissionID
            WHERE r.RoleCode = N'Supervisor'
            ORDER BY p.PermissionCode;
            """);
        Assert.Equal(["Customers.View", "Device.View", "Items.EditBranchPrice", "Items.EditBranchState", "Items.EditMaster", "Items.View", "Roles.View", "Setup.View", "Users.View"], supervisorPermissions);
        Assert.DoesNotContain("System.Administer", supervisorPermissions);
    }

    [Fact]
    public async Task FirstAdmin_IsCreatedExactlyOnce()
    {
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup/create-admin", AdminBody("other.admin", Password));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("تم إنشاء حساب مدير النظام من قبل", body);
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
    }

    [Fact]
    public async Task DuplicateUsername_IsRejected()
    {
        await ExecuteAsync(
            """
            DELETE FROM dbo.UserRoles;
            DELETE FROM dbo.UserBranchAccess;
            """);
        try
        {
            using var client = fixture.CreateClient();
            client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
            var response = await client.PostAsJsonAsync("/api/setup/create-admin", AdminBody(fixture.Username, Password));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("اسم المستخدم مستخدم بالفعل", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
        }
        finally
        {
            await ExecuteAsync(
                """
                INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
                SELECT u.UserID, r.RoleID, SYSUTCDATETIME()
                FROM dbo.Users AS u
                CROSS JOIN dbo.Roles AS r
                WHERE r.RoleCode = N'Admin'
                  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles AS existing WHERE existing.UserID = u.UserID AND existing.RoleID = r.RoleID);

                INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                SELECT u.UserID, b.BranchID, 1, SYSUTCDATETIME()
                FROM dbo.Users AS u
                CROSS JOIN dbo.Branches AS b
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.UserBranchAccess AS existing
                    WHERE existing.UserID = u.UserID AND existing.BranchID = b.BranchID);
                """);
        }
    }

    [Fact]
    public async Task Password_IsStoredHashed()
    {
        var hash = await QueryStringAsync("SELECT PasswordHash FROM dbo.Users;");
        Assert.NotEqual(Password, hash);
        Assert.DoesNotContain(Password, hash);
        Assert.StartsWith("AQAAAA", hash);
    }

    [Fact]
    public async Task CorrectPassword_LogsIn()
    {
        using var client = DeviceClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<CurrentUserDto>(Json);
        Assert.Equal(fixture.Username, profile!.Username);
        Assert.Equal("مدير التجربة", profile.DisplayName);
        Assert.Contains(profile.Roles, role => role.RoleCode == "Admin");
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task IncorrectPassword_Fails()
    {
        var before = await QueryIntAsync("SELECT COUNT(*) FROM dbo.UserLoginAudit;");
        using var client = DeviceClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "wrong-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("اسم المستخدم أو كلمة المرور غير صحيحة", await response.Content.ReadAsStringAsync());
        Assert.Equal("InvalidPassword", await QueryStringAsync(
            "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        var storedPassword = await QueryStringAsync(
            "SELECT TOP (1) ISNULL(FailureReason, N'') + N'|' + ISNULL(ClientInfo, N'') FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;");
        Assert.DoesNotContain("wrong-pass", storedPassword);
        Assert.True(await QueryIntAsync("SELECT COUNT(*) FROM dbo.UserLoginAudit;") > before);
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task InactiveUser_CannotLogin()
    {
        await ExecuteAsync("UPDATE dbo.Users SET IsActive = 0;");
        try
        {
            using var client = DeviceClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("اسم المستخدم أو كلمة المرور غير صحيحة", await response.Content.ReadAsStringAsync());
            Assert.Equal("InactiveUser", await QueryStringAsync(
                "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        }
        finally
        {
            await ExecuteAsync("UPDATE dbo.Users SET IsActive = 1;");
            await ClearLockoutAsync();
        }
    }

    [Fact]
    public async Task InactiveDevice_CannotLogin()
    {
        await ExecuteAsync("UPDATE dbo.Devices SET IsActive = 0;");
        try
        {
            using var client = DeviceClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("تعذر تسجيل الدخول من هذا الجهاز", await response.Content.ReadAsStringAsync());
            Assert.Equal("InactiveDevice", await QueryStringAsync(
                "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        }
        finally
        {
            await ExecuteAsync("UPDATE dbo.Devices SET IsActive = 1;");
        }
    }

    [Fact]
    public async Task RevokedDevice_CannotLogin()
    {
        await ExecuteAsync("UPDATE dbo.Devices SET RevokedAt = SYSUTCDATETIME();");
        try
        {
            using var client = DeviceClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("تعذر تسجيل الدخول من هذا الجهاز", await response.Content.ReadAsStringAsync());
            Assert.Equal("RevokedDevice", await QueryStringAsync(
                "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        }
        finally
        {
            await ExecuteAsync("UPDATE dbo.Devices SET RevokedAt = NULL;");
        }
    }

    [Fact]
    public async Task UserWithoutBranchAccess_CannotLogin()
    {
        await ExecuteAsync("UPDATE dbo.UserBranchAccess SET IsActive = 0;");
        try
        {
            using var client = DeviceClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("اسم المستخدم أو كلمة المرور غير صحيحة", await response.Content.ReadAsStringAsync());
            Assert.Equal("NoBranchAccess", await QueryStringAsync(
                "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        }
        finally
        {
            await ExecuteAsync("UPDATE dbo.UserBranchAccess SET IsActive = 1;");
            await ClearLockoutAsync();
        }
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailedCount()
    {
        await ExecuteAsync("UPDATE dbo.Users SET FailedLoginCount = 3, LockoutEndAt = NULL;");
        using var client = DeviceClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await QueryIntAsync("SELECT FailedLoginCount FROM dbo.Users;"));
        Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users WHERE LockoutEndAt IS NOT NULL;"));
    }

    [Fact]
    public async Task FailedLogin_IncrementsFailedCount()
    {
        await ClearLockoutAsync();
        using var client = DeviceClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await QueryIntAsync("SELECT FailedLoginCount FROM dbo.Users;"));
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount()
    {
        await ClearLockoutAsync();
        using var client = DeviceClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "bad-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        Assert.Equal(5, await QueryIntAsync("SELECT FailedLoginCount FROM dbo.Users;"));
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users WHERE LockoutEndAt IS NOT NULL;"));

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("تعذر تسجيل الدخول حالياً", await locked.Content.ReadAsStringAsync());
        Assert.Equal("LockedOut", await QueryStringAsync(
            "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task Logout_ClearsAuthentication()
    {
        using var client = DeviceClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Devices WHERE RevokedAt IS NULL AND IsActive = 1;"));
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task CurrentUser_RequiresAuthentication()
    {
        using var client = DeviceClient();
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CurrentUser_NeverReturnsPasswordHash()
    {
        using var client = DeviceClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        var body = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, body);
        var hash = await QueryStringAsync("SELECT PasswordHash FROM dbo.Users;");
        Assert.DoesNotContain(hash, body);

        var me = await client.GetAsync("/api/auth/me");
        var meBody = await me.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.DoesNotContain("passwordHash", meBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hash, meBody);
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task Reload_PreservesAuthenticatedSession()
    {
        using var client = DeviceClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var profile = await me.Content.ReadFromJsonAsync<CurrentUserDto>(Json);
        Assert.Equal(fixture.Username, profile!.Username);
        await ClearLockoutAsync();
    }

    [Fact]
    public async Task RestartingApi_DoesNotDuplicateSeedData()
    {
        await using var restarted = new XtraPosWebFactory();
        using var client = restarted.CreateClient();
        var status = await client.GetFromJsonAsync<StatusDto>("/api/setup/status", Json);
        Assert.Equal("Configured", status!.Status);

        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
        Assert.Equal(3, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Roles;"));
        Assert.Equal(14, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Permissions;"));
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'002_UsersRolesPermissions';"));
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'003_NormalizeSystemRoles';"));
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'004_ItemsInventoryFoundation';"));
        Assert.Equal(1, await QueryIntAsync(
            "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'005_CatalogModelRefinement';"));
        Assert.Equal(8, await QueryIntAsync("SELECT COUNT(*) FROM dbo.PriceTypes;"));
        Assert.Equal(16, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovementTypes;"));
    }

    private HttpClient DeviceClient()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        return client;
    }

    private static object AdminBody(string username, string password) => new
    {
        username,
        displayName = "مدير آخر",
        password,
        confirmPassword = password,
    };

    private static async Task ClearLockoutAsync()
    {
        await ExecuteAsync("UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndAt = NULL;");
    }

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

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(
            TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private sealed class StatusDto
    {
        public string Status { get; set; } = "";
    }

    private sealed class CurrentUserDto
    {
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<RoleDto> Roles { get; set; } = [];
    }

    private sealed class RoleDto
    {
        public string RoleCode { get; set; } = "";
        public string RoleName { get; set; } = "";
    }
}
