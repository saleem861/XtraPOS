using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class SalesFoundationTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task Migration016_CreatesSalesDocumentsAndBranchLocalNumbers()
    {
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'016_SalesFoundation';"));
        Assert.Equal(2, await QueryIntAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN (N'Sales', N'SaleItems');"));
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM sys.sequences WHERE name = N'SaleNumberSequence';"));
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Sales_BranchID_CartID';"));
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Sales_BranchID_SaleNumber';"));
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Sales_LegacyIdentity';"));
    }

    [Fact]
    public async Task FixedDraft_UsesServerPriceAndOwnerAndRejectsASecondCartSale()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var itemId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        var packageCartId = Guid.NewGuid();
        var stockBefore = await StockFingerprintAsync();
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{itemId}', N'SALE-FIXED', N'صنف ثابت'),
                    ('{packageId}', N'SALE-PACK', N'طرد'),
                    ('{ownerId}', N'SALE-OWNER', N'مالك'),
                    ('{missingId}', N'SALE-NOPRICE', N'بدون سعر');
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{packageId}', 4, '{ownerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (NEWID(), '{itemId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{packageId}', '{price0}', NULL, 8, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            var posted = await service.PostDraftAsync(
                Command(cartId, null, actor, new FixedSaleLineInput(itemId, 2m, 10m, 2m)),
                CancellationToken.None);
            var draft = Assert.IsType<SaleDraftPosted>(posted);
            Assert.False(draft.IdempotentReplay);
            Assert.Null(await QueryNullableGuidAsync($"SELECT CustomerID FROM dbo.Sales WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal("Draft", await QueryTextAsync($"SELECT Status FROM dbo.Sales WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT LineTotal FROM dbo.SaleItems WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal(10m, await QueryDecimalAsync($"SELECT UnitPrice FROM dbo.SaleItems WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal("Price0", await QueryTextAsync($"SELECT PriceTypeCodeSnapshot FROM dbo.SaleItems WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal(PriceSources.SharedDefault, await QueryTextAsync($"SELECT PriceSource FROM dbo.SaleItems WHERE SaleID = '{draft.SaleId}';"));
            Assert.Equal(itemId, await QueryGuidAsync($"SELECT StockOwnerItemID FROM dbo.SaleItems WHERE SaleID = '{draft.SaleId}';"));

            var package = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(packageCartId, null, actor, new FixedSaleLineInput(packageId, 1m, 8m, 4m)),
                CancellationToken.None));
            Assert.Equal(4m, await QueryDecimalAsync($"SELECT StockOwnerQuantity FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));
            Assert.Equal(4m, await QueryDecimalAsync($"SELECT StockOwnerConversionFactor FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));
            Assert.Equal(ownerId, await QueryGuidAsync($"SELECT StockOwnerItemID FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));

            var replay = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(cartId, null, actor, new FixedSaleLineInput(itemId, 2m, 10m, 2m)),
                CancellationToken.None));
            Assert.Equal(draft.SaleId, replay.SaleId);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Sales WHERE CartID = '{cartId}';"));

            var fakePrice = await service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(itemId, 1m, 999m, 1m)),
                CancellationToken.None);
            Assert.Equal("UnitPrice", Assert.IsType<SalePriceChanged>(fakePrice).Changes[0].Field);
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SaleItems WHERE UnitPrice = 999;"));

            var fakeOwner = await service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(packageId, 1m, 8m, 99m)),
                CancellationToken.None);
            Assert.Contains(Assert.IsType<SalePriceChanged>(fakeOwner).Changes, change => change.Field == "StockOwnerQuantity");
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SaleItems WHERE StockOwnerQuantity = 99;"));

            await ExecuteAsync($"UPDATE dbo.ItemPrices SET Price = 12 WHERE ItemID = '{itemId}' AND Price = 10;");
            var changed = Assert.IsType<SalePriceChanged>(await service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(itemId, 1m, 10m, 1m)),
                CancellationToken.None));
            Assert.Contains(changed.Changes, change => change.Field == "UnitPrice" && change.Authoritative == 12m);

            var missing = await Assert.ThrowsAsync<PricingException>(() => service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(missingId, 1m, 1m, 1m)),
                CancellationToken.None));
            Assert.Equal(PricingCodes.PriceNotFound, missing.Code);
            await Assert.ThrowsAsync<SaleValidationException>(() => service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(itemId, 0m, 12m, 1m)),
                CancellationToken.None));
            await Assert.ThrowsAsync<SaleValidationException>(() => service.PostDraftAsync(
                Command(Guid.NewGuid(), null, actor, new FixedSaleLineInput(itemId, -1m, 12m, 1m)),
                CancellationToken.None));
            Assert.Equal(stockBefore, await StockFingerprintAsync());
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID IN ('{cartId}', '{packageCartId}'));
                DELETE FROM dbo.Sales WHERE CartID IN ('{cartId}', '{packageCartId}');
                DELETE FROM dbo.ItemPrices WHERE ItemID IN ('{itemId}', '{packageId}', '{missingId}');
                DELETE FROM dbo.ItemUnits WHERE ItemID = '{packageId}';
                DELETE FROM dbo.Items WHERE ItemID IN ('{itemId}', '{packageId}', '{ownerId}', '{missingId}');
                """);
        }
    }

    [Fact]
    public async Task VariableDraft_PreservesScaleSnapshotAndDetectsPriceChanges()
    {
        var actor = await ActorAsync();
        var businessId = await QueryGuidAsync("SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';");
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var referenceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        var number = VariableValuePricing.ReferenceItemNumber;
        var weight = 20m / 180m;
        var ownerQuantity = weight * 4m;
        try
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{ownerId}', N'1360', N'معسل فرنسي وقية'),
                    ('{referenceId}', N'{number}', N'معسل فرنسي 1 كيلو');
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{referenceId}', 4, '{ownerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', NULL, 180, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, VariableValueDiscountPercent, IsActive)
                VALUES ('{customerId}', '{businessId}', N'خصم بيع', 22, 1);
                """);
            var service = Service();
            var posted = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                new SalePostCommand(cartId, null, actor, [VariableLine(20m, 180m, weight, ownerQuantity)]),
                CancellationToken.None));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT EnteredScaleValue FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(180m, await QueryDecimalAsync($"SELECT ReferenceBasePrice FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(weight, await QueryDecimalAsync($"SELECT WeightKg FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(4m, await QueryDecimalAsync($"SELECT StockOwnerConversionFactor FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(ownerQuantity, await QueryDecimalAsync($"SELECT StockOwnerQuantity FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(0m, await QueryDecimalAsync($"SELECT VariableValueDiscountPercent FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT FinalSellingPrice FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT LineTotal FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(VariableValuePricing.RoundingPolicyName, await QueryTextAsync($"SELECT RoundingPolicy FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));
            Assert.Equal(ownerId, await QueryGuidAsync($"SELECT StockOwnerItemID FROM dbo.SaleItems WHERE SaleID = '{posted.SaleId}';"));

            var fakeQuantity = Assert.IsType<SalePriceChanged>(await service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [VariableLine(20m, 180m, weight, 99m)]),
                CancellationToken.None));
            Assert.Contains(fakeQuantity.Changes, change => change.Field == "StockOwnerQuantity" && change.Authoritative == ownerQuantity);

            await ExecuteAsync($"UPDATE dbo.ItemPrices SET Price = 90 WHERE ItemID = '{referenceId}';");
            var baseChanged = Assert.IsType<SalePriceChanged>(await service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [VariableLine(20m, 180m, weight, ownerQuantity)]),
                CancellationToken.None));
            Assert.Contains(baseChanged.Changes, change => change.Field == "ReferenceBasePrice" && change.Authoritative == 90m);
            await ExecuteAsync($"UPDATE dbo.ItemPrices SET Price = 180 WHERE ItemID = '{referenceId}';");

            var discountChanged = Assert.IsType<SalePriceChanged>(await service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), customerId, actor, [VariableLine(20m, 180m, weight, ownerQuantity)]),
                CancellationToken.None));
            Assert.Contains(discountChanged.Changes, change => change.Field == "FinalSellingPrice" && change.Authoritative == 15m);

            await ExecuteAsync($"DELETE FROM dbo.ItemPrices WHERE ItemID = '{referenceId}';");
            var missing = await Assert.ThrowsAsync<PricingException>(() => service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [VariableLine(20m, 180m, weight, ownerQuantity)]),
                CancellationToken.None));
            Assert.Equal(VariableValueCodes.BasePriceNotFound, missing.Code);

            await ExecuteAsync($"""
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', NULL, 180, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{ownerId}', 4, '{referenceId}', 1);
                """);
            var broken = await Assert.ThrowsAsync<PricingException>(() => service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [VariableLine(20m, 180m, weight, ownerQuantity)]),
                CancellationToken.None));
            Assert.Equal(VariableValueCodes.InvalidUnitConversion, broken.Code);
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID = '{cartId}');
                DELETE FROM dbo.Sales WHERE CartID = '{cartId}';
                DELETE FROM dbo.Customers WHERE CustomerID = '{customerId}';
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                """);
        }
    }

    [Fact]
    public async Task SaleNumbers_AreUniqueUnderConcurrentDrafts()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var itemId = Guid.NewGuid();
        var firstCart = Guid.NewGuid();
        var secondCart = Guid.NewGuid();
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'SALE-RACE', N'سباق');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{price0}', NULL, 5, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            var line = new FixedSaleLineInput(itemId, 1m, 5m, 1m);
            var results = await Task.WhenAll(
                service.PostDraftAsync(Command(firstCart, null, actor, line), CancellationToken.None),
                service.PostDraftAsync(Command(secondCart, null, actor, line), CancellationToken.None));
            var numbers = results.Select(result => Assert.IsType<SaleDraftPosted>(result).SaleNumber).ToArray();
            Assert.Equal(2, numbers.Distinct().Count());
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Sales WHERE CartID IN ('{firstCart}', '{secondCart}');"));
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(DISTINCT SaleNumber) FROM dbo.Sales WHERE CartID IN ('{firstCart}', '{secondCart}');"));
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID IN ('{firstCart}', '{secondCart}'));
                DELETE FROM dbo.Sales WHERE CartID IN ('{firstCart}', '{secondCart}');
                DELETE FROM dbo.ItemPrices WHERE ItemID = '{itemId}';
                DELETE FROM dbo.Items WHERE ItemID = '{itemId}';
                """);
        }
    }

    [Fact]
    public async Task VariablePortions_MultiplyTheResolvedPortionWithoutChangingTheScaleReading()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var referenceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var singleCart = Guid.NewGuid();
        var tripleCart = Guid.NewGuid();
        var number = VariableValuePricing.ReferenceItemNumber;
        var weight = 20m / 180m;
        var ownerQuantity = weight * 4m;
        var movements = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;");
        try
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{ownerId}', N'1360', N'معسل فرنسي وقية'),
                    ('{referenceId}', N'{number}', N'معسل فرنسي 1 كيلو');
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{referenceId}', 4, '{ownerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{referenceId}', '{price0}', NULL, 180, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            var single = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                new SalePostCommand(singleCart, null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, 1m)]),
                CancellationToken.None));
            Assert.Equal(1m, await QueryDecimalAsync($"SELECT Quantity FROM dbo.SaleItems WHERE SaleID = '{single.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT LineTotal FROM dbo.SaleItems WHERE SaleID = '{single.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT EnteredScaleValue FROM dbo.SaleItems WHERE SaleID = '{single.SaleId}';"));

            var triple = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                new SalePostCommand(tripleCart, null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, 3m)]),
                CancellationToken.None));
            Assert.Equal(3m, await QueryDecimalAsync($"SELECT Quantity FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(60m, await QueryDecimalAsync($"SELECT LineTotal FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT FinalSellingPrice FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT EnteredScaleValue FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(weight, await QueryDecimalAsync($"SELECT WeightKg FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(weight * 1000m, await QueryDecimalAsync($"SELECT WeightGrams FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(ownerQuantity * 3m, await QueryDecimalAsync($"SELECT StockOwnerQuantity FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));

            var replay = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                new SalePostCommand(tripleCart, null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, 3m)]),
                CancellationToken.None));
            Assert.Equal(triple.SaleId, replay.SaleId);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(3m, await QueryDecimalAsync($"SELECT Quantity FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SaleItems WHERE SaleID = '{triple.SaleId}';"));

            await Assert.ThrowsAsync<SaleValidationException>(() => service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, 0m)]),
                CancellationToken.None));
            await Assert.ThrowsAsync<SaleValidationException>(() => service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, -2m)]),
                CancellationToken.None));
            await Assert.ThrowsAsync<SaleValidationException>(() => service.PostDraftAsync(
                new SalePostCommand(Guid.NewGuid(), null, actor, [new VariableSaleLineInput(20m, 20m, 180m, weight, ownerQuantity, 1.5m)]),
                CancellationToken.None));
            Assert.Equal(movements, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE CartID IN ('{singleCart}', '{tripleCart}'));
                DELETE FROM dbo.Sales WHERE CartID IN ('{singleCart}', '{tripleCart}');
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                """);
        }
    }

    private static SalePostCommand Command(Guid cartId, Guid? customerId, SalePostActor actor, FixedSaleLineInput line) =>
        new(cartId, customerId, actor, [line]);

    private static VariableSaleLineInput VariableLine(decimal finalPrice, decimal basePrice, decimal weight, decimal ownerQuantity) =>
        new(20m, finalPrice, basePrice, weight, ownerQuantity);

    private async Task<SalePostActor> ActorAsync()
    {
        var deviceId = fixture.DeviceId;
        var stationId = await QueryGuidAsync($"SELECT StationID FROM dbo.Devices WHERE DeviceID = '{deviceId}';");
        var userId = await QueryGuidAsync("SELECT UserID FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        return new SalePostActor(stationId, deviceId, userId);
    }

    private SalePostingService Service()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SalePostingService>();
    }

    private static async Task<string> StockFingerprintAsync() =>
        await QueryTextAsync("""
            SELECT CONCAT(
                (SELECT COUNT(*) FROM dbo.StockMovements), '|',
                (SELECT COUNT(*) FROM dbo.BranchStockBalances));
            """);

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal> QueryDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing guid."));
    }

    private static async Task<Guid?> QueryNullableGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<string> QueryTextAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }
}
