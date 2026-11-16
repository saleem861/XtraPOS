using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Operations;

namespace XtraPOS.Api.Sales;

public static class SaleCentralRelay
{
    public const string OperationType = "CompletedSale";

    public static async Task EnqueueCompletedAsync(SqlConnection branch, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var table = new SqlCommand("SELECT OBJECT_ID(N'dbo.BranchOperationLog', N'U');", branch, transaction);
        if (await table.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        await using var sale = new SqlCommand(
            "SELECT SaleNumber, BranchID, POSStationID, CashierUserID FROM dbo.Sales WHERE SaleID = @sale;",
            branch,
            transaction);
        sale.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await sale.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var number = reader.GetInt64(0);
        var branchId = reader.GetGuid(1);
        var stationId = reader.GetGuid(2);
        var userId = reader.GetGuid(3);
        await reader.CloseAsync();
        await BranchOperationLog.EnqueueAsync(branch, transaction, new BranchOperationDraft(
            saleId,
            OperationType,
            saleId.ToString("N"),
            string.Create(CultureInfo.InvariantCulture, $"ترحيل بيعة {number}"),
            "Sales",
            saleId,
            branchId,
            userId,
            stationId), cancellationToken);
    }

    public static async Task<string?> RelayAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID, SourceEntityID
            FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled')
              AND OperationType = N'CompletedSale'
            ORDER BY Serial;
            """,
            branch);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var operationId = reader.GetGuid(0);
        var saleId = reader.GetGuid(1);
        await reader.CloseAsync();
        try
        {
            await CopyAsync(branch, central, saleId, cancellationToken);
            await BranchOperationLog.MarkAsync(branch, null, operationId, "Completed", "تم ترحيل البيعة إلى المركز.", cancellationToken);
            return "Completed";
        }
        catch (SqlException exception) when (exception.Number is 208 or 547 or 2601 or 2627)
        {
            await BranchOperationLog.MarkAsync(branch, null, operationId, "Blocked", "تعذر ترحيل البيعة إلى جداول المركز.", cancellationToken);
            return "Blocked";
        }
    }

    private static async Task CopyAsync(SqlConnection branch, SqlConnection central, Guid saleId, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(central, "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Sales WHERE SaleID = @id) THEN 1 ELSE 0 END;", saleId, cancellationToken))
        {
            return;
        }

        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken);
        await EnsureBranchAsync(branch, central, transaction, saleId, cancellationToken);
        await EnsureStationAsync(branch, central, transaction, saleId, cancellationToken);
        await EnsureDeviceAsync(branch, central, transaction, saleId, cancellationToken);
        await EnsureUserAsync(branch, central, transaction, saleId, cancellationToken);
        await EnsureCustomerAsync(branch, central, transaction, saleId, cancellationToken);
        await EnsureSessionAsync(branch, central, transaction, saleId, cancellationToken);
        await InsertSaleAsync(branch, central, transaction, saleId, cancellationToken);
        await InsertItemsAsync(branch, central, transaction, saleId, cancellationToken);
        await InsertPaymentsAsync(branch, central, transaction, saleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureBranchAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT branch.BranchID, branch.BusinessID, branch.BranchCode, branch.BranchName, branch.BranchType, branch.IsActive, branch.CreatedAt
            FROM dbo.Branches AS branch
            INNER JOIN dbo.Sales AS sale ON sale.BranchID = branch.BranchID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id = reader.GetGuid(0);
        var businessId = reader.GetGuid(1);
        var code = reader.IsDBNull(2) ? null : reader.GetString(2);
        var name = reader.GetString(3);
        var type = reader.GetString(4);
        var active = reader.GetBoolean(5);
        var created = reader.GetDateTime(6);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchID = @id)
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
            VALUES (@id, @business, @code, @name, @type, @active, @created);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        insert.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        insert.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = (object?)code ?? DBNull.Value;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
        insert.Parameters.Add("@type", SqlDbType.NVarChar, 30).Value = type;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureStationAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT station.StationID, station.BranchID, station.StationNumber, station.StationName, station.IsActive, station.CreatedAt
            FROM dbo.POSStations AS station
            INNER JOIN dbo.Sales AS sale ON sale.POSStationID = station.StationID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id = reader.GetGuid(0);
        var branchId = reader.GetGuid(1);
        var number = reader.GetInt32(2);
        var name = reader.GetString(3);
        var active = reader.GetBoolean(4);
        var created = reader.GetDateTime(5);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.POSStations WHERE StationID = @id)
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt)
            VALUES (@id, @branch, @number, @name, @active, @created);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insert.Parameters.Add("@number", SqlDbType.Int).Value = number;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 100).Value = name;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDeviceAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT device.DeviceID, device.BranchID, device.StationID, device.DeviceName, device.DeviceType, device.IsActive, device.RegisteredAt, device.CreatedAt
            FROM dbo.Devices AS device
            INNER JOIN dbo.Sales AS sale ON sale.DeviceID = device.DeviceID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id = reader.GetGuid(0);
        var branchId = reader.GetGuid(1);
        var stationId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
        var name = reader.GetString(3);
        var type = reader.GetString(4);
        var active = reader.GetBoolean(5);
        var registered = reader.GetDateTime(6);
        var created = reader.GetDateTime(7);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Devices WHERE DeviceID = @id)
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt)
            VALUES (@id, @branch, @station, @name, @type, @active, @registered, @created);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insert.Parameters.Add("@station", SqlDbType.UniqueIdentifier).Value = (object?)stationId ?? DBNull.Value;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 150).Value = name;
        insert.Parameters.Add("@type", SqlDbType.NVarChar, 30).Value = type;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@registered", SqlDbType.DateTime2).Value = registered;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureUserAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT cashier.UserID, cashier.Username, cashier.NormalizedUsername, cashier.DisplayName, cashier.PasswordHash, cashier.IsActive, cashier.MustChangePassword, cashier.CreatedAt
            FROM dbo.Users AS cashier
            INNER JOIN dbo.Sales AS sale ON sale.CashierUserID = cashier.UserID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id = reader.GetGuid(0);
        var username = reader.GetString(1);
        var normalized = reader.GetString(2);
        var display = reader.GetString(3);
        var hash = reader.GetString(4);
        var active = reader.GetBoolean(5);
        var mustChange = reader.GetBoolean(6);
        var created = reader.GetDateTime(7);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE UserID = @id)
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES (@id, @username, @normalized, @display, @hash, @active, @mustChange, @created);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        insert.Parameters.Add("@username", SqlDbType.NVarChar, 100).Value = username;
        insert.Parameters.Add("@normalized", SqlDbType.NVarChar, 100).Value = normalized;
        insert.Parameters.Add("@display", SqlDbType.NVarChar, 150).Value = display;
        insert.Parameters.Add("@hash", SqlDbType.NVarChar, 1000).Value = hash;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@mustChange", SqlDbType.Bit).Value = mustChange;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureCustomerAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT customer.CustomerID, customer.BusinessID, customer.CustomerNumber, customer.Name, customer.Phone, customer.NormalizedPhone, customer.IsActive, customer.CreatedAt, customer.UpdatedAt
            FROM dbo.Customers AS customer
            INNER JOIN dbo.Sales AS sale ON sale.CustomerID = customer.CustomerID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var id = reader.GetGuid(0);
        var businessId = reader.GetGuid(1);
        var number = reader.IsDBNull(2) ? null : reader.GetString(2);
        var name = reader.GetString(3);
        var phone = reader.IsDBNull(4) ? null : reader.GetString(4);
        var normalized = reader.IsDBNull(5) ? null : reader.GetString(5);
        var active = reader.GetBoolean(6);
        var created = reader.GetDateTime(7);
        var updated = reader.GetDateTime(8);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerID = @id)
            INSERT INTO dbo.Customers (CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, IsActive, CreatedAt, UpdatedAt)
            VALUES (@id, @business, @number, @name, @phone, @normalized, @active, @created, @updated);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        insert.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        insert.Parameters.Add("@number", SqlDbType.NVarChar, 50).Value = (object?)number ?? DBNull.Value;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = name;
        insert.Parameters.Add("@phone", SqlDbType.NVarChar, 50).Value = (object?)phone ?? DBNull.Value;
        insert.Parameters.Add("@normalized", SqlDbType.NVarChar, 50).Value = (object?)normalized ?? DBNull.Value;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        insert.Parameters.Add("@updated", SqlDbType.DateTime2).Value = updated;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSessionAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT session.CashSessionID, session.BusinessID, session.BranchID, session.POSStationID, session.DeviceID,
                   session.OpenedByUserID, session.OpenedAt, session.OpeningCashAmount, session.Status,
                   session.ClosedByUserID, session.ClosedAt, session.ExpectedCashAmount, session.CountedCashAmount, session.CashDifference, session.CreatedAt
            FROM dbo.POSCashSessions AS session
            INNER JOIN dbo.Sales AS sale ON sale.CashSessionID = session.CashSessionID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var values = new object[15];
        reader.GetValues(values);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions WHERE CashSessionID = @id)
            INSERT INTO dbo.POSCashSessions
                (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status,
                 ClosedByUserID, ClosedAt, ExpectedCashAmount, CountedCashAmount, CashDifference, CreatedAt)
            VALUES
                (@id, @business, @branch, @station, @device, @openedBy, @openedAt, @opening, @status,
                 @closedBy, @closedAt, @expected, @counted, @difference, @created);
            """,
            central,
            transaction);
        string[] names = ["@id", "@business", "@branch", "@station", "@device", "@openedBy", "@openedAt", "@opening", "@status", "@closedBy", "@closedAt", "@expected", "@counted", "@difference", "@created"];
        for (var index = 0; index < names.Length; index++)
        {
            insert.Parameters.AddWithValue(names[index], values[index] is DBNull ? DBNull.Value : values[index]);
        }

        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSaleAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID,
                   SaleNumber, Status, Subtotal, DiscountTotal, Total, CreatedAt, CompletedAt, LegacySource, LegacySaleKey
            FROM dbo.Sales WHERE SaleID = @sale;
            """,
            branch);
        read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var values = new object[18];
        reader.GetValues(values);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.Sales
                (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID,
                 SaleNumber, Status, Subtotal, DiscountTotal, Total, CreatedAt, CompletedAt, LegacySource, LegacySaleKey)
            VALUES
                (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17);
            """,
            central,
            transaction);
        for (var index = 0; index < values.Length; index++)
        {
            insert.Parameters.AddWithValue("@p" + index.ToString(CultureInfo.InvariantCulture), values[index] is DBNull ? DBNull.Value : values[index]);
        }

        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertItemsAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        var rows = new List<object[]>();
        await using (var read = new SqlCommand(
            """
            SELECT SaleItemID, SaleID, LineNumber, PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot, Quantity, UnitPrice, LineTotal,
                   CustomerID, PriceTypeID, PriceTypeCodeSnapshot, PriceSource, PricingResolvedAt, StockOwnerItemID, StockOwnerItemNumberSnapshot,
                   StockOwnerConversionFactor, StockOwnerQuantity, ReferenceItemID, ReferenceItemNumberSnapshot, ReferenceItemNameSnapshot,
                   EnteredScaleValue, ReferenceBasePrice, ReferencePriceSource, WeightKg, WeightGrams, VariableValueDiscountPercent, RawDiscountedValue,
                   FinalSellingPrice, RoundingPolicy, ProjectedCostAtPricing, UnitCostSnapshot, LineCostSnapshot, CostSource, CreatedAt,
                   ResolvedUnitPrice, ResolvedPriceSource, PriceOverrideMode, AppliedSpecialPriceID, AppliedMinimumQuantity, AppliedBundlePrice, AppliedTargetUnitPrice
            FROM dbo.SaleItems WHERE SaleID = @sale ORDER BY LineNumber;
            """,
            branch))
        {
            read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
        }

        foreach (var values in rows)
        {
            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.SaleItems
                    (SaleItemID, SaleID, LineNumber, PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot, Quantity, UnitPrice, LineTotal,
                     CustomerID, PriceTypeID, PriceTypeCodeSnapshot, PriceSource, PricingResolvedAt, StockOwnerItemID, StockOwnerItemNumberSnapshot,
                     StockOwnerConversionFactor, StockOwnerQuantity, ReferenceItemID, ReferenceItemNumberSnapshot, ReferenceItemNameSnapshot,
                     EnteredScaleValue, ReferenceBasePrice, ReferencePriceSource, WeightKg, WeightGrams, VariableValueDiscountPercent, RawDiscountedValue,
                     FinalSellingPrice, RoundingPolicy, ProjectedCostAtPricing, UnitCostSnapshot, LineCostSnapshot, CostSource, CreatedAt,
                     ResolvedUnitPrice, ResolvedPriceSource, PriceOverrideMode, AppliedSpecialPriceID, AppliedMinimumQuantity, AppliedBundlePrice, AppliedTargetUnitPrice)
                VALUES
                    (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23,@p24,@p25,@p26,@p27,@p28,@p29,@p30,@p31,@p32,@p33,@p34,@p35,@p36,@p37,@p38,@p39,@p40,@p41,@p42);
                """,
                central,
                transaction);
            for (var index = 0; index < values.Length; index++)
            {
                insert.Parameters.AddWithValue("@p" + index.ToString(CultureInfo.InvariantCulture), values[index] is DBNull ? DBNull.Value : values[index]);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertPaymentsAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid saleId, CancellationToken cancellationToken)
    {
        var rows = new List<object[]>();
        await using (var read = new SqlCommand(
            """
            SELECT SalePaymentID, SaleID, CashSessionID, BranchID, ClientPaymentKey, PaymentMethodCode, Amount, TenderedAmount, ChangeAmount, ExternalReference, CreatedByUserID, CreatedAt
            FROM dbo.SalePayments WHERE SaleID = @sale;
            """,
            branch))
        {
            read.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
        }

        foreach (var values in rows)
        {
            var userId = values[10] is Guid id ? id : Guid.Empty;
            if (userId != Guid.Empty)
            {
                await EnsureUserByIdAsync(branch, central, transaction, userId, cancellationToken);
            }

            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.SalePayments
                    (SalePaymentID, SaleID, CashSessionID, BranchID, ClientPaymentKey, PaymentMethodCode, Amount, TenderedAmount, ChangeAmount, ExternalReference, CreatedByUserID, CreatedAt)
                VALUES
                    (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11);
                """,
                central,
                transaction);
            for (var index = 0; index < values.Length; index++)
            {
                insert.Parameters.AddWithValue("@p" + index.ToString(CultureInfo.InvariantCulture), values[index] is DBNull ? DBNull.Value : values[index]);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureUserByIdAsync(SqlConnection branch, SqlConnection central, SqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            "SELECT UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt FROM dbo.Users WHERE UserID = @id;",
            branch);
        read.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = userId;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;
        var username = reader.GetString(1);
        var normalized = reader.GetString(2);
        var display = reader.GetString(3);
        var hash = reader.GetString(4);
        var active = reader.GetBoolean(5);
        var mustChange = reader.GetBoolean(6);
        var created = reader.GetDateTime(7);
        await reader.CloseAsync();
        await using var insert = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE UserID = @id)
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES (@id, @username, @normalized, @display, @hash, @active, @mustChange, @created);
            """,
            central,
            transaction);
        insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = userId;
        insert.Parameters.Add("@username", SqlDbType.NVarChar, 100).Value = username;
        insert.Parameters.Add("@normalized", SqlDbType.NVarChar, 100).Value = normalized;
        insert.Parameters.Add("@display", SqlDbType.NVarChar, 150).Value = display;
        insert.Parameters.Add("@hash", SqlDbType.NVarChar, 1000).Value = hash;
        insert.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        insert.Parameters.Add("@mustChange", SqlDbType.Bit).Value = mustChange;
        insert.Parameters.Add("@created", SqlDbType.DateTime2).Value = created;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
