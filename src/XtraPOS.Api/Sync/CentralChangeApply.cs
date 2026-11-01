using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Customers;

namespace XtraPOS.Api.Sync;

public static class BranchCustomerReplacement
{
    private static readonly HashSet<string> CustomerOwnedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CustomerAdministrationAudit",
        "CustomerPhoneVerificationChallenges",
    };

    public static bool IsCustomerOwned(string table) => CustomerOwnedTables.Contains(table);

    public static bool CanReplace(IReadOnlyDictionary<string, int> dependencyCounts) =>
        dependencyCounts.All(pair => pair.Value == 0 || IsCustomerOwned(pair.Key));
}

public static class CentralChangeApply
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static bool CanApply(string entityType) =>
        entityType is "Items" or "ItemUnits" or "ItemPrices" or "Customers" or "CustomerClassifications" or "CustomerItemSpecialPrices";

    public static async Task ApplyAsync(
        SqlConnection connection,
        string entityType,
        Guid entityId,
        string? payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload) || !CanApply(entityType))
        {
            return;
        }

        using var document = JsonDocument.Parse(payload);
        if (entityType == "Items")
        {
            await ApplyItemAsync(connection, entityId, document.RootElement, cancellationToken);
        }
        else if (entityType == "ItemUnits")
        {
            await ApplyUnitsAsync(connection, entityId, document.RootElement, cancellationToken);
        }
        else if (entityType == "ItemPrices")
        {
            await ApplyDefaultPriceAsync(connection, entityId, document.RootElement, cancellationToken);
        }
        else if (entityType is "Customers" or "CustomerClassifications")
        {
            await ApplyCustomerAsync(connection, null, entityType, payload, authoritative: false, cancellationToken);
        }
        else if (entityType == "CustomerItemSpecialPrices")
        {
            await ApplySpecialPriceAsync(connection, null, payload, cancellationToken);
        }
    }

    public static async Task ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string entityType,
        Guid entityId,
        string? payload,
        CancellationToken cancellationToken,
        bool authoritative = false)
    {
        if (entityType is "Customers" or "CustomerClassifications")
        {
            await ApplyCustomerAsync(connection, transaction, entityType, payload, authoritative, cancellationToken);
            return;
        }

        if (entityType == "CustomerItemSpecialPrices")
        {
            await ApplySpecialPriceAsync(connection, transaction, payload, cancellationToken);
            return;
        }

        await ApplyAsync(connection, entityType, entityId, payload, cancellationToken);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static async Task ApplySpecialPriceAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string? payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        var row = JsonSerializer.Deserialize<CustomerItemSpecialPriceService.SpecialPriceChange>(payload, Json);
        if (row is null)
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = @id)
                UPDATE dbo.CustomerItemSpecialPrices
                SET SpecialPrice = @price, MinimumQuantity = @minimum, BundlePrice = @bundle, IsActive = @active,
                    ValidFrom = @from, ValidTo = @to, Notes = @notes, UpdatedAt = @updated
                WHERE CustomerItemSpecialPriceID = @id
                  AND (UpdatedAt IS NULL OR @updated >= UpdatedAt);
            ELSE
                INSERT INTO dbo.CustomerItemSpecialPrices (
                    CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive,
                    ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt)
                VALUES (
                    @id, @business, @customer, @item, @price, @minimum, @bundle, @active,
                    @from, @to, @notes, @created, @updated);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", row.CustomerItemSpecialPriceId);
        command.Parameters.AddWithValue("@business", row.BusinessId);
        command.Parameters.AddWithValue("@customer", row.CustomerId);
        command.Parameters.AddWithValue("@item", row.ItemId);
        command.Parameters.AddWithValue("@price", row.SpecialPrice);
        command.Parameters.AddWithValue("@minimum", row.MinimumQuantity);
        command.Parameters.AddWithValue("@bundle", row.BundlePrice);
        command.Parameters.AddWithValue("@active", row.IsActive);
        command.Parameters.AddWithValue("@from", (object?)row.ValidFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("@to", (object?)row.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("@notes", (object?)row.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", row.CreatedAt);
        command.Parameters.AddWithValue("@updated", row.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyItemAsync(
        SqlConnection connection,
        Guid itemId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var sets = new List<string>();
        await using var command = new SqlCommand { Connection = connection };
        AddString(payload, command, sets, "itemName", "ItemName");
        AddString(payload, command, sets, "itemType", "ItemType");
        AddString(payload, command, sets, "manufacturer", "Manufacturer");
        AddString(payload, command, sets, "color", "Color");
        AddString(payload, command, sets, "taste", "Taste");
        AddString(payload, command, sets, "size", "Size");
        AddString(payload, command, sets, "packing", "Packing");
        AddString(payload, command, sets, "classification", "Classification");
        AddString(payload, command, sets, "countryOfOrigin", "CountryOfOrigin");
        AddString(payload, command, sets, "productType", "ProductType");
        AddGuid(payload, command, sets, "parentItemId", "ParentItemID");
        AddBool(payload, command, sets, "isActive", "IsActive");
        AddBool(payload, command, sets, "isProhibited", "IsProhibited");
        AddBool(payload, command, sets, "isPublishedOnline", "IsPublishedOnline");
        if (sets.Count == 0)
        {
            return;
        }

        sets.Add("UpdatedAt = SYSUTCDATETIME()");
        command.CommandText = $"UPDATE dbo.Items SET {string.Join(", ", sets)} WHERE ItemID = @id;";
        command.Parameters.AddWithValue("@id", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyUnitsAsync(
        SqlConnection connection,
        Guid itemId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (payload.TryGetProperty("removeItemUnitIds", out var removed) && removed.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in removed.EnumerateArray())
            {
                if (!Guid.TryParse(entry.GetString(), out var unitId))
                {
                    continue;
                }

                await using var delete = new SqlCommand(
                    "DELETE FROM dbo.ItemUnits WHERE ItemUnitID = @id AND ItemID = @item;",
                    connection);
                delete.Parameters.AddWithValue("@id", unitId);
                delete.Parameters.AddWithValue("@item", itemId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (!payload.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var unit in units.EnumerateArray())
        {
            if (!unit.TryGetProperty("itemUnitId", out var idElement) || !Guid.TryParse(idElement.GetString(), out var unitId))
            {
                continue;
            }

            var factor = unit.TryGetProperty("unitsPerPackage", out var factorElement) ? factorElement.GetDecimal() : 1m;
            Guid? internalId = unit.TryGetProperty("internalUnitItemId", out var internalElement) && internalElement.ValueKind != JsonValueKind.Null
                && Guid.TryParse(internalElement.GetString(), out var parsed)
                ? parsed
                : null;
            var number = Text(unit, "internalUnitItemNumber");
            var name = Text(unit, "internalUnitItemName");
            var active = !unit.TryGetProperty("isActive", out var activeElement) || activeElement.GetBoolean();
            var baseUnit = unit.TryGetProperty("isBaseUnit", out var baseElement) && baseElement.GetBoolean();

            await using var command = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.ItemUnits WHERE ItemUnitID = @id)
                    UPDATE dbo.ItemUnits
                    SET UnitsPerPackage = @factor,
                        InternalUnitItemID = @internalId,
                        InternalUnitItemNumber = @number,
                        InternalUnitItemName = @name,
                        IsBaseUnit = @baseUnit,
                        IsActive = @active,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE ItemUnitID = @id AND ItemID = @item;
                ELSE
                    INSERT INTO dbo.ItemUnits
                        (ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt)
                    VALUES
                        (@id, @item, @factor, @internalId, @number, @name, @baseUnit, @active, SYSUTCDATETIME(), SYSUTCDATETIME());
                """,
                connection);
            command.Parameters.AddWithValue("@id", unitId);
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@factor", factor);
            command.Parameters.AddWithValue("@internalId", (object?)internalId ?? DBNull.Value);
            command.Parameters.AddWithValue("@number", (object?)number ?? DBNull.Value);
            command.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("@baseUnit", baseUnit);
            command.Parameters.AddWithValue("@active", active);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyDefaultPriceAsync(
        SqlConnection connection,
        Guid itemId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("price", out var priceElement))
        {
            return;
        }

        var price = priceElement.GetDecimal();
        var active = !payload.TryGetProperty("isActive", out var activeElement) || activeElement.GetBoolean();
        Guid priceTypeId;
        if (payload.TryGetProperty("priceTypeId", out var typeElement) && Guid.TryParse(typeElement.GetString(), out var parsedType))
        {
            priceTypeId = parsedType;
        }
        else if (payload.TryGetProperty("priceCode", out var codeElement))
        {
            await using var lookup = new SqlCommand(
                "SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code;",
                connection);
            lookup.Parameters.AddWithValue("@code", codeElement.GetString() ?? "");
            var found = await lookup.ExecuteScalarAsync(cancellationToken);
            if (found is not Guid typeId)
            {
                return;
            }

            priceTypeId = typeId;
        }
        else
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            UPDATE dbo.ItemPrices
            SET Price = @price, IsActive = @active, UpdatedAt = SYSUTCDATETIME()
            WHERE ItemID = @item AND PriceTypeID = @type AND BranchID IS NULL;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.ItemPrices (ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                VALUES (@item, @type, NULL, @price, @active, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@type", priceTypeId);
        command.Parameters.AddWithValue("@price", price);
        command.Parameters.AddWithValue("@active", active);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddString(JsonElement payload, SqlCommand command, List<string> sets, string jsonName, string column)
    {
        if (!payload.TryGetProperty(jsonName, out var value))
        {
            return;
        }

        sets.Add($"{column} = @{column}");
        command.Parameters.AddWithValue("@" + column, value.ValueKind == JsonValueKind.Null ? DBNull.Value : value.GetString() ?? "");
    }

    private static void AddBool(JsonElement payload, SqlCommand command, List<string> sets, string jsonName, string column)
    {
        if (!payload.TryGetProperty(jsonName, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return;
        }

        sets.Add($"{column} = @{column}");
        command.Parameters.AddWithValue("@" + column, value.GetBoolean());
    }

    private static void AddGuid(JsonElement payload, SqlCommand command, List<string> sets, string jsonName, string column)
    {
        if (!payload.TryGetProperty(jsonName, out var value))
        {
            return;
        }

        sets.Add($"{column} = @{column}");
        if (value.ValueKind == JsonValueKind.Null || !Guid.TryParse(value.GetString(), out var id))
        {
            command.Parameters.AddWithValue("@" + column, DBNull.Value);
            return;
        }

        command.Parameters.AddWithValue("@" + column, id);
    }

    private static async Task ApplyCustomerAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string entityType,
        string? payload,
        bool authoritative,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        if (entityType == "CustomerClassifications")
        {
            var classification = JsonSerializer.Deserialize<CustomerSyncClassification>(payload, Json);
            if (classification is not null)
            {
                await UpsertClassificationAsync(connection, transaction, classification, cancellationToken);
            }

            return;
        }

        var customer = JsonSerializer.Deserialize<CustomerSyncPayload>(payload, Json);
        if (customer is null)
        {
            return;
        }

        if (customer.Classification is not null)
        {
            await UpsertClassificationAsync(connection, transaction, customer.Classification, cancellationToken);
        }

        var priceTypeId = await ResolvePriceTypeAsync(connection, transaction, customer.PriceTypeId, customer.PriceCode, cancellationToken);
        await ReleaseCustomerNumberAsync(connection, transaction, customer.BusinessId, customer.CustomerId, customer.CustomerNumber, cancellationToken);
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerID = @id)
                UPDATE dbo.Customers
                SET BusinessID = @business,
                    CustomerNumber = @number,
                    Name = @name,
                    NameSearchStrict = @nameStrict,
                    NameSearchLoose = @nameLoose,
                    Phone = @phone,
                    NormalizedPhone = @normalized,
                    SecondaryPhone = @secondaryPhone,
                    SecondaryNormalizedPhone = @secondaryNormalized,
                    CustomerClassificationID = @classification,
                    PriceTypeID = @priceType,
                    VariableValueDiscountPercent = @discount,
                    IsActive = @active,
                    Notes = @notes,
                    LegacySource = @legacySource,
                    LegacyCustomerKey = @legacyKey,
                    WhatsAppPhoneE164 = @whatsApp,
                    PhoneVerificationStatus = @phoneStatus,
                    PhoneVerifiedAt = @verifiedAt,
                    PhoneVerifiedE164 = @verifiedPhone,
                    UpdatedAt = @updatedAt
                WHERE CustomerID = @id
                  AND (@authoritative = 1 OR UpdatedAt IS NULL OR @updatedAt >= UpdatedAt);
            ELSE
                INSERT INTO dbo.Customers (
                    CustomerID, BusinessID, CustomerNumber, Name, NameSearchStrict, NameSearchLoose, Phone, NormalizedPhone,
                    SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                    VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey,
                    WhatsAppPhoneE164, PhoneVerificationStatus, PhoneVerifiedAt, PhoneVerifiedE164,
                    CreatedAt, UpdatedAt)
                VALUES (
                    @id, @business, @number, @name, @nameStrict, @nameLoose, @phone, @normalized,
                    @secondaryPhone, @secondaryNormalized, @classification, @priceType,
                    @discount, @active, @notes, @legacySource, @legacyKey,
                    @whatsApp, @phoneStatus, @verifiedAt, @verifiedPhone,
                    @createdAt, @updatedAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@nameStrict", ArabicSearchNormalizer.NormalizeStrict(customer.Name));
        command.Parameters.AddWithValue("@nameLoose", ArabicSearchNormalizer.NormalizeLoose(customer.Name));
        command.Parameters.AddWithValue("@authoritative", authoritative);
        command.Parameters.AddWithValue("@id", customer.CustomerId);
        command.Parameters.AddWithValue("@business", customer.BusinessId);
        command.Parameters.AddWithValue("@number", (object?)customer.CustomerNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@name", customer.Name);
        command.Parameters.AddWithValue("@phone", (object?)customer.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("@normalized", (object?)customer.NormalizedPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@secondaryPhone", (object?)customer.SecondaryPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@secondaryNormalized", (object?)customer.SecondaryNormalizedPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@classification", (object?)customer.CustomerClassificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceType", (object?)priceTypeId ?? DBNull.Value);
        var discount = command.Parameters.Add("@discount", SqlDbType.Decimal);
        discount.Precision = 7;
        discount.Scale = 4;
        discount.Value = customer.VariableValueDiscountPercent;
        command.Parameters.AddWithValue("@active", customer.IsActive);
        command.Parameters.AddWithValue("@notes", (object?)customer.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("@legacySource", (object?)customer.LegacySource ?? DBNull.Value);
        command.Parameters.AddWithValue("@legacyKey", (object?)customer.LegacyCustomerKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@whatsApp", (object?)customer.WhatsAppPhoneE164 ?? DBNull.Value);
        command.Parameters.AddWithValue("@phoneStatus", string.IsNullOrWhiteSpace(customer.PhoneVerificationStatus) ? "Unverified" : customer.PhoneVerificationStatus);
        command.Parameters.AddWithValue("@verifiedAt", (object?)customer.PhoneVerifiedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@verifiedPhone", (object?)customer.PhoneVerifiedE164 ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", customer.CreatedAt);
        command.Parameters.AddWithValue("@updatedAt", customer.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertClassificationAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CustomerSyncClassification classification,
        CancellationToken cancellationToken)
    {
        var priceTypeId = await ResolvePriceTypeAsync(
            connection,
            transaction,
            classification.DefaultPriceTypeId,
            classification.DefaultPriceCode,
            cancellationToken);
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.CustomerClassifications WHERE CustomerClassificationID = @id)
                UPDATE dbo.CustomerClassifications
                SET BusinessID = @business,
                    Code = @code,
                    Name = @name,
                    DefaultPriceTypeID = @priceType,
                    DefaultVariableValueDiscountPercent = @discount,
                    IsDefault = @isDefault,
                    IsActive = @active,
                    UpdatedAt = @updatedAt
                WHERE CustomerClassificationID = @id;
            ELSE
                INSERT INTO dbo.CustomerClassifications (
                    CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
                    DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                VALUES (
                    @id, @business, @code, @name, @priceType,
                    @discount, @isDefault, @active, @createdAt, @updatedAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", classification.CustomerClassificationId);
        command.Parameters.AddWithValue("@business", classification.BusinessId);
        command.Parameters.AddWithValue("@code", (object?)classification.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("@name", classification.Name);
        command.Parameters.AddWithValue("@priceType", (object?)priceTypeId ?? DBNull.Value);
        var discount = command.Parameters.Add("@discount", SqlDbType.Decimal);
        discount.Precision = 7;
        discount.Scale = 4;
        discount.Value = classification.DefaultVariableValueDiscountPercent;
        command.Parameters.AddWithValue("@isDefault", classification.IsDefault);
        command.Parameters.AddWithValue("@active", classification.IsActive);
        command.Parameters.AddWithValue("@createdAt", classification.CreatedAt);
        command.Parameters.AddWithValue("@updatedAt", classification.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseCustomerNumberAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid businessId,
        Guid customerId,
        string? customerNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerNumber))
        {
            return;
        }

        await using var lookup = new SqlCommand(
            """
            SELECT CustomerID
            FROM dbo.Customers
            WHERE BusinessID = @business AND CustomerNumber = @number AND CustomerID <> @id;
            """,
            connection,
            transaction);
        lookup.Parameters.AddWithValue("@business", businessId);
        lookup.Parameters.AddWithValue("@number", customerNumber);
        lookup.Parameters.AddWithValue("@id", customerId);
        var other = await lookup.ExecuteScalarAsync(cancellationToken);
        if (other is not Guid staleId)
        {
            return;
        }

        var dependencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var references = new List<(string Table, string Column)>();
        await using (var relations = new SqlCommand(
            """
            SELECT OBJECT_NAME(parent.object_id) AS TableName, COL_NAME(parent.object_id, parent.column_id) AS ColumnName
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS map ON map.constraint_object_id = fk.object_id
            INNER JOIN sys.columns AS parent ON parent.object_id = map.parent_object_id AND parent.column_id = map.parent_column_id
            WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.Customers');
            """,
            connection,
            transaction))
        {
            await using var relationReader = await relations.ExecuteReaderAsync(cancellationToken);
            while (await relationReader.ReadAsync(cancellationToken))
            {
                references.Add((relationReader.GetString(0), relationReader.GetString(1)));
            }

            await relationReader.CloseAsync();
            foreach (var reference in references)
            {
                await using var count = new SqlCommand(
                    $"SELECT COUNT(*) FROM dbo.[{reference.Table}] WHERE [{reference.Column}] = @id;",
                    connection,
                    transaction);
                count.Parameters.AddWithValue("@id", staleId);
                var rows = (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
                dependencies[reference.Table] = dependencies.GetValueOrDefault(reference.Table) + rows;
            }
        }

        if (!BranchCustomerReplacement.CanReplace(dependencies))
        {
            var blocked = string.Join(
                ", ",
                dependencies.Where(pair => pair.Value > 0 && !BranchCustomerReplacement.IsCustomerOwned(pair.Key)).Select(pair => pair.Key));
            throw new InvalidOperationException("Branch customer number belongs to a customer that still has dependent records: " + blocked + ".");
        }

        foreach (var reference in references)
        {
            await using var deleteChild = new SqlCommand(
                $"DELETE FROM dbo.[{reference.Table}] WHERE [{reference.Column}] = @id;",
                connection,
                transaction);
            deleteChild.Parameters.AddWithValue("@id", staleId);
            await deleteChild.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deleteCustomer = new SqlCommand(
            "DELETE FROM dbo.Customers WHERE CustomerID = @id;",
            connection,
            transaction);
        deleteCustomer.Parameters.AddWithValue("@id", staleId);
        await deleteCustomer.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> ResolvePriceTypeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid? priceTypeId,
        string? priceCode,
        CancellationToken cancellationToken)
    {
        if (priceTypeId is null)
        {
            return null;
        }

        await using (var exists = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.PriceTypes WHERE PriceTypeID = @id;",
            connection,
            transaction))
        {
            exists.Parameters.AddWithValue("@id", priceTypeId.Value);
            if ((int)(await exists.ExecuteScalarAsync(cancellationToken) ?? 0) == 1)
            {
                return priceTypeId;
            }
        }

        if (string.IsNullOrWhiteSpace(priceCode))
        {
            throw new InvalidOperationException("Customer price type is not present on the branch.");
        }

        await using var mapped = new SqlCommand(
            "SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code;",
            connection,
            transaction);
        mapped.Parameters.AddWithValue("@code", priceCode);
        var value = await mapped.ExecuteScalarAsync(cancellationToken);
        return value is Guid resolved
            ? resolved
            : throw new InvalidOperationException("Customer price type is not present on the branch.");
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
