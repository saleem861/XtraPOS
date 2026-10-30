using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Sales;

public sealed class HeldSaleException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class HeldSaleCodes
{
    public const string Empty = "EMPTY_CART";
    public const string NotDraft = "HELD_SALE_NOT_DRAFT";
    public const string Conflict = "HELD_SALE_CONFLICT";
    public const string NotFound = "HELD_SALE_NOT_FOUND";
    public const string CustomerMissing = "CUSTOMER_NOT_IN_BRANCH";
}

public sealed record HeldSaleSummary(
    Guid SaleId,
    long SaleNumber,
    DateTime CreatedAt,
    Guid? CustomerId,
    string? CustomerNumber,
    string? CustomerName,
    int LineCount,
    decimal Total,
    string? UpdatedBy,
    string? Notes,
    string RowVersion);

public sealed record HeldSaleLine(
    string LineType,
    Guid? ItemId,
    string ItemNumber,
    string ItemName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string PriceSource,
    string PriceOverrideMode,
    decimal? ManualUnitPrice,
    decimal? ManualLineTotal,
    string? PriceTypeId,
    string? PriceTypeCode,
    decimal? EnteredScaleValue,
    decimal? ReferenceBasePrice,
    decimal? WeightKg,
    decimal? WeightGrams,
    decimal? StockOwnerQuantity,
    string? StockOwnerItemId,
    string? StockOwnerItemNumber,
    decimal? ConversionFactor,
    decimal? DiscountPercent,
    decimal? FinalSellingPrice);

public sealed record HeldSaleDetail(
    Guid SaleId,
    Guid CartId,
    long SaleNumber,
    string Status,
    DateTime CreatedAt,
    Guid? CustomerId,
    string? CustomerName,
    decimal Total,
    string RowVersion,
    IReadOnlyList<HeldSaleLine> Lines);

public sealed class HeldLineInput
{
    public string LineType { get; init; } = "";
    public Guid? ItemId { get; init; }
    public decimal Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? LineTotal { get; init; }
    public string? PriceOverrideMode { get; init; }
    public Guid? StockOwnerItemId { get; init; }
    public decimal? EnteredScaleValue { get; init; }
    public decimal? ReferenceBasePrice { get; init; }
    public decimal? WeightKg { get; init; }
    public decimal? WeightGrams { get; init; }
    public decimal? ConversionFactor { get; init; }
    public decimal? StockOwnerQuantity { get; init; }
    public decimal? DiscountPercent { get; init; }
    public decimal? FinalSellingPrice { get; init; }
    public string? PriceSource { get; init; }
}

