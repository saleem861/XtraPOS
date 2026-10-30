using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtraPOS.Api.Data;
using XtraPOS.Api.Import;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class LegacyBulkImportTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task ImportRemaining_KeepsPrivilegedRolesAndImportsTheRestAsUsers()
    {
        var adminHash = await QueryStringAsync(
            "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        try
        {
            var named = await ImportNamedAsync(new FakeSource(NamedRows()));
            Assert.Equal(LegacyImportOutcome.Created, named.Items.Single(item => item.RequestedName == "سليم جيوسي").Outcome);

            var report = await ImportRemainingAsync(new FakeSource(RemainingRows()));
            Assert.Equal(0, report.Failed);
            Assert.Equal(6, report.Examined);
            Assert.Equal(3, report.Matched);
            Assert.Equal(2, report.Created);
            Assert.Equal(1, report.ActiveCreated);
            Assert.Equal(1, report.InactiveCreated);
            Assert.Equal(1, report.Skipped);
            Assert.Equal(LegacyImportReasons.UsernameConflict, report.Items.Single(item => item.EmployeeNumber == "ADMIN").Reason);

            Assert.Equal(RoleCodes.Admin, await RoleCodeAsync("905017083"));
            Assert.Equal(RoleCodes.Supervisor, await RoleCodeAsync("402120067"));
            Assert.Equal(RoleCodes.Supervisor, await RoleCodeAsync("421600008"));
            Assert.Equal(RoleCodes.User, await RoleCodeAsync("880001"));
            Assert.Equal(RoleCodes.User, await RoleCodeAsync("880002"));
            Assert.Equal("880002", await QueryStringAsync(
                "SELECT Username FROM dbo.Users WHERE EmployeeNumber = N'880002';"));
            Assert.Equal("أحمد علي", await QueryStringAsync(
                "SELECT FullName FROM dbo.Users WHERE EmployeeNumber = N'880002';"));
            Assert.Equal(1, await ActiveFlagAsync("880001"));
            Assert.Equal(0, await ActiveFlagAsync("880002"));
            Assert.Equal(1, await AccessFlagAsync("880001"));
            Assert.Equal(0, await AccessFlagAsync("880002"));
            Assert.Equal(1, await QueryIntAsync(
                "SELECT CAST(MustChangePassword AS int) FROM dbo.Users WHERE EmployeeNumber = N'880001';"));

            var active = report.Items.Single(item => item.EmployeeNumber == "880001");
            Assert.NotNull(active.TemporaryPassword);
            Assert.Null(report.Items.Single(item => item.EmployeeNumber == "880002").TemporaryPassword);
            var hash = await QueryStringAsync(
                "SELECT PasswordHash FROM dbo.Users WHERE EmployeeNumber = N'880001';");
            Assert.StartsWith("AQAAAA", hash);
            Assert.DoesNotContain(active.TemporaryPassword, hash);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
                Assert.NotEqual(
                    PasswordVerificationResult.Failed,
                    passwords.Verify(new AppUser(), hash, active.TemporaryPassword));
            }

            var inactiveHash = await QueryStringAsync(
                "SELECT PasswordHash FROM dbo.Users WHERE EmployeeNumber = N'880002';");
            Assert.StartsWith("AQAAAA", inactiveHash);
            Assert.DoesNotContain("UserPassword", inactiveHash, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(adminHash, await QueryStringAsync(
                "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));
            Assert.Equal("", await QueryStringAsync(
                "SELECT ISNULL(EmployeeNumber, N'') FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));
            Assert.Equal("مدير التجربة", await QueryStringAsync(
                "SELECT DisplayName FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));

            var second = await ImportRemainingAsync(new FakeSource(RemainingRows()));
            Assert.Equal(0, second.Created);
            Assert.Equal(5, second.Matched);
            Assert.All(second.Items.Where(item => item.Outcome == "Matched"), item => Assert.Null(item.TemporaryPassword));
            Assert.Equal(1, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = N'880001';"));
            Assert.Equal(1, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.UserRoles AS assigned
                INNER JOIN dbo.Users AS users ON users.UserID = assigned.UserID
                WHERE users.EmployeeNumber = N'905017083';
                """));
            Assert.Equal(1, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.UserBranchAccess AS access
                INNER JOIN dbo.Users AS users ON users.UserID = access.UserID
                WHERE users.EmployeeNumber = N'880001';
                """));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task ImportRemaining_DoesNotDemoteAnExistingSupervisor()
    {
        await ExecuteAsync(
            """
            INSERT INTO dbo.Users
                (Username, NormalizedUsername, DisplayName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
            SELECT N'مشرف ثابت', N'مشرف ثابت', N'مشرف ثابت', N'424242', PasswordHash, 1, 0, 0, SYSUTCDATETIME()
            FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';

            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT users.UserID, roles.RoleID, SYSUTCDATETIME()
            FROM dbo.Users AS users
            CROSS JOIN dbo.Roles AS roles
            WHERE users.EmployeeNumber = N'424242' AND roles.RoleCode = N'Supervisor';
            """);
        try
        {
            var report = await ImportRemainingAsync(new FakeSource(
            [
                Row("424242", "مشرف ثابت", "مشرف ثابت", null),
            ]));
            Assert.Equal("Matched", report.Items.Single().Outcome);
            Assert.Equal(RoleCodes.Supervisor, report.Items.Single().RoleCode);
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.UserRoles AS assigned
                INNER JOIN dbo.Users AS users ON users.UserID = assigned.UserID
                INNER JOIN dbo.Roles AS roles ON roles.RoleID = assigned.RoleID
                WHERE users.EmployeeNumber = N'424242' AND roles.RoleCode = N'User';
                """));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task ImportRemaining_ReportsDuplicateEmployeeNumbersWithoutBlockingOthers()
    {
        try
        {
            var report = await ImportRemainingAsync(new FakeSource(
            [
                Row("5555", "أول مكرر", "أول مكرر", null),
                Row("5555", "ثان مكرر", "ثان مكرر", null),
                Row("6666", "مستخدم سليم", "مستخدم سليم", null),
            ]));
            Assert.Equal(2, report.Skipped);
            Assert.Equal(1, report.Created);
            Assert.All(
                report.Items.Where(item => item.EmployeeNumber == "5555"),
                item => Assert.Equal(LegacyImportReasons.DuplicateEmployeeNumber, item.Reason));
            Assert.Equal(RoleCodes.User, await RoleCodeAsync("6666"));
            Assert.Equal(0, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = N'5555';"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task ImportRemaining_ReportsAmbiguousExistingEmployeeNumber()
    {
        await ExecuteAsync(
            """
            DROP INDEX UX_Users_EmployeeNumber ON dbo.Users;

            INSERT INTO dbo.Users
                (Username, NormalizedUsername, DisplayName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
            SELECT N'نسخة أ', N'نسخة أ', N'نسخة أ', N'777001', PasswordHash, 1, 0, 0, SYSUTCDATETIME()
            FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';

            INSERT INTO dbo.Users
                (Username, NormalizedUsername, DisplayName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
            SELECT N'نسخة ب', N'نسخة ب', N'نسخة ب', N'777001', PasswordHash, 1, 0, 0, SYSUTCDATETIME()
            FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';
            """);
        try
        {
            var report = await ImportRemainingAsync(new FakeSource(
            [
                Row("777001", "نسخة أ", "نسخة أ", null),
                Row("777002", "شخص آخر", "شخص آخر", null),
            ]));
            Assert.Equal(LegacyImportReasons.AmbiguousExistingMatch, report.Items.Single(item => item.EmployeeNumber == "777001").Reason);
            Assert.Equal("Created", report.Items.Single(item => item.EmployeeNumber == "777002").Outcome);
            Assert.Equal(2, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = N'777001';"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
            await ExecuteAsync(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = N'UX_Users_EmployeeNumber' AND object_id = OBJECT_ID(N'dbo.Users'))
                BEGIN
                    CREATE UNIQUE INDEX UX_Users_EmployeeNumber
                        ON dbo.Users (EmployeeNumber)
                        WHERE EmployeeNumber IS NOT NULL;
                END
                """);
        }
    }

    [Fact]
    public async Task ImportRemaining_InactiveUserCannotLogin()
    {
        try
        {
            var report = await ImportRemainingAsync(new FakeSource(
            [
                Row("880010", "موظف سابق", "موظف سابق", DateTime.UtcNow.Date),
            ]));
            var item = report.Items.Single();
            Assert.Equal("Created", item.Outcome);
            Assert.False(item.IsActive);
            Assert.Null(item.TemporaryPassword);
            Assert.Equal(0, await AccessFlagAsync("880010"));

            using var client = fixture.CreateClient();
            client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { username = "موظف سابق", password = "unused-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("InactiveUser", await QueryStringAsync(
                "SELECT TOP (1) FailureReason FROM dbo.UserLoginAudit ORDER BY LoginAuditID DESC;"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public void EndWork_DeterminesInitialActiveState()
    {
        Assert.True(LegacyUserImporter.IsLegacyActive(null));
        Assert.True(LegacyUserImporter.IsLegacyActive(DateTime.UtcNow.Date.AddDays(1)));
        Assert.False(LegacyUserImporter.IsLegacyActive(DateTime.UtcNow.Date));
        Assert.False(LegacyUserImporter.IsLegacyActive(DateTime.UtcNow.Date.AddDays(-1)));
        Assert.Equal(
            "أحمد علي",
            LegacyUserImporter.BuildFullName(new LegacyEmployeeRecord(
                "1", null, null, "أحمد", "علي", null, null, null, null, null, null, null)));
    }

    private async Task<LegacyUserImportReport> ImportNamedAsync(ILegacyEmployeeSource source)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var importer = new LegacyUserImporter(
            scope.ServiceProvider.GetRequiredService<BranchDbContext>(),
            scope.ServiceProvider.GetRequiredService<PasswordService>(),
            source,
            scope.ServiceProvider.GetRequiredService<ILogger<LegacyUserImporter>>());
        return await importer.ImportAsync(CancellationToken.None);
    }

    private async Task<LegacyBulkImportReport> ImportRemainingAsync(ILegacyEmployeeSource source)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var importer = new LegacyUserImporter(
            scope.ServiceProvider.GetRequiredService<BranchDbContext>(),
            scope.ServiceProvider.GetRequiredService<PasswordService>(),
            source,
            scope.ServiceProvider.GetRequiredService<ILogger<LegacyUserImporter>>());
        return await importer.ImportRemainingAsync(CancellationToken.None);
    }

    private static List<LegacyEmployeeRecord> NamedRows()
    {
        return
        [
            Row("905017083", "سليم جيوسي", "سليم أحمد سليم جيوسي", null),
            Row("402120067", "طارق دعاس", "طارق نزار يوسف دعاس", null),
            Row("421600008", "عبدالله ريان", "عبدالله فريد عبدالله ريان", null),
        ];
    }

    private static List<LegacyEmployeeRecord> RemainingRows()
    {
        var rows = NamedRows();
        rows.Add(Row("880001", "موظف حالي", "موظف حالي", null));
        rows.Add(new LegacyEmployeeRecord(
            "880002", null, null, "أحمد", "علي", null, null, null, null, null, null, DateTime.UtcNow.Date.AddDays(-3)));
        rows.Add(Row("ADMIN", "admin", "شخص مختلف", null));
        return rows;
    }

    private static LegacyEmployeeRecord Row(string id, string? shortName, string? fullName, DateTime? endWork)
    {
        return new LegacyEmployeeRecord(id, shortName, fullName, null, null, null, null, null, null, null, null, endWork);
    }

    private static async Task<string> RoleCodeAsync(string employeeNumber)
    {
        return await QueryStringAsync(
            $"""
            SELECT roles.RoleCode
            FROM dbo.UserRoles AS assigned
            INNER JOIN dbo.Users AS users ON users.UserID = assigned.UserID
            INNER JOIN dbo.Roles AS roles ON roles.RoleID = assigned.RoleID
            WHERE users.EmployeeNumber = N'{employeeNumber}';
            """);
    }

    private static async Task<int> ActiveFlagAsync(string employeeNumber)
    {
        return await QueryIntAsync(
            $"SELECT CAST(IsActive AS int) FROM dbo.Users WHERE EmployeeNumber = N'{employeeNumber}';");
    }

    private static async Task<int> AccessFlagAsync(string employeeNumber)
    {
        return await QueryIntAsync(
            $"""
            SELECT CAST(access.IsActive AS int)
            FROM dbo.UserBranchAccess AS access
            INNER JOIN dbo.Users AS users ON users.UserID = access.UserID
            WHERE users.EmployeeNumber = N'{employeeNumber}';
            """);
    }

    private static async Task RemoveImportedUsersAsync()
    {
        await ExecuteAsync(
            """
            DELETE access
            FROM dbo.UserBranchAccess AS access
            INNER JOIN dbo.Users AS users ON users.UserID = access.UserID
            WHERE users.NormalizedUsername <> N'ADMIN';

            DELETE assigned
            FROM dbo.UserRoles AS assigned
            INNER JOIN dbo.Users AS users ON users.UserID = assigned.UserID
            WHERE users.NormalizedUsername <> N'ADMIN';

            DELETE audit
            FROM dbo.UserLoginAudit AS audit
            INNER JOIN dbo.Users AS users ON users.UserID = audit.UserID
            WHERE users.NormalizedUsername <> N'ADMIN';

            DELETE FROM dbo.UserLoginAudit WHERE UserID IS NULL;

            DELETE FROM dbo.Users WHERE NormalizedUsername <> N'ADMIN';
            """);
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
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? "" : (string)value;
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(
            TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private sealed class FakeSource(IReadOnlyList<LegacyEmployeeRecord> rows) : ILegacyEmployeeSource
    {
        public Task<IReadOnlyList<LegacyEmployeeRecord>> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(rows);
        }
    }
}
