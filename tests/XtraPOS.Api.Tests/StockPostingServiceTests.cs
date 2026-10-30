using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class StockPostingServiceTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task Deltas_DoNotInitializeUnknownStock()
    {
        var branchId = await BranchIdAsync();
        var known = await InsertItemAsync("SP6-KNOWN");
        var unknown = await InsertItemAsync("SP6-UNKNOWN");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{branchId}', '{known}', 10, 1, 8);
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{unknown}', NULL, 0);
                """);
            var service = Service();

            var purchase = await service.PostDeltaAsync(Delta(branchId, known, StockSourceTypes.Purchase, 5, 8), null, CancellationToken.None);
            Assert.False(purchase.Replay);
            Assert.Equal(15m, await QuantityAsync(branchId, known));
            Assert.Equal(8m, await CostAsync(branchId, known));

            var sale = await service.PostDeltaAsync(Delta(branchId, known, StockSourceTypes.Sale, -3, 8), null, CancellationToken.None);
            Assert.Equal(12m, await QuantityAsync(branchId, known));
            Assert.Equal(8m, await CostAsync(branchId, known));
            Assert.Equal(StockPostingModes.Delta, sale.PostingMode);

            await service.PostDeltaAsync(Delta(branchId, unknown, StockSourceTypes.Purchase, 5, 4), null, CancellationToken.None);
            await service.PostDeltaAsync(Delta(branchId, unknown, StockSourceTypes.Sale, -2, 4), null, CancellationToken.None);
            await service.PostDeltaAsync(Delta(branchId, unknown, StockSourceTypes.SaleReturn, 2, 4), null, CancellationToken.None);
            await service.PostDeltaAsync(Delta(branchId, unknown, StockSourceTypes.OpeningBalance, 9, 4), null, CancellationToken.None);
            Assert.False(await InitializedAsync(branchId, unknown));
            Assert.Null(await QuantityAsync(branchId, unknown));
            Assert.Null(await CostAsync(branchId, unknown));
            Assert.Equal(4, await MovementCountAsync(branchId, unknown));
        }
        finally
        {
            await CleanupAsync(known, unknown);
        }
    }

    [Fact]
    public async Task AbsolutePosts_SetQuantityWithoutAddingToNull()
    {
        var branchId = await BranchIdAsync();
        var counted = await InsertItemAsync("SP6-COUNT");
        var known = await InsertItemAsync("SP6-COUNT-KNOWN");
        var opening = await InsertItemAsync("SP6-OPEN");
        var reconciled = await InsertItemAsync("SP6-RECON");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{known}', 12, 1);
                """);
            var service = Service();

            var fromUnknown = await service.SetAbsoluteQuantityAsync(
                Absolute(branchId, counted, StockSourceTypes.InventoryCount, 20, null), null, CancellationToken.None);
            Assert.Equal(StockPostingModes.Absolute, fromUnknown.PostingMode);
            Assert.Null(fromUnknown.PreviousQuantity);
            Assert.Equal(20m, fromUnknown.ObservedQuantity);
            Assert.Equal(20m, fromUnknown.QuantityDelta);
            Assert.Equal(20m, fromUnknown.ResultingQuantity);
            Assert.Equal(20m, await QuantityAsync(branchId, counted));
            Assert.True(await InitializedAsync(branchId, counted));

            var fromKnown = await service.SetAbsoluteQuantityAsync(
                Absolute(branchId, known, StockSourceTypes.InventoryCount, 20, null), null, CancellationToken.None);
            Assert.Equal(12m, fromKnown.PreviousQuantity);
            Assert.Equal(8m, fromKnown.QuantityDelta);
            Assert.Equal(20m, fromKnown.ObservedQuantity);
            Assert.Equal(20m, await QuantityAsync(branchId, known));

            await service.SetAbsoluteQuantityAsync(
                Absolute(branchId, opening, StockSourceTypes.OpeningBalance, 7, 3), null, CancellationToken.None);
            Assert.Equal(7m, await QuantityAsync(branchId, opening));
            Assert.Equal(3m, await CostAsync(branchId, opening));

            await service.SetAbsoluteQuantityAsync(
                Absolute(branchId, reconciled, StockSourceTypes.InventoryReconciliation, 4, null), null, CancellationToken.None);
            Assert.Equal(4m, await QuantityAsync(branchId, reconciled));

            await Assert.ThrowsAsync<StockPostingException>(() => service.SetAbsoluteQuantityAsync(
                Absolute(branchId, counted, StockSourceTypes.Sale, 1, null), null, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(counted, known, opening, reconciled);
        }
    }

    [Fact]
    public async Task Replay_PostsOnce_AndEachReceiptPostsSeparately()
    {
        var branchId = await BranchIdAsync();
        var itemId = await InsertItemAsync("SP6-REPLAY");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized)
                VALUES ('{branchId}', '{itemId}', 0, 1);
                """);
            var service = Service();
            var lineId = Guid.NewGuid();
            var first = await service.PostDeltaAsync(
                Delta(branchId, itemId, StockSourceTypes.Purchase, 5, 2, lineId), null, CancellationToken.None);
            var second = await service.PostDeltaAsync(
                Delta(branchId, itemId, StockSourceTypes.Purchase, 5, 2, lineId), null, CancellationToken.None);
            Assert.True(second.Replay);
            Assert.Equal(first.StockMovementId, second.StockMovementId);
            Assert.Equal(5m, await QuantityAsync(branchId, itemId));
            Assert.Equal(1, await MovementCountAsync(branchId, itemId));

            await service.PostDeltaAsync(Delta(branchId, itemId, StockSourceTypes.TransferIn, 40, 2), null, CancellationToken.None);
            await service.PostDeltaAsync(Delta(branchId, itemId, StockSourceTypes.TransferIn, 35, 2), null, CancellationToken.None);
            Assert.Equal(80m, await QuantityAsync(branchId, itemId));
            Assert.Equal(2, await QueryIntAsync($"""
                SELECT COUNT(*)
                FROM dbo.StockMovements
                WHERE BranchID = '{branchId}' AND ItemID = '{itemId}' AND SourceType = N'TransferIn';
                """));
        }
        finally
        {
            await CleanupAsync(itemId);
        }
    }

    [Fact]
    public async Task WeightedAverage_UpdatesOnlyWhenAnInboundCostIsKnown()
    {
        var branchId = await BranchIdAsync();
        var ownerId = await InsertItemAsync("SP6-AVG");
        var packageId = await InsertItemAsync("SP6-PKG");
        var nullCost = await InsertItemAsync("SP6-NULLCOST");
        var nullIncoming = await InsertItemAsync("SP6-NULLIN");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.ItemUnits (ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, IsBaseUnit)
                VALUES ('{packageId}', 4, '{ownerId}', N'SP6-AVG', 0);
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{branchId}', '{ownerId}', 10, 1, 8),
                       ('{branchId}', '{nullCost}', 10, 1, NULL),
                       ('{branchId}', '{nullIncoming}', 10, 1, 8);
                INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, UnitCost, IsAvailable, IsActive)
                VALUES ('{branchId}', '{ownerId}', 1, 1, 1),
                       ('{branchId}', '{packageId}', 1, 1, 1);
                """);
            var service = Service();
            await service.PostDeltaAsync(Delta(branchId, ownerId, StockSourceTypes.Purchase, 10, 12), null, CancellationToken.None);
            Assert.Equal(20m, await QuantityAsync(branchId, ownerId));
            Assert.Equal(10m, await CostAsync(branchId, ownerId));
            Assert.Equal(10m, await SkuCostAsync(branchId, ownerId));
            Assert.Equal(40m, await SkuCostAsync(branchId, packageId));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{packageId}';"));

            var beforePackage = await SkuCostAsync(branchId, packageId);
            await service.PostDeltaAsync(Delta(branchId, ownerId, StockSourceTypes.Sale, -4, 99), null, CancellationToken.None);
            Assert.Equal(16m, await QuantityAsync(branchId, ownerId));
            Assert.Equal(10m, await CostAsync(branchId, ownerId));
            Assert.Equal(beforePackage, await SkuCostAsync(branchId, packageId));

            await service.PostDeltaAsync(Delta(branchId, nullCost, StockSourceTypes.Purchase, 5, 4), null, CancellationToken.None);
            Assert.Equal(15m, await QuantityAsync(branchId, nullCost));
            Assert.Equal(4m, await CostAsync(branchId, nullCost));

            await service.PostDeltaAsync(Delta(branchId, nullIncoming, StockSourceTypes.Purchase, 5, null), null, CancellationToken.None);
            Assert.Equal(15m, await QuantityAsync(branchId, nullIncoming));
            Assert.Equal(8m, await CostAsync(branchId, nullIncoming));

            await Assert.ThrowsAsync<StockPostingException>(() => service.PostDeltaAsync(
                Delta(branchId, packageId, StockSourceTypes.Purchase, 1, 1), null, CancellationToken.None));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.BranchStockBalances WHERE ItemID = '{packageId}';"));
        }
        finally
        {
            await CleanupAsync(ownerId, packageId, nullCost, nullIncoming);
        }
    }

    [Fact]
    public async Task MissingBalance_DeltaStaysUnknown_AndAbsoluteInitializes()
    {
        var branchId = await BranchIdAsync();
        var deltaItem = await InsertItemAsync("SP6-MISSING-D");
        var absoluteItem = await InsertItemAsync("SP6-MISSING-A");
        try
        {
            var service = Service();
            await service.PostDeltaAsync(Delta(branchId, deltaItem, StockSourceTypes.Purchase, 5, 2), null, CancellationToken.None);
            Assert.False(await InitializedAsync(branchId, deltaItem));
            Assert.Null(await QuantityAsync(branchId, deltaItem));
            Assert.Equal(1, await MovementCountAsync(branchId, deltaItem));

            await service.SetAbsoluteQuantityAsync(
                Absolute(branchId, absoluteItem, StockSourceTypes.InventoryCount, 6, null), null, CancellationToken.None);
            Assert.True(await InitializedAsync(branchId, absoluteItem));
            Assert.Equal(6m, await QuantityAsync(branchId, absoluteItem));
        }
        finally
        {
            await CleanupAsync(deltaItem, absoluteItem);
        }
    }

    [Fact]
    public async Task ConcurrentDeltas_DoNotLoseQuantity()
    {
        var branchId = await BranchIdAsync();
        var itemId = await InsertItemAsync("SP6-RACE");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{branchId}', '{itemId}', 10, 1, 1);
                """);
            var service = Service();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                service.PostDeltaAsync(Delta(branchId, itemId, StockSourceTypes.Sale, -1, 1), null, CancellationToken.None)));
            Assert.Equal(2m, await QuantityAsync(branchId, itemId));
            Assert.Equal(8, await MovementCountAsync(branchId, itemId));
        }
        finally
        {
            await CleanupAsync(itemId);
        }
    }

    [Fact]
    public async Task RolledBackTransaction_LeavesBalanceAndMovementsUntouched()
    {
        var branchId = await BranchIdAsync();
        var itemId = await InsertItemAsync("SP6-ROLLBACK");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.BranchStockBalances (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, UnitCost)
                VALUES ('{branchId}', '{itemId}', 10, 1, 5);
                """);
            var service = Service();
            await using var connection = new SqlConnection(TestDatabase.ConnectionString());
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await service.PostDeltaAsync(
                Delta(branchId, itemId, StockSourceTypes.Purchase, 5, 9),
                new StockPostingSession(connection, transaction),
                CancellationToken.None);
            await transaction.RollbackAsync();

            Assert.Equal(10m, await QuantityAsync(branchId, itemId));
            Assert.Equal(5m, await CostAsync(branchId, itemId));
            Assert.Equal(0, await MovementCountAsync(branchId, itemId));
        }
        finally
        {
            await CleanupAsync(itemId);
        }
    }

    private StockPostingService Service()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<StockPostingService>();
    }

    private static StockDeltaRequest Delta(
        Guid branchId,
        Guid itemId,
        string sourceType,
        decimal delta,
        decimal? cost,
        Guid? lineId = null) =>
        new(branchId, itemId, sourceType, Guid.NewGuid(), lineId ?? Guid.NewGuid(), delta, cost, DateTime.UtcNow, null);

    private static StockAbsoluteRequest Absolute(
        Guid branchId,
        Guid itemId,
        string sourceType,
        decimal quantity,
        decimal? cost) =>
        new(branchId, itemId, sourceType, Guid.NewGuid(), Guid.NewGuid(), quantity, cost, DateTime.UtcNow, null);

    private async Task<Guid> BranchIdAsync() =>
        await QueryGuidAsync("SELECT TOP (1) BranchID FROM dbo.Branches;");

    private async Task<Guid> InsertItemAsync(string number)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES ('{id}', N'{number}', N'{number}');");
        return id;
    }

    private async Task<decimal?> QuantityAsync(Guid branchId, Guid itemId) =>
        await QueryNullableDecimalAsync($"SELECT OnHandQuantity FROM dbo.BranchStockBalances WHERE BranchID = '{branchId}' AND ItemID = '{itemId}';");

    private async Task<decimal?> CostAsync(Guid branchId, Guid itemId) =>
        await QueryNullableDecimalAsync($"SELECT UnitCost FROM dbo.BranchStockBalances WHERE BranchID = '{branchId}' AND ItemID = '{itemId}';");

    private async Task<decimal?> SkuCostAsync(Guid branchId, Guid itemId) =>
        await QueryNullableDecimalAsync($"SELECT UnitCost FROM dbo.ItemBranchInventory WHERE BranchID = '{branchId}' AND ItemID = '{itemId}';");

    private async Task<bool> InitializedAsync(Guid branchId, Guid itemId) =>
        await QueryIntAsync($"SELECT IsQuantityInitialized FROM dbo.BranchStockBalances WHERE BranchID = '{branchId}' AND ItemID = '{itemId}';") == 1;

    private async Task<int> MovementCountAsync(Guid branchId, Guid itemId) =>
        await QueryIntAsync($"SELECT COUNT(*) FROM dbo.StockMovements WHERE BranchID = '{branchId}' AND ItemID = '{itemId}';");

    private async Task CleanupAsync(params Guid[] itemIds)
    {
        if (itemIds.Length == 0)
        {
            return;
        }

        var list = string.Join(", ", itemIds.Select(id => $"'{id}'"));
        await ExecuteAsync($"""
            DELETE FROM dbo.StockMovements WHERE ItemID IN ({list});
            DELETE FROM dbo.BranchStockBalances WHERE ItemID IN ({list});
            DELETE FROM dbo.ItemBranchInventory WHERE ItemID IN ({list});
            DELETE FROM dbo.ItemUnits WHERE ItemID IN ({list}) OR InternalUnitItemID IN ({list});
            DELETE FROM dbo.Items WHERE ItemID IN ({list});
            """);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing guid."));
    }

    private async Task<decimal?> QueryNullableDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToDecimal(value);
    }
}
