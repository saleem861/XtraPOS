using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CheckoutTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task Checkout_CompletesCashCardSplitAndKeepsOneSaleOnRetry()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var itemId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        await OpenAsync(actor, 100m);
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'CHK-CASH', N'صنف نقدي');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            var line = Fixed(itemId, 2m, 10m, 2m);
            var cash = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, cartId, null, [line], [Pay("Cash", 20m, 50m)], CancellationToken.None)).Sale;
            Assert.Equal(20m, cash.Total);
            Assert.Equal(30m, cash.Payments[0].ChangeAmount);
            Assert.Equal("Completed", await QueryTextAsync($"SELECT Status FROM dbo.Sales WHERE SaleID = '{cash.SaleId}';"));
            Assert.Equal(-2m, await QueryDecimalAsync($"SELECT QuantityDelta FROM dbo.StockMovements WHERE SourceDocumentID = '{cash.SaleId}';"));

            var replay = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, cartId, null, [line], [Pay("Cash", 20m, 50m)], CancellationToken.None)).Sale;
            Assert.Equal(cash.SaleId, replay.SaleId);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Sales WHERE CartID = '{cartId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SaleItems WHERE SaleID = '{cash.SaleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID = '{cash.SaleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = '{cash.SaleId}';"));

            var raced = await Task.WhenAll(
                service.CheckoutAsync(actor, cartId, null, [line], [Pay("Cash", 20m, 50m)], CancellationToken.None),
                service.CheckoutAsync(actor, cartId, null, [line], [Pay("Cash", 20m, 50m)], CancellationToken.None));
            Assert.Equal(cash.SaleId, Assert.IsType<CheckoutSucceeded>(raced[0]).Sale.SaleId);
            Assert.Equal(cash.SaleId, Assert.IsType<CheckoutSucceeded>(raced[1]).Sale.SaleId);

            var cardCart = Guid.NewGuid();
            var card = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, cardCart, null, [line], [Pay("Card", 20m, null)], CancellationToken.None)).Sale;
            Assert.Null(card.Payments[0].TenderedAmount);
            Assert.Equal(0m, card.Payments[0].ChangeAmount);

            var splitCart = Guid.NewGuid();
            var split = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, splitCart, null, [line], [Pay("Cash", 8m, 10m), Pay("Card", 12m, null)], CancellationToken.None)).Sale;
            Assert.Equal(2, split.Payments.Count);

            var closed = await Sessions().CloseAsync(actor, 128m, CancellationToken.None);
            Assert.Equal(128m, closed.ExpectedCashAmount);
            Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SyncOutbox;"));
        }
        finally
        {
            await CleanupAsync(actor, itemId);
        }
    }

    [Fact]
    public async Task Checkout_RejectsInvalidCheckoutBeforeAnyWrite()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var itemId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var plainId = Guid.NewGuid();
        var before = await FingerprintAsync();
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{itemId}', N'CHK-PRICE', N'بسعر'),
                    ('{parentId}', N'CHK-PARENT', N'تشكيلة'),
                    ('{plainId}', N'CHK-NOPRICE', N'بدون سعر');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, ParentItemID) VALUES
                    ('{childId}', N'CHK-CHILD', N'مقاس', '{parentId}');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (NEWID(), '{itemId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{parentId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{childId}', '{price0}', NULL, 6, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            var line = Fixed(itemId, 1m, 10m, 1m);
            await Assert.ThrowsAsync<CheckoutException>(() => service.CheckoutAsync(actor, Guid.NewGuid(), null, [line], [Pay("Cash", 10m, 10m)], CancellationToken.None));

            await OpenAsync(actor, 0m);
            var mismatch = await Assert.ThrowsAsync<CheckoutException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [line], [Pay("Cash", 9m, 9m)], CancellationToken.None));
            Assert.Equal(CheckoutCodes.PaymentTotalMismatch, mismatch.Code);

            var changed = Assert.IsType<CheckoutPriceRejected>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(itemId, 1m, 99m, 1m)], [Pay("Cash", 10m, 10m)], CancellationToken.None));
            Assert.Equal("UnitPrice", changed.Changes[0].Field);

            await Assert.ThrowsAsync<AssortmentRuleException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(parentId, 1m, 10m, 1m)], [Pay("Cash", 10m, 10m)], CancellationToken.None));
            var child = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(childId, 1m, 6m, 1m)], [Pay("Cash", 6m, 6m)], CancellationToken.None));
            Assert.Equal(childId, await QueryGuidAsync($"SELECT ItemID FROM dbo.SaleItems WHERE SaleID = '{child.Sale.SaleId}';"));

            await Assert.ThrowsAsync<PricingException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), Guid.NewGuid(), [line], [Pay("Cash", 10m, 10m)], CancellationToken.None));
            await Assert.ThrowsAsync<PricingException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(plainId, 1m, 1m, 1m)], [Pay("Cash", 1m, 1m)], CancellationToken.None));
            Assert.Equal(before.Sales + 1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Sales;"));
        }
        finally
        {
            await CleanupAsync(actor, itemId, parentId, childId, plainId);
        }
    }

    [Fact]
    public async Task Checkout_PostsFixedVariableAndNegativeOrUnknownStock()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var singleId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var negativeId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();
        var stockOwnerId = Guid.NewGuid();
        var number = VariableValuePricing.ReferenceItemNumber;
        var weight = 20m / 180m;
        var portionOwner = weight * 4m;
        await OpenAsync(actor, 0m);
        try
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.ItemPrices WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.ItemUnits WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'))
                    OR InternalUnitItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360'));
                DELETE FROM dbo.Items WHERE ItemNumber IN (N'{number}', N'1360');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{singleId}', N'CHK-ONE', N'واحد'),
                    ('{packageId}', N'CHK-PACK', N'طرد'),
                    ('{ownerId}', N'CHK-OWNER', N'مالك'),
                    ('{negativeId}', N'CHK-NEG', N'سالب'),
                    ('{unknownId}', N'CHK-UNK', N'مجهول'),
                    ('{stockOwnerId}', N'1360', N'معسل فرنسي وقية'),
                    ('{referenceId}', N'{number}', N'معسل فرنسي 1 كيلو');
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive) VALUES
                    (NEWID(), '{packageId}', 4, '{ownerId}', 1),
                    (NEWID(), '{referenceId}', 4, '{stockOwnerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt) VALUES
                    (NEWID(), '{singleId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{packageId}', '{price0}', NULL, 8, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{negativeId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{unknownId}', '{price0}', NULL, 3, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{referenceId}', '{price0}', NULL, 180, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{actor.BranchId}', '{negativeId}', 2, 1, 4);
                """);
            var service = Service();
            var single = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(singleId, 2m, 10m, 2m)], [Pay("Cash", 20m, 20m)], CancellationToken.None)).Sale;
            Assert.Equal(-2m, await QueryDecimalAsync($"SELECT QuantityDelta FROM dbo.StockMovements WHERE SourceDocumentID = '{single.SaleId}';"));

            var package = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(packageId, 1m, 8m, 4m)], [Pay("Card", 8m, null)], CancellationToken.None)).Sale;
            Assert.Equal(-4m, await QueryDecimalAsync($"SELECT QuantityDelta FROM dbo.StockMovements WHERE SourceDocumentID = '{package.SaleId}';"));
            Assert.Equal(ownerId, await QueryGuidAsync($"SELECT StockOwnerItemID FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));

            var onePortion = Variable(weight, portionOwner, 1m);
            var portionSale = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [onePortion], [Pay("Cash", 20m, 20m)], CancellationToken.None)).Sale;
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = '{portionSale.SaleId}';"));

            var three = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Variable(weight, portionOwner, 3m)], [Pay("Cash", 60m, 60m)], CancellationToken.None)).Sale;
            Assert.Equal(3m, await QueryDecimalAsync($"SELECT Quantity FROM dbo.SaleItems WHERE SaleID = '{three.SaleId}';"));
            Assert.Equal(60m, await QueryDecimalAsync($"SELECT LineTotal FROM dbo.SaleItems WHERE SaleID = '{three.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT EnteredScaleValue FROM dbo.SaleItems WHERE SaleID = '{three.SaleId}';"));
            Assert.Equal(portionOwner * 3m, await QueryDecimalAsync($"SELECT StockOwnerQuantity FROM dbo.SaleItems WHERE SaleID = '{three.SaleId}';"));
            Assert.Equal(weight * 1000m, await QueryDecimalAsync($"SELECT WeightGrams FROM dbo.SaleItems WHERE SaleID = '{three.SaleId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = '{three.SaleId}';"));

            await Assert.ThrowsAsync<CheckoutException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Variable(weight, portionOwner, 0m)], [Pay("Cash", 20m, 20m)], CancellationToken.None));
            await Assert.ThrowsAsync<CheckoutException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Variable(weight, portionOwner, -1m)], [Pay("Cash", 20m, 20m)], CancellationToken.None));
            await Assert.ThrowsAsync<CheckoutException>(() => service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Variable(weight, portionOwner, 1.5m)], [Pay("Cash", 20m, 20m)], CancellationToken.None));

            var negative = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(negativeId, 5m, 10m, 5m)], [Pay("Card", 50m, null)], CancellationToken.None)).Sale;
            Assert.Equal(-3m, await QueryDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE ItemID = '{negativeId}';"));
            Assert.Equal(4m, await QueryDecimalAsync($"SELECT UnitCostSnapshot FROM dbo.SaleItems WHERE SaleID = '{negative.SaleId}';"));
            Assert.Equal(20m, await QueryDecimalAsync($"SELECT LineCostSnapshot FROM dbo.SaleItems WHERE SaleID = '{negative.SaleId}';"));
            await Stock().PostDeltaAsync(new StockDeltaRequest(
                actor.BranchId, negativeId, StockSourceTypes.Purchase, Guid.NewGuid(), Guid.NewGuid(), 10m, 4m, DateTime.UtcNow, actor.UserId), null, CancellationToken.None);
            Assert.Equal(7m, await QueryDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE ItemID = '{negativeId}';"));

            var unknown = Assert.IsType<CheckoutSucceeded>(await service.CheckoutAsync(
                actor, Guid.NewGuid(), null, [Fixed(unknownId, 2m, 3m, 2m)], [Pay("Cash", 6m, 6m)], CancellationToken.None)).Sale;
            Assert.Equal(0, await QueryIntAsync($"SELECT IsQuantityInitialized FROM dbo.BranchStockBalances WHERE ItemID = '{unknownId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT CASE WHEN OnHandQuantity IS NULL THEN 1 ELSE 0 END FROM dbo.BranchStockBalances WHERE ItemID = '{unknownId}';"));
            Assert.Null(await QueryNullableDecimalAsync($"SELECT UnitCostSnapshot FROM dbo.SaleItems WHERE SaleID = '{unknown.SaleId}';"));
            Assert.Equal(VariableValuePricing.CostUnavailable, await QueryTextAsync($"SELECT CostSource FROM dbo.SaleItems WHERE SaleID = '{unknown.SaleId}';"));
        }
        finally
        {
            await CleanupAsync(actor, singleId, packageId, ownerId, negativeId, unknownId, referenceId, stockOwnerId);
        }
    }

    [Fact]
    public async Task Checkout_RollsBackEveryFailedStage()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var itemId = Guid.NewGuid();
        await OpenAsync(actor, 0m);
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{itemId}', N'CHK-ROLL', N'تراجع');
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (NEWID(), '{itemId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            var service = Service();
            foreach (var fault in new[] { CheckoutFault.AfterSale, CheckoutFault.AfterItems, CheckoutFault.DuringPayment, CheckoutFault.DuringStock })
            {
                var cartId = Guid.NewGuid();
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckoutAsync(
                    actor, cartId, null, [Fixed(itemId, 1m, 10m, 1m)], [Pay("Cash", 10m, 10m)], fault, CancellationToken.None));
                Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.Sales WHERE CartID = '{cartId}';"));
                Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID NOT IN (SELECT SaleID FROM dbo.Sales);"));
                Assert.Equal(0, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID NOT IN (SELECT SaleID FROM dbo.Sales) AND SourceType = N'Sale';"));
            }
        }
        finally
        {
            await CleanupAsync(actor, itemId);
        }
    }

    private static CheckoutLine Fixed(Guid itemId, decimal quantity, decimal price, decimal ownerQuantity) => new()
    {
        CartLineId = Guid.NewGuid(),
        LineType = "Fixed",
        ItemId = itemId,
        Quantity = quantity,
        ExpectedUnitPrice = price,
        ExpectedStockOwnerQuantity = ownerQuantity,
    };

    private static CheckoutLine Variable(decimal weight, decimal ownerQuantity, decimal quantity) => new()
    {
        CartLineId = Guid.NewGuid(),
        LineType = "VariableValueByWeight",
        EnteredScaleValue = 20m,
        Quantity = quantity,
        ExpectedFinalSellingPrice = 20m,
        ExpectedReferenceBasePrice = 180m,
        ExpectedWeightKg = weight,
        ExpectedStockOwnerQuantity = ownerQuantity,
    };

    private static CheckoutPaymentInput Pay(string method, decimal amount, decimal? tendered) => new()
    {
        ClientPaymentKey = Guid.NewGuid(),
        Method = method,
        Amount = amount,
        TenderedAmount = tendered,
    };

    private async Task OpenAsync(PosDeviceContext actor, decimal opening)
    {
        await ExecuteAsync($"DELETE FROM dbo.SalePayments WHERE CashSessionID IN (SELECT CashSessionID FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}'); UPDATE dbo.Sales SET CashSessionID = NULL WHERE DeviceID = '{actor.DeviceId}'; DELETE FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}';");
        Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(actor, opening, CancellationToken.None));
    }

    private async Task<PosDeviceContext> ActorAsync()
    {
        var deviceId = fixture.DeviceId;
        var branchId = await QueryGuidAsync($"SELECT BranchID FROM dbo.Devices WHERE DeviceID = '{deviceId}';");
        var businessId = await QueryGuidAsync($"SELECT BusinessID FROM dbo.Branches WHERE BranchID = '{branchId}';");
        var stationId = await QueryGuidAsync($"SELECT StationID FROM dbo.Devices WHERE DeviceID = '{deviceId}';");
        var userId = await QueryGuidAsync("SELECT UserID FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        var name = await QueryTextAsync("SELECT DisplayName FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        return new PosDeviceContext(userId, name, businessId, branchId, stationId, deviceId);
    }

    private CheckoutService Service() => fixture.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<CheckoutService>();

    private CashSessionService Sessions() => fixture.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<CashSessionService>();

    private StockPostingService Stock() => fixture.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<StockPostingService>();

    private async Task<(int Sales, int Payments, int Movements)> FingerprintAsync() =>
        (await QueryIntAsync("SELECT COUNT(*) FROM dbo.Sales;"),
            await QueryIntAsync("SELECT COUNT(*) FROM dbo.SalePayments;"),
            await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));

    private static async Task CleanupAsync(PosDeviceContext actor, params Guid[] items)
    {
        var list = string.Join(", ", items.Select(id => $"'{id}'"));
        await ExecuteAsync($"""
            DELETE FROM dbo.StockMovements WHERE BranchID = '{actor.BranchId}' AND (ItemID IN ({list}) OR SourceType = N'Sale');
            DELETE FROM dbo.SalePayments WHERE BranchID = '{actor.BranchId}';
            DELETE FROM dbo.SaleItems WHERE SaleID IN (SELECT SaleID FROM dbo.Sales WHERE DeviceID = '{actor.DeviceId}');
            UPDATE dbo.Sales SET CashSessionID = NULL WHERE DeviceID = '{actor.DeviceId}';
            DELETE FROM dbo.Sales WHERE DeviceID = '{actor.DeviceId}';
            DELETE FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}';
            DELETE FROM dbo.BranchStockBalances WHERE ItemID IN ({list});
            DELETE FROM dbo.ItemPrices WHERE ItemID IN ({list});
            DELETE FROM dbo.ItemUnits WHERE ItemID IN ({list}) OR InternalUnitItemID IN ({list});
            DELETE FROM dbo.Items WHERE ParentItemID IN ({list});
            DELETE FROM dbo.Items WHERE ItemID IN ({list});
            """);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<decimal> QueryDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal?> QueryNullableDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDecimal(value);
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
