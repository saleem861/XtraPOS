using Microsoft.Data.SqlClient;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Catalog;

public static class PricingCodes
{
    public const string ItemNotFound = "ITEM_NOT_FOUND";
    public const string ItemNotSellable = "ITEM_NOT_SELLABLE";
    public const string CustomerNotFound = "CUSTOMER_NOT_FOUND";
    public const string CustomerInactive = "CUSTOMER_INACTIVE";
    public const string PriceTypeNotFound = "PRICE_TYPE_NOT_FOUND";
    public const string PriceNotFound = "PRICE_NOT_FOUND";
    public const string SpecialPriceUnconfirmed = "SPECIAL_PRICE_UNCONFIRMED";
}

public static class PriceSources
{
    public const string BranchOverride = "BranchOverride";
    public const string SharedDefault = "CentralDefault";
    public const string CustomerItemSpecial = "CustomerItemSpecial";
}

public sealed class PricingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record FixedPriceResolution(
    Guid ItemId,
    Guid? CustomerId,
    Guid PriceTypeId,
    string PriceTypeCode,
    decimal BasePrice,
    decimal ResolvedPrice,
    string PriceSource,
    Guid BranchId,
    string PricingMode,
    Guid? AppliedSpecialPriceId = null,
    decimal? AppliedMinimumQuantity = null,
    decimal? AppliedBundlePrice = null,
    decimal? AppliedTargetUnitPrice = null);

public sealed class PricingResolver(SqlConnectionFactory connections, SyncEndpointStore endpoints)
{
    public const string FixedMode = "Fixed";
    public const string DefaultPriceCode = "Price0";

    public async Task<FixedPriceResolution> ResolveFixedPriceAsync(
        Guid itemId,
        Guid? customerId,
        CancellationToken cancellationToken,
        decimal quantity = 1m,
        bool applySpecial = true)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var branchId = await ReadGuidSettingAsync(connection, "BranchID", cancellationToken);
        var businessId = await ReadGuidSettingAsync(connection, "BusinessID", cancellationToken);

        var item = await ReadItemAsync(connection, itemId, cancellationToken);
        if (item is null)
        {
            throw new PricingException(PricingCodes.ItemNotFound, "الصنف غير موجود.");
        }

        if (!item.Value.IsActive || item.Value.IsProhibited)
        {
            throw new PricingException(PricingCodes.ItemNotSellable, "الصنف غير متاح للبيع.");
        }

        Guid? requestedPriceTypeId = null;
        if (customerId is Guid customer)
        {
            var loaded = await ReadCustomerAsync(connection, customer, cancellationToken);
            if (loaded is null)
            {
                loaded = await ReadCentralCustomerAsync(customer, cancellationToken);
            }
            if (loaded is null || loaded.Value.BusinessId != businessId)
            {
                throw new PricingException(PricingCodes.CustomerNotFound, "الزبون غير موجود.");
            }

            if (!loaded.Value.IsActive)
            {
                throw new PricingException(PricingCodes.CustomerInactive, "الزبون غير فعال.");
            }

            requestedPriceTypeId = loaded.Value.PriceTypeId;
        }

        var priceType = requestedPriceTypeId is Guid selectedType
            ? await ReadPriceTypeByIdAsync(connection, selectedType, cancellationToken)
            : await ReadPriceTypeByCodeAsync(connection, DefaultPriceCode, cancellationToken);
        if (priceType is null)
        {
            throw new PricingException(PricingCodes.PriceTypeNotFound, "نوع السعر غير موجود.");
        }

        var price = await ReadPriceAsync(connection, itemId, priceType.Value.PriceTypeId, branchId, cancellationToken);
        if (price is null)
        {
            throw new PricingException(PricingCodes.PriceNotFound, "لا يوجد سعر لهذا النوع.");
        }

        var basePrice = price.Value.Price;
        if (!string.Equals(priceType.Value.PriceCode, DefaultPriceCode, StringComparison.Ordinal))
        {
            var standardType = await ReadPriceTypeByCodeAsync(connection, DefaultPriceCode, cancellationToken);
            if (standardType is not null)
            {
                var standard = await ReadPriceAsync(connection, itemId, standardType.Value.PriceTypeId, branchId, cancellationToken);
                if (standard is not null)
                {
                    basePrice = standard.Value.Price;
                }
            }
        }

