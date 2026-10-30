using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class LocalDeviceRecoveryTests
{
    private const string DatabaseName = "XtraPOS_Branch_Test_DeviceRecovery";
    private const string Password = "Admin1234";

    [Fact]
    public async Task Recovery_CreatesTheFirstStationAndDevice_ThenReusesThemWithoutMigrating()
    {
        await AssertLiveFingerprintAsync();
        await ResetSchemaAsync();
        var factory = new XtraPosWebFactory(DatabaseName);
        try
        {
            await SeedAdminAsync(factory, includeStation: false, includeDevice: false);
            Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
            Assert.DoesNotContain("003_NormalizeSystemRoles", await MigrationsAsync());

            using var client = factory.CreateClient();
            var context = await client.GetFromJsonAsync<RecoveryContext>("/api/setup/local-device-recovery");
            Assert.NotNull(context);
            Assert.Equal("منشأة الاستعادة", context.BusinessName);
            Assert.Empty(context.Stations);

            var rejected = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(null, "جهاز الاستعادة", "wrong-password"));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM dbo.POSStations;"));

            var created = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(null, "جهاز الاستعادة", Password));
            var createdBody = await created.Content.ReadFromJsonAsync<RecoveryDevice>();
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.NotNull(createdBody);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.POSStations;"));
            Assert.Equal("كاونتر 1", await TextAsync("SELECT StationName FROM dbo.POSStations;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal("جهاز الاستعادة", await TextAsync("SELECT DeviceName FROM dbo.Devices;"));

            var again = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(createdBody.StationId, "جهاز الاستعادة", Password));
            var againBody = await again.Content.ReadFromJsonAsync<RecoveryDevice>();
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(createdBody.DeviceId, againBody!.DeviceId);
            Assert.Equal(createdBody.StationId, againBody.StationId);
            Assert.NotEqual(createdBody.DeviceToken, againBody.DeviceToken);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.POSStations;"));

            using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            login.Headers.Add("X-Device-Id", againBody.DeviceId.ToString());
            login.Headers.Add("X-Device-Token", againBody.DeviceToken);
            login.Content = JsonContent.Create(new { username = "admin", password = Password });
            var loggedIn = await client.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);

            Assert.Equal("001_InitialSchema,002_UsersRolesPermissions", await MigrationsAsync());
            Assert.Equal(0, await CountAsync("SELECT CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;"));
            Assert.Equal(0, await CountAsync("SELECT CASE WHEN COL_LENGTH(N'dbo.Businesses', N'Description') IS NULL THEN 0 ELSE 1 END;"));
            await AssertLiveFingerprintAsync();
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropAsync();
        }
    }

    [Fact]
    public async Task Recovery_ReusesTheExistingStationAndDevice()
    {
        await ResetSchemaAsync();
        var factory = new XtraPosWebFactory(DatabaseName);
        try
        {
            var seeded = await SeedAdminAsync(factory, includeStation: true, includeDevice: true);
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            var body = await response.Content.ReadFromJsonAsync<RecoveryDevice>();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(seeded.StationId, body!.StationId);
            Assert.Equal(seeded.DeviceId, body.DeviceId);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.POSStations;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal("001_InitialSchema,002_UsersRolesPermissions", await MigrationsAsync());
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropAsync();
        }
    }

    [Fact]
    public async Task Recovery_AcceptsTemporaryAdminPassword_WithoutClearingMustChangePassword()
    {
        await ResetSchemaAsync();
        var factory = new XtraPosWebFactory(DatabaseName);
        try
        {
            var seeded = await SeedAdminAsync(factory, includeStation: true, includeDevice: true);
            await ExecuteAsync(
                """
                UPDATE dbo.Users
                SET MustChangePassword = 1,
                    FailedLoginCount = 5,
                    LockoutEndAt = DATEADD(minute, 15, SYSUTCDATETIME())
                WHERE Username = N'admin';
                """);

            using var client = factory.CreateClient();
            var locked = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            var lockedBody = await locked.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
            Assert.Contains(LocalDeviceRecovery.LockedMessage, lockedBody);
            Assert.DoesNotContain("تعذر تسجيل الدخول حالياً", lockedBody);
            Assert.Equal(1, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));
            Assert.Equal(5, await CountAsync("SELECT FailedLoginCount FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));

            await ExecuteAsync(
                """
                UPDATE dbo.Users
                SET IsActive = 0, LockoutEndAt = NULL, FailedLoginCount = 0
                WHERE Username = N'admin';
                """);
            var inactive = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
            Assert.Contains(AuthService.InvalidCredentialsMessage, await inactive.Content.ReadAsStringAsync());
            Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM dbo.Devices WHERE DeviceName <> N'صندوق 1';"));

            await ExecuteAsync("UPDATE dbo.Users SET IsActive = 1 WHERE Username = N'admin';");
            var wrong = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", "wrong-password"));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains(AuthService.InvalidCredentialsMessage, await wrong.Content.ReadAsStringAsync());
            Assert.Equal(1, await CountAsync("SELECT FailedLoginCount FROM dbo.Users;"));

            await ExecuteAsync(
                """
                UPDATE dbo.UserRoles
                SET RoleID = (SELECT RoleID FROM dbo.Roles WHERE RoleCode = N'Manager')
                WHERE UserID = (SELECT UserID FROM dbo.Users WHERE Username = N'admin');
                """);
            var manager = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            Assert.Equal(HttpStatusCode.BadRequest, manager.StatusCode);
            Assert.Contains(LocalDeviceRecovery.NotAuthorizedMessage, await manager.Content.ReadAsStringAsync());
            Assert.Equal(1, await CountAsync("SELECT FailedLoginCount FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));

            await ExecuteAsync(
                """
                UPDATE dbo.UserRoles
                SET RoleID = (SELECT RoleID FROM dbo.Roles WHERE RoleCode = N'Admin')
                WHERE UserID = (SELECT UserID FROM dbo.Users WHERE Username = N'admin');
                UPDATE dbo.Users
                SET MustChangePassword = 1,
                    FailedLoginCount = 5,
                    LockoutEndAt = DATEADD(minute, -1, SYSUTCDATETIME())
                WHERE Username = N'admin';
                """);
            var recovered = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            var recoveredBody = await recovered.Content.ReadFromJsonAsync<RecoveryDevice>();
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(seeded.DeviceId, recoveredBody!.DeviceId);
            Assert.Equal(seeded.StationId, recoveredBody.StationId);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Businesses;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.POSStations;"));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal(1, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));
            Assert.Equal(0, await CountAsync("SELECT FailedLoginCount FROM dbo.Users;"));
            Assert.Equal(1, await CountAsync("SELECT CASE WHEN LockoutEndAt IS NULL THEN 1 ELSE 0 END FROM dbo.Users;"));

            var again = await client.PostAsJsonAsync("/api/setup/recover-main-device", Request(seeded.StationId, "صندوق 1", Password));
            var againBody = await again.Content.ReadFromJsonAsync<RecoveryDevice>();
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(seeded.DeviceId, againBody!.DeviceId);
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Equal(1, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));

            using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            login.Headers.Add("X-Device-Id", againBody.DeviceId.ToString());
            login.Headers.Add("X-Device-Token", againBody.DeviceToken);
            login.Content = JsonContent.Create(new { username = "admin", password = Password });
            var loggedIn = await client.SendAsync(login);
            var loggedInBody = await loggedIn.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
            Assert.Contains("\"mustChangePassword\":true", loggedInBody, StringComparison.OrdinalIgnoreCase);

            var rejectedChange = await ChangePasswordAsync(client, againBody, "wrong-password", "Changed123");
            Assert.Equal(HttpStatusCode.BadRequest, rejectedChange.StatusCode);
            Assert.Equal(1, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));

            var changed = await ChangePasswordAsync(client, againBody, Password, "Changed123");
            var changedBody = await changed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Contains("\"mustChangePassword\":false", changedBody, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, await CountAsync("SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));
            Assert.Equal("001_InitialSchema,002_UsersRolesPermissions", await MigrationsAsync());
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropAsync();
        }
    }

    private static async Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client,
        RecoveryDevice device,
        string currentPassword,
        string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password");
        request.Headers.Add("X-Device-Id", device.DeviceId.ToString());
        request.Headers.Add("X-Device-Token", device.DeviceToken);
        request.Content = JsonContent.Create(new
        {
            currentPassword,
            newPassword,
            confirmPassword = newPassword,
        });
        return await client.SendAsync(request);
    }

    private static object Request(Guid? stationId, string deviceName, string password) => new
    {
        username = "admin",
        password,
        stationId,
        stationNumber = stationId is null ? 1 : (int?)null,
        stationName = stationId is null ? "كاونتر 1" : null,
        deviceName,
    };

    private static async Task<SeededIds> SeedAdminAsync(XtraPosWebFactory factory, bool includeStation, bool includeDevice)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var now = DateTime.UtcNow;
        var business = new Business
        {
            BusinessName = "منشأة الاستعادة",
            IsActive = true,
            CreatedAt = now,
        };
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        var branch = new Branch
        {
            BusinessId = business.BusinessId,
            BranchName = "فرع الاستعادة",
            BranchType = "Retail",
            IsActive = true,
            CreatedAt = now,
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var user = new AppUser
        {
            Username = "admin",
            NormalizedUsername = "ADMIN",
            DisplayName = "مدير الاستعادة",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = passwords.Hash(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var adminRole = await db.Roles.SingleAsync(role => role.RoleCode == RoleCodes.Admin);
        db.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = adminRole.RoleId, AssignedAt = now });
        db.UserBranchAccess.Add(new UserBranchAccess
        {
            UserId = user.UserId,
            BranchId = branch.BranchId,
            IsActive = true,
            GrantedAt = now,
        });
        Guid stationId = Guid.Empty;
        Guid deviceId = Guid.Empty;
        if (includeStation)
        {
            var station = new PosStation
            {
                BranchId = branch.BranchId,
                StationNumber = 1,
                StationName = "كاونتر 1",
                IsActive = true,
                CreatedAt = now,
            };
            db.PosStations.Add(station);
            await db.SaveChangesAsync();
            stationId = station.StationId;
            if (includeDevice)
            {
                var device = new Device
                {
                    BranchId = branch.BranchId,
                    StationId = station.StationId,
                    DeviceName = "صندوق 1",
                    DeviceType = DeviceTokenService.DeviceTypePos,
                    DeviceTokenHash = DeviceTokenService.Hash("previous-token"),
                    IsActive = true,
                    RegisteredAt = now,
                    CreatedAt = now,
                };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
                deviceId = device.DeviceId;
            }
        }

        await db.SaveChangesAsync();
        return new SeededIds(stationId, deviceId);
    }

    private static async Task ResetSchemaAsync()
    {
        await DropAsync();
        await ExecuteMasterAsync($"CREATE DATABASE [{TestDatabase.Require(DatabaseName)}] COLLATE Arabic_CI_AS;");
        await ExecuteAsync(await ReadMigrationAsync("XtraPOS.Api.Data.Migrations.001_InitialSchema.sql"));
        await ExecuteAsync(
            """
            IF COL_LENGTH(N'dbo.Businesses', N'Description') IS NOT NULL
                ALTER TABLE dbo.Businesses DROP COLUMN Description;
            """);
        await ExecuteAsync(await ReadMigrationAsync("XtraPOS.Api.Data.Migrations.002_UsersRolesPermissions.sql"));
        await ExecuteAsync(
            """
            INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName)
            VALUES (N'001_InitialSchema', N'Initial branch schema'),
                   (N'002_UsersRolesPermissions', N'Users roles and permissions');
            """);
    }

    private static async Task<string> ReadMigrationAsync(string resourceName)
    {
        await using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource {resourceName}.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task AssertLiveFingerprintAsync()
    {
        await using var connection = new SqlConnection(LiveConnection());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT STRING_AGG(MigrationID, N',') WITHIN GROUP (ORDER BY MigrationID) FROM dbo.DatabaseSchemaHistory;
            SELECT COUNT(*) FROM dbo.Businesses;
            SELECT COUNT(*) FROM dbo.Branches;
            SELECT COUNT(*) FROM dbo.Users;
            SELECT COUNT(*) FROM dbo.POSStations;
            SELECT COUNT(*) FROM dbo.Devices;
            SELECT CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("001_InitialSchema,002_UsersRolesPermissions", reader.GetString(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
    }

    private static async Task<int> CountAsync(string sql)
    {
        await using var connection = TestConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> TextAsync(string sql)
    {
        await using var connection = TestConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<string> MigrationsAsync()
    {
        return await TextAsync("SELECT STRING_AGG(MigrationID, N',') WITHIN GROUP (ORDER BY MigrationID) FROM dbo.DatabaseSchemaHistory;");
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = TestConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync()
    {
        var name = TestDatabase.Require(DatabaseName);
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static SqlConnection TestConnection()
    {
        return new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = TestDatabase.Require(DatabaseName),
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationName = "XtraPOS.Tests",
        }.ConnectionString);
    }

    private static string LiveConnection()
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

    private sealed record SeededIds(Guid StationId, Guid DeviceId);

    private sealed class RecoveryContext
    {
        public string BusinessName { get; set; } = "";
        public RecoveryStation[] Stations { get; set; } = [];
    }

    private sealed class RecoveryStation
    {
        public Guid StationId { get; set; }
    }

    private sealed class RecoveryDevice
    {
        public Guid DeviceId { get; set; }
        public Guid StationId { get; set; }
        public string DeviceToken { get; set; } = "";
    }
}
