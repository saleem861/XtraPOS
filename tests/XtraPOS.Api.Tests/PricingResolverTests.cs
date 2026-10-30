using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class PricingResolverTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task FixedPrice_FollowsCustomerTypeThenBranchOverrideWithoutFallingThrough()
    {
        var branchId = await QueryGuidAsync("SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BranchID';");
        var businessId = await QueryGuidAsync("SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';");
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var price1 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price1';");
        var dealer = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Dealer';");
        var itemId = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var price1Customer = Guid.NewGuid();
        var dealerCustomer = Guid.NewGuid();
        var nullTypeCustomer = Guid.NewGuid();
        var inactiveCustomer = Guid.NewGuid();
        var discountedCustomer = Guid.NewGuid();
        var classifiedCustomer = Guid.NewGuid();
        var classificationId = Guid.NewGuid();
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'PX-FIXED', N'صنف ثابت');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (NEWID(), '{itemId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{itemId}', '{price1}', NULL, 8, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{itemId}', '{dealer}', NULL, 6, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES ('{otherBranch}', '{businessId}', N'97', N'فرع آخر', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{price0}', '{otherBranch}', 99, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerClassifications
                    (CustomerClassificationID, BusinessID, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive)
                VALUES ('{classificationId}', '{businessId}', N'تصنيف', '{dealer}', 25, 0, 1);
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, Name, CustomerClassificationID, PriceTypeID, VariableValueDiscountPercent, IsActive)
                VALUES
                    ('{price1Customer}', '{businessId}', N'سعر 1', NULL, '{price1}', 0, 1),
                    ('{dealerCustomer}', '{businessId}', N'جملة', NULL, '{dealer}', 0, 1),
                    ('{nullTypeCustomer}', '{businessId}', N'بدون نوع', NULL, NULL, 0, 1),
                    ('{inactiveCustomer}', '{businessId}', N'موقوف', NULL, '{price1}', 0, 0),
                    ('{discountedCustomer}', '{businessId}', N'خصم', NULL, '{price1}', 15, 1),
                    ('{classifiedCustomer}', '{businessId}', N'تصنيف مختلف', '{classificationId}', '{price1}', 20, 1);
                """);
            var resolver = Resolver();

            var anonymous = await resolver.ResolveFixedPriceAsync(itemId, null, CancellationToken.None);
            Assert.Equal("Price0", anonymous.PriceTypeCode);
            Assert.Equal(10m, anonymous.ResolvedPrice);
            Assert.Equal(PriceSources.SharedDefault, anonymous.PriceSource);
            Assert.Equal(branchId, anonymous.BranchId);
            Assert.Equal(PricingResolver.FixedMode, anonymous.PricingMode);

            var price1Result = await resolver.ResolveFixedPriceAsync(itemId, price1Customer, CancellationToken.None);
            Assert.Equal("Price1", price1Result.PriceTypeCode);
            Assert.Equal(8m, price1Result.ResolvedPrice);

            var dealerResult = await resolver.ResolveFixedPriceAsync(itemId, dealerCustomer, CancellationToken.None);
            Assert.Equal("Dealer", dealerResult.PriceTypeCode);
            Assert.Equal(6m, dealerResult.ResolvedPrice);

            Assert.Equal("Price0", (await resolver.ResolveFixedPriceAsync(itemId, nullTypeCustomer, CancellationToken.None)).PriceTypeCode);
            var classified = await resolver.ResolveFixedPriceAsync(itemId, classifiedCustomer, CancellationToken.None);
            Assert.Equal("Price1", classified.PriceTypeCode);
            Assert.Equal(8m, classified.ResolvedPrice);
            var discounted = await resolver.ResolveFixedPriceAsync(itemId, discountedCustomer, CancellationToken.None);
            Assert.Equal(10m, discounted.BasePrice);
            Assert.Equal(8m, discounted.ResolvedPrice);

            await ExecuteAsync($"""
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{price0}', '{branchId}', 7.5, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var overridden = await resolver.ResolveFixedPriceAsync(itemId, null, CancellationToken.None);
            Assert.Equal(7.5m, overridden.ResolvedPrice);
            Assert.Equal(PriceSources.BranchOverride, overridden.PriceSource);

            await AssertCodeAsync(() => resolver.ResolveFixedPriceAsync(itemId, inactiveCustomer, CancellationToken.None), PricingCodes.CustomerInactive);
            await AssertCodeAsync(() => resolver.ResolveFixedPriceAsync(itemId, Guid.NewGuid(), CancellationToken.None), PricingCodes.CustomerNotFound);
            await AssertCodeAsync(() => resolver.ResolveFixedPriceAsync(Guid.NewGuid(), null, CancellationToken.None), PricingCodes.ItemNotFound);

            var zeroItem = Guid.NewGuid();
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{zeroItem}', N'PX-ZERO', N'سعر صفر');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{zeroItem}', '{price0}', NULL, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            Assert.Equal(0m, (await resolver.ResolveFixedPriceAsync(zeroItem, null, CancellationToken.None)).ResolvedPrice);

            var missing = Guid.NewGuid();
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{missing}', N'PX-MISS', N'بدون السعر المطلوب');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{missing}', '{price0}', NULL, 4, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            await AssertCodeAsync(() => resolver.ResolveFixedPriceAsync(missing, price1Customer, CancellationToken.None), PricingCodes.PriceNotFound);

            var before = await FingerprintAsync();
            await resolver.ResolveFixedPriceAsync(itemId, null, CancellationToken.None);
            await resolver.ResolveFixedPriceAsync(itemId, price1Customer, CancellationToken.None);
            Assert.Equal(before, await FingerprintAsync());
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.Customers WHERE Name IN (N'سعر 1', N'جملة', N'بدون نوع', N'موقوف', N'خصم', N'تصنيف مختلف');
                DELETE FROM dbo.CustomerClassifications WHERE CustomerClassificationID = '{classificationId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID IN ('{itemId}', (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'PX-ZERO'), (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'PX-MISS'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'PX-FIXED', N'PX-ZERO', N'PX-MISS');
                DELETE FROM dbo.Branches WHERE BranchID = '{otherBranch}';
                """);
        }
    }

    private PricingResolver Resolver()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PricingResolver>();
    }

    private static async Task AssertCodeAsync(Func<Task> action, string code)
    {
        var error = await Assert.ThrowsAsync<PricingException>(action);
        Assert.Equal(code, error.Code);
    }

    private async Task<string> FingerprintAsync() =>
        await QueryTextAsync("""
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.Items), '|',
                (SELECT COUNT(*) FROM dbo.ItemPrices), '|',
                (SELECT COUNT(*) FROM dbo.Customers), '|',
                (SELECT COUNT(*) FROM dbo.BranchStockBalances), '|',
                (SELECT COUNT(*) FROM dbo.StockMovements), '|',
                (SELECT COUNT(*) FROM dbo.ItemPriceHistory), '|',
                (SELECT COUNT(*) FROM dbo.CustomerAdministrationAudit));
            """);

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing guid."));
    }

    private static async Task<string> QueryTextAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }
}
