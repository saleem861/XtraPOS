using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class DatabaseLifecycleTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task ExistingDatabase_SurvivesStartupMigrationsAndRepeatSetup()
    {
        const string marker = "T-LIFE-KEEP";
        await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemNumber = N'{marker}';");
        var beforeUsers = await ScalarAsync("SELECT COUNT(*) FROM dbo.Users;");
        var beforeBranches = await ScalarAsync("SELECT COUNT(*) FROM dbo.Branches;");
        var beforeDevices = await ScalarAsync("SELECT COUNT(*) FROM dbo.Devices;");
        var businessName = await TextAsync("SELECT TOP (1) BusinessName FROM dbo.Businesses;");
        await ExecuteAsync(
            $"""
            INSERT INTO dbo.Items (ItemNumber, ItemName, IsActive, IsProhibited)
            VALUES (N'{marker}', N'lifecycle marker', 1, 0);
            """);
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var connections = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
            var migrations = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await connections.EnsureBranchDatabaseAsync(CancellationToken.None);
            await migrations.ApplyPendingAsync(CancellationToken.None);

            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'{marker}';"));
            Assert.Equal(beforeUsers, await ScalarAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(beforeBranches, await ScalarAsync("SELECT COUNT(*) FROM dbo.Branches;"));
            Assert.Equal(beforeDevices, await ScalarAsync("SELECT COUNT(*) FROM dbo.Devices;"));
            Assert.Contains(MigrationRunner.ImportHistoryMigrationId, await MigrationIdsAsync());

            using var client = fixture.CreateClient();
            var response = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = "منشأة يجب ألا تُستبدل",
                branchName = "فرع يجب ألا يُستبدل",
                branchType = "Retail",
                stationNumber = 9,
                stationName = "كاونتر بديل",
                deviceName = "جهاز بديل",
                username = "replacement-admin",
                displayName = "مدير بديل",
                password = "Admin1234",
                confirmPassword = "Admin1234",
            });
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"alreadyConfigured\":true", body);
            Assert.Contains("\"type\":\"completed\"", body);
            Assert.DoesNotContain("\"type\":\"failed\"", body);
            Assert.Equal(businessName, await TextAsync("SELECT TOP (1) BusinessName FROM dbo.Businesses;"));
            Assert.Equal(beforeUsers, await ScalarAsync("SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'{marker}';"));
            Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.DataImportHistory;"));
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemNumber = N'{marker}';");
        }
    }

    [Fact]
    public async Task ConnectionProbe_DoesNotReturnTheSubmittedPassword()
    {
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup/test-connection", new
        {
            intent = "existing",
            server = "localhost",
            databaseName = TestDatabase.Name,
            authenticationType = "Integrated",
            password = "SqlSecret-NotForBrowser",
            expectedRole = "Branch",
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SqlSecret-NotForBrowser", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"databaseRole\":\"Branch\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentReset_IsNotExposedByTheRunningApi()
    {
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/dev/reset", new { confirmation = "RESET-XTRAPOS-BRANCH" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(TestDatabase.Name, await TextAsync("SELECT DB_NAME();"));
    }

    [Fact]
    public void DestructiveReset_RequiresDevelopmentAndTheExactDatabaseName()
    {
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(false, BranchDatabase.Name, BranchDatabase.Name, out var production));
        Assert.Contains("Development", production);

        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, null, out _));
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, "XtraPOS_Branch ", out _));
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, "xtrapos_branch", out _));
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, "XtraPOS_%", out _));
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, "XtraPOS_*", out _));
        Assert.False(DevelopmentDatabaseReset.TryAuthorize(true, TestDatabase.Name, BranchDatabase.Name, out _));

        Assert.True(DevelopmentDatabaseReset.TryAuthorize(true, BranchDatabase.Name, BranchDatabase.Name, out var allowed));
        Assert.Equal("", allowed);
        Assert.True(DevelopmentDatabaseReset.IsRequested(["--reset-development-database", "--confirm-database", BranchDatabase.Name]));
        Assert.True(DevelopmentDatabaseStatus.IsRequested([DevelopmentDatabaseStatus.CommandName]));
        Assert.False(DevelopmentDatabaseReset.IsRequested([DevelopmentDatabaseStatus.CommandName]));
        Assert.False(DevelopmentDatabaseStatus.IsRequested(["--reset-development-database"]));
        Assert.Equal(
            BranchDatabase.Name,
            DevelopmentDatabaseReset.ReadConfirmedDatabase(["--reset-development-database", "--confirm-database", BranchDatabase.Name]));
    }

    [Fact]
    public void TestDatabaseGuard_RejectsTheLiveBranchDatabase()
    {
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require(BranchDatabase.Name));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("XtraPOS_Branch"));
        Assert.False(BranchDatabase.IsTestName(BranchDatabase.Name));
    }

    [Fact]
    public void EmptyCatalogWarning_RequiresACompletedImportAndNoItems()
    {
        Assert.False(CatalogConsistency.ShouldWarn(setupExists: true, masterCatalogCompleted: false, itemCount: 0));
        Assert.False(CatalogConsistency.ShouldWarn(setupExists: true, masterCatalogCompleted: true, itemCount: 2811));
        Assert.False(CatalogConsistency.ShouldWarn(setupExists: false, masterCatalogCompleted: true, itemCount: 0));
        Assert.True(CatalogConsistency.ShouldWarn(setupExists: true, masterCatalogCompleted: true, itemCount: 0));
        Assert.Equal(
            "Master catalog is empty although this database was previously initialized.",
            CatalogConsistency.EmptyAfterInitializationMessage);
    }

    [Fact]
    public async Task StartupConsistencyCheck_DoesNotTreatTheCurrentTestCatalogAsMissing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
        Assert.False(await CatalogConsistency.MasterCatalogIsUnexpectedlyEmptyAsync(connections, CancellationToken.None));
    }

    private static async Task<List<string>> MigrationIdsAsync()
    {
        var ids = new List<string>();
        await using var connection = Open();
        await using var command = new SqlCommand("SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY MigrationID;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<int> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> TextAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }
}
