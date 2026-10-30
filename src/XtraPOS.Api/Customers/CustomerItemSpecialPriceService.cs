using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Customers;

public sealed record CustomerItemSpecialPriceRecord(
    Guid CustomerItemSpecialPriceId,
    Guid ItemId,
    string ItemNumber,
    string ItemName,
    decimal BasePrice,
    decimal SpecialPrice,
    decimal MinimumQuantity,
    decimal BundlePrice,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    bool IsActive,
    string RowVersion);

public sealed class CustomerItemSpecialPriceRequest
{
    public string? ItemNumber { get; set; }
    public decimal? SpecialPrice { get; set; }
    public decimal? MinimumQuantity { get; set; }
    public decimal? BundlePrice { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public string? RowVersion { get; set; }
}

public sealed class CustomerItemSpecialPriceService(SqlConnectionFactory connections, SyncEndpointStore endpoints)
{
    public async Task<IReadOnlyList<CustomerItemSpecialPriceRecord>> ListAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenCentralAsync(cancellationToken);
        return await ReadListAsync(connection, customerId, cancellationToken);
    }

    public async Task<CustomerItemSpecialPriceRecord> SaveAsync(
        Guid actorUserId,
        Guid customerId,
        Guid? specialPriceId,
        CustomerItemSpecialPriceRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new CustomerMasterException("الكمية والسعر الاستثنائي يجب أن يكونا أكبر من صفر.");
        }

        var quantity = request.MinimumQuantity ?? 1m;
        var bundle = request.BundlePrice ?? request.SpecialPrice;
        if (quantity <= 0m || bundle is not decimal bundlePrice || bundlePrice <= 0m)
        {
            throw new CustomerMasterException("الكمية والسعر الاستثنائي يجب أن يكونا أكبر من صفر.");
        }

        var price = TargetUnitPrice(bundlePrice, quantity);
        bundlePrice = decimal.Round(bundlePrice, 4, MidpointRounding.AwayFromZero);
        quantity = decimal.Round(quantity, 4, MidpointRounding.AwayFromZero);
        if (request.ValidFrom is DateTime from && request.ValidTo is DateTime to && to < from)
        {
            throw new CustomerMasterException("تاريخ نهاية السعر الخاص يسبق تاريخ البداية.");
        }

