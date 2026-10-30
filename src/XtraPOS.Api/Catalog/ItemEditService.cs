using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Catalog;

public sealed class ItemEditService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    CentralItemApi central)
{
    public async Task<ItemEditForm?> GetEditFormAsync(CatalogCaller caller, Guid itemId, CancellationToken cancellationToken)
    {
        var local = await ReadLocalItemAsync(itemId, cancellationToken);
        if (local is null)
        {
            return null;
        }

        var topology = await ResolveAsync(cancellationToken);
        var state = await ReadBranchStateAsync(caller.BranchId, itemId, cancellationToken);
        var prices = await ReadLocalPricesAsync(caller.BranchId, itemId, cancellationToken);
        var masterReadOnly = false;
        string? message = null;
        var reachable = topology.Mode == ItemEditing.Central;
        var item = local;
        if (topology.Mode == ItemEditing.CentralLinked)
        {
            reachable = topology.Settings is not null
                && await CentralItemApi.CanReachAsync(topology.Settings.BuildConnectionString(topology.Settings.DatabaseName), cancellationToken);
            if (!reachable)
            {
                masterReadOnly = true;
                message = ItemEditing.CentralOfflineMessage;
            }
            else
            {
                try
                {
                    var snapshot = await central.ReadItemSnapshotAsync(
                        topology.Settings!.BuildConnectionString(topology.Settings.DatabaseName),
                        itemId,
                        cancellationToken);
                    if (snapshot is null)
                    {
                        masterReadOnly = true;
                        message = "الصنف غير موجود في الإدارة المركزية بعد.";
                    }
                    else
                    {
                        item = snapshot.Item;
                        prices = MergeCentralPrices(prices, snapshot.Prices);
                    }
                }
                catch (ItemEditException exception) when (exception.StatusCode == StatusCodes.Status503ServiceUnavailable)
                {
                    reachable = false;
                    masterReadOnly = true;
                    message = exception.Message;
                }
            }
        }

        return new ItemEditForm(
            topology.Mode,
            reachable,
            masterReadOnly,
            message,
            caller.CanEditMaster && !masterReadOnly,
            caller.CanEditBranchState,
            caller.CanEditBranchPrice,
            item.ItemName,
            item.ItemType,
            item.Manufacturer,
            item.Color,
            item.Taste,
            item.Size,
            item.Packing,
            item.Classification,
            item.CountryOfOrigin,
            item.ProductType,
            item.ParentItemId,
            item.IsActive,
            item.IsProhibited,
            item.IsPublishedOnline,
            masterReadOnly ? null : item.RowVersion,
            state.UnitCost,
            state.IsAvailable,
            state.IsActive,
            prices);
    }

