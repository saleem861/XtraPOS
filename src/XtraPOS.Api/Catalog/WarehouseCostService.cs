using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Catalog;

public sealed class WarehouseCostService(SyncEndpointStore endpoints)
{
    public static decimal WeightedAverage(decimal onHand, decimal? currentCost, decimal receivedQuantity, decimal receivedCost)
    {
        if (receivedQuantity <= 0)
        {
            throw new InvalidOperationException("Received quantity must be positive.");
        }

        var nextQuantity = onHand + receivedQuantity;
        if (nextQuantity <= 0)
        {
            return decimal.Round(receivedCost, 4);
        }

        var previous = currentCost ?? 0m;
        return decimal.Round((onHand * previous + receivedQuantity * receivedCost) / nextQuantity, 4);
    }

    public async Task<decimal> ReceiveAsync(Guid itemId, decimal quantity, decimal unitCost, Guid userId, CancellationToken cancellationToken)
    {
        var central = await OpenCentralAsync(cancellationToken);
        await using (central.Connection)
        {
            var (onHand, current) = await ReadWarehouseAsync(central.Connection, itemId, cancellationToken);
            var next = WeightedAverage(onHand, current, quantity, unitCost);
            await PublishAsync(central.Connection, itemId, next, quantity, userId, cancellationToken);
            return next;
        }
    }

    public async Task SetCostAsync(Guid itemId, decimal unitCost, Guid userId, CancellationToken cancellationToken)
    {
        var central = await OpenCentralAsync(cancellationToken);
        await using (central.Connection)
        {
            await PublishAsync(central.Connection, itemId, decimal.Round(unitCost, 4), 0m, userId, cancellationToken);
        }
    }

    private async Task<(SqlConnection Connection, SqlEndpointSettings Settings)> OpenCentralAsync(CancellationToken cancellationToken)
    {
        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken)
            ?? throw new ItemEditException(StatusCodes.Status409Conflict, "لم يتم ربط الفرع بقاعدة مركزية.");
        var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        return (connection, settings);
    }

    private static async Task<(decimal OnHand, decimal? Cost)> ReadWarehouseAsync(SqlConnection connection, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) balance.OnHandQuantity, balance.UnitCost
            FROM dbo.BranchStockBalances AS balance
            INNER JOIN dbo.Branches AS branch ON branch.BranchID = balance.BranchID
            WHERE balance.ItemID = @item AND branch.BranchType = N'Warehouse'
            ORDER BY balance.UpdatedAt DESC;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0m, null);
        }

        return (reader.GetDecimal(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1));
    }

    private static async Task PublishAsync(
        SqlConnection connection,
        Guid itemId,
        decimal unitCost,
        decimal receivedQuantity,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new SqlCommand(
            """
            UPDATE dbo.ItemBranchInventory
            SET UnitCost = @cost, UpdatedAt = SYSUTCDATETIME()
            WHERE ItemID = @item;

            UPDATE dbo.BranchStockBalances
            SET UnitCost = @cost, UpdatedAt = SYSUTCDATETIME()
            WHERE ItemID = @item;

            IF @received > 0
                UPDATE balance
                SET OnHandQuantity = balance.OnHandQuantity + @received,
                    UpdatedAt = SYSUTCDATETIME()
                FROM dbo.BranchStockBalances AS balance
                INNER JOIN dbo.Branches AS branch ON branch.BranchID = balance.BranchID
                WHERE balance.ItemID = @item AND branch.BranchType = N'Warehouse';

            IF OBJECT_ID(N'dbo.SyncChangeLog', N'U') IS NOT NULL
                INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload, ChangedByUserID)
                VALUES (N'ItemCosts', @item, N'Upsert', @payload, @user);
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@cost", unitCost);
            command.Parameters.AddWithValue("@received", receivedQuantity);
            command.Parameters.AddWithValue("@payload", CentralChangeApply.Serialize(new { unitCost }));
            command.Parameters.AddWithValue("@user", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
