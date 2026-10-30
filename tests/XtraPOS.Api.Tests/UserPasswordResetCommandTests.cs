using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class UserPasswordResetCommandTests
{
    private const string DatabaseName = "XtraPOS_Branch_Test_PasswordReset";

    [Fact]
    public void Authorization_RequiresTheExactDatabaseAndUserKey()
    {
        Assert.False(UserPasswordResetCommand.TryAuthorize(BranchDatabase.Name, "XtraPOS_Branch_Test_PasswordReset", "905017083", out _));
        Assert.False(UserPasswordResetCommand.TryAuthorize(BranchDatabase.Name, "XtraPOS_Branch*", "905017083", out _));
        Assert.False(UserPasswordResetCommand.TryAuthorize(BranchDatabase.Name, BranchDatabase.Name + " ", "905017083", out _));
        Assert.False(UserPasswordResetCommand.TryAuthorize(DatabaseName, DatabaseName, "905017083*", out _));
        Assert.True(UserPasswordResetCommand.TryAuthorize(BranchDatabase.Name, BranchDatabase.Name, "905017083", out var allowed));
        Assert.Equal("", allowed);
    }

    [Fact]
    public async Task Reset_UpdatesOnlyTheMatchingUser_AndLeavesSchemaAt002()
    {
        await ResetSchemaAsync();
        var factory = new XtraPosWebFactory(DatabaseName);
        try
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
            var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
            var now = DateTime.UtcNow;
            var target = new AppUser
            {
                Username = "905017083",
                NormalizedUsername = "905017083",
                DisplayName = "المدير المستهدف",
                EmployeeNumber = "905017083",
                IsActive = true,
                MustChangePassword = false,
                FailedLoginCount = 3,
                LockoutEndAt = now.AddMinutes(10),
                CreatedAt = now,
            };
            target.PasswordHash = passwords.Hash(target, "OldPassword-1");
            var other = new AppUser
            {
                Username = "other",
                NormalizedUsername = "OTHER",
                DisplayName = "مستخدم آخر",
                IsActive = true,
                CreatedAt = now,
            };
            other.PasswordHash = passwords.Hash(other, "OtherPassword-1");
            var otherHash = other.PasswordHash;
            db.Users.AddRange(target, other);
            var role = await db.Roles.SingleAsync(item => item.RoleCode == RoleCodes.Admin);
            await db.SaveChangesAsync();
            db.UserRoles.Add(new UserRole { UserId = target.UserId, RoleId = role.RoleId, AssignedAt = now });
            await db.SaveChangesAsync();

            var missing = await UserPasswordResetCommand.ExecuteAsync(db, passwords, "missing-user", TextWriter.Null, CancellationToken.None);
            Assert.False(missing.Updated);
            Assert.Equal(0, missing.MatchCount);

            var reset = await UserPasswordResetCommand.ExecuteAsync(db, passwords, "905017083", TextWriter.Null, CancellationToken.None);
            Assert.True(reset.Updated);
            Assert.NotNull(reset.TemporaryPassword);
            Assert.True(reset.TemporaryPassword!.Length >= 12);

            var saved = await db.Users.SingleAsync(item => item.Username == "905017083");
            Assert.Equal(PasswordVerificationResult.Success, passwords.Verify(saved, saved.PasswordHash, reset.TemporaryPassword));
            Assert.True(saved.MustChangePassword);
            Assert.Equal(0, saved.FailedLoginCount);
            Assert.Null(saved.LockoutEndAt);
            Assert.True(saved.IsActive);
            Assert.Equal(otherHash, await db.Users.Where(item => item.Username == "other").Select(item => item.PasswordHash).SingleAsync());
            Assert.Equal(2, await db.Users.CountAsync());
            Assert.Equal(RoleCodes.Admin, await db.UserRoles.Where(item => item.UserId == saved.UserId).Select(item => item.Role.RoleCode).SingleAsync());
            Assert.Equal(
                "001_InitialSchema,002_UsersRolesPermissions",
                string.Join(",", await db.SchemaHistory.Select(item => item.MigrationId).OrderBy(item => item).ToListAsync()));
        }
        finally
        {
            await factory.DisposeAsync();
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await DropAsync();
        }
    }

    private static async Task ResetSchemaAsync()
    {
        await DropAsync();
        await ExecuteMasterAsync($"CREATE DATABASE [{TestDatabase.Require(DatabaseName)}] COLLATE Arabic_CI_AS;");
        await ExecuteAsync(await ReadMigrationAsync("XtraPOS.Api.Data.Migrations.001_InitialSchema.sql"));
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

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = TestDatabase.Require(DatabaseName),
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync()
    {
        var name = TestDatabase.Require(DatabaseName);
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """,
            connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
