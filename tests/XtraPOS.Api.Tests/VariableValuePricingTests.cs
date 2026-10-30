using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class VariableValueRoundingTests
{
    [Theory]
    [InlineData("15.60", "15")]
    [InlineData("20", "20")]
    [InlineData("10.25", "10")]
    [InlineData("10.50", "10")]
    [InlineData("10.75", "11")]
    [InlineData("11.25", "11")]
    [InlineData("11.75", "12")]
    public void LegacyRounding_UsesToEvenMidpointThenFloor(string raw, string expected)
    {
        var rounded = VariableValuePricing.ApplyLegacyVariableValueRounding(decimal.Parse(raw));
        Assert.Equal(decimal.Parse(expected), rounded);
    }
}

[Collection("Branch")]
public sealed class VariableValuePricingTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task VariableValue_PricesByScaleAndDiscountWithoutChangingWeightOrStock()
    {
        var branchId = await QueryGuidAsync("SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BranchID';");
        var businessId = await QueryGuidAsync("SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';");
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var dealer = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Dealer';");
        var referenceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var discounted = Guid.NewGuid();
        var dealerCustomer = Guid.NewGuid();
        var classified = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var classificationId = Guid.NewGuid();
        var number = VariableValuePricing.ReferenceItemNumber;
        try
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.BranchStockBalances WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{ownerId}', N'1360', N'معسل فرنسي وقية'),
                    ('{referenceId}', N'{number}', N'معسل فرنسي 1 كيلو');
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{referenceId}', 4, '{ownerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (NEWID(), '{referenceId}', '{price0}', NULL, 180, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{referenceId}', '{dealer}', NULL, 100, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES ('{otherBranch}', '{businessId}', N'96', N'فرع آخر', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', '{otherBranch}', 50, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerClassifications
                    (CustomerClassificationID, BusinessID, Name, DefaultVariableValueDiscountPercent, IsDefault, IsActive)
                VALUES ('{classificationId}', '{businessId}', N'تصنيف قيمة', 50, 0, 1);
                INSERT INTO dbo.Customers
                    (CustomerID, BusinessID, Name, CustomerClassificationID, PriceTypeID, VariableValueDiscountPercent, IsActive)
                VALUES
                    ('{discounted}', '{businessId}', N'خصم 22', NULL, NULL, 22, 1),
                    ('{dealerCustomer}', '{businessId}', N'جملة متغيرة', NULL, '{dealer}', 22, 1),
                    ('{classified}', '{businessId}', N'تصنيف متغير', '{classificationId}', NULL, 22, 1),
                    ('{inactive}', '{businessId}', N'موقوف متغير', NULL, NULL, 22, 0);
                """);
            var resolver = Resolver();
            var plain = await resolver.ResolveAsync(20m, null, CancellationToken.None);
            Assert.Equal(20m / 180m, plain.WeightKg);
            Assert.Equal(20m / 180m * 1000m, plain.WeightGrams);
            Assert.Equal(20m, plain.FinalSellingPrice);
            Assert.Equal(4m, plain.StockOwnerConversionFactor);
            Assert.Equal(20m / 180m * 4m, plain.ProjectedStockOwnerQuantity);
            Assert.Equal(number, plain.ReferenceItemNumber);
            Assert.Equal("1360", plain.StockOwnerItemNumber);
            Assert.Equal(PriceSources.SharedDefault, plain.ReferencePriceSource);
            Assert.Equal(180m, plain.ReferenceBasePrice);
            Assert.Null(plain.ProjectedCost);
            Assert.Equal(VariableValuePricing.CostUnavailable, plain.CostSource);
            Assert.Equal(branchId, plain.BranchId);

            var reduced = await resolver.ResolveAsync(20m, discounted, CancellationToken.None);
            Assert.Equal(plain.WeightKg, reduced.WeightKg);
            Assert.Equal(plain.ProjectedStockOwnerQuantity, reduced.ProjectedStockOwnerQuantity);
            Assert.Equal(15.60m, reduced.RawDiscountedValue);
            Assert.Equal(15m, reduced.FinalSellingPrice);
            Assert.Equal(VariableValuePricing.RoundingPolicyName, reduced.RoundingPolicy);

            var dealerResult = await resolver.ResolveAsync(20m, dealerCustomer, CancellationToken.None);
            Assert.Equal(plain.WeightKg, dealerResult.WeightKg);
            Assert.Equal(180m, dealerResult.ReferenceBasePrice);
            var classifiedResult = await resolver.ResolveAsync(20m, classified, CancellationToken.None);
            Assert.Equal(22m, classifiedResult.VariableValueDiscountPercent);
            Assert.Equal(15m, classifiedResult.FinalSellingPrice);

            await ExecuteAsync($"""
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', '{branchId}', 90, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var overridden = await resolver.ResolveAsync(20m, null, CancellationToken.None);
            Assert.Equal(90m, overridden.ReferenceBasePrice);
            Assert.Equal(PriceSources.BranchOverride, overridden.ReferencePriceSource);
            Assert.Equal(20m / 90m, overridden.WeightKg);
            await ExecuteAsync($"DELETE FROM dbo.ItemPrices WHERE ItemID = '{referenceId}' AND BranchID = '{branchId}';");

            await ExecuteAsync($"DELETE FROM dbo.ItemPrices WHERE ItemID = '{referenceId}' AND PriceTypeID = '{price0}';");
            await AssertCodeAsync(() => resolver.ResolveAsync(20m, null, CancellationToken.None), VariableValueCodes.BasePriceNotFound);
            await ExecuteAsync($"""
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', NULL, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            await AssertCodeAsync(() => resolver.ResolveAsync(20m, null, CancellationToken.None), VariableValueCodes.BasePriceInvalid);
            await ExecuteAsync($"UPDATE dbo.ItemPrices SET Price = 180 WHERE ItemID = '{referenceId}' AND PriceTypeID = '{price0}' AND BranchID IS NULL;");

            await AssertCodeAsync(() => resolver.ResolveAsync(0m, null, CancellationToken.None), VariableValueCodes.InvalidEnteredScaleValue);
            await AssertCodeAsync(() => resolver.ResolveAsync(-1m, null, CancellationToken.None), VariableValueCodes.InvalidEnteredScaleValue);
            await AssertCodeAsync(() => resolver.ResolveAsync(20m, inactive, CancellationToken.None), PricingCodes.CustomerInactive);
            await AssertCodeAsync(() => resolver.ResolveAsync(20m, Guid.NewGuid(), CancellationToken.None), PricingCodes.CustomerNotFound);

            var before = await FingerprintAsync();
            await resolver.ResolveAsync(20m, null, CancellationToken.None);
            await resolver.ResolveAsync(20m, discounted, CancellationToken.None);
            Assert.Equal(before, await FingerprintAsync());

            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{branchId}', '{ownerId}', 10, 1, 8);
                """);
            var costed = await resolver.ResolveAsync(20m, null, CancellationToken.None);
            Assert.Equal((20m / 180m * 4m) * 8m, costed.ProjectedCost);
            Assert.Equal(VariableValuePricing.CostFromOwner, costed.CostSource);

            await ExecuteAsync($"""
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{ownerId}', 2, '{referenceId}', 1);
                """);
            await AssertCodeAsync(() => resolver.ResolveAsync(20m, null, CancellationToken.None), VariableValueCodes.InvalidUnitConversion);
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.Customers WHERE Name IN (N'خصم 22', N'جملة متغيرة', N'تصنيف متغير', N'موقوف متغير');
                DELETE FROM dbo.CustomerClassifications WHERE Name = N'تصنيف قيمة';
                DELETE FROM dbo.BranchStockBalances WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                DELETE FROM dbo.Branches WHERE BranchID = '{otherBranch}';
                """);
        }
    }

    private VariableValuePricingResolver Resolver()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<VariableValuePricingResolver>();
    }

    private static async Task AssertCodeAsync(Func<Task> action, string code)
    {
        var error = await Assert.ThrowsAsync<PricingException>(action);
        Assert.Equal(code, error.Code);
    }

    private static async Task<string> FingerprintAsync() =>
        await QueryTextAsync("""
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.Items), '|',
                (SELECT COUNT(*) FROM dbo.ItemPrices), '|',
                (SELECT COUNT(*) FROM dbo.Customers), '|',
                (SELECT COUNT(*) FROM dbo.BranchStockBalances), '|',
                (SELECT COUNT(*) FROM dbo.StockMovements), '|',
                (SELECT COUNT(*) FROM dbo.ItemPriceHistory));
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
