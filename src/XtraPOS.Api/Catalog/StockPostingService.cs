using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Catalog;

public static class StockSourceTypes
{
    public const string Sale = "Sale";
    public const string Purchase = "Purchase";
    public const string SaleReturn = "SaleReturn";
    public const string PurchaseReturn = "PurchaseReturn";
    public const string TransferOut = "TransferOut";
    public const string TransferIn = "TransferIn";
    public const string TransferReversal = "TransferReversal";
    public const string TransferRejectReturn = "TransferRejectReturn";
    public const string InventoryCount = "InventoryCount";
    public const string OpeningBalance = "OpeningBalance";
    public const string InventoryReconciliation = "InventoryReconciliation";
    public const string Damage = "Damage";
    public const string CostAdjustment = "CostAdjustment";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Sale,
        Purchase,
        SaleReturn,
        PurchaseReturn,
        TransferOut,
        TransferIn,
        TransferReversal,
        TransferRejectReturn,
        InventoryCount,
        OpeningBalance,
        InventoryReconciliation,
        Damage,
        CostAdjustment,
    };

    public static readonly IReadOnlySet<string> Absolute = new HashSet<string>(StringComparer.Ordinal)
    {
        InventoryCount,
        OpeningBalance,
        InventoryReconciliation,
    };
}

public static class StockPostingModes
{
    public const string Delta = "Delta";
    public const string Absolute = "Absolute";
}

public sealed class StockPostingException(string message) : InvalidOperationException(message);

/// <summary>
/// An open branch SQL transaction. Document services post commercial rows and stock
/// through the same session so a failure rolls both back.
/// </summary>
public sealed class StockPostingSession(SqlConnection connection, SqlTransaction transaction)
{
    public SqlConnection Connection { get; } = connection;
    public SqlTransaction Transaction { get; } = transaction;
}

public sealed record StockDeltaRequest(
    Guid BranchId,
    Guid OwnerItemId,
    string SourceType,
    Guid? SourceDocumentId,
    Guid SourceLineId,
    decimal PhysicalDelta,
    decimal? MovementUnitCost,
    DateTime OccurredAt,
    Guid? ActorUserId);

public sealed record StockAbsoluteRequest(
    Guid BranchId,
    Guid OwnerItemId,
    string SourceType,
    Guid? SourceDocumentId,
    Guid SourceLineId,
    decimal AbsoluteQuantity,
    decimal? OptionalUnitCost,
    DateTime OccurredAt,
    Guid? ActorUserId);

public sealed record StockPostingResult(
    Guid StockMovementId,
    bool Replay,
    string PostingMode,
    decimal? PreviousQuantity,
    decimal QuantityDelta,
    decimal? ObservedQuantity,
    decimal? ResultingQuantity,
    decimal? ResultingUnitCost,
    bool IsQuantityInitialized);