    public async Task<ItemWriteResponse> UpdateMasterAsync(
        CatalogCaller caller,
        Guid itemId,
        UpdateItemMasterRequest request,
        CancellationToken cancellationToken)
    {
        if (!caller.CanEditMaster)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, ItemEditing.CentralDeniedMessage);
        }

        var local = await ReadLocalItemAsync(itemId, cancellationToken)
            ?? throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود.");
        if (!string.IsNullOrWhiteSpace(request.ItemNumber)
            && !string.Equals(local.ItemNumber.Trim(), request.ItemNumber.Trim(), StringComparison.Ordinal))
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, ItemEditing.ItemNumberImmutableMessage);
        }

        var topology = await ResolveAsync(cancellationToken);
        if (topology.Mode == ItemEditing.Standalone)
        {
            var version = await UpdateLocalItemAsync(itemId, request, cancellationToken);
            return new ItemWriteResponse(version, null);
        }

        var connectionString = await RequireCentralConnectionAsync(topology, cancellationToken);
        var result = await central.UpdateItemAsync(connectionString, new CentralCaller(caller.UserId, caller.BranchId), itemId, request, cancellationToken);
        if (topology.Mode == ItemEditing.CentralLinked)
        {
            await ApplyLinkedChangeAsync(topology, result, "Items", itemId, cancellationToken);
        }

        return new ItemWriteResponse(result.RowVersion, result.ChangeVersion);
    }

    public async Task<ItemWriteResponse> UpdateUnitsAsync(
        CatalogCaller caller,
        Guid itemId,
        UpdateItemUnitsRequest request,
        CancellationToken cancellationToken)
    {
        if (!caller.CanEditMaster)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, ItemEditing.CentralDeniedMessage);
        }

        if (await ReadLocalItemAsync(itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود.");
        }

        await ResolveUnitTargetsAsync(request.Units ?? [], cancellationToken);
        var prepared = PrepareUnits(itemId, request.Units ?? []);
        var existing = await ReadUnitsAsync(itemId, cancellationToken);
        if (IsStructuralUnitChange(existing, prepared) && await HasOperationalStockAsync(caller.BranchId, itemId, existing, cancellationToken))
        {
            throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.UnitChangeRejectedMessage);
        }

        var removed = existing
            .Where(unit => prepared.All(next => next.ItemUnitId != unit.ItemUnitId))
            .Select(unit => unit.ItemUnitId)
            .ToList();
        var topology = await ResolveAsync(cancellationToken);
        if (topology.Mode == ItemEditing.Standalone)
        {
            var version = await UpdateLocalUnitsAsync(itemId, prepared, removed, request.RowVersion, cancellationToken);
            return new ItemWriteResponse(version, null);
        }

        var connectionString = await RequireCentralConnectionAsync(topology, cancellationToken);
        var result = await central.UpdateUnitsAsync(
            connectionString,
            new CentralCaller(caller.UserId, caller.BranchId),
            itemId,
            prepared,
            removed,
            request.RowVersion,
            cancellationToken);
        if (topology.Mode == ItemEditing.CentralLinked)
        {
            await ApplyLinkedChangeAsync(topology, result, "ItemUnits", itemId, cancellationToken);
        }

        return new ItemWriteResponse(result.RowVersion, result.ChangeVersion);
    }

    public async Task<ItemWriteResponse> UpdateDefaultPriceAsync(
        CatalogCaller caller,
        Guid itemId,
        UpdateDefaultPriceRequest request,
        CancellationToken cancellationToken)
    {
        if (!caller.CanEditMaster)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, ItemEditing.CentralDeniedMessage);
        }

        var code = request.PriceCode?.Trim() ?? "";
        if (code.Length == 0)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "نوع السعر مطلوب.");
        }

        if (await ReadLocalItemAsync(itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود.");
        }

        var topology = await ResolveAsync(cancellationToken);
        if (topology.Mode == ItemEditing.Standalone)
        {
            var version = await UpdateLocalDefaultPriceAsync(itemId, code, request.Price, request.RowVersion, cancellationToken);
            return new ItemWriteResponse(version, null);
        }

        var connectionString = await RequireCentralConnectionAsync(topology, cancellationToken);
        var result = await central.UpdateDefaultPriceAsync(
            connectionString,
            new CentralCaller(caller.UserId, caller.BranchId),
            itemId,
            code,
            request.Price,
            request.RowVersion,
            cancellationToken);
        if (topology.Mode == ItemEditing.CentralLinked)
        {
            await ApplyLinkedChangeAsync(topology, result, "ItemPrices", itemId, cancellationToken);
        }

        return new ItemWriteResponse(result.RowVersion, result.ChangeVersion);
    }

    public async Task UpdateBranchPriceAsync(
        CatalogCaller caller,
        Guid itemId,
        UpdateBranchPriceRequest request,
        CancellationToken cancellationToken)
    {
        if (!caller.CanEditBranchPrice)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, "ليس لديك صلاحية تعديل سعر هذا الفرع.");
        }

        var code = request.PriceCode?.Trim() ?? "";
        if (code.Length == 0)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "نوع السعر مطلوب.");
        }

        if (await ReadLocalItemAsync(itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود.");
        }

        await using var connection = await OpenBranchAsync(cancellationToken);
        var priceTypeId = await LocalPriceTypeAsync(connection, code, cancellationToken);
        if (request.Price is null)
        {
            await using var delete = new SqlCommand(
                "DELETE FROM dbo.ItemPrices WHERE ItemID = @item AND PriceTypeID = @type AND BranchID = @branch;",
                connection);
            delete.Parameters.AddWithValue("@item", itemId);
            delete.Parameters.AddWithValue("@type", priceTypeId);
            delete.Parameters.AddWithValue("@branch", caller.BranchId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var command = new SqlCommand(
            """
            UPDATE dbo.ItemPrices
            SET Price = @price, IsActive = 1, UpdatedAt = SYSUTCDATETIME()
            WHERE ItemID = @item AND PriceTypeID = @type AND BranchID = @branch;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (@item, @type, @branch, @price, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@type", priceTypeId);
        command.Parameters.AddWithValue("@branch", caller.BranchId);
        command.Parameters.AddWithValue("@price", request.Price.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateBranchStateAsync(
        CatalogCaller caller,
        Guid itemId,
        UpdateBranchStateRequest request,
        CancellationToken cancellationToken)
    {
        if (!caller.CanEditBranchState)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, "ليس لديك صلاحية تعديل بيانات هذا الفرع.");
        }

        if (await ReadLocalItemAsync(itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود.");
        }

        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.ItemBranchInventory WHERE BranchID = @branch AND ItemID = @item)
                UPDATE dbo.ItemBranchInventory
                SET UnitCost = @cost, IsAvailable = @available, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
                WHERE BranchID = @branch AND ItemID = @item;
            ELSE
                INSERT INTO dbo.ItemBranchInventory
                    (BranchID, ItemID, UnitCost, DailySalesRate, IsAvailable, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    (@branch, @item, @cost, NULL, @available, @active, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@branch", caller.BranchId);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@cost", (object?)request.UnitCost ?? DBNull.Value);
        command.Parameters.AddWithValue("@available", request.IsAvailable);
        command.Parameters.AddWithValue("@active", request.IsActive);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> RequireCentralConnectionAsync(ItemTopology topology, CancellationToken cancellationToken)
    {
        if (topology.Mode == ItemEditing.Central)
        {
            return connections.CreateBranchConnectionString();
        }

        if (topology.Settings is null
            || !await CentralItemApi.CanReachAsync(topology.Settings.BuildConnectionString(topology.Settings.DatabaseName), cancellationToken))
        {
            throw new ItemEditException(StatusCodes.Status503ServiceUnavailable, ItemEditing.CentralOfflineMessage);
        }

        return topology.Settings.BuildConnectionString(topology.Settings.DatabaseName);
    }

    private async Task ApplyLinkedChangeAsync(
        ItemTopology topology,
        CentralWriteResult result,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        await CentralChangeApply.ApplyAsync(connection, entityType, entityId, result.Payload, cancellationToken);
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SyncInbox WHERE EndpointID = @endpoint AND ChangeSequence = @sequence)
                INSERT INTO dbo.SyncInbox (EndpointID, ChangeSequence, EntityType, EntityId)
                VALUES (@endpoint, @sequence, @entity, @id);
            """,
            connection);
        command.Parameters.AddWithValue("@endpoint", topology.EndpointId!.Value);
        command.Parameters.AddWithValue("@sequence", result.ChangeVersion);
        command.Parameters.AddWithValue("@entity", entityType);
        command.Parameters.AddWithValue("@id", entityId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ItemTopology> ResolveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var roleCommand = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            connection);
        var role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return new ItemTopology(ItemEditing.Central, null, null);
        }

        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (endpoint is not null && string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
            return new ItemTopology(ItemEditing.CentralLinked, settings, endpoint.EndpointId);
        }

        return new ItemTopology(ItemEditing.Standalone, null, null);
    }

    private async Task<StoredItem?> ReadLocalItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
                   CountryOfOrigin, ProductType, ParentItemID, IsActive, IsProhibited, IsPublishedOnline, RowVersion
            FROM dbo.Items
            WHERE ItemID = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11),
            reader.GetBoolean(12),
            reader.GetBoolean(13),
            reader.GetBoolean(14),
            RowVersions.Format((byte[])reader[15]));
    }

    private async Task<BranchStateRow> ReadBranchStateAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT UnitCost, IsAvailable, IsActive
            FROM dbo.ItemBranchInventory
            WHERE BranchID = @branch AND ItemID = @item;
            """,
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new BranchStateRow(null, true, true);
        }

        return new BranchStateRow(
            reader.IsDBNull(0) ? null : reader.GetDecimal(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2));
    }

    private async Task<IReadOnlyList<ItemEditPrice>> ReadLocalPricesAsync(Guid branchId, Guid itemId, CancellationToken cancellationToken)
    {
        var prices = new List<ItemEditPrice>();
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT type.PriceCode, type.PriceName, master.Price, master.RowVersion, branch.Price
            FROM dbo.PriceTypes AS type
            LEFT JOIN dbo.ItemPrices AS master
                ON master.PriceTypeID = type.PriceTypeID AND master.ItemID = @item AND master.BranchID IS NULL AND master.IsActive = 1
            LEFT JOIN dbo.ItemPrices AS branch
                ON branch.PriceTypeID = type.PriceTypeID AND branch.ItemID = @item AND branch.BranchID = @branch AND branch.IsActive = 1
            WHERE type.IsActive = 1
            ORDER BY type.SortOrder;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@branch", branchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(new ItemEditPrice(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(3) ? null : RowVersions.Format((byte[])reader[3])));
        }

        return prices;
    }

    private static IReadOnlyList<ItemEditPrice> MergeCentralPrices(
        IReadOnlyList<ItemEditPrice> local,
        IReadOnlyList<CentralPriceSnapshot> centralPrices)
    {
        var byCode = centralPrices.ToDictionary(price => price.PriceCode, StringComparer.OrdinalIgnoreCase);
        return local
            .Select(price => byCode.TryGetValue(price.PriceCode, out var central)
                ? price with { DefaultPrice = central.Price, DefaultRowVersion = central.RowVersion }
                : price)
            .ToList();
    }

    private async Task<List<PreparedUnit>> ReadUnitsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var units = new List<PreparedUnit>();
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT ItemUnitID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsActive
            FROM dbo.ItemUnits
            WHERE ItemID = @item;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            units.Add(new PreparedUnit(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5)));
        }

        return units;
    }

    private async Task ResolveUnitTargetsAsync(IReadOnlyList<ItemUnitWrite> units, CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        foreach (var unit in units)
        {
            if (unit.InternalUnitItemId is not null || string.IsNullOrWhiteSpace(unit.InternalUnitItemNumber))
            {
                continue;
            }

            await using var command = new SqlCommand(
                "SELECT ItemID, ItemName FROM dbo.Items WHERE ItemNumber = @number;",
                connection);
            command.Parameters.AddWithValue("@number", unit.InternalUnitItemNumber.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ItemEditException(StatusCodes.Status400BadRequest, "الوحدة الداخلية غير موجودة.");
            }

            unit.InternalUnitItemId = reader.GetGuid(0);
            unit.InternalUnitItemName ??= reader.GetString(1);
        }
    }

    private static List<PreparedUnit> PrepareUnits(Guid itemId, IReadOnlyList<ItemUnitWrite> requested)
    {
        var units = new List<PreparedUnit>();
        foreach (var unit in requested)
        {
            if (unit.UnitsPerPackage <= 0)
            {
                throw new ItemEditException(StatusCodes.Status400BadRequest, "عدد الوحدات يجب أن يكون أكبر من صفر.");
            }

            if (unit.InternalUnitItemId == itemId)
            {
                throw new ItemEditException(StatusCodes.Status400BadRequest, "لا يمكن أن تكون الوحدة الداخلية هي الصنف نفسه.");
            }

            units.Add(new PreparedUnit(
                unit.ItemUnitId ?? Guid.NewGuid(),
                unit.UnitsPerPackage,
                unit.InternalUnitItemId,
                string.IsNullOrWhiteSpace(unit.InternalUnitItemNumber) ? null : unit.InternalUnitItemNumber.Trim(),
                string.IsNullOrWhiteSpace(unit.InternalUnitItemName) ? null : unit.InternalUnitItemName.Trim(),
                unit.IsActive));
        }

        return units;
    }

    private static bool IsStructuralUnitChange(IReadOnlyList<PreparedUnit> existing, IReadOnlyList<PreparedUnit> next)
    {
        var current = existing.Where(unit => unit.IsActive).ToDictionary(unit => unit.ItemUnitId);
        var incoming = next.Where(unit => unit.IsActive).ToDictionary(unit => unit.ItemUnitId);
        if (current.Count != incoming.Count)
        {
            return true;
        }

        foreach (var (id, unit) in incoming)
        {
            if (!current.TryGetValue(id, out var previous)
                || previous.UnitsPerPackage != unit.UnitsPerPackage
                || previous.InternalUnitItemId != unit.InternalUnitItemId)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasOperationalStockAsync(
        Guid branchId,
        Guid itemId,
        IReadOnlyList<PreparedUnit> existing,
        CancellationToken cancellationToken)
    {
        var ids = existing.Select(unit => unit.InternalUnitItemId).Where(id => id is not null).Select(id => id!.Value).Append(itemId).Distinct().ToArray();
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand { Connection = connection };
        var names = new List<string>();
        for (var index = 0; index < ids.Length; index++)
        {
            var name = "@id" + index;
            names.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }

        command.Parameters.AddWithValue("@branch", branchId);
        command.CommandText =
            $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM dbo.StockMovements WHERE BranchID = @branch AND ItemID IN ({string.Join(", ", names)})
            ) OR EXISTS (
                SELECT 1 FROM dbo.BranchStockBalances
                WHERE BranchID = @branch AND IsQuantityInitialized = 1 AND ItemID IN ({string.Join(", ", names)})
            ) THEN 1 ELSE 0 END;
            """;
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    private async Task<string> UpdateLocalItemAsync(Guid itemId, UpdateItemMasterRequest request, CancellationToken cancellationToken)
    {
        var name = request.ItemName?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "اسم الصنف مطلوب.");
        }

        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.Items
            SET ItemName = @name,
                ItemNameSearchStrict = @nameStrict,
                ItemNameSearchLoose = @nameLoose,
                ItemType = @type,
                Manufacturer = @manufacturer,
                Color = @color,
                Taste = @taste,
                Size = @size,
                Packing = @packing,
                Classification = @classification,
                CountryOfOrigin = @origin,
                ProductType = @productType,
                ParentItemID = @parent,
                IsActive = @active,
                IsProhibited = @prohibited,
                IsPublishedOnline = @published,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.RowVersion
            WHERE ItemID = @id AND RowVersion = @version;
            """,
            connection);
        command.Parameters.AddWithValue("@id", itemId);
        command.Parameters.AddWithValue("@version", RowVersions.Parse(request.RowVersion));
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@nameStrict", ArabicSearchNormalizer.NormalizeStrict(name));
        command.Parameters.AddWithValue("@nameLoose", ArabicSearchNormalizer.NormalizeLoose(name));
        command.Parameters.AddWithValue("@type", Db(request.ItemType));
        command.Parameters.AddWithValue("@manufacturer", Db(request.Manufacturer));
        command.Parameters.AddWithValue("@color", Db(request.Color));
        command.Parameters.AddWithValue("@taste", Db(request.Taste));
        command.Parameters.AddWithValue("@size", Db(request.Size));
        command.Parameters.AddWithValue("@packing", Db(request.Packing));
        command.Parameters.AddWithValue("@classification", Db(request.Classification));
        command.Parameters.AddWithValue("@origin", Db(request.CountryOfOrigin));
        command.Parameters.AddWithValue("@productType", Db(request.ProductType));
        command.Parameters.AddWithValue("@parent", (object?)request.ParentItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@active", request.IsActive);
        command.Parameters.AddWithValue("@prohibited", request.IsProhibited);
        command.Parameters.AddWithValue("@published", request.IsPublishedOnline);
        if (await command.ExecuteScalarAsync(cancellationToken) is not byte[] version)
        {
            throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
        }

        return RowVersions.Format(version);
    }

    private async Task<string> UpdateLocalUnitsAsync(
        Guid itemId,
        IReadOnlyList<PreparedUnit> units,
        IReadOnlyList<Guid> removed,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        byte[] version;
        await using (var touch = new SqlCommand(
            """
            UPDATE dbo.Items SET UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.RowVersion
            WHERE ItemID = @id AND RowVersion = @version;
            """,
            connection,
            transaction))
        {
            touch.Parameters.AddWithValue("@id", itemId);
            touch.Parameters.AddWithValue("@version", RowVersions.Parse(rowVersion));
            if (await touch.ExecuteScalarAsync(cancellationToken) is not byte[] next)
            {
                throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
            }

            version = next;
        }

        foreach (var unitId in removed)
        {
            await using var delete = new SqlCommand(
                "DELETE FROM dbo.ItemUnits WHERE ItemUnitID = @id AND ItemID = @item;",
                connection,
                transaction);
            delete.Parameters.AddWithValue("@id", unitId);
            delete.Parameters.AddWithValue("@item", itemId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var unit in units)
        {
            await using var command = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.ItemUnits WHERE ItemUnitID = @id)
                    UPDATE dbo.ItemUnits
                    SET UnitsPerPackage = @factor, InternalUnitItemID = @internalId, InternalUnitItemNumber = @number,
                        InternalUnitItemName = @name, IsBaseUnit = 0, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
                    WHERE ItemUnitID = @id AND ItemID = @item;
                ELSE
                    INSERT INTO dbo.ItemUnits
                        (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt)
                    VALUES
                        (@id, @item, @factor, @internalId, @number, @name, 0, @active, SYSUTCDATETIME(), SYSUTCDATETIME());
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", unit.ItemUnitId);
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@factor", unit.UnitsPerPackage);
            command.Parameters.AddWithValue("@internalId", (object?)unit.InternalUnitItemId ?? DBNull.Value);
            command.Parameters.AddWithValue("@number", (object?)unit.InternalUnitItemNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@name", (object?)unit.InternalUnitItemName ?? DBNull.Value);
            command.Parameters.AddWithValue("@active", unit.IsActive);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return RowVersions.Format(version);
    }

    private async Task<string> UpdateLocalDefaultPriceAsync(
        Guid itemId,
        string priceCode,
        decimal price,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenBranchAsync(cancellationToken);
        var priceTypeId = await LocalPriceTypeAsync(connection, priceCode, cancellationToken);
        await using var existing = new SqlCommand(
            """
            SELECT RowVersion FROM dbo.ItemPrices
            WHERE ItemID = @item AND PriceTypeID = @type AND BranchID IS NULL;
            """,
            connection);
        existing.Parameters.AddWithValue("@item", itemId);
        existing.Parameters.AddWithValue("@type", priceTypeId);
        var current = await existing.ExecuteScalarAsync(cancellationToken) as byte[];
        if (current is null)
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                OUTPUT inserted.RowVersion
                VALUES (@item, @type, NULL, @price, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                """,
                connection);
            insert.Parameters.AddWithValue("@item", itemId);
            insert.Parameters.AddWithValue("@type", priceTypeId);
            insert.Parameters.AddWithValue("@price", price);
            var created = (byte[])(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Price insert failed."));
            return RowVersions.Format(created);
        }

        await using var update = new SqlCommand(
            """
            UPDATE dbo.ItemPrices
            SET Price = @price, IsActive = 1, UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.RowVersion
            WHERE ItemID = @item AND PriceTypeID = @type AND BranchID IS NULL AND RowVersion = @version;
            """,
            connection);
        update.Parameters.AddWithValue("@item", itemId);
        update.Parameters.AddWithValue("@type", priceTypeId);
        update.Parameters.AddWithValue("@price", price);
        update.Parameters.AddWithValue("@version", RowVersions.Parse(rowVersion));
        if (await update.ExecuteScalarAsync(cancellationToken) is not byte[] version)
        {
            throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
        }

        return RowVersions.Format(version);
    }

    private static async Task<Guid> LocalPriceTypeAsync(SqlConnection connection, string priceCode, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code AND IsActive = 1;",
            connection);
        command.Parameters.AddWithValue("@code", priceCode);
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid id)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "نوع السعر غير موجود.");
        }

        return id;
    }

    private async Task<SqlConnection> OpenBranchAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static object Db(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? DBNull.Value : trimmed;
    }

    private sealed record ItemTopology(string Mode, SqlEndpointSettings? Settings, Guid? EndpointId);

    private sealed record BranchStateRow(decimal? UnitCost, bool IsAvailable, bool IsActive);
}