public sealed class HeldSaleService(SqlConnectionFactory connections, CustomerMasterService customers)
{
    public async Task<HeldSaleDetail> SaveAsync(
        PosDeviceContext actor,
        Guid? saleId,
        Guid cartId,
        Guid? customerId,
        string? rowVersion,
        IReadOnlyList<HeldLineInput> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            throw new HeldSaleException(HeldSaleCodes.Empty, "لا توجد أصناف لحفظها.", StatusCodes.Status400BadRequest);
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await EnsureBranchCustomerAsync(connection, actor, customerId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var id = saleId ?? Guid.NewGuid();
        if (saleId is null)
        {
            await InsertHeaderAsync(connection, transaction, actor, id, cartId, customerId, cancellationToken);
        }
        else
        {
            await ReplaceDraftAsync(connection, transaction, actor, id, customerId, rowVersion, cancellationToken);
        }

        var snapshots = await ReadSnapshotsAsync(connection, transaction, lines, cancellationToken);
        var total = decimal.Round(snapshots.Sum(line => line.LineTotal), 4, MidpointRounding.AwayFromZero);
        var number = 1;
        foreach (var line in snapshots)
        {
            await InsertLineAsync(connection, transaction, id, number, line, cancellationToken);
            number++;
        }

        await using var totals = new SqlCommand(
            """
            UPDATE dbo.Sales
            SET CustomerID = @customer, Subtotal = @total, DiscountTotal = 0, Total = @total, CreatedAt = SYSUTCDATETIME()
            WHERE SaleID = @id AND Status = N'Draft';
            """,
            connection,
            transaction);
        totals.Parameters.AddWithValue("@id", id);
        totals.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        AddMoney(totals, "@total", total);
        if (await totals.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new HeldSaleException(HeldSaleCodes.NotDraft, "تعذر تحديث المسودة.", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ReadAsync(connection, actor, id, cancellationToken)
            ?? throw new HeldSaleException(HeldSaleCodes.NotFound, "المسودة غير موجودة.", StatusCodes.Status404NotFound);
    }

    public async Task<IReadOnlyList<HeldSaleSummary>> ListAsync(
        PosDeviceContext actor,
        string? customer,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (from is not null && to is not null && to < from)
        {
            throw new HeldSaleException(HeldSaleCodes.Empty, "تاريخ النهاية أسبق من تاريخ البداية.", StatusCodes.Status400BadRequest);
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (100)
                s.SaleID, s.SaleNumber, s.CreatedAt, s.CustomerID, c.CustomerNumber, c.Name,
                (SELECT COUNT(*) FROM dbo.SaleItems i WHERE i.SaleID = s.SaleID),
                s.Total, u.DisplayName, s.RowVersion
            FROM dbo.Sales s
            LEFT JOIN dbo.Customers c ON c.CustomerID = s.CustomerID
            LEFT JOIN dbo.Users u ON u.UserID = s.CashierUserID
            WHERE s.BranchID = @branch
              AND s.POSStationID = @station
              AND s.Status = N'Draft'
              AND s.CashSessionID IS NULL
              AND (
                    @customer = N''
                    OR c.Name LIKE @like
                    OR c.CustomerNumber LIKE @like
                  )
              AND (
                    @hasRange = 0
                    OR CAST(((s.CreatedAt AT TIME ZONE 'UTC') AT TIME ZONE 'Israel Standard Time') AS date) BETWEEN @from AND @to
                  )
            ORDER BY s.CreatedAt DESC;
            """,
            connection);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@station", actor.StationId);
        var term = customer?.Trim() ?? "";
        command.Parameters.AddWithValue("@customer", term);
        command.Parameters.AddWithValue("@like", $"%{term}%");
        command.Parameters.AddWithValue("@hasRange", from is null || to is null ? 0 : 1);
        command.Parameters.AddWithValue("@from", from?.ToDateTime(TimeOnly.MinValue) ?? new DateTime(2000, 1, 1));
        command.Parameters.AddWithValue("@to", to?.ToDateTime(TimeOnly.MinValue) ?? new DateTime(2000, 1, 1));
        var rows = new List<HeldSaleSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new HeldSaleSummary(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                null,
                Convert.ToBase64String((byte[])reader[9])));
        }

        return rows;
    }

    public async Task<HeldSaleDetail> GetAsync(PosDeviceContext actor, Guid saleId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return await ReadAsync(connection, actor, saleId, cancellationToken)
            ?? throw new HeldSaleException(HeldSaleCodes.NotFound, "المسودة غير موجودة.", StatusCodes.Status404NotFound);
    }

    public async Task ReleaseAsync(PosDeviceContext actor, Guid saleId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.Sales
            SET Status = N'Cancelled'
            WHERE SaleID = @id AND BranchID = @branch AND POSStationID = @station AND Status = N'Draft' AND CashSessionID IS NULL;
            """,
            connection);
        command.Parameters.AddWithValue("@id", saleId);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@station", actor.StationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new HeldSaleException(HeldSaleCodes.NotDraft, "هذه المسودة لم تعد متاحة.", StatusCodes.Status409Conflict);
        }
    }

    private async Task EnsureBranchCustomerAsync(
        SqlConnection connection,
        PosDeviceContext actor,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        if (customerId is not Guid customer || await CustomerExistsAsync(connection, actor, customer, cancellationToken)) return;
        try
        {
            await customers.ReapplyBranchCopyAsync(customer, cancellationToken);
        }
        catch (CustomerMasterException exception)
        {
            throw new HeldSaleException(HeldSaleCodes.CustomerMissing, exception.Message, exception.StatusCode);
        }

        if (!await CustomerExistsAsync(connection, actor, customer, cancellationToken))
        {
            throw new HeldSaleException(HeldSaleCodes.CustomerMissing, "تعذر إضافة الزبون إلى الفرع.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<bool> CustomerExistsAsync(
        SqlConnection connection,
        PosDeviceContext actor,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT 1 FROM dbo.Customers WHERE CustomerID = @id AND BusinessID = @business;",
            connection);
        command.Parameters.AddWithValue("@id", customerId);
        command.Parameters.AddWithValue("@business", actor.BusinessId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertHeaderAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PosDeviceContext actor,
        Guid saleId,
        Guid cartId,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Sales
            (
                SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID,
                SaleNumber, Status, Subtotal, DiscountTotal, Total
            )
            VALUES
            (
                @sale, @business, @branch, @station, @device, @cart, @customer, @cashier,
                NEXT VALUE FOR dbo.SaleNumberSequence, N'Draft', 0, 0, 0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@business", actor.BusinessId);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@station", actor.StationId);
        command.Parameters.AddWithValue("@device", actor.DeviceId);
        command.Parameters.AddWithValue("@cart", cartId);
        command.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@cashier", actor.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceDraftAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PosDeviceContext actor,
        Guid saleId,
        Guid? customerId,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        byte[] version;
        try
        {
            version = Convert.FromBase64String(rowVersion ?? "");
        }
        catch (FormatException)
        {
            throw new HeldSaleException(HeldSaleCodes.Conflict, "تغيّرت المسودة. أعد تحميل المحفوظات.", StatusCodes.Status409Conflict);
        }

        await using var guard = new SqlCommand(
            """
            SELECT Status
            FROM dbo.Sales WITH (UPDLOCK, HOLDLOCK)
            WHERE SaleID = @id AND BranchID = @branch AND POSStationID = @station AND RowVersion = @version;
            """,
            connection,
            transaction);
        guard.Parameters.AddWithValue("@id", saleId);
        guard.Parameters.AddWithValue("@branch", actor.BranchId);
        guard.Parameters.AddWithValue("@station", actor.StationId);
        var parameter = guard.Parameters.Add("@version", SqlDbType.Timestamp, version.Length);
        parameter.Value = version;
        var status = await guard.ExecuteScalarAsync(cancellationToken) as string;
        if (status is null)
        {
            throw new HeldSaleException(HeldSaleCodes.Conflict, "تغيّرت المسودة. أعد تحميل المحفوظات.", StatusCodes.Status409Conflict);
        }

        if (status != SaleStatuses.Draft)
        {
            throw new HeldSaleException(HeldSaleCodes.NotDraft, "لا يمكن استكمال مسودة أُتمّت أو أُلغيت.", StatusCodes.Status409Conflict);
        }

        await using var delete = new SqlCommand("DELETE FROM dbo.SaleItems WHERE SaleID = @id;", connection, transaction);
        delete.Parameters.AddWithValue("@id", saleId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        _ = customerId;
    }

    private async Task<HeldSaleDetail?> ReadAsync(SqlConnection connection, PosDeviceContext actor, Guid saleId, CancellationToken cancellationToken)
    {
        await using var header = new SqlCommand(
            """
            SELECT s.SaleID, s.CartID, s.SaleNumber, s.Status, s.CreatedAt, s.CustomerID, c.Name, s.Total, s.RowVersion, s.CashSessionID
            FROM dbo.Sales s
            LEFT JOIN dbo.Customers c ON c.CustomerID = s.CustomerID
            WHERE s.SaleID = @id AND s.BranchID = @branch AND s.POSStationID = @station;
            """,
            connection);
        header.Parameters.AddWithValue("@id", saleId);
        header.Parameters.AddWithValue("@branch", actor.BranchId);
        header.Parameters.AddWithValue("@station", actor.StationId);
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = reader.GetString(3);
        var session = reader.IsDBNull(9) ? (Guid?)null : reader.GetGuid(9);
        if (status != SaleStatuses.Draft || session is not null)
        {
            throw new HeldSaleException(HeldSaleCodes.NotDraft, "لا يمكن استكمال مسودة أُتمّت أو أُلغيت.", StatusCodes.Status409Conflict);
        }

        var detail = new HeldSaleDetail(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            status,
            reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetDecimal(7),
            Convert.ToBase64String((byte[])reader[8]),
            []);
        await reader.CloseAsync();
        var lines = new List<HeldSaleLine>();
        await using var items = new SqlCommand(
            """
            SELECT PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot, Quantity, UnitPrice, LineTotal, PriceSource,
                   PriceOverrideMode, ResolvedUnitPrice, PriceTypeID, PriceTypeCodeSnapshot, EnteredScaleValue, ReferenceBasePrice,
                   WeightKg, WeightGrams, StockOwnerQuantity, StockOwnerItemID, StockOwnerItemNumberSnapshot, StockOwnerConversionFactor,
                   VariableValueDiscountPercent, FinalSellingPrice
            FROM dbo.SaleItems
            WHERE SaleID = @id
            ORDER BY LineNumber;
            """,
            connection);
        items.Parameters.AddWithValue("@id", saleId);
        await using var lineReader = await items.ExecuteReaderAsync(cancellationToken);
        while (await lineReader.ReadAsync(cancellationToken))
        {
            var mode = lineReader.GetString(8);
            lines.Add(new HeldSaleLine(
                lineReader.GetString(0),
                lineReader.GetGuid(1),
                lineReader.GetString(2),
                lineReader.GetString(3),
                lineReader.GetDecimal(4),
                lineReader.GetDecimal(5),
                lineReader.GetDecimal(6),
                lineReader.GetString(7),
                mode,
                mode == "UnitPrice" ? lineReader.GetDecimal(5) : null,
                mode == "LineTotal" ? lineReader.GetDecimal(6) : null,
                lineReader.IsDBNull(10) ? null : lineReader.GetGuid(10).ToString(),
                lineReader.IsDBNull(11) ? null : lineReader.GetString(11),
                lineReader.IsDBNull(12) ? null : lineReader.GetDecimal(12),
                lineReader.IsDBNull(13) ? null : lineReader.GetDecimal(13),
                lineReader.IsDBNull(14) ? null : lineReader.GetDecimal(14),
                lineReader.IsDBNull(15) ? null : lineReader.GetDecimal(15),
                lineReader.GetDecimal(16),
                lineReader.GetGuid(17).ToString(),
                lineReader.GetString(18),
                lineReader.GetDecimal(19),
                lineReader.IsDBNull(20) ? null : lineReader.GetDecimal(20),
                lineReader.IsDBNull(21) ? null : lineReader.GetDecimal(21)));
        }

        return detail with { Lines = lines };
    }

    private static async Task<List<Snapshot>> ReadSnapshotsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<HeldLineInput> lines,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<Snapshot>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || line.ItemId is not Guid itemId || line.UnitPrice is not decimal price)
            {
                throw new HeldSaleException(SaleCodes.InvalidQuantity, "سطر الصنف غير صالح.", StatusCodes.Status400BadRequest);
            }

            var item = await ReadItemAsync(connection, transaction, itemId, cancellationToken);
            var ownerId = line.StockOwnerItemId ?? itemId;
            var owner = ownerId == itemId ? item : await ReadItemAsync(connection, transaction, ownerId, cancellationToken);
            var mode = line.LineType == "VariableValueByWeight" ? "VariableValueByWeight" : "Fixed";
            var total = line.LineTotal ?? decimal.Round(price * line.Quantity, 4, MidpointRounding.AwayFromZero);
            var factor = line.ConversionFactor is > 0 ? line.ConversionFactor.Value : 1m;
            var ownerQuantity = line.StockOwnerQuantity is > 0 ? line.StockOwnerQuantity.Value : line.Quantity;
            snapshots.Add(new Snapshot(mode, itemId, item.Number, item.Name, line.Quantity, price, total, line.PriceOverrideMode ?? "None", price, line.PriceSource ?? mode, ownerId, owner.Number, factor, ownerQuantity, line.EnteredScaleValue, line.ReferenceBasePrice, line.WeightKg, line.WeightGrams, line.DiscountPercent, line.FinalSellingPrice));
        }

        return snapshots;
    }

    private static async Task<(string Number, string Name)> ReadItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new HeldSaleException(PricingCodes.ItemNotFound, "الصنف غير موجود.", StatusCodes.Status404NotFound);
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task InsertLineAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid saleId,
        int lineNumber,
        Snapshot line,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SaleItems
            (
                SaleItemID, SaleID, LineNumber, PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot,
                Quantity, UnitPrice, LineTotal, PriceSource, PricingResolvedAt, StockOwnerItemID,
                StockOwnerItemNumberSnapshot, StockOwnerConversionFactor, StockOwnerQuantity,
                ResolvedUnitPrice, ResolvedPriceSource, PriceOverrideMode,
                EnteredScaleValue, ReferenceBasePrice, WeightKg, WeightGrams, VariableValueDiscountPercent, FinalSellingPrice,
                ReferenceItemID, ReferenceItemNumberSnapshot, ReferenceItemNameSnapshot
            )
            VALUES
            (
                @id, @sale, @line, @mode, @item, @number, @name,
                @quantity, @unit, @total, @source, SYSUTCDATETIME(), @owner,
                @ownerNumber, @factor, @ownerQuantity,
                @resolved, @resolvedSource, @override,
                @entered, @base, @weightKg, @weightGrams, @discount, @final,
                @reference, @referenceNumber, @referenceName
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@line", lineNumber);
        command.Parameters.AddWithValue("@mode", line.Mode);
        command.Parameters.AddWithValue("@item", line.ItemId);
        command.Parameters.AddWithValue("@number", line.Number);
        command.Parameters.AddWithValue("@name", line.Name);
        AddDecimal(command, "@quantity", line.Quantity, 18, 4);
        AddMoney(command, "@unit", line.UnitPrice);
        AddMoney(command, "@total", line.LineTotal);
        command.Parameters.AddWithValue("@source", line.ResolvedSource);
        command.Parameters.AddWithValue("@owner", line.OwnerId);
        command.Parameters.AddWithValue("@ownerNumber", line.OwnerNumber);
        AddDecimal(command, "@factor", line.Factor, 38, 28);
        AddDecimal(command, "@ownerQuantity", line.OwnerQuantity, 38, 28);
        AddMoney(command, "@resolved", line.ResolvedUnitPrice);
        command.Parameters.AddWithValue("@resolvedSource", line.ResolvedSource);
        command.Parameters.AddWithValue("@override", line.OverrideMode);
        AddNullableMoney(command, "@entered", line.EnteredScaleValue);
        AddNullableMoney(command, "@base", line.ReferenceBasePrice);
        AddNullableDecimal(command, "@weightKg", line.WeightKg);
        AddNullableDecimal(command, "@weightGrams", line.WeightGrams);
        AddNullableDecimal(command, "@discount", line.DiscountPercent);
        AddNullableMoney(command, "@final", line.FinalSellingPrice);
        command.Parameters.AddWithValue("@reference", line.Mode == "VariableValueByWeight" ? line.ItemId : DBNull.Value);
        command.Parameters.AddWithValue("@referenceNumber", line.Mode == "VariableValueByWeight" ? line.Number : DBNull.Value);
        command.Parameters.AddWithValue("@referenceName", line.Mode == "VariableValueByWeight" ? line.Name : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = value;
    }

    private static void AddNullableMoney(SqlCommand command, string name, decimal? value) => AddNullableDecimal(command, name, value);

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value, byte precision, byte scale)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private sealed record Snapshot(
        string Mode,
        Guid ItemId,
        string Number,
        string Name,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineTotal,
        string OverrideMode,
        decimal ResolvedUnitPrice,
        string ResolvedSource,
        Guid OwnerId,
        string OwnerNumber,
        decimal Factor,
        decimal OwnerQuantity,
        decimal? EnteredScaleValue,
        decimal? ReferenceBasePrice,
        decimal? WeightKg,
        decimal? WeightGrams,
        decimal? DiscountPercent,
        decimal? FinalSellingPrice);
}
