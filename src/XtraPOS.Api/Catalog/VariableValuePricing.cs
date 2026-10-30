using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Catalog;

public static class VariableValuePricing
{
    public const string Mode = "VariableValueByWeight";
    public const string ReferenceItemNumber = "1300";
    public const string RoundingPolicyName = "LegacyVariableValueV1";
    public const string CostUnavailable = "Unavailable";
    public const string CostFromOwner = "StockOwnerUnitCost";

    public static decimal ApplyLegacyVariableValueRounding(decimal rawDiscountedValue)
    {
        var doubled = decimal.Round(rawDiscountedValue * 2m, 0, MidpointRounding.ToEven);
        return decimal.Floor(doubled / 2m);
    }
}

public static class VariableValueCodes
{
    public const string ReferenceItemNotFound = "REFERENCE_ITEM_NOT_FOUND";
    public const string ReferenceItemNotSellable = "REFERENCE_ITEM_NOT_SELLABLE";
    public const string BasePriceNotFound = "VARIABLE_BASE_PRICE_NOT_FOUND";
    public const string BasePriceInvalid = "VARIABLE_BASE_PRICE_INVALID";
    public const string InvalidEnteredScaleValue = "INVALID_ENTERED_SCALE_VALUE";
    public const string StockOwnerNotFound = "STOCK_OWNER_NOT_FOUND";
    public const string InvalidUnitConversion = "INVALID_UNIT_CONVERSION";
}

public sealed record VariableValueResolution(
    string PricingMode,
    Guid ReferenceItemId,
    string ReferenceItemNumber,
    Guid StockOwnerItemId,
    string StockOwnerItemNumber,
    decimal EnteredScaleValue,
    string ReferencePriceTypeCode,
    decimal ReferenceBasePrice,
    string ReferencePriceSource,
    decimal WeightKg,
    decimal WeightGrams,
    decimal StockOwnerConversionFactor,
    decimal ProjectedStockOwnerQuantity,
    Guid? CustomerId,
    decimal VariableValueDiscountPercent,
    decimal RawDiscountedValue,
    decimal FinalSellingPrice,
    string RoundingPolicy,
    decimal? ProjectedCost,
    string CostSource,
    Guid BranchId);

public sealed class VariableValuePricingResolver(SqlConnectionFactory connections, SyncEndpointStore endpoints)
{
    public async Task<VariableValueResolution> ResolveAsync(
        decimal enteredScaleValue,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        if (enteredScaleValue <= 0m)
        {
            throw new PricingException(VariableValueCodes.InvalidEnteredScaleValue, "قيمة الميزان يجب أن تكون أكبر من صفر.");
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var branchId = await ReadGuidSettingAsync(connection, "BranchID", cancellationToken);
        var businessId = await ReadGuidSettingAsync(connection, "BusinessID", cancellationToken);
        var reference = await ReadReferenceItemAsync(connection, cancellationToken);
        if (reference is null)
        {
            throw new PricingException(VariableValueCodes.ReferenceItemNotFound, "صنف القيمة المتغيرة غير موجود.");
        }

        if (!reference.Value.IsActive || reference.Value.IsProhibited)
        {
            throw new PricingException(VariableValueCodes.ReferenceItemNotSellable, "صنف القيمة المتغيرة غير متاح للبيع.");
        }

        var discountPercent = 0m;
        if (customerId is Guid customer)
        {
            var loaded = await ReadCustomerAsync(connection, customer, cancellationToken);
            if (loaded is null)
            {
                var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
                var settings = endpoint is not null && string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal)
                    ? await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken)
                    : null;
                if (settings is not null && string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal))
                {
                    await using var remote = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
                    await remote.OpenAsync(cancellationToken);
                    loaded = await ReadCustomerAsync(remote, customer, cancellationToken);
                }
            }
            if (loaded is null || loaded.Value.BusinessId != businessId)
            {
                throw new PricingException(PricingCodes.CustomerNotFound, "الزبون غير موجود.");
            }

            if (!loaded.Value.IsActive)
            {
                throw new PricingException(PricingCodes.CustomerInactive, "الزبون غير فعال.");
            }