        var tierSource = price.Value.BranchId == branchId ? PriceSources.BranchOverride : PriceSources.SharedDefault;
        var special = applySpecial && customerId is Guid specialCustomer
            ? await ReadSpecialPriceAsync(connection, specialCustomer, itemId, cancellationToken)
            : null;
        var selected = CustomerItemSpecialPriceService.SelectResolvedPrice(
            special?.TargetUnitPrice,
            price.Value.Price,
            tierSource,
            special?.MinimumQuantity ?? 1m,
            quantity);
        var applied = selected.Source == PriceSources.CustomerItemSpecial;
        return new FixedPriceResolution(
            itemId,
            customerId,
            priceType.Value.PriceTypeId,
            priceType.Value.PriceCode,
            basePrice,
            selected.Price,
            selected.Source,
            branchId,
            FixedMode,
            applied ? special?.CustomerItemSpecialPriceId : null,
            applied ? special?.MinimumQuantity : null,
            applied ? special?.BundlePrice : null,
            applied ? special?.TargetUnitPrice : null);
    }

    private sealed record SpecialPriceRow(Guid CustomerItemSpecialPriceId, decimal TargetUnitPrice, decimal MinimumQuantity, decimal BundlePrice);

    private async Task<SpecialPriceRow?> ReadSpecialPriceAsync(SqlConnection branch, Guid customerId, Guid itemId, CancellationToken cancellationToken)
    {
        var local = await ReadSpecialPriceRowAsync(branch, customerId, itemId, cancellationToken);
        if (local is not null)
        {
            return local;
        }

        if (await ReplicaOwnsSpecialsAsync(branch, cancellationToken))
        {
            return null;
        }

        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null || endpoint is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            await using var central = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await central.OpenAsync(cancellationToken);
            return await ReadSpecialPriceRowAsync(central, customerId, itemId, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            throw new PricingException(PricingCodes.SpecialPriceUnconfirmed, "تعذر تأكيد السعر الاستثنائي للزبون. لن يُستخدم سعر اللائحة بدلاً منه.");
        }
    }

    private static async Task<bool> ReplicaOwnsSpecialsAsync(SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'CustomerReplicaState';",
            branch);
        var state = await command.ExecuteScalarAsync(cancellationToken) as string;
        return state is SharedDataSync.ReplicaReady or SharedDataSync.ReplicaStale;
    }

    private static async Task<SpecialPriceRow?> ReadSpecialPriceRowAsync(SqlConnection connection, Guid customerId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) CustomerItemSpecialPriceID, SpecialPrice, MinimumQuantity, BundlePrice
            FROM dbo.CustomerItemSpecialPrices
            WHERE CustomerID = @customer AND ItemID = @item AND IsActive = 1
              AND (ValidFrom IS NULL OR ValidFrom <= SYSUTCDATETIME())
              AND (ValidTo IS NULL OR ValidTo >= SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SpecialPriceRow(reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3));
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
            throw new PricingException(PricingCodes.PriceNotFound, "تعذر تحديد الفرع الحالي.");
        }

        return id;
    }

    private static async Task<(bool IsActive, bool IsProhibited)?> ReadItemAsync(
        SqlConnection connection,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT IsActive, IsProhibited FROM dbo.Items WHERE ItemID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task<(Guid BusinessId, bool IsActive, Guid? PriceTypeId)?> ReadCentralCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (endpoint is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return null;
        }

        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null || !string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return null;
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        return await ReadCustomerAsync(connection, customerId, cancellationToken);
    }

    private static async Task<(Guid BusinessId, bool IsActive, Guid? PriceTypeId)?> ReadCustomerAsync(
        SqlConnection connection,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT BusinessID, IsActive, PriceTypeID
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

        return (
            reader.GetGuid(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task<(Guid PriceTypeId, string PriceCode)?> ReadPriceTypeByIdAsync(
        SqlConnection connection,
        Guid priceTypeId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT PriceTypeID, PriceCode FROM dbo.PriceTypes WHERE PriceTypeID = @id AND IsActive = 1;",
            connection);
        command.Parameters.AddWithValue("@id", priceTypeId);
        return await ReadPriceTypeAsync(command, cancellationToken);
    }

    private static async Task<(Guid PriceTypeId, string PriceCode)?> ReadPriceTypeByCodeAsync(
        SqlConnection connection,
        string priceCode,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT PriceTypeID, PriceCode FROM dbo.PriceTypes WHERE PriceCode = @code AND IsActive = 1;",
            connection);
        command.Parameters.AddWithValue("@code", priceCode);
        return await ReadPriceTypeAsync(command, cancellationToken);
    }

    private static async Task<(Guid PriceTypeId, string PriceCode)?> ReadPriceTypeAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetString(1));
    }

    private static async Task<(Guid? BranchId, decimal Price)?> ReadPriceAsync(
        SqlConnection connection,
        Guid itemId,
        Guid priceTypeId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) BranchID, Price
            FROM dbo.ItemPrices
            WHERE ItemID = @item
              AND PriceTypeID = @type
              AND IsActive = 1
              AND (BranchID = @branch OR BranchID IS NULL)
            ORDER BY CASE WHEN BranchID = @branch THEN 0 ELSE 1 END;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@type", priceTypeId);
        command.Parameters.AddWithValue("@branch", branchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetDecimal(1));
    }
}
