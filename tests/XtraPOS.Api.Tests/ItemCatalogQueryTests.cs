using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class ItemCatalogQueryTests(BranchApiFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Search_FiltersActivityAndPagesArabicNames()
    {
        await CleanupAsync();
        var activeId = await InsertItemAsync("T5UI-A", "قهوة برازيلية", true, false);
        var prohibitedId = await InsertItemAsync("T5UI-B", "شاي ممنوع", true, true);
        await InsertItemAsync("T5UI-C", "صنف متوقف", false, false);
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();

            var active = await catalog.SearchPageAsync("T5UI-", ItemActivityFilter.Active, 1, 1, ItemSort.Number, CancellationToken.None);
            Assert.Equal(2, active.TotalCount);
            Assert.Equal(1, active.Page);
            Assert.Equal(1, active.PageSize);
            Assert.Equal(["T5UI-A"], active.Items.Select(item => item.ItemNumber).ToArray());

            var second = await catalog.SearchPageAsync("T5UI-", ItemActivityFilter.Active, 2, 1, ItemSort.Number, CancellationToken.None);
            Assert.Equal(prohibitedId, second.Items.Single().ItemId);
            Assert.True(second.Items.Single().IsProhibited);
            Assert.True(second.Items.Single().IsActive);

            var inactive = await catalog.SearchPageAsync("T5UI-", ItemActivityFilter.Inactive, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal(["T5UI-C"], inactive.Items.Select(item => item.ItemNumber).ToArray());

            var all = await catalog.SearchPageAsync("T5UI-", ItemActivityFilter.All, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal(3, all.TotalCount);

            var arabic = await catalog.SearchPageAsync("برازيلية", ItemActivityFilter.Active, 1, 50, ItemSort.Name, CancellationToken.None);
            Assert.Equal(activeId, arabic.Items.Single().ItemId);

            var byNumber = await catalog.SearchPageAsync("T5UI-B", ItemActivityFilter.All, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal("T5UI-B", byNumber.Items.Single().ItemNumber);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task CatalogList_ReturnsKnownItemsWhenInventoryTablesAreEmpty()
    {
        await ExecuteAsync("DELETE FROM dbo.Items WHERE ItemNumber IN (N'1087', N'1360', N'50', N'150');");
        await InsertItemAsync("1087", "نكهة لومي فراولة وبطيخ 10 ml علبة", true, false);
        await InsertItemAsync("1360", "مولاس", true, false);
        await InsertItemAsync("50", "50", false, false);
        try
        {
            Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.ItemBranchInventory;"));
            Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var activeCount = await ScalarAsync("SELECT COUNT(*) FROM dbo.Items WHERE IsActive = 1;");
            var page = await catalog.SearchPageAsync(null, ItemActivityFilter.Active, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal(activeCount, page.TotalCount);
            Assert.NotEmpty(page.Items);

            var byNumber = await catalog.SearchPageAsync("1087", ItemActivityFilter.Active, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal("1087", byNumber.Items.Single().ItemNumber);

            var lime = await catalog.SearchPageAsync("  لومي  ", ItemActivityFilter.Active, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Contains(lime.Items, item => item.ItemNumber == "1087");

            var strawberry = await catalog.SearchPageAsync("فراولة", ItemActivityFilter.Active, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Contains(strawberry.Items, item => item.ItemNumber == "1087");

            var molasses = await catalog.SearchPageAsync("1360", ItemActivityFilter.Active, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal("1360", molasses.Items.Single().ItemNumber);

            await InsertItemAsync("150", "صنف 150", false, false);
            var inactive = await catalog.SearchPageAsync("50", ItemActivityFilter.Inactive, 1, 50, ItemSort.Number, CancellationToken.None);
            Assert.Equal("50", inactive.Items[0].ItemNumber);
            Assert.Contains(inactive.Items, item => item.ItemNumber == "150");
            Assert.False(inactive.Items[0].IsActive);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM dbo.Items WHERE ItemNumber IN (N'1087', N'1360', N'50', N'150');");
        }
    }

    [Fact]
    public async Task InactiveTechnicalItem_IsAbsentFromTheActiveQuery()
    {
        await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemNumber = N'{ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber}';");
        await InsertItemAsync(ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber, "XTRAPOS delete safety test", false, false);
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();
            var active = await catalog.SearchPageAsync(
                ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber,
                ItemActivityFilter.Active,
                1,
                50,
                ItemSort.Number,
                CancellationToken.None);
            var inactive = await catalog.SearchPageAsync(
                ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber,
                ItemActivityFilter.Inactive,
                1,
                50,
                ItemSort.Number,
                CancellationToken.None);
            Assert.Empty(active.Items);
            Assert.Equal(ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber, inactive.Items.Single().ItemNumber);
            Assert.False(inactive.Items.Single().IsActive);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.Items WHERE ItemNumber = N'{ConfirmedTechnicalItemCleanup.DeleteSafetyItemNumber}';");
        }
    }

    [Fact]
    public async Task Prices_KeepZeroDistinctFromMissing_AndDerivedInventoryIsNotZeroByDefault()
    {
        await CleanupAsync();
        var branchId = await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");
        var parentId = await InsertItemAsync("T5UI-PARENT", "صنف أب", true, false);
        var ownerId = await InsertItemAsync("T5UI-OWNER", "وحدة الأساس", true, false);
        var packageId = await InsertDetailItemAsync(parentId);
        var dealerId = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Dealer';");
        try
        {
            await ExecuteAsync(
                $"""
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, Price)
                VALUES ('{packageId}', '{dealerId}', 0);
                INSERT INTO dbo.ItemUnits (ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive)
                VALUES ('{packageId}', 4, '{ownerId}', N'T5UI-OWNER', N'اسم الوحدة المسجل', 0, 1);
                """);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ItemCatalogService>();

            var detail = await catalog.GetItemDetailAsync(packageId, CancellationToken.None);
            Assert.Equal("T5UI-PARENT", detail!.ParentItemNumber);
            Assert.Equal("صنف أب", detail.ParentItemName);
            Assert.Equal("أحمر", detail.Color);

            var prices = await catalog.GetDefaultPriceViewsAsync(packageId, branchId, CancellationToken.None);
            Assert.NotNull(prices);
            Assert.Equal(0m, prices.Single(price => price.PriceCode == "Dealer").Price);
            Assert.Null(prices.Single(price => price.PriceCode == "Retail").Price);

            var units = await catalog.GetUnitViewsAsync(packageId, CancellationToken.None);
            Assert.False(units!.OwnsPhysicalStock);
            Assert.Equal("T5UI-OWNER", units.StockOwnerItemNumber);
            Assert.Equal(4m, units.BaseQuantityFactor);
            Assert.Equal("اسم الوحدة المسجل", units.Units.Single().RecordedUnitName);
            Assert.Equal("وحدة الأساس", units.Units.Single().CurrentItemName);

            var pending = await catalog.GetInventoryViewAsync(branchId, packageId, CancellationToken.None);
            Assert.Equal(ItemQuantityState.BranchPending, pending!.QuantityState);
            Assert.Null(pending.OnHandQuantity);
            Assert.True(pending.IsDerived);

            await ExecuteAsync(
                $"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{ownerId}', NULL, 0);
                """);
            var unknown = await catalog.GetInventoryViewAsync(branchId, packageId, CancellationToken.None);
            Assert.Equal(ItemQuantityState.NotCounted, unknown!.QuantityState);
            Assert.Null(unknown.OnHandQuantity);

            await ExecuteAsync(
                $"""
                UPDATE dbo.BranchStockBalances
                SET OnHandQuantity = 0, IsQuantityInitialized = 1
                WHERE ItemID = '{ownerId}';
                """);
            var knownZero = await catalog.GetInventoryViewAsync(branchId, ownerId, CancellationToken.None);
            Assert.Equal(ItemQuantityState.Known, knownZero!.QuantityState);
            Assert.Equal(0m, knownZero.OnHandQuantity);
            Assert.False(knownZero.IsDerived);

            await ExecuteAsync(
                $"""
                UPDATE dbo.BranchStockBalances
                SET OnHandQuantity = 8
                WHERE ItemID = '{ownerId}';
                """);
            var derived = await catalog.GetInventoryViewAsync(branchId, packageId, CancellationToken.None);
            Assert.Equal(ItemQuantityState.Known, derived!.QuantityState);
            Assert.Equal(2m, derived.OnHandQuantity);

            var movements = await catalog.GetMovementViewsAsync(branchId, packageId, CancellationToken.None);
            Assert.Empty(movements!);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task ItemsApi_RequiresAnAuthenticatedViewer()
    {
        await ExecuteAsync("UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndAt = NULL;");
        using var anonymous = fixture.CreateClient();
        var denied = await anonymous.GetAsync("/api/items");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "Admin1234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var page = await client.GetFromJsonAsync<ItemPageResult>("/api/items?search=T5UI-missing&pageSize=10", Json);
        Assert.NotNull(page);
        Assert.Equal(50, page.PageSize);
        Assert.Equal(0, page.TotalCount);
    }

    private static async Task<Guid> InsertItemAsync(string number, string name, bool active, bool prohibited)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Items (ItemNumber, ItemName, IsActive, IsProhibited)
            OUTPUT INSERTED.ItemID
            VALUES (@number, @name, @active, @prohibited);
            """,
            connection);
        command.Parameters.AddWithValue("@number", number);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@active", active);
        command.Parameters.AddWithValue("@prohibited", prohibited);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<Guid> InsertDetailItemAsync(Guid parentId)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Items
                (ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification, CountryOfOrigin, ProductType, ParentItemID, IsActive)
            OUTPUT INSERTED.ItemID
            VALUES
                (N'T5UI-PACK', N'عبوة', N'نوع', N'الشركة', N'أحمر', N'حلو', N'كبير', N'علبة', N'تصنيف', N'لبنان', N'Good', @parent, 1);
            """,
            connection);
        command.Parameters.AddWithValue("@parent", parentId);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<int> ScalarAsync(string sql)
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

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync()
    {
        await ExecuteAsync(
            """
            DELETE movements
            FROM dbo.StockMovements AS movements
            INNER JOIN dbo.Items AS items ON items.ItemID = movements.ItemID
            WHERE items.ItemNumber LIKE N'T5UI-%';

            DELETE prices
            FROM dbo.ItemPrices AS prices
            INNER JOIN dbo.Items AS items ON items.ItemID = prices.ItemID
            WHERE items.ItemNumber LIKE N'T5UI-%';

            DELETE balances
            FROM dbo.BranchStockBalances AS balances
            INNER JOIN dbo.Items AS items ON items.ItemID = balances.ItemID
            WHERE items.ItemNumber LIKE N'T5UI-%';

            DELETE inventory
            FROM dbo.ItemBranchInventory AS inventory
            INNER JOIN dbo.Items AS items ON items.ItemID = inventory.ItemID
            WHERE items.ItemNumber LIKE N'T5UI-%';

            DELETE units
            FROM dbo.ItemUnits AS units
            WHERE units.ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber LIKE N'T5UI-%')
               OR units.InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber LIKE N'T5UI-%');

            DELETE FROM dbo.Items WHERE ItemNumber LIKE N'T5UI-%' AND ParentItemID IS NOT NULL;
            DELETE FROM dbo.Items WHERE ItemNumber LIKE N'T5UI-%';
            """);
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }
}