            discountPercent = loaded.Value.DiscountPercent;
        }

        var price = await ReadPrice0Async(connection, reference.Value.ItemId, branchId, cancellationToken);
        if (price is null)
        {
            throw new PricingException(VariableValueCodes.BasePriceNotFound, "لا يوجد سعر كيلو مرجعي.");
        }

        if (price.Value.Price <= 0m)
        {
            throw new PricingException(VariableValueCodes.BasePriceInvalid, "سعر الكيلو المرجعي غير صالح.");
        }

        var owner = await ResolveOwnerAsync(connection, reference.Value.ItemId, cancellationToken);
        var weightKg = enteredScaleValue / price.Value.Price;
        var quantity = weightKg * owner.Factor;
        var raw = enteredScaleValue * (1m - (discountPercent / 100m));
        var finalPrice = VariableValuePricing.ApplyLegacyVariableValueRounding(raw);
        var unitCost = await ReadOwnerUnitCostAsync(connection, branchId, owner.ItemId, cancellationToken);
        return new VariableValueResolution(
            VariableValuePricing.Mode,
            reference.Value.ItemId,
            reference.Value.ItemNumber,
            owner.ItemId,
            owner.ItemNumber,
            enteredScaleValue,
            PricingResolver.DefaultPriceCode,
            price.Value.Price,
            price.Value.BranchId == branchId ? PriceSources.BranchOverride : PriceSources.SharedDefault,
            weightKg,
            weightKg * 1000m,
            owner.Factor,
            quantity,
            customerId,
            discountPercent,
            raw,
            finalPrice,
            VariableValuePricing.RoundingPolicyName,
            unitCost is null ? null : quantity * unitCost.Value,
            unitCost is null ? VariableValuePricing.CostUnavailable : VariableValuePricing.CostFromOwner,
            branchId);
    }

    private static async Task<(Guid ItemId, string ItemNumber, decimal Factor)> ResolveOwnerAsync(
        SqlConnection connection,
        Guid referenceItemId,
        CancellationToken cancellationToken)
    {
        var links = new List<StockUnitLink>();
        await using (var command = new SqlCommand(
            """
            SELECT ItemID, InternalUnitItemID, UnitsPerPackage
            FROM dbo.ItemUnits
            WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL AND UnitsPerPackage > 0;
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                links.Add(new StockUnitLink(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
            }
        }

        StockOwnerResolution resolution;
        try
        {
            resolution = StockOwnership.Resolve(referenceItemId, links);
        }
        catch (StockOwnershipException)
        {
            throw new PricingException(VariableValueCodes.InvalidUnitConversion, "سلسلة وحدات الصنف المرجعي غير صالحة.");
        }

        await using var ownerCommand = new SqlCommand(
            "SELECT ItemNumber FROM dbo.Items WHERE ItemID = @id;",
            connection);
        ownerCommand.Parameters.AddWithValue("@id", resolution.StockOwnerItemId);
        var number = await ownerCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new PricingException(VariableValueCodes.StockOwnerNotFound, "مالك المخزون غير موجود.");
        }

        return (resolution.StockOwnerItemId, number, resolution.BaseQuantityFactor);
    }

    private static async Task<(Guid ItemId, string ItemNumber, bool IsActive, bool IsProhibited)?> ReadReferenceItemAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT ItemID, ItemNumber, IsActive, IsProhibited
            FROM dbo.Items
            WHERE ItemNumber = @number;
            """,
            connection);
        command.Parameters.AddWithValue("@number", VariableValuePricing.ReferenceItemNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3));
    }

    private static async Task<(Guid? BranchId, decimal Price)?> ReadPrice0Async(
        SqlConnection connection,
        Guid itemId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) price.BranchID, price.Price
            FROM dbo.ItemPrices AS price
            INNER JOIN dbo.PriceTypes AS priceType ON priceType.PriceTypeID = price.PriceTypeID
            WHERE price.ItemID = @item
              AND priceType.PriceCode = @code
              AND priceType.IsActive = 1
              AND price.IsActive = 1
              AND (price.BranchID = @branch OR price.BranchID IS NULL)
            ORDER BY CASE WHEN price.BranchID = @branch THEN 0 ELSE 1 END;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@code", PricingResolver.DefaultPriceCode);
        command.Parameters.AddWithValue("@branch", branchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetDecimal(1));
    }

    private static async Task<(Guid BusinessId, bool IsActive, decimal DiscountPercent)?> ReadCustomerAsync(
        SqlConnection connection,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT BusinessID, IsActive, VariableValueDiscountPercent
            FROM dbo.Customers
            WHERE CustomerID = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetBoolean(1), reader.GetDecimal(2));
    }

    private static async Task<decimal?> ReadOwnerUnitCostAsync(
        SqlConnection connection,
        Guid branchId,
        Guid ownerItemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT UnitCost
            FROM dbo.BranchStockBalances
            WHERE BranchID = @branch AND ItemID = @item;
            """,
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@item", ownerItemId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToDecimal(value);
    }

    private static async Task<Guid> ReadGuidSettingAsync(
        SqlConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = @key;",
            connection);
        command.Parameters.AddWithValue("@key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (!Guid.TryParse(value, out var id))
        {
            throw new PricingException(VariableValueCodes.ReferenceItemNotFound, "تعذر تحديد الفرع الحالي.");
        }

        return id;
    }
}
