using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sales;

public sealed class SalePostingService(
    SqlConnectionFactory connections,
    PricingResolver fixedPricing,
    VariableValuePricingResolver variablePricing)
{
    public async Task<SalePostResult> PostDraftAsync(SalePostCommand command, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var branchId = await ReadGuidSettingAsync(connection, "BranchID", cancellationToken);
        var businessId = await ReadGuidSettingAsync(connection, "BusinessID", cancellationToken);
        var existing = await FindSaleAsync(connection, branchId, command.CartId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        if (command.Lines.Count == 0)
        {
            throw new SaleValidationException(SaleCodes.EmptyCart, "السلة فارغة.");
        }

        ValidateQuantities(command.Lines);
        await ValidateActorAsync(connection, branchId, command.Actor, cancellationToken);

        var snapshots = new List<LineSnapshot>();
        var changes = new List<SalePriceChange>();
        var lineNumber = 1;
        foreach (var line in command.Lines)
        {
            var snapshot = line switch
            {
                FixedSaleLineInput fixedLine => await BuildFixedAsync(connection, branchId, command.CustomerId, fixedLine, lineNumber, changes, cancellationToken),
                VariableSaleLineInput variableLine => await BuildVariableAsync(command.CustomerId, variableLine, lineNumber, changes, cancellationToken),
                _ => throw new SaleValidationException(SaleCodes.InvalidQuantity, "سطر السلة غير معروف."),
            };
            snapshots.Add(snapshot);
            lineNumber++;
        }

        if (changes.Count > 0)
        {
            return new SalePriceChanged(changes);
        }

        return await InsertDraftAsync(connection, businessId, branchId, command, snapshots, cancellationToken);
    }

    private static void ValidateQuantities(IReadOnlyList<SaleLineInput> lines)
    {
        foreach (var line in lines)
        {
            if (line is FixedSaleLineInput fixedLine && fixedLine.Quantity <= 0m)
            {
                throw new SaleValidationException(SaleCodes.InvalidQuantity, "كمية البيع يجب أن تكون أكبر من صفر.");
            }
        }
    }

    private async Task<LineSnapshot> BuildFixedAsync(
        SqlConnection connection,
        Guid branchId,
        Guid? customerId,
        FixedSaleLineInput line,
        int lineNumber,
        List<SalePriceChange> changes,
        CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(connection, line.ItemId, cancellationToken);
        await RejectUnsupportedAssortmentParentAsync(connection, line.ItemId, item.Number, item.Name, cancellationToken);
        var price = await fixedPricing.ResolveFixedPriceAsync(line.ItemId, customerId, cancellationToken, line.Quantity);
        var owner = ResolveOwner(line.ItemId, await ReadLinksAsync(connection, cancellationToken));
        var ownerNumber = await ReadItemNumberAsync(connection, owner.StockOwnerItemId, cancellationToken);
        var ownerQuantity = line.Quantity * owner.BaseQuantityFactor;
        var lineTotal = decimal.Round(price.ResolvedPrice * line.Quantity, 4, MidpointRounding.AwayFromZero);
        Compare(changes, lineNumber, "UnitPrice", line.ExpectedUnitPrice, price.ResolvedPrice);
        Compare(changes, lineNumber, "StockOwnerQuantity", line.ExpectedStockOwnerQuantity, ownerQuantity);
        var cost = await ReadOwnerCostAsync(connection, branchId, owner.StockOwnerItemId, ownerQuantity, cancellationToken);
        return new LineSnapshot(
            lineNumber,
            PricingResolver.FixedMode,
            line.ItemId,
            item.Number,
            item.Name,
            line.Quantity,
            price.ResolvedPrice,
            lineTotal,
            customerId,
            price.PriceTypeId,
            price.PriceTypeCode,
            price.PriceSource,
            owner.StockOwnerItemId,
            ownerNumber,
            owner.BaseQuantityFactor,
            ownerQuantity,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            cost.Projected,
            cost.UnitCost,
            cost.LineCost,
            cost.Source);
    }

    private async Task<LineSnapshot> BuildVariableAsync(
        Guid? customerId,
        VariableSaleLineInput line,
        int lineNumber,
        List<SalePriceChange> changes,
        CancellationToken cancellationToken)
    {
        if (line.Quantity < 1m || line.Quantity != decimal.Truncate(line.Quantity))
        {
            throw new SaleValidationException(SaleCodes.InvalidQuantity, "عدد الحصص يجب أن يكون عدداً صحيحاً أكبر من صفر.");
        }

        var price = await variablePricing.ResolveAsync(line.EnteredScaleValue, customerId, cancellationToken);
        Compare(changes, lineNumber, "FinalSellingPrice", line.ExpectedFinalSellingPrice, price.FinalSellingPrice);
        Compare(changes, lineNumber, "ReferenceBasePrice", line.ExpectedReferenceBasePrice, price.ReferenceBasePrice);
        Compare(changes, lineNumber, "WeightKg", line.ExpectedWeightKg, price.WeightKg);
        Compare(changes, lineNumber, "StockOwnerQuantity", line.ExpectedStockOwnerQuantity, price.ProjectedStockOwnerQuantity);
        var portions = line.Quantity;
        var lineTotal = decimal.Round(price.FinalSellingPrice * portions, 4, MidpointRounding.AwayFromZero);
        var totalOwnerQuantity = price.ProjectedStockOwnerQuantity * portions;
        var lineCost = price.ProjectedCost is null ? (decimal?)null : decimal.Round(price.ProjectedCost.Value * portions, 4, MidpointRounding.AwayFromZero);
        var referenceName = await ReadItemNameAsync(price.ReferenceItemId, cancellationToken);
        var priceTypeId = await ReadPriceTypeIdAsync(price.ReferencePriceTypeCode, cancellationToken);
        return new LineSnapshot(
            lineNumber,
            VariableValuePricing.Mode,
            price.ReferenceItemId,
            price.ReferenceItemNumber,
            referenceName,
            portions,
            price.FinalSellingPrice,
            lineTotal,
            customerId,
            priceTypeId,
            price.ReferencePriceTypeCode,
            price.ReferencePriceSource,
            price.StockOwnerItemId,
            price.StockOwnerItemNumber,
            price.StockOwnerConversionFactor,
            totalOwnerQuantity,
            price.ReferenceItemId,
            price.ReferenceItemNumber,
            referenceName,
            price.EnteredScaleValue,
            price.ReferenceBasePrice,
            price.ReferencePriceSource,
            price.WeightKg,
            price.WeightGrams,
            price.VariableValueDiscountPercent,
            price.RawDiscountedValue,
            price.FinalSellingPrice,
            price.RoundingPolicy,
            price.ProjectedCost,
            null,
            lineCost,
            price.CostSource);
    }

    private static async Task RejectUnsupportedAssortmentParentAsync(
        SqlConnection connection,
        Guid itemId,
        string itemNumber,
        string itemName,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM dbo.Items
            WHERE ParentItemID = @item
              AND IsActive = 1;
            """,
            connection);
        command.Parameters.AddWithValue("@item", itemId);
        var children = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (children > 0)
        {
            throw new AssortmentRuleException(itemId, itemNumber, itemName);
        }
    }

    private static void Compare(List<SalePriceChange> changes, int lineNumber, string field, decimal expected, decimal authoritative)
    {
        if (expected != authoritative)
        {
            changes.Add(new SalePriceChange(lineNumber, field, expected, authoritative));
        }
    }

    private async Task<SaleDraftPosted> InsertDraftAsync(
        SqlConnection connection,
        Guid businessId,
        Guid branchId,
        SalePostCommand command,
        IReadOnlyList<LineSnapshot> lines,
        CancellationToken cancellationToken)
    {
        var subtotal = lines.Sum(line => line.LineTotal);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var saleId = Guid.NewGuid();
            await using var header = new SqlCommand(
                """
                INSERT INTO dbo.Sales
                (
                    SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID,
                    SaleNumber, Status, Subtotal, DiscountTotal, Total
                )
                OUTPUT INSERTED.SaleNumber
                VALUES
                (
                    @sale, @business, @branch, @station, @device, @cart, @customer, @cashier,
                    NEXT VALUE FOR dbo.SaleNumberSequence, @status, @subtotal, 0, @total
                );
                """,
                connection,
                transaction);
            header.Parameters.AddWithValue("@sale", saleId);
            header.Parameters.AddWithValue("@business", businessId);
            header.Parameters.AddWithValue("@branch", branchId);
            header.Parameters.AddWithValue("@station", command.Actor.StationId);
            header.Parameters.AddWithValue("@device", command.Actor.DeviceId);
            header.Parameters.AddWithValue("@cart", command.CartId);
            header.Parameters.AddWithValue("@customer", (object?)command.CustomerId ?? DBNull.Value);
            header.Parameters.AddWithValue("@cashier", command.Actor.CashierUserId);
            header.Parameters.AddWithValue("@status", SaleStatuses.Draft);
            AddMoney(header, "@subtotal", subtotal);
            AddMoney(header, "@total", subtotal);
            var saleNumber = Convert.ToInt64(await header.ExecuteScalarAsync(cancellationToken));

            foreach (var line in lines)
            {
                await InsertLineAsync(connection, transaction, saleId, line, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new SaleDraftPosted(saleId, saleNumber, false);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            var replay = await FindSaleAsync(connection, branchId, command.CartId, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            throw;
        }
    }

    private static async Task InsertLineAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid saleId,
        LineSnapshot line,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SaleItems
            (
                SaleItemID, SaleID, LineNumber, PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot,
                Quantity, UnitPrice, LineTotal, CustomerID, PriceTypeID, PriceTypeCodeSnapshot, PriceSource,
                PricingResolvedAt, StockOwnerItemID, StockOwnerItemNumberSnapshot, StockOwnerConversionFactor,
                StockOwnerQuantity, ReferenceItemID, ReferenceItemNumberSnapshot, ReferenceItemNameSnapshot,
                EnteredScaleValue, ReferenceBasePrice, ReferencePriceSource, WeightKg, WeightGrams,
                VariableValueDiscountPercent, RawDiscountedValue, FinalSellingPrice, RoundingPolicy,
                ProjectedCostAtPricing, UnitCostSnapshot, LineCostSnapshot, CostSource
            )
            VALUES
            (
                @id, @sale, @line, @mode, @item, @itemNumber, @itemName,
                @quantity, @unitPrice, @lineTotal, @customer, @priceType, @priceCode, @priceSource,
                SYSUTCDATETIME(),                 @owner, @ownerNumber, CONVERT(decimal(38, 28), @factor),
                CONVERT(decimal(38, 28), @ownerQuantity), @reference, @referenceNumber, @referenceName,
                @entered, @basePrice, @baseSource, CONVERT(decimal(38, 28), @weightKg), CONVERT(decimal(38, 28), @weightGrams),
                @discount, @raw, @final, @rounding,
                @projected, @unitCost, @lineCost, @costSource
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@line", line.LineNumber);
        command.Parameters.AddWithValue("@mode", line.PricingMode);
        command.Parameters.AddWithValue("@item", line.ItemId);
        command.Parameters.AddWithValue("@itemNumber", line.ItemNumber);
        command.Parameters.AddWithValue("@itemName", line.ItemName);
        AddDecimal(command, "@quantity", line.Quantity, 18, 4);
        AddMoney(command, "@unitPrice", line.UnitPrice);
        AddMoney(command, "@lineTotal", line.LineTotal);
        command.Parameters.AddWithValue("@customer", (object?)line.CustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceType", (object?)line.PriceTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceCode", (object?)line.PriceTypeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceSource", line.PriceSource);
        command.Parameters.AddWithValue("@owner", line.StockOwnerItemId);
        command.Parameters.AddWithValue("@ownerNumber", line.StockOwnerItemNumber);
        AddWideDecimal(command, "@factor", line.StockOwnerFactor);
        AddWideDecimal(command, "@ownerQuantity", line.StockOwnerQuantity);
        command.Parameters.AddWithValue("@reference", (object?)line.ReferenceItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@referenceNumber", (object?)line.ReferenceItemNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@referenceName", (object?)line.ReferenceItemName ?? DBNull.Value);
        AddNullableMoney(command, "@entered", line.EnteredScaleValue);
        AddNullableMoney(command, "@basePrice", line.ReferenceBasePrice);
        command.Parameters.AddWithValue("@baseSource", (object?)line.ReferencePriceSource ?? DBNull.Value);
        AddNullableWideDecimal(command, "@weightKg", line.WeightKg);
        AddNullableWideDecimal(command, "@weightGrams", line.WeightGrams);
        AddNullableDecimal(command, "@discount", line.DiscountPercent, 7, 4);
        AddNullableMoney(command, "@raw", line.RawDiscountedValue);
        AddNullableMoney(command, "@final", line.FinalSellingPrice);
        command.Parameters.AddWithValue("@rounding", (object?)line.RoundingPolicy ?? DBNull.Value);
        AddNullableMoney(command, "@projected", line.ProjectedCost);
        AddNullableMoney(command, "@unitCost", line.UnitCost);
        AddNullableMoney(command, "@lineCost", line.LineCost);
        command.Parameters.AddWithValue("@costSource", (object?)line.CostSource ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StockOwnerResolution ResolveOwner(Guid itemId, IReadOnlyList<StockUnitLink> links)
    {
        try
        {
            return StockOwnership.Resolve(itemId, links);
        }
        catch (StockOwnershipException)
        {
            throw new PricingException(VariableValueCodes.InvalidUnitConversion, "سلسلة وحدات الصنف غير صالحة.");
        }
    }

    private static async Task<SaleDraftPosted?> FindSaleAsync(
        SqlConnection connection,
        Guid branchId,
        Guid cartId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT SaleID, SaleNumber FROM dbo.Sales WHERE BranchID = @branch AND CartID = @cart;",
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@cart", cartId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SaleDraftPosted(reader.GetGuid(0), reader.GetInt64(1), true);
    }

    private static async Task ValidateActorAsync(
        SqlConnection connection,
        Guid branchId,
        SalePostActor actor,
        CancellationToken cancellationToken)
    {
        await using var device = new SqlCommand(
            "SELECT StationID FROM dbo.Devices WHERE DeviceID = @id AND BranchID = @branch AND IsActive = 1 AND RevokedAt IS NULL;",
            connection);
        device.Parameters.AddWithValue("@id", actor.DeviceId);
        device.Parameters.AddWithValue("@branch", branchId);
        var station = await device.ExecuteScalarAsync(cancellationToken);
        if (station is not Guid stationId || stationId != actor.StationId)
        {
            throw new SaleValidationException(SaleCodes.DeviceNotFound, "جهاز نقطة البيع غير معروف.");
        }

        await using var user = new SqlCommand(
            "SELECT 1 FROM dbo.Users WHERE UserID = @id AND IsActive = 1;",
            connection);
        user.Parameters.AddWithValue("@id", actor.CashierUserId);
        if (await user.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new SaleValidationException(SaleCodes.UserNotFound, "الكاشير غير معروف.");
        }
    }

    private async Task<string> ReadItemNameAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return (await ReadItemAsync(connection, itemId, cancellationToken)).Name;
    }

    private async Task<Guid?> ReadPriceTypeIdAsync(string priceCode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code;",
            connection);
        command.Parameters.AddWithValue("@code", priceCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<(string Number, string Name)> ReadItemAsync(
        SqlConnection connection,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PricingException(PricingCodes.ItemNotFound, "الصنف غير موجود.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> ReadItemNumberAsync(
        SqlConnection connection,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT ItemNumber FROM dbo.Items WHERE ItemID = @id;", connection);
        command.Parameters.AddWithValue("@id", itemId);
        var number = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new PricingException(VariableValueCodes.StockOwnerNotFound, "مالك المخزون غير موجود.");
        }

        return number;
    }

    private static async Task<List<StockUnitLink>> ReadLinksAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var links = new List<StockUnitLink>();
        await using var command = new SqlCommand(
            """
            SELECT ItemID, InternalUnitItemID, UnitsPerPackage
            FROM dbo.ItemUnits
            WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL AND UnitsPerPackage > 0;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new StockUnitLink(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
        }

        return links;
    }

    private static async Task<(decimal? UnitCost, decimal? LineCost, decimal? Projected, string? Source)> ReadOwnerCostAsync(
        SqlConnection connection,
        Guid branchId,
        Guid ownerItemId,
        decimal ownerQuantity,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT UnitCost FROM dbo.BranchStockBalances WHERE BranchID = @branch AND ItemID = @item;",
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@item", ownerItemId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not decimal unitCost)
        {
            return (null, null, null, null);
        }

        var lineCost = decimal.Round(unitCost * ownerQuantity, 4, MidpointRounding.AwayFromZero);
        return (unitCost, lineCost, lineCost, VariableValuePricing.CostFromOwner);
    }

    private static async Task<Guid> ReadGuidSettingAsync(SqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CONVERT(uniqueidentifier, SettingValue) FROM dbo.SystemSettings WHERE SettingKey = @key;",
            connection);
        command.Parameters.AddWithValue("@key", key);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException($"Missing {key}."));
    }

    private static void AddMoney(SqlCommand command, string name, decimal value) =>
        AddDecimal(command, name, value, 19, 4);

    private static void AddNullableMoney(SqlCommand command, string name, decimal? value) =>
        AddNullableDecimal(command, name, value, 19, 4);

    private static void AddDecimal(SqlCommand command, string name, decimal value, byte precision, byte scale)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = precision,
            Scale = scale,
            Value = value,
        });
    }

    private static void AddWideDecimal(SqlCommand command, string name, decimal value) =>
        command.Parameters.AddWithValue(name, value.ToString("G29", CultureInfo.InvariantCulture));

    private static void AddNullableWideDecimal(SqlCommand command, string name, decimal? value) =>
        command.Parameters.AddWithValue(name, (object?)value?.ToString("G29", CultureInfo.InvariantCulture) ?? DBNull.Value);

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value, byte precision, byte scale)
    {
        var parameter = new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = precision,
            Scale = scale,
            Value = (object?)value ?? DBNull.Value,
        };
        command.Parameters.Add(parameter);
    }

    private sealed record LineSnapshot(
        int LineNumber,
        string PricingMode,
        Guid ItemId,
        string ItemNumber,
        string ItemName,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineTotal,
        Guid? CustomerId,
        Guid? PriceTypeId,
        string? PriceTypeCode,
        string PriceSource,
        Guid StockOwnerItemId,
        string StockOwnerItemNumber,
        decimal StockOwnerFactor,
        decimal StockOwnerQuantity,
        Guid? ReferenceItemId,
        string? ReferenceItemNumber,
        string? ReferenceItemName,
        decimal? EnteredScaleValue,
        decimal? ReferenceBasePrice,
        string? ReferencePriceSource,
        decimal? WeightKg,
        decimal? WeightGrams,
        decimal? DiscountPercent,
        decimal? RawDiscountedValue,
        decimal? FinalSellingPrice,
        string? RoundingPolicy,
        decimal? ProjectedCost,
        decimal? UnitCost,
        decimal? LineCost,
        string? CostSource);
}
