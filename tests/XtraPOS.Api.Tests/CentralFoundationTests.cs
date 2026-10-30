using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CentralFoundationTests(BranchApiFixture fixture)
{
    private const string CentralName = "XtraPOS_Central_Test_Integration";
    private const string PlainName = "XtraPOS_Central_Test_Plain";
    private const string AbsentName = "XtraPOS_Central_Test_NotCreated";

    [Fact]
    public async Task CentralFoundation_CreatesLinksAndSyncsWithoutTouchingStock()
    {
        Assert.Throws<InvalidOperationException>(() => CentralDatabase.Require(BranchDatabase.Name));
        Assert.Throws<InvalidOperationException>(() => CentralDatabase.Require(CentralDatabase.Name + "_drop"));
        Assert.True(CentralOwnedEntities.IsRegistered("Customers"));
        Assert.True(CentralOwnedEntities.IsRegistered("CustomerClassifications"));
        Assert.False(BranchRemoteSync.IsImplemented);

        await DropCentralAsync(CentralName);
        await DropCentralAsync(PlainName);
        Assert.False(await DatabaseExistsAsync(AbsentName));
        using (var client = fixture.CreateClient())
        {
            var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.False(await DatabaseExistsAsync(AbsentName));
        Assert.False(await DatabaseExistsAsync(CentralName));

        var settings = LocalCentral(CentralName);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var databases = scope.ServiceProvider.GetRequiredService<CentralDatabaseService>();
        var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
        var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
        await databases.CreateIfMissingAsync(settings, CancellationToken.None);
        await databases.CreateIfMissingAsync(settings, CancellationToken.None);
        await databases.VerifyExistingAsync(settings, CancellationToken.None);

        await CreatePlainAsync();
        await Assert.ThrowsAsync<CentralRejectedException>(() => databases.VerifyExistingAsync(LocalCentral(PlainName), CancellationToken.None));

        var branchId = await BranchGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var priceTypeId = await BranchGuidAsync("SELECT TOP (1) PriceTypeID FROM dbo.PriceTypes;");
        var itemId = Guid.NewGuid();
        var beforeBalances = await BranchIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;");
        var beforeInventory = await BranchIntAsync("SELECT COUNT(*) FROM dbo.ItemBranchInventory;");
        var beforeMovements = await BranchIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;");
        await BranchExecuteAsync(
            $"""
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited, IsPublishedOnline, CreatedAt, UpdatedAt)
            VALUES ('{itemId}', N'T6A-ITEM', N'اسم فرعي', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
            VALUES ('{itemId}', '{priceTypeId}', '{branchId}', 9, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
            VALUES ('{branchId}', '{itemId}', 4, 1);
            UPDATE dbo.Users SET LastLoginAt = SYSUTCDATETIME() WHERE Username = N'admin';
            """);
        var loginBefore = await BranchDateAsync("SELECT LastLoginAt FROM dbo.Users WHERE Username = N'admin';");

        try
        {
            var first = await sync.PromoteAsync(settings, CancellationToken.None);
            var second = await sync.PromoteAsync(settings, CancellationToken.None);
            Assert.Equal(first, second);
            await CentralExecuteAsync($"UPDATE dbo.Items SET ItemName = N'اسم مركزي' WHERE ItemID = '{itemId}';");
            Assert.Equal(0, await CentralIntAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN (N'ItemBranchInventory', N'BranchStockBalances', N'StockMovements');"));
            Assert.Equal(1, await CentralIntAsync("SELECT COUNT(*) FROM dbo.SyncEntityOwnership WHERE EntityType = N'Customers' AND IsImplemented = 1;"));
            Assert.Equal(0, await CentralIntAsync("SELECT COUNT(*) FROM dbo.Customers;"));
            Assert.True(await CentralIntAsync("SELECT COUNT(*) FROM dbo.DataImportHistory WHERE ImportType = N'PromoteSharedDataToCentral' AND Status = N'Completed';") >= 1);

            var saved = await store.SaveAsync(settings, SqlEndpointSettings.Linked, CancellationToken.None);
            Assert.Equal(DatabaseRole.Central, saved.DatabaseRole);
            Assert.DoesNotContain("password", saved.ToString(), StringComparison.OrdinalIgnoreCase);

            await sync.BootstrapAsync(settings, CancellationToken.None);
            await sync.BootstrapAsync(settings, CancellationToken.None);
            Assert.Equal("اسم مركزي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(4m, await BranchDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';"));
            Assert.Equal(9m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID = '{branchId}';"));
            Assert.Equal(beforeBalances + 1, await BranchIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(beforeInventory, await BranchIntAsync("SELECT COUNT(*) FROM dbo.ItemBranchInventory;"));
            Assert.Equal(beforeMovements, await BranchIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
            Assert.Equal(0, await BranchIntAsync($"SELECT COUNT(*) FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));
            Assert.Equal(loginBefore, await BranchDateAsync("SELECT LastLoginAt FROM dbo.Users WHERE Username = N'admin';"));

            var payload = "{\"itemName\":\"اسم بعد المزامنة\"}".Replace("'", "''");
            await CentralExecuteAsync(
                $"INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload) VALUES (N'Items', '{itemId}', N'Update', N'{payload}');");
            var pull = await sync.PullAsync(saved.EndpointId, settings, CancellationToken.None);
            var duplicate = await sync.PullAsync(saved.EndpointId, settings, CancellationToken.None);
            Assert.True(pull.CentralReachable);
            Assert.Equal(1, pull.Applied);
            Assert.Equal(0, duplicate.Applied);
            Assert.Equal("اسم بعد المزامنة", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(1, await BranchIntAsync($"SELECT COUNT(*) FROM dbo.SyncInbox WHERE EndpointID = '{saved.EndpointId}';"));
            Assert.Equal(4m, await BranchDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';"));

            var offline = await sync.PullAsync(saved.EndpointId, settings with { Server = "127.0.0.1", Port = 1 }, CancellationToken.None);
            Assert.False(offline.CentralReachable);
            Assert.Equal("اسم بعد المزامنة", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.False(string.IsNullOrWhiteSpace(await BranchTextAsync($"SELECT LastError FROM dbo.SyncEndpoints WHERE EndpointID = '{saved.EndpointId}';")));

            const string secret = "SqlSecret-NotForBrowser";
            using var client = fixture.CreateClient();
            client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
            var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "Admin1234" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var remote = await client.PostAsJsonAsync("/api/central/branch-remote", new
            {
                locationKind = "Remote",
                server = "localhost",
                databaseName = "T6ARemoteDb",
                authenticationType = "Integrated",
                username = "remote-user",
                password = secret,
            });
            var remoteBody = await remote.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, remote.StatusCode);
            Assert.Contains("PendingRemoteSyncSetup", remoteBody);
            Assert.DoesNotContain(secret, remoteBody);
            Assert.False(await DatabaseExistsAsync("T6ARemoteDb"));
            Assert.Equal(DatabaseRole.BranchRemote, (await store.GetAsync(DatabaseRole.BranchRemote, CancellationToken.None))!.DatabaseRole);
        }
        finally
        {
            await BranchExecuteAsync(
                $"""
                DELETE FROM dbo.SyncInbox;
                DELETE FROM dbo.SyncCheckpoints;
                DELETE FROM dbo.SyncOutbox;
                DELETE FROM dbo.SyncEndpoints;
                DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
                DELETE FROM dbo.DataImportHistory WHERE ImportType = N'PromoteSharedDataToCentral';
                """);
            await DropCentralAsync(CentralName);
            await DropCentralAsync(PlainName);
        }
    }

    private static SqlEndpointSettings LocalCentral(string name) =>
        new(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            name,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);

    private static async Task CreatePlainAsync()
    {
        var name = CentralDatabase.Require(PlainName);
        await using var connection = Master();
        await using var command = new SqlCommand($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = Master();
        await using var command = new SqlCommand("SELECT DB_ID(@name);", connection);
        command.Parameters.AddWithValue("@name", name);
        var value = await command.ExecuteScalarAsync();
        return value is not null and not DBNull;
    }

    private static async Task DropCentralAsync(string name)
    {
        var safe = CentralDatabase.Require(name);
        if (string.Equals(safe, CentralDatabase.Name, StringComparison.Ordinal) || string.Equals(safe, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
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

    private static async Task CentralExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CentralIntAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral(CentralName).BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

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

    private static async Task<decimal> BranchDecimalAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static async Task<string> BranchTextAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<DateTime> BranchDateAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (DateTime)(await command.ExecuteScalarAsync() ?? DateTime.MinValue);
    }

    private static async Task<Guid> BranchGuidAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static SqlConnection OpenBranch()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private static SqlConnection Master()
    {
        var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        connection.Open();
        return connection;
    }
}
