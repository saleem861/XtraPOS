using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Sync;

public sealed record CentralCaller(Guid UserId, Guid BranchId);

public sealed record CentralWriteResult(string RowVersion, long ChangeVersion, string Payload);

public sealed class CentralItemApi
{
    public async Task<CentralWriteResult> UpdateItemAsync(
        string connectionString,
        CentralCaller caller,
        Guid itemId,
        UpdateItemMasterRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await AuthorizeAsync(connection, transaction, caller, cancellationToken);
        var current = await ReadItemAsync(connection, transaction, itemId, cancellationToken)
            ?? throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود في الإدارة المركزية.");
        RejectItemNumberChange(current.ItemNumber, request.ItemNumber);
        var name = RequireName(request.ItemName);
        if (request.ParentItemId == itemId)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "لا يمكن أن يكون الصنف أباً لنفسه.");
        }

        if (request.ParentItemId is Guid parent && await ReadItemAsync(connection, transaction, parent, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "الصنف الأب غير موجود.");
        }

        var version = RowVersions.Parse(request.RowVersion);
        await using var update = new SqlCommand(
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
            connection,
            transaction);
        update.Parameters.AddWithValue("@id", itemId);
        update.Parameters.AddWithValue("@version", version);
        update.Parameters.AddWithValue("@name", name);
        update.Parameters.AddWithValue("@nameStrict", ArabicSearchNormalizer.NormalizeStrict(name));
        update.Parameters.AddWithValue("@nameLoose", ArabicSearchNormalizer.NormalizeLoose(name));
        update.Parameters.AddWithValue("@type", Db(request.ItemType));
        update.Parameters.AddWithValue("@manufacturer", Db(request.Manufacturer));
        update.Parameters.AddWithValue("@color", Db(request.Color));
        update.Parameters.AddWithValue("@taste", Db(request.Taste));
        update.Parameters.AddWithValue("@size", Db(request.Size));
        update.Parameters.AddWithValue("@packing", Db(request.Packing));
        update.Parameters.AddWithValue("@classification", Db(request.Classification));
        update.Parameters.AddWithValue("@origin", Db(request.CountryOfOrigin));
        update.Parameters.AddWithValue("@productType", Db(request.ProductType));
        update.Parameters.AddWithValue("@parent", (object?)request.ParentItemId ?? DBNull.Value);
        update.Parameters.AddWithValue("@active", request.IsActive);
        update.Parameters.AddWithValue("@prohibited", request.IsProhibited);
        update.Parameters.AddWithValue("@published", request.IsPublishedOnline);
        var rowVersion = await update.ExecuteScalarAsync(cancellationToken) as byte[];
        if (rowVersion is null)
        {
            throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
        }

        var payload = CentralChangeApply.Serialize(new
        {
            itemNumber = current.ItemNumber,
            itemName = name,
            itemType = Blank(request.ItemType),
            manufacturer = Blank(request.Manufacturer),
            color = Blank(request.Color),
            taste = Blank(request.Taste),
            size = Blank(request.Size),
            packing = Blank(request.Packing),
            classification = Blank(request.Classification),
            countryOfOrigin = Blank(request.CountryOfOrigin),
            productType = Blank(request.ProductType),
            parentItemId = request.ParentItemId,
            isActive = request.IsActive,
            isProhibited = request.IsProhibited,
            isPublishedOnline = request.IsPublishedOnline,
        });
        var sequence = await WriteChangeAsync(connection, transaction, "Items", itemId, "Update", payload, caller.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CentralWriteResult(RowVersions.Format(rowVersion), sequence, payload);
    }

    public async Task<CentralWriteResult> UpdateUnitsAsync(
        string connectionString,
        CentralCaller caller,
        Guid itemId,
        IReadOnlyList<PreparedUnit> units,
        IReadOnlyList<Guid> removedUnitIds,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await AuthorizeAsync(connection, transaction, caller, cancellationToken);
        if (await ReadItemAsync(connection, transaction, itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود في الإدارة المركزية.");
        }

        var version = RowVersions.Parse(rowVersion);
        await using (var touch = new SqlCommand(
            """
            UPDATE dbo.Items
            SET UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.RowVersion
            WHERE ItemID = @id AND RowVersion = @version;
            """,
            connection,
            transaction))
        {
            touch.Parameters.AddWithValue("@id", itemId);
            touch.Parameters.AddWithValue("@version", version);
            if (await touch.ExecuteScalarAsync(cancellationToken) is not byte[] nextVersion)
            {
                throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
            }

            version = nextVersion;
        }

        foreach (var unitId in removedUnitIds)
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
            if (unit.InternalUnitItemId is Guid internalId && await ReadItemAsync(connection, transaction, internalId, cancellationToken) is null)
            {
                throw new ItemEditException(StatusCodes.Status400BadRequest, "الوحدة الداخلية غير موجودة.");
            }

            await using var command = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.ItemUnits WHERE ItemUnitID = @id)
                    UPDATE dbo.ItemUnits
                    SET UnitsPerPackage = @factor,
                        InternalUnitItemID = @internalId,
                        InternalUnitItemNumber = @number,
                        InternalUnitItemName = @name,
                        IsBaseUnit = 0,
                        IsActive = @active,
                        UpdatedAt = SYSUTCDATETIME()
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

        var payload = CentralChangeApply.Serialize(new
        {
            units = units.Select(unit => new
            {
                itemUnitId = unit.ItemUnitId,
                unitsPerPackage = unit.UnitsPerPackage,
                internalUnitItemId = unit.InternalUnitItemId,
                internalUnitItemNumber = unit.InternalUnitItemNumber,
                internalUnitItemName = unit.InternalUnitItemName,
                isBaseUnit = false,
                isActive = unit.IsActive,
            }),
            removeItemUnitIds = removedUnitIds,
        });
        var sequence = await WriteChangeAsync(connection, transaction, "ItemUnits", itemId, "Update", payload, caller.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CentralWriteResult(RowVersions.Format(version), sequence, payload);
    }

    public async Task<CentralWriteResult> UpdateDefaultPriceAsync(
        string connectionString,
        CentralCaller caller,
        Guid itemId,
        string priceCode,
        decimal price,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await AuthorizeAsync(connection, transaction, caller, cancellationToken);
        if (await ReadItemAsync(connection, transaction, itemId, cancellationToken) is null)
        {
            throw new ItemEditException(StatusCodes.Status404NotFound, "الصنف غير موجود في الإدارة المركزية.");
        }

        var priceTypeId = await PriceTypeIdAsync(connection, transaction, priceCode, cancellationToken);
        byte[] nextVersion;
        await using (var existing = new SqlCommand(
            """
            SELECT RowVersion
            FROM dbo.ItemPrices
            WHERE ItemID = @item AND PriceTypeID = @type;
            """,
            connection,
            transaction))
        {
            existing.Parameters.AddWithValue("@item", itemId);
            existing.Parameters.AddWithValue("@type", priceTypeId);
            var current = await existing.ExecuteScalarAsync(cancellationToken) as byte[];
            if (current is null)
            {
                await using var insert = new SqlCommand(
                    """
                    INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt)
                    OUTPUT inserted.RowVersion
                    VALUES (@item, @type, @price, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """,
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("@item", itemId);
                insert.Parameters.AddWithValue("@type", priceTypeId);
                insert.Parameters.AddWithValue("@price", price);
                nextVersion = (byte[])(await insert.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Price insert did not return a version."));
            }
            else
            {
                var version = RowVersions.Parse(rowVersion);
                await using var update = new SqlCommand(
                    """
                    UPDATE dbo.ItemPrices
                    SET Price = @price, IsActive = 1, UpdatedAt = SYSUTCDATETIME()
                    OUTPUT inserted.RowVersion
                    WHERE ItemID = @item AND PriceTypeID = @type AND RowVersion = @version;
                    """,
                    connection,
                    transaction);
                update.Parameters.AddWithValue("@item", itemId);
                update.Parameters.AddWithValue("@type", priceTypeId);
                update.Parameters.AddWithValue("@price", price);
                update.Parameters.AddWithValue("@version", version);
                if (await update.ExecuteScalarAsync(cancellationToken) is not byte[] updated)
                {
                    throw new ItemEditException(StatusCodes.Status409Conflict, ItemEditing.ConflictMessage);
                }

                nextVersion = updated;
            }
        }

        var payload = CentralChangeApply.Serialize(new
        {
            priceTypeId,
            priceCode,
            price,
            isActive = true,
        });
        var sequence = await WriteChangeAsync(connection, transaction, "ItemPrices", itemId, "Update", payload, caller.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CentralWriteResult(RowVersions.Format(nextVersion), sequence, payload);
    }

    public async Task<CentralItemSnapshot?> ReadItemSnapshotAsync(
        string connectionString,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            var item = await ReadItemAsync(connection, null, itemId, cancellationToken);
            if (item is null)
            {
                return null;
            }

            var prices = new List<CentralPriceSnapshot>();
            await using var command = new SqlCommand(
                """
                SELECT type.PriceCode, price.Price, price.RowVersion
                FROM dbo.ItemPrices AS price
                INNER JOIN dbo.PriceTypes AS type ON type.PriceTypeID = price.PriceTypeID
                WHERE price.ItemID = @id AND price.IsActive = 1;
                """,
                connection);
            command.Parameters.AddWithValue("@id", itemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                prices.Add(new CentralPriceSnapshot(
                    reader.GetString(0),
                    reader.GetDecimal(1),
                    RowVersions.Format((byte[])reader[2])));
            }

            return new CentralItemSnapshot(item, prices);
        }
        catch (ItemEditException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException)
        {
            throw new ItemEditException(StatusCodes.Status503ServiceUnavailable, ItemEditing.CentralOfflineMessage);
        }
    }

    public static async Task<bool> CanReachAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task AuthorizeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CentralCaller caller,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                u.IsActive,
                b.IsActive,
                a.IsActive,
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM dbo.UserRoles AS ur
                    INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.IsActive = 1
                    INNER JOIN dbo.RolePermissions AS rp ON rp.RoleID = r.RoleID
                    INNER JOIN dbo.Permissions AS p ON p.PermissionID = rp.PermissionID AND p.IsActive = 1
                    WHERE ur.UserID = u.UserID
                      AND p.PermissionCode IN (@edit, @admin)
                ) THEN 1 ELSE 0 END
            FROM dbo.Users AS u
            INNER JOIN dbo.UserBranchAccess AS a ON a.UserID = u.UserID AND a.BranchID = @branch
            INNER JOIN dbo.Branches AS b ON b.BranchID = a.BranchID
            WHERE u.UserID = @user;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@user", caller.UserId);
        command.Parameters.AddWithValue("@branch", caller.BranchId);
        command.Parameters.AddWithValue("@edit", PermissionCodes.ItemsEditMaster);
        command.Parameters.AddWithValue("@admin", PermissionCodes.SystemAdminister);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !reader.GetBoolean(0)
            || !reader.GetBoolean(1)
            || !reader.GetBoolean(2)
            || reader.GetInt32(3) != 1)
        {
            throw new ItemEditException(StatusCodes.Status403Forbidden, ItemEditing.CentralDeniedMessage);
        }
    }

    private static async Task<long> WriteChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string entityType,
        Guid entityId,
        string operation,
        string payload,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload, ChangedByUserID)
            OUTPUT inserted.ChangeSequence
            VALUES (@entity, @id, @operation, @payload, @user);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@entity", entityType);
        command.Parameters.AddWithValue("@id", entityId);
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@user", userId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<StoredItem?> ReadItemAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
                   CountryOfOrigin, ProductType, ParentItemID, IsActive, IsProhibited, IsPublishedOnline, RowVersion
            FROM dbo.Items
            WHERE ItemID = @id;
            """,
            connection,
            transaction);
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

    private static async Task<Guid> PriceTypeIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string priceCode,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code AND IsActive = 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", priceCode);
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid id)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "نوع السعر غير موجود.");
        }

        return id;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or TimeoutException)
        {
            await connection.DisposeAsync();
            throw new ItemEditException(StatusCodes.Status503ServiceUnavailable, ItemEditing.CentralOfflineMessage);
        }
    }

    private static void RejectItemNumberChange(string current, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        if (!string.Equals(current.Trim(), requested.Trim(), StringComparison.Ordinal))
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, ItemEditing.ItemNumberImmutableMessage);
        }
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 500)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "اسم الصنف مطلوب.");
        }

        return trimmed;
    }

    private static object Db(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? DBNull.Value : trimmed;
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed record PreparedUnit(
    Guid ItemUnitId,
    decimal UnitsPerPackage,
    Guid? InternalUnitItemId,
    string? InternalUnitItemNumber,
    string? InternalUnitItemName,
    bool IsActive);

public sealed record CentralItemSnapshot(StoredItem Item, IReadOnlyList<CentralPriceSnapshot> Prices);

public sealed record CentralPriceSnapshot(string PriceCode, decimal Price, string RowVersion);

public sealed record StoredItem(
    string ItemNumber,
    string ItemName,
    string? ItemType,
    string? Manufacturer,
    string? Color,
    string? Taste,
    string? Size,
    string? Packing,
    string? Classification,
    string? CountryOfOrigin,
    string? ProductType,
    Guid? ParentItemId,
    bool IsActive,
    bool IsProhibited,
    bool IsPublishedOnline,
    string RowVersion);

public static class RowVersions
{
    public static string Format(byte[] value) => Convert.ToBase64String(value);

    public static byte[] Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "إصدار السجل مطلوب.");
        }

        try
        {
            return Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            throw new ItemEditException(StatusCodes.Status400BadRequest, "إصدار السجل غير صالح.");
        }
    }
}
