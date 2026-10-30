using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Import;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class LegacyUserImportTests(BranchApiFixture fixture)
{
    private const string Salim = "سليم جيوسي";
    private const string Tareq = "طارق دعاس";
    private const string Abdullah = "عبدالله ريان";

    [Fact]
    public void LegacyQuery_DoesNotReadPasswordsOrSensitiveColumns()
    {
        var sql = SqlLegacyEmployeeSource.SelectSql;
        Assert.Contains("IDNumber", sql, StringComparison.Ordinal);
        Assert.Contains("UserShortName", sql, StringComparison.Ordinal);
        Assert.Contains("UserFullName", sql, StringComparison.Ordinal);
        foreach (var column in SqlLegacyEmployeeSource.SelectedColumns)
        {
            Assert.Contains(column, sql, StringComparison.Ordinal);
        }

        string[] forbidden =
        [
            "UserPassword",
            "BasicSalary",
            "BankAccountNumber",
            "IsMarried",
            "ChildrenCount",
            "CanPayMoney",
            "CanSeeConnectionStrings",
        ];
        foreach (var column in forbidden)
        {
            Assert.DoesNotContain(column, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RoleMigration_PreservesRoleIdsAndIsIdempotent()
    {
        var roles = await ReadRolesAsync();
        var adminId = roles.Single(role => role.Code == RoleCodes.Admin).Id;
        var supervisorId = roles.Single(role => role.Code == RoleCodes.Supervisor).Id;
        var userId = roles.Single(role => role.Code == RoleCodes.User).Id;
        var permissionCount = await QueryIntAsync("SELECT COUNT(*) FROM dbo.RolePermissions;");
        var supervisorPermissionCount = await QueryIntAsync(
            $"SELECT COUNT(*) FROM dbo.RolePermissions WHERE RoleID = '{supervisorId}';");

        try
        {
            await ExecuteAsync(
                $"""
                UPDATE dbo.Roles
                SET RoleCode = N'Manager', RoleName = N'مدير'
                WHERE RoleID = '{supervisorId}';

                UPDATE dbo.Roles
                SET RoleCode = N'Cashier', RoleName = N'كاشير'
                WHERE RoleID = '{userId}';

                DELETE FROM dbo.DatabaseSchemaHistory
                WHERE MigrationID = N'003_NormalizeSystemRoles';
                """);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
                await runner.ApplyPendingAsync(CancellationToken.None);
            }

            Assert.Equal(RoleCodes.Admin, await RoleCodeAsync(adminId));
            Assert.Equal(RoleCodes.Supervisor, await RoleCodeAsync(supervisorId));
            Assert.Equal("مشرف", await RoleNameAsync(supervisorId));
            Assert.Equal(RoleCodes.User, await RoleCodeAsync(userId));
            Assert.Equal("مستخدم", await RoleNameAsync(userId));
            Assert.Equal(permissionCount, await QueryIntAsync("SELECT COUNT(*) FROM dbo.RolePermissions;"));
            Assert.Equal(supervisorPermissionCount, await QueryIntAsync(
                $"SELECT COUNT(*) FROM dbo.RolePermissions WHERE RoleID = '{supervisorId}';"));
            Assert.Equal(1, await QueryIntAsync(
                $"""
                SELECT COUNT(*)
                FROM dbo.RolePermissions AS granted
                INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = granted.PermissionID
                WHERE granted.RoleID = '{userId}'
                  AND permission.PermissionCode = N'Items.View';
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.RolePermissions AS granted
                WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles AS roles WHERE roles.RoleID = granted.RoleID);
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.UserRoles AS assigned
                WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles AS roles WHERE roles.RoleID = assigned.RoleID);
                """));

            await ExecuteAsync(
                "DELETE FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'003_NormalizeSystemRoles';");
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
                await runner.ApplyPendingAsync(CancellationToken.None);
            }

            Assert.Equal(RoleCodes.Supervisor, await RoleCodeAsync(supervisorId));
            Assert.Equal(RoleCodes.User, await RoleCodeAsync(userId));
            Assert.Equal(1, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'003_NormalizeSystemRoles';"));
            Assert.Equal(0, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Roles WHERE RoleCode IN (N'Manager', N'Cashier');"));
        }
        finally
        {
            await ExecuteAsync(
                """
                UPDATE dbo.Roles
                SET RoleCode = N'Supervisor', RoleName = N'مشرف'
                WHERE RoleCode = N'Manager';

                UPDATE dbo.Roles
                SET RoleCode = N'User', RoleName = N'مستخدم'
                WHERE RoleCode = N'Cashier';

                IF NOT EXISTS (
                    SELECT 1 FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'003_NormalizeSystemRoles')
                BEGIN
                    INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                    VALUES (N'003_NormalizeSystemRoles', N'Normalize system roles', N'test', SYSUTCDATETIME());
                END
                """);
        }
    }

    [Fact]
    public async Task Import_AssignsRolesBranchAccessAndHashedPasswords()
    {
        var adminHash = await QueryStringAsync(
            "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        try
        {
            var first = await ImportAsync(new FakeSource(StandardRows()));
            Assert.False(first.DevelopmentAdminReused);
            Assert.True(first.DevelopmentAdminRemainedSeparate);
            AssertOutcome(first, Salim, LegacyImportOutcome.Created, RoleCodes.Admin, "905017083");
            AssertOutcome(first, Tareq, LegacyImportOutcome.Created, RoleCodes.Supervisor, "402120067");
            AssertOutcome(first, Abdullah, LegacyImportOutcome.Created, RoleCodes.Supervisor, "421600008");
            Assert.Equal(4, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(adminHash, await QueryStringAsync(
                "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));

            foreach (var item in first.Items)
            {
                Assert.NotNull(item.TemporaryPassword);
                Assert.True(item.MustChangePassword);
                Assert.True(item.BranchAccess);
                var hash = await QueryStringAsync(
                    $"SELECT PasswordHash FROM dbo.Users WHERE EmployeeNumber = N'{item.EmployeeNumber}';");
                Assert.StartsWith("AQAAAA", hash);
                Assert.DoesNotContain(item.TemporaryPassword, hash);
                Assert.NotEqual(item.TemporaryPassword, hash);
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
                Assert.NotEqual(
                    PasswordVerificationResult.Failed,
                    passwords.Verify(new AppUser(), hash, item.TemporaryPassword));
                Assert.Equal(1, await QueryIntAsync(
                    $"""
                    SELECT COUNT(*)
                    FROM dbo.UserBranchAccess AS access
                    INNER JOIN dbo.Users AS users ON users.UserID = access.UserID
                    WHERE users.EmployeeNumber = N'{item.EmployeeNumber}' AND access.IsActive = 1;
                    """));
                Assert.Equal(item.RoleCode, await QueryStringAsync(
                    $"""
                    SELECT roles.RoleCode
                    FROM dbo.UserRoles AS assigned
                    INNER JOIN dbo.Users AS users ON users.UserID = assigned.UserID
                    INNER JOIN dbo.Roles AS roles ON roles.RoleID = assigned.RoleID
                    WHERE users.EmployeeNumber = N'{item.EmployeeNumber}';
                    """));
            }

            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.Users
                GROUP BY NormalizedUsername
                HAVING COUNT(*) > 1;
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.Users
                WHERE EmployeeNumber IS NOT NULL
                GROUP BY EmployeeNumber
                HAVING COUNT(*) > 1;
                """));
            Assert.Equal(0, await QueryIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.RolePermissions AS granted
                INNER JOIN dbo.Roles AS roles ON roles.RoleID = granted.RoleID
                INNER JOIN dbo.Permissions AS permissions ON permissions.PermissionID = granted.PermissionID
                WHERE roles.RoleCode IN (N'Supervisor', N'User')
                  AND permissions.PermissionCode = N'System.Administer';
                """));

            var second = await ImportAsync(new FakeSource(StandardRows()));
            Assert.False(second.DevelopmentAdminReused);
            Assert.True(second.DevelopmentAdminRemainedSeparate);
            Assert.All(second.Items, item => Assert.Equal(LegacyImportOutcome.Updated, item.Outcome));
            Assert.All(second.Items, item => Assert.Null(item.TemporaryPassword));
            Assert.Equal(4, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(4, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.UserBranchAccess WHERE IsActive = 1;"));
            Assert.Equal(4, await QueryIntAsync("SELECT COUNT(*) FROM dbo.UserRoles;"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task Import_LinksExistingAdminWithoutResettingPassword()
    {
        var hash = await QueryStringAsync(
            "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        await ExecuteAsync(
            """
            UPDATE dbo.Users
            SET FullName = N'سليم أحمد سليم جيوسي'
            WHERE NormalizedUsername = N'ADMIN';
            """);
        try
        {
            var report = await ImportAsync(new FakeSource(StandardRows()));
            Assert.True(report.DevelopmentAdminReused);
            Assert.False(report.DevelopmentAdminRemainedSeparate);
            var salim = report.Items.Single(item => item.RequestedName == Salim);
            Assert.Equal(LegacyImportOutcome.Updated, salim.Outcome);
            Assert.Null(salim.TemporaryPassword);
            Assert.Equal("admin", salim.Username);
            Assert.Equal("905017083", salim.EmployeeNumber);
            Assert.Equal(RoleCodes.Admin, salim.RoleCode);
            Assert.False(salim.MustChangePassword);
            Assert.Equal(hash, await QueryStringAsync(
                "SELECT PasswordHash FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));
            Assert.Equal(Salim, await QueryStringAsync(
                "SELECT DisplayName FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';"));
            Assert.Equal(3, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Users;"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
            await ExecuteAsync(
                """
                UPDATE dbo.Users
                SET DisplayName = N'مدير التجربة',
                    FullName = NULL,
                    EmployeeNumber = NULL,
                    MustChangePassword = 0
                WHERE NormalizedUsername = N'ADMIN';
                """);
        }
    }

    [Fact]
    public async Task Import_ReportsMissingLegacyMatch()
    {
        try
        {
            var rows = StandardRows().Where(row => row.UserShortName != Salim).ToList();
            var report = await ImportAsync(new FakeSource(rows));
            var salim = report.Items.Single(item => item.RequestedName == Salim);
            Assert.Equal(LegacyImportOutcome.Rejected, salim.Outcome);
            Assert.Equal("No legacy row matches.", salim.Message);
            Assert.Null(salim.TemporaryPassword);
            Assert.Equal(0, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = N'905017083';"));
            AssertOutcome(report, Tareq, LegacyImportOutcome.Created, RoleCodes.Supervisor, "402120067");
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task Import_RejectsAmbiguousLegacyMatch()
    {
        try
        {
            var rows = StandardRows().ToList();
            rows.Add(Person("111", Salim, "سليم آخر"));
            var report = await ImportAsync(new FakeSource(rows));
            var salim = report.Items.Single(item => item.RequestedName == Salim);
            Assert.Equal(LegacyImportOutcome.Rejected, salim.Outcome);
            Assert.Equal("More than one legacy row matches.", salim.Message);
            Assert.Equal(0, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber IN (N'905017083', N'111');"));
            AssertOutcome(report, Abdullah, LegacyImportOutcome.Created, RoleCodes.Supervisor, "421600008");
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    [Fact]
    public async Task Import_ReportsUsernameConflict()
    {
        await ExecuteAsync(
            """
            INSERT INTO dbo.Users
                (Username, NormalizedUsername, DisplayName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
            SELECT N'سليم جيوسي', N'سليم جيوسي', N'شخص آخر', N'1', PasswordHash, 1, 0, 0, SYSUTCDATETIME()
            FROM dbo.Users
            WHERE NormalizedUsername = N'ADMIN';
            """);
        try
        {
            var report = await ImportAsync(new FakeSource(StandardRows()));
            var salim = report.Items.Single(item => item.RequestedName == Salim);
            Assert.Equal(LegacyImportOutcome.Rejected, salim.Outcome);
            Assert.Equal("Username conflicts with an unrelated user.", salim.Message);
            Assert.Equal("1", await QueryStringAsync(
                "SELECT EmployeeNumber FROM dbo.Users WHERE NormalizedUsername = N'سليم جيوسي';"));
            Assert.Equal("شخص آخر", await QueryStringAsync(
                "SELECT DisplayName FROM dbo.Users WHERE NormalizedUsername = N'سليم جيوسي';"));
            Assert.Equal(0, await QueryIntAsync(
                "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = N'905017083';"));
        }
        finally
        {
            await RemoveImportedUsersAsync();
        }
    }

    private async Task<LegacyUserImportReport> ImportAsync(ILegacyEmployeeSource source)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var importer = new LegacyUserImporter(
            scope.ServiceProvider.GetRequiredService<BranchDbContext>(),
            scope.ServiceProvider.GetRequiredService<PasswordService>(),
            source,
            scope.ServiceProvider.GetRequiredService<ILogger<LegacyUserImporter>>());
        return await importer.ImportAsync(CancellationToken.None);
    }

    private static void AssertOutcome(
        LegacyUserImportReport report,
        string name,
        LegacyImportOutcome outcome,
        string roleCode,
        string employeeNumber)
    {
        var item = report.Items.Single(candidate => candidate.RequestedName == name);
        Assert.Equal(outcome, item.Outcome);
        Assert.Equal(roleCode, item.RoleCode);
        Assert.Equal(employeeNumber, item.EmployeeNumber);
        Assert.Equal(name, item.Username);
        Assert.Equal(name, item.DisplayName);
        Assert.True(item.BranchAccess);
    }

    private static List<LegacyEmployeeRecord> StandardRows()
    {
        return
        [
            Person("905017083", Salim, "سليم أحمد سليم جيوسي"),
            Person("402120067", Tareq, "طارق نزار يوسف دعاس"),
            Person("421600008", Abdullah, "عبدالله فريد عبدالله ريان"),
        ];
    }

    private static LegacyEmployeeRecord Person(string id, string shortName, string fullName)
    {
        return new LegacyEmployeeRecord(id, shortName, fullName, null, null, null, null, null, null, null, null, null);
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

            DELETE FROM dbo.Users WHERE NormalizedUsername <> N'ADMIN';
            """);
    }

    private static async Task<List<RoleRow>> ReadRolesAsync()
    {
        var roles = new List<RoleRow>();
        await using var connection = Open();
        await using var command = new SqlCommand("SELECT RoleID, RoleCode FROM dbo.Roles;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            roles.Add(new RoleRow(reader.GetGuid(0), reader.GetString(1)));
        }

        return roles;
    }

    private static async Task<string> RoleCodeAsync(Guid roleId)
    {
        return await QueryStringAsync($"SELECT RoleCode FROM dbo.Roles WHERE RoleID = '{roleId}';");
    }

    private static async Task<string> RoleNameAsync(Guid roleId)
    {
        return await QueryStringAsync($"SELECT RoleName FROM dbo.Roles WHERE RoleID = '{roleId}';");
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

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(
            TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private sealed record RoleRow(Guid Id, string Code);

    private sealed class FakeSource(IReadOnlyList<LegacyEmployeeRecord> rows) : ILegacyEmployeeSource
    {
        public Task<IReadOnlyList<LegacyEmployeeRecord>> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(rows);
        }
    }
}
