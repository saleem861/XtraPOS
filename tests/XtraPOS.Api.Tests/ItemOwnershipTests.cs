using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class ItemOwnershipTests(BranchApiFixture fixture)
{
    private const string CentralName = "XtraPOS_Central_Test_Ownership";
    private const string Secret = "SqlSecret-NotForBrowser";

    [Fact]
    public async Task StandaloneBranch_EditsItsOwnMasterData_AndDeniesUsersWithoutEditMaster()
    {
        var itemId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await CleanupBranchAsync(itemId, userId);
        var branchId = await BranchGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var priceTypeId = await BranchGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        await BranchExecuteAsync(
            $"""
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited, IsPublishedOnline, CreatedAt, UpdatedAt)
            VALUES ('{itemId}', N'OWN-1', N'اسم مستقل', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
            VALUES ('{itemId}', '{priceTypeId}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        try
        {
            using var client = await LoginAsync(fixture.Username, "Admin1234");
            var listed = await client.GetAsync($"/api/items?search=OWN-1&isActive=true");
            var listedBody = await listed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Contains("اسم مستقل", listedBody);
            Assert.DoesNotContain(Secret, listedBody);

            var form = await ReadFormAsync(client, itemId);
            Assert.Equal("Standalone", form.GetProperty("topology").GetString());
            Assert.True(form.GetProperty("canEditMaster").GetBoolean());
            Assert.False(form.GetProperty("masterReadOnly").GetBoolean());

            var renamed = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(form, "اسم بعد التعديل"));
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.Equal("اسم بعد التعديل", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));

            var blockedNumber = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(await ReadFormAsync(client, itemId), "اسم بعد التعديل", "OWN-2"));
            Assert.Equal(HttpStatusCode.BadRequest, blockedNumber.StatusCode);
            Assert.Contains("رقم الصنف ثابت", await blockedNumber.Content.ReadAsStringAsync());
            Assert.Equal("OWN-1", await BranchTextAsync($"SELECT ItemNumber FROM dbo.Items WHERE ItemID = '{itemId}';"));

            var stale = await ReadFormAsync(client, itemId);
            await BranchExecuteAsync($"UPDATE dbo.Items SET ItemName = N'تغيير متزامن' WHERE ItemID = '{itemId}';");
            var conflict = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(stale, "لن يُحفظ"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("تغيير متزامن", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));

            var branchPrice = await client.PutAsJsonAsync($"/api/items/{itemId}/prices/branch", new { priceCode = "Price0", price = 9 });
            Assert.Equal(HttpStatusCode.OK, branchPrice.StatusCode);
            Assert.Equal(10m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID IS NULL;"));
            Assert.Equal(9m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID = '{branchId}';"));

            var state = await client.PutAsJsonAsync($"/api/items/{itemId}/branch-state", new { unitCost = 3.5m, isAvailable = true, isActive = true });
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            Assert.Equal(3.5m, await BranchDecimalAsync($"SELECT UnitCost FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));

            await CreateUserAsync(userId, branchId, "cashier.own", "User");
            using var cashier = await LoginAsync("cashier.own", "User12345");
            var before = await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';");
            var denied = await cashier.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(stale, "ممنوع"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(before, await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(0, await BranchIntAsync(
                """
                SELECT COUNT(*)
                FROM dbo.RolePermissions AS rp
                INNER JOIN dbo.Roles AS r ON r.RoleID = rp.RoleID
                INNER JOIN dbo.Permissions AS p ON p.PermissionID = rp.PermissionID
                WHERE r.RoleCode = N'User'
                  AND p.PermissionCode IN (N'Items.EditMaster', N'Items.EditBranchState', N'Items.EditBranchPrice');
                """));
        }
        finally
        {
            await CleanupBranchAsync(itemId, userId);
        }
    }

    [Fact]
    public async Task CentralLinkedBranch_RoutesMasterEditsAndPreservesBranchState()
    {
        var itemId = Guid.NewGuid();
        var innerId = Guid.NewGuid();
        await CleanupBranchAsync(itemId, Guid.Empty);
        await CleanupBranchAsync(innerId, Guid.Empty);
        await DropCentralAsync();
        var branchId = await BranchGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var priceTypeId = await BranchGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var movementTypeId = await BranchGuidAsync("SELECT TOP (1) MovementTypeID FROM dbo.StockMovementTypes;");
        await BranchExecuteAsync(
            $"""
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited, IsPublishedOnline, CreatedAt, UpdatedAt)
            VALUES
                ('{itemId}', N'OWN-C', N'اسم فرعي', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                ('{innerId}', N'OWN-U', N'وحدة داخلية', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
            VALUES
                ('{itemId}', '{priceTypeId}', NULL, 25, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                ('{itemId}', '{priceTypeId}', '{branchId}', 23, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, UnitCost, DailySalesRate, IsAvailable, IsActive)
            VALUES ('{branchId}', '{itemId}', 4.5, 1.25, 1, 1);
            """);

        var settings = LocalCentral();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var databases = scope.ServiceProvider.GetRequiredService<CentralDatabaseService>();
        var sync = scope.ServiceProvider.GetRequiredService<SharedDataSync>();
        var store = scope.ServiceProvider.GetRequiredService<SyncEndpointStore>();
        await databases.CreateIfMissingAsync(settings, CancellationToken.None);
        await sync.PromoteAsync(settings, CancellationToken.None);
        var saved = await store.SaveAsync(settings, SqlEndpointSettings.Linked, CancellationToken.None);

        try
        {
            using var client = await LoginAsync(fixture.Username, "Admin1234");
            await BranchExecuteAsync($"UPDATE dbo.SyncEndpoints SET Port = 1, Username = N'{Secret}' WHERE EndpointID = '{saved.EndpointId}';");
            var offlineList = await client.GetAsync("/api/items?search=OWN-C&isActive=all");
            var offlineBody = await offlineList.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, offlineList.StatusCode);
            Assert.Contains("اسم فرعي", offlineBody);
            Assert.DoesNotContain(Secret, offlineBody);

            var offlineForm = await client.GetAsync($"/api/items/{itemId}/edit-form");
            var offlineFormBody = await offlineForm.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, offlineForm.StatusCode);
            Assert.Contains("تعذر الاتصال بالإدارة المركزية", offlineFormBody);
            Assert.DoesNotContain(Secret, offlineFormBody);
            Assert.DoesNotContain("Password", offlineFormBody);
            Assert.DoesNotContain("Server=", offlineFormBody);

            var offlineSave = await client.PutAsJsonAsync($"/api/items/{itemId}", new { itemName = "لن يُكتب محليًا", isActive = true, rowVersion = "AAAAAAA=" });
            var offlineSaveBody = await offlineSave.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, offlineSave.StatusCode);
            Assert.Contains("تعذر الاتصال بالإدارة المركزية", offlineSaveBody);
            Assert.Equal("اسم فرعي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal("اسم فرعي", await CentralTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));

            var offlineState = await client.PutAsJsonAsync($"/api/items/{itemId}/branch-state", new { unitCost = 4.5m, isAvailable = false, isActive = true });
            Assert.Equal(HttpStatusCode.OK, offlineState.StatusCode);
            Assert.False(await BranchBoolAsync($"SELECT IsAvailable FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));

            await BranchExecuteAsync($"UPDATE dbo.SyncEndpoints SET Port = NULL, Username = NULL WHERE EndpointID = '{saved.EndpointId}';");
            var form = await ReadFormAsync(client, itemId);
            Assert.Equal("CentralLinked", form.GetProperty("topology").GetString());
            Assert.True(form.GetProperty("canEditMaster").GetBoolean());
            var originalVersion = form.GetProperty("rowVersion").GetString();

            var number = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(form, "اسم فرعي", "OWN-CHANGED"));
            Assert.Equal(HttpStatusCode.BadRequest, number.StatusCode);
            Assert.Equal("OWN-C", await CentralTextAsync($"SELECT ItemNumber FROM dbo.Items WHERE ItemID = '{itemId}';"));

            var renamed = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(form, "اسم مركزي"));
            var renamedBody = await renamed.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.DoesNotContain(Secret, renamedBody);
            Assert.Equal("اسم مركزي", await CentralTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal("اسم مركزي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(4.5m, await BranchDecimalAsync($"SELECT UnitCost FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));
            Assert.Equal(1.25m, await BranchDecimalAsync($"SELECT DailySalesRate FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));
            Assert.Equal(23m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID = '{branchId}';"));
            var adminId = await BranchGuidAsync("SELECT UserID FROM dbo.Users WHERE Username = N'admin';");
            Assert.Equal(adminId, await CentralGuidAsync("SELECT TOP (1) ChangedByUserID FROM dbo.SyncChangeLog WHERE EntityType = N'Items' AND EntityId = '" + itemId + "' ORDER BY ChangeSequence DESC;"));
            Assert.True(await CentralLongAsync("SELECT TOP (1) ChangeSequence FROM dbo.SyncChangeLog WHERE EntityId = '" + itemId + "' ORDER BY ChangeSequence DESC;") > 0);

            var conflict = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(form, "كتابة قديمة"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("اسم مركزي", await CentralTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal("اسم مركزي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(originalVersion, form.GetProperty("rowVersion").GetString());

            var priceForm = await ReadFormAsync(client, itemId);
            string? priceVersion = null;
            foreach (var price in priceForm.GetProperty("prices").EnumerateArray())
            {
                if (price.GetProperty("priceCode").GetString() == "Price0")
                {
                    priceVersion = price.GetProperty("defaultRowVersion").GetString();
                }
            }

            var priceSave = await client.PutAsJsonAsync($"/api/items/{itemId}/prices/default", new { priceCode = "Price0", price = 27, rowVersion = priceVersion });
            Assert.Equal(HttpStatusCode.OK, priceSave.StatusCode);
            Assert.Equal(27m, await CentralDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}';"));
            Assert.Equal(27m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID IS NULL;"));
            Assert.Equal(23m, await BranchDecimalAsync($"SELECT Price FROM dbo.ItemPrices WHERE ItemID = '{itemId}' AND BranchID = '{branchId}';"));

            var unitForm = await ReadFormAsync(client, itemId);
            var units = await client.PutAsJsonAsync($"/api/items/{itemId}/units", new
            {
                rowVersion = unitForm.GetProperty("rowVersion").GetString(),
                units = new[]
                {
                    new { unitsPerPackage = 6m, internalUnitItemId = innerId, internalUnitItemNumber = "OWN-U", internalUnitItemName = "وحدة داخلية", isActive = true },
                },
            });
            Assert.Equal(HttpStatusCode.OK, units.StatusCode);
            Assert.Equal(6m, await CentralDecimalAsync($"SELECT UnitsPerPackage FROM dbo.ItemUnits WHERE ItemID = '{itemId}';"));
            Assert.Equal(6m, await BranchDecimalAsync($"SELECT UnitsPerPackage FROM dbo.ItemUnits WHERE ItemID = '{itemId}';"));

            await BranchExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', 8, 1);
                INSERT INTO dbo.StockMovements (BranchID, ItemID, MovementTypeID, QuantityDelta, OccurredAt)
                VALUES ('{branchId}', '{itemId}', '{movementTypeId}', 1, SYSUTCDATETIME());
                """);
            var afterUnits = await ReadFormAsync(client, itemId);
            var dangerous = await client.PutAsJsonAsync($"/api/items/{itemId}/units", new
            {
                rowVersion = afterUnits.GetProperty("rowVersion").GetString(),
                units = new[]
                {
                    new { unitsPerPackage = 12m, internalUnitItemId = innerId, internalUnitItemNumber = "OWN-U", internalUnitItemName = "وحدة داخلية", isActive = true },
                },
            });
            Assert.Equal(HttpStatusCode.Conflict, dangerous.StatusCode);
            Assert.Contains("لن يتم تعديل سجل المخزون", await dangerous.Content.ReadAsStringAsync());
            Assert.Equal(6m, await CentralDecimalAsync($"SELECT UnitsPerPackage FROM dbo.ItemUnits WHERE ItemID = '{itemId}';"));
            Assert.Equal(1, await BranchIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE ItemID = '{itemId}';"));
            Assert.Equal(8m, await BranchDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';"));

            var firstPull = await sync.PullAsync(saved.EndpointId, settings, CancellationToken.None);
            var secondPull = await sync.PullAsync(saved.EndpointId, settings, CancellationToken.None);
            Assert.Equal(0, firstPull.Applied);
            Assert.Equal(0, secondPull.Applied);
            Assert.Equal("اسم مركزي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal(4.5m, await BranchDecimalAsync($"SELECT UnitCost FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';"));
            Assert.Equal(1, await BranchIntAsync($"SELECT COUNT(*) FROM dbo.ItemUnits WHERE ItemID = '{itemId}';"));

            await CentralExecuteAsync(
                """
                DELETE rp
                FROM dbo.RolePermissions AS rp
                INNER JOIN dbo.Permissions AS p ON p.PermissionID = rp.PermissionID
                WHERE p.PermissionCode IN (N'Items.EditMaster', N'System.Administer');
                """);
            var current = await ReadFormAsync(client, itemId);
            var bypass = await client.PutAsJsonAsync($"/api/items/{itemId}", MasterBody(current, "تجاوز مرفوض"));
            Assert.Equal(HttpStatusCode.Forbidden, bypass.StatusCode);
            Assert.Equal("اسم مركزي", await CentralTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
            Assert.Equal("اسم مركزي", await BranchTextAsync($"SELECT ItemName FROM dbo.Items WHERE ItemID = '{itemId}';"));
        }
        finally
        {
            await BranchExecuteAsync(
                $"""
                DELETE FROM dbo.SyncInbox;
                DELETE FROM dbo.SyncCheckpoints;
                DELETE FROM dbo.SyncOutbox;
                DELETE FROM dbo.SyncEndpoints;
                DELETE FROM dbo.StockMovements WHERE ItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.BranchStockBalances WHERE ItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.ItemBranchInventory WHERE ItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.ItemUnits WHERE ItemID IN ('{itemId}', '{innerId}') OR InternalUnitItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.ItemPrices WHERE ItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.Items WHERE ItemID IN ('{itemId}', '{innerId}');
                DELETE FROM dbo.DataImportHistory WHERE ImportType = N'PromoteSharedDataToCentral';
                """);
            await DropCentralAsync();
        }
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> ReadFormAsync(HttpClient client, Guid itemId)
    {
        var response = await client.GetAsync($"/api/items/{itemId}/edit-form");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static object MasterBody(JsonElement form, string name, string? itemNumber = null) => new
    {
        itemNumber,
        itemName = name,
        itemType = Text(form, "itemType"),
        manufacturer = Text(form, "manufacturer"),
        color = Text(form, "color"),
        taste = Text(form, "taste"),
        size = Text(form, "size"),
        packing = Text(form, "packing"),
        classification = Text(form, "classification"),
        countryOfOrigin = Text(form, "countryOfOrigin"),
        productType = Text(form, "productType"),
        parentItemId = form.TryGetProperty("parentItemId", out var parent) && parent.ValueKind != JsonValueKind.Null ? parent.GetString() : null,
        isActive = form.GetProperty("isActive").GetBoolean(),
        isProhibited = form.GetProperty("isProhibited").GetBoolean(),
        isPublishedOnline = form.GetProperty("isPublishedOnline").GetBoolean(),
        rowVersion = form.GetProperty("rowVersion").GetString(),
    };

    private static string? Text(JsonElement form, string name) =>
        form.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private async Task CreateUserAsync(Guid userId, Guid branchId, string username, string roleCode)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var hash = passwords.Hash(new Data.AppUser { UserId = userId, Username = username }, "User12345").Replace("'", "''");
        var roleId = await BranchGuidAsync($"SELECT RoleID FROM dbo.Roles WHERE RoleCode = N'{roleCode}';");
        await BranchExecuteAsync(
            $"""
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES ('{userId}', N'{username}', N'{username.ToUpperInvariant()}', N'مستخدم التجربة', N'{hash}', 1, 0, SYSUTCDATETIME());
            INSERT INTO dbo.UserRoles (UserID, RoleID) VALUES ('{userId}', '{roleId}');
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive) VALUES ('{userId}', '{branchId}', 1);
            """);
    }

    private static async Task CleanupBranchAsync(Guid itemId, Guid userId)
    {
        await BranchExecuteAsync(
            $"""
            DELETE FROM dbo.SyncInbox;
            DELETE FROM dbo.SyncCheckpoints;
            DELETE FROM dbo.SyncOutbox;
            DELETE FROM dbo.SyncEndpoints;
            DELETE FROM dbo.StockMovements WHERE ItemID = '{itemId}';
            DELETE FROM dbo.BranchStockBalances WHERE ItemID = '{itemId}';
            DELETE FROM dbo.ItemBranchInventory WHERE ItemID = '{itemId}';
            DELETE FROM dbo.ItemUnits WHERE ItemID = '{itemId}' OR InternalUnitItemID = '{itemId}';
            DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
            DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
            DELETE FROM dbo.UserLoginAudit WHERE UserID = '{userId}' OR UserID IN (SELECT UserID FROM dbo.Users WHERE Username = N'cashier.own');
            DELETE FROM dbo.UserBranchAccess WHERE UserID = '{userId}' OR UserID IN (SELECT UserID FROM dbo.Users WHERE Username = N'cashier.own');
            DELETE FROM dbo.UserRoles WHERE UserID = '{userId}' OR UserID IN (SELECT UserID FROM dbo.Users WHERE Username = N'cashier.own');
            DELETE FROM dbo.Users WHERE UserID = '{userId}' OR Username = N'cashier.own';
            """);
    }

    private static SqlEndpointSettings LocalCentral() =>
        new(DatabaseRole.Central, SqlEndpointSettings.Local, "localhost", null, null, CentralName, SqlEndpointSettings.Integrated, null, null, true, true);

    private static async Task DropCentralAsync()
    {
        var safe = CentralDatabase.Require(CentralName);
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
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CentralTextAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<decimal> CentralDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static async Task<Guid> CentralGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<long> CentralLongAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
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

    private static async Task<bool> BranchBoolAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<string> BranchTextAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
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