public sealed class StockPostingService(SqlConnectionFactory connections)
{
    public async Task<T> InTransactionAsync<T>(
        Func<StockPostingSession, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await work(new StockPostingSession(connection, transaction), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<StockPostingResult> PostDeltaAsync(
        StockDeltaRequest request,
        StockPostingSession? session,
        CancellationToken cancellationToken) =>
        ExecuteAsync(session, (active, token) => PostDeltaCoreAsync(active, request, token), cancellationToken);

    public async Task<IReadOnlyList<StockPostingResult>> PostDeltasAsync(
        IReadOnlyList<StockDeltaRequest> requests,
        StockPostingSession? session,
        CancellationToken cancellationToken)
    {
        var ordered = requests.OrderBy(request => request.OwnerItemId).ToArray();
        if (session is not null)
        {
            var results = new List<StockPostingResult>(ordered.Length);
            foreach (var request in ordered)
            {
                results.Add(await PostDeltaCoreAsync(session, request, cancellationToken));
            }

            return results;
        }

        return await InTransactionAsync(async (active, token) =>
        {
            var results = new List<StockPostingResult>(ordered.Length);
            foreach (var request in ordered)
            {
                results.Add(await PostDeltaCoreAsync(active, request, token));
            }

            return (IReadOnlyList<StockPostingResult>)results;
        }, cancellationToken);
    }

    public Task<StockPostingResult> SetAbsoluteQuantityAsync(
        StockAbsoluteRequest request,
        StockPostingSession? session,
        CancellationToken cancellationToken) =>
        ExecuteAsync(session, (active, token) => SetAbsoluteCoreAsync(active, request, token), cancellationToken);

    private async Task<StockPostingResult> ExecuteAsync(
        StockPostingSession? session,
        Func<StockPostingSession, CancellationToken, Task<StockPostingResult>> action,
        CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            return await action(session, cancellationToken);
        }

        return await InTransactionAsync(action, cancellationToken);
    }

    private async Task<StockPostingResult> PostDeltaCoreAsync(
        StockPostingSession session,
        StockDeltaRequest request,
        CancellationToken cancellationToken)
    {
        RequireSource(request.SourceType, request.SourceLineId, absolute: false);
        await RequirePhysicalOwnerAsync(session, request.OwnerItemId, cancellationToken);
        var balance = await LockOrCreateUnknownAsync(session, request.BranchId, request.OwnerItemId, cancellationToken);
        var existing = await FindMovementAsync(
            session,
            request.BranchId,
            request.SourceType,
            request.SourceLineId,
            cancellationToken);
        if (existing is not null)
        {
            return existing with { Replay = true };
        }

        decimal? resultingQuantity = null;
        decimal? resultingCost = balance.UnitCost;
        var initialized = balance.IsQuantityInitialized;
        if (balance.IsQuantityInitialized && balance.OnHandQuantity is decimal current)
        {
            resultingQuantity = current + request.PhysicalDelta;
            resultingCost = NextAverage(current, balance.UnitCost, request.PhysicalDelta, request.MovementUnitCost);
            await UpdateBalanceAsync(
                session,
                request.BranchId,
                request.OwnerItemId,
                resultingQuantity,
                initialized: true,
                resultingCost,
                cancellationToken);
            if (resultingCost != balance.UnitCost)
            {
                await RefreshSkuCostsAsync(session, request.BranchId, request.OwnerItemId, resultingCost!.Value, cancellationToken);
            }
        }

        var movementCost = request.PhysicalDelta < 0
            ? balance.UnitCost
            : request.MovementUnitCost;
        return await InsertMovementAsync(
            session,
            request.BranchId,
            request.OwnerItemId,
            request.SourceType,
            request.SourceDocumentId,
            request.SourceLineId,
            StockPostingModes.Delta,
            balance.IsQuantityInitialized ? balance.OnHandQuantity : null,
            quantityDelta: request.PhysicalDelta,
            observedQuantity: null,
            resultingQuantity,
            resultingCost,
            movementCost,
            request.OccurredAt,
            request.ActorUserId,
            initialized,
            cancellationToken);
    }

    private async Task<StockPostingResult> SetAbsoluteCoreAsync(
        StockPostingSession session,
        StockAbsoluteRequest request,
        CancellationToken cancellationToken)
    {
        RequireSource(request.SourceType, request.SourceLineId, absolute: true);
        await RequirePhysicalOwnerAsync(session, request.OwnerItemId, cancellationToken);
        var balance = await LockOrCreateUnknownAsync(session, request.BranchId, request.OwnerItemId, cancellationToken);
        var existing = await FindMovementAsync(
            session,
            request.BranchId,
            request.SourceType,
            request.SourceLineId,
            cancellationToken);
        if (existing is not null)
        {
            return existing with { Replay = true };
        }

        var previous = balance.IsQuantityInitialized ? balance.OnHandQuantity : null;
        var quantityDelta = previous is decimal known
            ? request.AbsoluteQuantity - known
            : request.AbsoluteQuantity;
        var resultingCost = request.OptionalUnitCost ?? balance.UnitCost;
        await UpdateBalanceAsync(
            session,
            request.BranchId,
            request.OwnerItemId,
            request.AbsoluteQuantity,
            initialized: true,
            resultingCost,
            cancellationToken);
        if (request.OptionalUnitCost is not null && request.OptionalUnitCost != balance.UnitCost)
        {
            await RefreshSkuCostsAsync(
                session,
                request.BranchId,
                request.OwnerItemId,
                request.OptionalUnitCost.Value,
                cancellationToken);
        }

        return await InsertMovementAsync(
            session,
            request.BranchId,
            request.OwnerItemId,
            request.SourceType,
            request.SourceDocumentId,
            request.SourceLineId,
            StockPostingModes.Absolute,
            previous,
            quantityDelta,
            request.AbsoluteQuantity,
            request.AbsoluteQuantity,
            resultingCost,
            request.OptionalUnitCost ?? balance.UnitCost,
            request.OccurredAt,
            request.ActorUserId,
            initialized: true,
            cancellationToken);
    }

    /// <summary>
    /// Positive incoming quantity with a null unit cost does not invent an average.
    /// The known quantity still changes and the current owner cost stays as it is.
    /// A positive quantity with a cost replaces the average when the known quantity
    /// is zero, negative, or the current cost is null. Otherwise it blends by quantity.
    /// A negative or zero delta never changes the average.
    /// </summary>
    public static decimal? NextAverage(
        decimal currentQuantity,
        decimal? currentAverage,
        decimal physicalDelta,
        decimal? incomingUnitCost)
    {
        if (physicalDelta <= 0 || incomingUnitCost is null)
        {
            return currentAverage;
        }

        if (currentQuantity > 0 && currentAverage is decimal average)
        {
            var blended = ((currentQuantity * average) + (physicalDelta * incomingUnitCost.Value))
                / (currentQuantity + physicalDelta);
            return decimal.Round(blended, 4, MidpointRounding.AwayFromZero);
        }

        return decimal.Round(incomingUnitCost.Value, 4, MidpointRounding.AwayFromZero);
    }

    private static void RequireSource(string sourceType, Guid sourceLineId, bool absolute)
    {
        if (sourceLineId == Guid.Empty)
        {
            throw new StockPostingException("A stock posting requires a source line id.");
        }

        if (!StockSourceTypes.All.Contains(sourceType))
        {
            throw new StockPostingException($"Stock source type '{sourceType}' is not supported.");
        }

        if (absolute && !StockSourceTypes.Absolute.Contains(sourceType))
        {
            throw new StockPostingException($"{sourceType} cannot set an absolute quantity.");
        }
    }

    private static async Task RequirePhysicalOwnerAsync(
        StockPostingSession session,
        Guid ownerItemId,
        CancellationToken cancellationToken)
    {
        const string itemSql = "SELECT COUNT(*) FROM dbo.Items WHERE ItemID = @item;";
        await using (var item = new SqlCommand(itemSql, session.Connection, session.Transaction))
        {
            item.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
            var count = (int)(await item.ExecuteScalarAsync(cancellationToken) ?? 0);
            if (count == 0)
            {
                throw new StockPostingException("The stock owner item does not exist.");
            }
        }

        const string linkSql = """
            SELECT COUNT(DISTINCT InternalUnitItemID)
            FROM dbo.ItemUnits
            WHERE ItemID = @item
              AND IsActive = 1
              AND InternalUnitItemID IS NOT NULL
              AND InternalUnitItemID <> @item;
            """;
        await using var link = new SqlCommand(linkSql, session.Connection, session.Transaction);
        link.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
        var outward = (int)(await link.ExecuteScalarAsync(cancellationToken) ?? 0);
        if (outward > 0)
        {
            throw new StockPostingException("Physical stock posts only to the stock owner, not a package SKU.");
        }
    }

    private static async Task<BalanceRow> LockOrCreateUnknownAsync(
        StockPostingSession session,
        Guid branchId,
        Guid ownerItemId,
        CancellationToken cancellationToken)
    {
        var locked = await ReadBalanceAsync(session, branchId, ownerItemId, cancellationToken);
        if (locked is not null)
        {
            return locked;
        }

        const string insertSql = """
            INSERT INTO dbo.BranchStockBalances
                (BranchID, ItemID, OnHandQuantity, IsQuantityInitialized, CreatedAt, UpdatedAt)
            VALUES (@branch, @item, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        try
        {
            await using var insert = new SqlCommand(insertSql, session.Connection, session.Transaction);
            insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            insert.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
        }

        return await ReadBalanceAsync(session, branchId, ownerItemId, cancellationToken)
            ?? throw new StockPostingException("The stock owner balance could not be locked.");
    }

    private static async Task<BalanceRow?> ReadBalanceAsync(
        StockPostingSession session,
        Guid branchId,
        Guid ownerItemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OnHandQuantity, IsQuantityInitialized, UnitCost
            FROM dbo.BranchStockBalances WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE BranchID = @branch AND ItemID = @item;
            """;
        await using var command = new SqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BalanceRow(
            reader.IsDBNull(0) ? null : reader.GetDecimal(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetDecimal(2));
    }

    private static async Task UpdateBalanceAsync(
        StockPostingSession session,
        Guid branchId,
        Guid ownerItemId,
        decimal? onHandQuantity,
        bool initialized,
        decimal? unitCost,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.BranchStockBalances
            SET OnHandQuantity = @quantity,
                IsQuantityInitialized = @initialized,
                UnitCost = @cost,
                UpdatedAt = SYSUTCDATETIME()
            WHERE BranchID = @branch AND ItemID = @item;
            """;
        await using var command = new SqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add("@quantity", SqlDbType.Decimal).Value = (object?)onHandQuantity ?? DBNull.Value;
        command.Parameters["@quantity"].Precision = 18;
        command.Parameters["@quantity"].Scale = 4;
        command.Parameters.Add("@initialized", SqlDbType.Bit).Value = initialized;
        command.Parameters.Add("@cost", SqlDbType.Decimal).Value = (object?)unitCost ?? DBNull.Value;
        command.Parameters["@cost"].Precision = 19;
        command.Parameters["@cost"].Scale = 4;
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<StockPostingResult?> FindMovementAsync(
        StockPostingSession session,
        Guid branchId,
        string sourceType,
        Guid sourceLineId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT StockMovementID, PostingMode, PreviousQuantity, QuantityDelta, ObservedQuantity,
                   ResultingQuantity, ResultingUnitCost
            FROM dbo.StockMovements WITH (UPDLOCK, HOLDLOCK)
            WHERE BranchID = @branch AND SourceType = @source AND SourceLineID = @line;
            """;
        await using var command = new SqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 40).Value = sourceType;
        command.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = sourceLineId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var mode = reader.GetString(1);
        var resulting = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
        return new StockPostingResult(
            reader.GetGuid(0),
            Replay: false,
            mode,
            reader.IsDBNull(2) ? null : reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            resulting,
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            IsQuantityInitialized: mode == StockPostingModes.Absolute || resulting is not null);
    }

    private static async Task<StockPostingResult> InsertMovementAsync(
        StockPostingSession session,
        Guid branchId,
        Guid ownerItemId,
        string sourceType,
        Guid? sourceDocumentId,
        Guid sourceLineId,
        string postingMode,
        decimal? previousQuantity,
        decimal quantityDelta,
        decimal? observedQuantity,
        decimal? resultingQuantity,
        decimal? resultingUnitCost,
        decimal? movementUnitCost,
        DateTime occurredAt,
        Guid? actorUserId,
        bool initialized,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.StockMovements
                (BranchID, ItemID, MovementTypeID, QuantityDelta, UnitCost, OccurredAt, CreatedAt, CreatedByUserID,
                 ReferenceType, ReferenceID, SourceType, SourceDocumentID, SourceLineID, PostingMode,
                 PreviousQuantity, ObservedQuantity, ResultingQuantity, ResultingUnitCost)
            OUTPUT INSERTED.StockMovementID
            SELECT @branch, @item, movementType.MovementTypeID, @delta, @movementCost, @occurred, SYSUTCDATETIME(), @actor,
                   @source, @document, @source, @document, @line, @mode,
                   @previous, @observed, @resulting, @resultingCost
            FROM dbo.StockMovementTypes AS movementType
            WHERE movementType.MovementCode = @source;
            """;
        await using var command = new SqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        command.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = ownerItemId;
        AddDecimal(command, "@delta", quantityDelta, 18, 4);
        AddNullableDecimal(command, "@movementCost", movementUnitCost, 19, 4);
        command.Parameters.Add("@occurred", SqlDbType.DateTime2).Value = Truncate(occurredAt);
        command.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = (object?)actorUserId ?? DBNull.Value;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 40).Value = sourceType;
        command.Parameters.Add("@document", SqlDbType.UniqueIdentifier).Value = (object?)sourceDocumentId ?? DBNull.Value;
        command.Parameters.Add("@line", SqlDbType.UniqueIdentifier).Value = sourceLineId;
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 20).Value = postingMode;
        AddNullableDecimal(command, "@previous", previousQuantity, 18, 4);
        AddNullableDecimal(command, "@observed", observedQuantity, 18, 4);
        AddNullableDecimal(command, "@resulting", resultingQuantity, 18, 4);
        AddNullableDecimal(command, "@resultingCost", resultingUnitCost, 19, 4);
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is not Guid movementId)
        {
            throw new StockPostingException($"Stock movement type '{sourceType}' is not available.");
        }

        return new StockPostingResult(
            movementId,
            Replay: false,
            postingMode,
            previousQuantity,
            quantityDelta,
            observedQuantity,
            resultingQuantity,
            resultingUnitCost,
            initialized);
    }

    private static async Task RefreshSkuCostsAsync(
        StockPostingSession session,
        Guid branchId,
        Guid ownerItemId,
        decimal ownerAverage,
        CancellationToken cancellationToken)
    {
        const string linkSql = """
            SELECT ItemID, InternalUnitItemID, UnitsPerPackage
            FROM dbo.ItemUnits
            WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL;
            """;
        var links = new List<StockUnitLink>();
        var itemIds = new HashSet<Guid> { ownerItemId };
        await using (var command = new SqlCommand(linkSql, session.Connection, session.Transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemId = reader.GetGuid(0);
                var internalId = reader.GetGuid(1);
                links.Add(new StockUnitLink(itemId, internalId, reader.GetDecimal(2)));
                itemIds.Add(itemId);
                itemIds.Add(internalId);
            }
        }

        foreach (var itemId in itemIds)
        {
            StockOwnerResolution resolution;
            try
            {
                resolution = StockOwnership.Resolve(itemId, links);
            }
            catch (StockOwnershipException)
            {
                continue;
            }

            if (resolution.StockOwnerItemId != ownerItemId)
            {
                continue;
            }

            var displayCost = decimal.Round(
                ownerAverage * resolution.BaseQuantityFactor,
                4,
                MidpointRounding.AwayFromZero);
            const string updateSql = """
                UPDATE dbo.ItemBranchInventory
                SET UnitCost = @cost, UpdatedAt = SYSUTCDATETIME()
                WHERE BranchID = @branch AND ItemID = @item;
                """;
            await using var update = new SqlCommand(updateSql, session.Connection, session.Transaction);
            AddDecimal(update, "@cost", displayCost, 19, 4);
            update.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
            update.Parameters.Add("@item", SqlDbType.UniqueIdentifier).Value = itemId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static DateTime Truncate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private SqlConnection Open() => new(connections.CreateBranchConnectionString());

    private sealed record BalanceRow(decimal? OnHandQuantity, bool IsQuantityInitialized, decimal? UnitCost);
}
