using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class AssortmentSafetyGuardTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task FixedValidation_BlocksAssortmentParentsAndLeavesOrdinaryItemsOpen()
    {
        var actor = await ActorAsync();
        var price0 = await QueryGuidAsync("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0';");
        var ordinaryId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var inactiveChildId = Guid.NewGuid();
        var inactiveOnlyParentId = Guid.NewGuid();
        var inactiveOnlyChildId = Guid.NewGuid();
        var paymentsBefore = await QueryIntAsync("SELECT COUNT(*) FROM dbo.SalePayments;");
        var movementsBefore = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;");
        var balancesBefore = await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;");
        var outboxBefore = await QueryIntAsync("SELECT COUNT(*) FROM dbo.SyncOutbox;");
        var migrationsBefore = await QueryIntAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES
                    ('{ordinaryId}', N'ASG-ORD', N'صنف عادي'),
                    ('{packageId}', N'ASG-PACK', N'طرد'),
                    ('{ownerId}', N'ASG-OWNER', N'مالك'),
                    ('{parentId}', N'ASG-PARENT', N'تشكيلة'),
                    ('{inactiveOnlyParentId}', N'ASG-PARENT-OFF', N'تشكيلة متوقفة');
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, ParentItemID, IsActive) VALUES
                    ('{childId}', N'ASG-CHILD', N'مقاس', '{parentId}', 1),
                    ('{inactiveChildId}', N'ASG-CHILD-OFF', N'مقاس متوقف', '{parentId}', 0),
                    ('{inactiveOnlyChildId}', N'ASG-ONLY-OFF', N'ابن متوقف', '{inactiveOnlyParentId}', 0);
                INSERT INTO dbo.ItemUnits (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, IsActive)
                VALUES (NEWID(), '{packageId}', 4, '{ownerId}', 1);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (NEWID(), '{ordinaryId}', '{price0}', NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{packageId}', '{price0}', NULL, 8, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{parentId}', '{price0}', NULL, 20, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{childId}', '{price0}', NULL, 6, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (NEWID(), '{inactiveOnlyParentId}', '{price0}', NULL, 4, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);

            var service = Service();
            Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(ordinaryId, 1m, 10m, 1m, actor), CancellationToken.None));

            var package = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(packageId, 1m, 8m, 4m, actor), CancellationToken.None));
            Assert.Equal(4m, await QueryDecimalAsync($"SELECT StockOwnerQuantity FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));
            Assert.Equal(ownerId, await QueryGuidAsync($"SELECT StockOwnerItemID FROM dbo.SaleItems WHERE SaleID = '{package.SaleId}';"));

            var salesAfterAllowed = await QueryIntAsync("SELECT COUNT(*) FROM dbo.Sales;");
            var blocked = await Assert.ThrowsAsync<AssortmentRuleException>(() => service.PostDraftAsync(
                Command(parentId, 1m, 20m, 1m, actor), CancellationToken.None));
            Assert.Equal(SaleCodes.AssortmentRuleNotSupported, blocked.Code);
            Assert.Equal(parentId, blocked.ItemId);
            Assert.Equal("ASG-PARENT", blocked.ItemNumber);
            Assert.Equal("تشكيلة", blocked.ItemName);
            Assert.Equal("هذا الصنف مرتبط بتشكيلة أحجام/ألوان ولم يتم تفعيل خصم مخزون التشكيلة بعد.", blocked.Message);
            Assert.Equal(salesAfterAllowed, await QueryIntAsync("SELECT COUNT(*) FROM dbo.Sales;"));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SaleItems WHERE ItemID = '{parentId}';"));

            var child = Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(childId, 1m, 6m, 1m, actor), CancellationToken.None));
            Assert.Equal(childId, await QueryGuidAsync($"SELECT ItemID FROM dbo.SaleItems WHERE SaleID = '{child.SaleId}';"));

            Assert.IsType<SaleDraftPosted>(await service.PostDraftAsync(
                Command(inactiveOnlyParentId, 1m, 4m, 1m, actor), CancellationToken.None));

            Assert.Equal(paymentsBefore, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SalePayments;"));
            Assert.Equal(movementsBefore, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;"));
            Assert.Equal(balancesBefore, await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(outboxBefore, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SyncOutbox;"));
            Assert.Equal(migrationsBefore, await QueryIntAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;"));
        }
        finally
        {
            await ExecuteAsync($"""
                DELETE FROM dbo.SaleItems WHERE ItemID IN ('{ordinaryId}', '{packageId}', '{childId}', '{inactiveOnlyParentId}');
                DELETE FROM dbo.Sales WHERE CashierUserID = '{actor.CashierUserId}' AND LegacySource IS NULL
                    AND NOT EXISTS (SELECT 1 FROM dbo.SaleItems WHERE SaleItems.SaleID = dbo.Sales.SaleID);
                DELETE FROM dbo.ItemPrices WHERE ItemID IN ('{ordinaryId}', '{packageId}', '{parentId}', '{childId}', '{inactiveOnlyParentId}');
                DELETE FROM dbo.ItemUnits WHERE ItemID = '{packageId}';
                DELETE FROM dbo.Items WHERE ItemID IN ('{childId}', '{inactiveChildId}', '{inactiveOnlyChildId}');
                DELETE FROM dbo.Items WHERE ItemID IN ('{ordinaryId}', '{packageId}', '{ownerId}', '{parentId}', '{inactiveOnlyParentId}');
                """);
        }
    }

    private static SalePostCommand Command(Guid itemId, decimal quantity, decimal price, decimal ownerQuantity, SalePostActor actor) =>
        new(Guid.NewGuid(), null, actor, [new FixedSaleLineInput(itemId, quantity, price, ownerQuantity)]);

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
}