        await using var connection = await OpenCentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var item = await FindItemAsync(connection, transaction, request.ItemNumber, cancellationToken);
        var businessId = await ReadBusinessAsync(connection, transaction, customerId, cancellationToken);
        var id = specialPriceId ?? Guid.NewGuid();
        await using var command = new SqlCommand(
            specialPriceId is null
                ? """
                  INSERT INTO dbo.CustomerItemSpecialPrices (
                      CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive,
                      ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt, CreatedByUserID, UpdatedByUserID)
                  VALUES (
                      @id, @business, @customer, @item, @price, @minimum, @bundle, @active,
                      @from, @to, @notes, SYSUTCDATETIME(), SYSUTCDATETIME(), @actor, @actor);
                  """
                : """
                  UPDATE dbo.CustomerItemSpecialPrices
                  SET SpecialPrice = @price, MinimumQuantity = @minimum, BundlePrice = @bundle, IsActive = @active, ValidFrom = @from, ValidTo = @to, Notes = @notes,
                      UpdatedAt = SYSUTCDATETIME(), UpdatedByUserID = @actor
                  WHERE CustomerItemSpecialPriceID = @id AND CustomerID = @customer AND RowVersion = @version;
                  """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@item", item.ItemId);
        var special = command.Parameters.Add("@price", SqlDbType.Decimal);
        special.Precision = 19;
        special.Scale = 4;
        special.Value = price;
        var minimum = command.Parameters.Add("@minimum", SqlDbType.Decimal);
        minimum.Precision = 18;
        minimum.Scale = 4;
        minimum.Value = quantity;
        var bundleParameter = command.Parameters.Add("@bundle", SqlDbType.Decimal);
        bundleParameter.Precision = 19;
        bundleParameter.Scale = 4;
        bundleParameter.Value = bundlePrice;
        command.Parameters.AddWithValue("@active", request.IsActive);
        command.Parameters.AddWithValue("@from", (object?)request.ValidFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)request.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("@notes", (object?)Trim(request.Notes) ?? DBNull.Value);
        command.Parameters.AddWithValue("@actor", actorUserId);
        if (specialPriceId is not null)
        {
            command.Parameters.Add("@version", SqlDbType.Timestamp).Value = ParseVersion(request.RowVersion);
        }

        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new CustomerMasterException("تعذر حفظ السعر الخاص.", StatusCodes.Status409Conflict);
            }
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new CustomerMasterException("يوجد سعر خاص لهذا الصنف بالفعل.");
        }

        await LogChangeAsync(connection, transaction, id, actorUserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await CopyToBranchAsync(connection, id, cancellationToken);
        return (await ReadListAsync(connection, customerId, cancellationToken)).First(row => row.CustomerItemSpecialPriceId == id);
    }

    public static decimal TargetUnitPrice(decimal bundlePrice, decimal minimumQuantity)
    {
        if (minimumQuantity <= 0m || bundlePrice <= 0m)
        {
            throw new CustomerMasterException("الكمية والسعر الاستثنائي يجب أن يكونا أكبر من صفر.");
        }

        var price = decimal.Round(bundlePrice / minimumQuantity, 4, MidpointRounding.AwayFromZero);
        if (price <= 0m)
        {
            throw new CustomerMasterException("السعر المستهدف يجب أن يكون أكبر من صفر.");
        }

        return price;
    }

    public static (decimal MinimumQuantity, decimal BundlePrice) MigratedRule(decimal existingUnitPrice) => (1m, existingUnitPrice);

    public sealed record SpecialPriceSyncPayload(Guid CustomerItemSpecialPriceId, decimal MinimumQuantity, decimal BundlePrice, decimal TargetUnitPrice);

    public static (decimal Price, string Source) SelectResolvedPrice(
        decimal? specialPrice,
        decimal tierPrice,
        string tierSource,
        decimal minimumQuantity = 1m,
        decimal quantity = 1m)
    {
        return specialPrice is decimal special && quantity >= minimumQuantity && minimumQuantity > 0m
            ? (special, PriceSources.CustomerItemSpecial)
            : (tierPrice, tierSource);
    }

    public static async Task InsertDraftsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid actorUserId,
        Guid businessId,
        Guid customerId,
        IReadOnlyList<CustomerSpecialPriceDraft>? drafts,
        CancellationToken cancellationToken)
    {
        if (drafts is null || drafts.Count == 0)
        {
            return;
        }

        foreach (var draft in drafts)
        {
            var quantity = decimal.Round(draft.MinimumQuantity <= 0m ? throw new CustomerMasterException("الكمية والسعر الاستثنائي يجب أن يكونا أكبر من صفر.") : draft.MinimumQuantity, 4, MidpointRounding.AwayFromZero);
            var bundle = decimal.Round(draft.BundlePrice, 4, MidpointRounding.AwayFromZero);
            var price = TargetUnitPrice(bundle, quantity);
            await using var itemCommand = new SqlCommand("SELECT ItemID FROM dbo.Items WHERE ItemNumber = @number;", connection, transaction);
            itemCommand.Parameters.AddWithValue("@number", draft.ItemNumber?.Trim() ?? "");
            var item = await itemCommand.ExecuteScalarAsync(cancellationToken);
            if (item is not Guid itemId)
            {
                throw new CustomerMasterException("الصنف غير موجود.", StatusCodes.Status404NotFound);
            }

            await using var command = new SqlCommand(
                """
                INSERT INTO dbo.CustomerItemSpecialPrices (
                    CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive,
                    CreatedAt, UpdatedAt, CreatedByUserID, UpdatedByUserID)
                VALUES (
                    @id, @business, @customer, @item, @price, @minimum, @bundle, 1,
                    SYSUTCDATETIME(), SYSUTCDATETIME(), @actor, @actor);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", Guid.NewGuid());
            command.Parameters.AddWithValue("@business", businessId);
            command.Parameters.AddWithValue("@customer", customerId);
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@actor", actorUserId);
            var special = command.Parameters.Add("@price", SqlDbType.Decimal);
            special.Precision = 19;
            special.Scale = 4;
            special.Value = price;
            var minimum = command.Parameters.Add("@minimum", SqlDbType.Decimal);
            minimum.Precision = 18;
            minimum.Scale = 4;
            minimum.Value = quantity;
            var bundleParameter = command.Parameters.Add("@bundle", SqlDbType.Decimal);
            bundleParameter.Precision = 19;
            bundleParameter.Scale = 4;
            bundleParameter.Value = bundle;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task LogChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid id,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.SyncChangeLog', N'U');", connection, transaction);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        await using var read = new SqlCommand(
            """
            SELECT CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice,
                   IsActive, ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt
            FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = @id;
            """,
            connection,
            transaction);
        read.Parameters.AddWithValue("@id", id);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var payload = new SpecialPriceChange(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetDateTime(11),
            reader.GetDateTime(12));
        await reader.CloseAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload, ChangedByUserID)
            VALUES (N'CustomerItemSpecialPrices', @id, N'Upsert', @payload, @actor);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@payload", CentralChangeApply.Serialize(payload));
        command.Parameters.AddWithValue("@actor", actorUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public sealed record SpecialPriceChange(
        Guid CustomerItemSpecialPriceId,
        Guid BusinessId,
        Guid CustomerId,
        Guid ItemId,
        decimal SpecialPrice,
        decimal MinimumQuantity,
        decimal BundlePrice,
        bool IsActive,
        DateTime? ValidFrom,
        DateTime? ValidTo,
        string? Notes,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private async Task CopyToBranchAsync(SqlConnection central, Guid id, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive, ValidFrom, ValidTo, Notes,
                   CreatedAt, UpdatedAt, CreatedByUserID, UpdatedByUserID
            FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = @id;
            """,
            central);
        read.Parameters.AddWithValue("@id", id);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var values = new object[14];
        reader.GetValues(values);
        await reader.CloseAsync();
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var upsert = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = @id)
                UPDATE dbo.CustomerItemSpecialPrices
                SET SpecialPrice = @price, MinimumQuantity = @minimum, BundlePrice = @bundle, IsActive = @active, ValidFrom = @from, ValidTo = @to, Notes = @notes,
                    UpdatedAt = @updated, UpdatedByUserID = @updatedBy
                WHERE CustomerItemSpecialPriceID = @id;
            ELSE
                INSERT INTO dbo.CustomerItemSpecialPrices (
                    CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive,
                    ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt, CreatedByUserID, UpdatedByUserID)
                VALUES (
                    @id, @business, @customer, @item, @price, @minimum, @bundle, @active,
                    @from, @to, @notes, @created, @updated, @createdBy, @updatedBy);
            """,
            branch);
        upsert.Parameters.AddWithValue("@id", id);
        upsert.Parameters.AddWithValue("@business", values[0]);
        upsert.Parameters.AddWithValue("@customer", values[1]);
        upsert.Parameters.AddWithValue("@item", values[2]);
        upsert.Parameters.AddWithValue("@price", values[3]);
        upsert.Parameters.AddWithValue("@minimum", values[4]);
        upsert.Parameters.AddWithValue("@bundle", values[5]);
        upsert.Parameters.AddWithValue("@active", values[6]);
        upsert.Parameters.AddWithValue("@from", values[7]);
        upsert.Parameters.AddWithValue("@to", values[8]);
        upsert.Parameters.AddWithValue("@notes", values[9]);
        upsert.Parameters.AddWithValue("@created", values[10]);
        upsert.Parameters.AddWithValue("@updated", values[11]);
        upsert.Parameters.AddWithValue("@createdBy", values[12]);
        upsert.Parameters.AddWithValue("@updatedBy", values[13]);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<CustomerItemSpecialPriceRecord>> ReadListAsync(SqlConnection connection, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT specialRule.CustomerItemSpecialPriceID, specialRule.ItemID, item.ItemNumber, item.ItemName,
                   ISNULL((
                       SELECT TOP (1) price.Price
                       FROM dbo.ItemPrices AS price
                       INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
                       WHERE price.ItemID = item.ItemID AND priceType.PriceCode = N'Price0' AND price.IsActive = 1), 0),
                   specialRule.SpecialPrice, specialRule.MinimumQuantity, specialRule.BundlePrice,
                   specialRule.ValidFrom, specialRule.ValidTo, specialRule.IsActive, specialRule.RowVersion
            FROM dbo.CustomerItemSpecialPrices AS specialRule
            INNER JOIN dbo.Items AS item ON item.ItemID = specialRule.ItemID
            WHERE specialRule.CustomerID = @customer
            ORDER BY item.ItemNumber;
            """,
            connection);
        command.Parameters.AddWithValue("@customer", customerId);
        var rows = new List<CustomerItemSpecialPriceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CustomerItemSpecialPriceRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                reader.GetBoolean(10),
                Convert.ToBase64String((byte[])reader.GetValue(11))));
        }

        return rows;
    }

    private static async Task<(Guid ItemId, string ItemNumber)> FindItemAsync(SqlConnection connection, SqlTransaction transaction, string? itemNumber, CancellationToken cancellationToken)
    {
        var number = itemNumber?.Trim();
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new CustomerMasterException("يرجى اختيار الصنف.");
        }

        await using var command = new SqlCommand("SELECT ItemID FROM dbo.Items WHERE ItemNumber = @number;", connection, transaction);
        command.Parameters.AddWithValue("@number", number);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not Guid itemId)
        {
            throw new CustomerMasterException("الصنف غير موجود.", StatusCodes.Status404NotFound);
        }

        return (itemId, number);
    }

    private static async Task<Guid> ReadBusinessAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT BusinessID FROM dbo.Customers WHERE CustomerID = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", customerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not Guid businessId)
        {
            throw new CustomerMasterException("الزبون غير موجود.", StatusCodes.Status404NotFound);
        }

        return businessId;
    }

    private async Task<SqlConnection> OpenCentralAsync(CancellationToken cancellationToken)
    {
        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        var settings = endpoint is not null && string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal)
            ? await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken)
            : null;
        var connection = new SqlConnection(settings?.BuildConnectionString(settings.DatabaseName) ?? connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 1000 ? throw new CustomerMasterException("الملاحظة أطول من الحد المسموح.") : trimmed;
    }

    private static byte[] ParseVersion(string? rowVersion)
    {
        try
        {
            var bytes = Convert.FromBase64String(rowVersion ?? "");
            if (bytes.Length != 8)
            {
                throw new FormatException();
            }

            return bytes;
        }
        catch (FormatException)
        {
            throw new CustomerMasterException("إصدار السجل غير صالح.");
        }
    }
}
