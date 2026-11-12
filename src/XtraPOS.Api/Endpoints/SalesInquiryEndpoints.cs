using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class SalesInquiryEndpoints
{
    private const int PageSize = 50;
    private const int ExportCap = 5000;
    private const string LocalSale = "CONVERT(datetime2, (sale.CompletedAt AT TIME ZONE 'UTC') AT TIME ZONE 'Israel Standard Time')";

    public static void MapSalesInquiryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/sales-inquiry", InquiryAsync);
        app.MapGet("/api/pos/sales-inquiry/export", ExportAsync);
        app.MapGet("/api/pos/sales-inquiry/{saleId:guid}", DocumentAsync);
    }

    private static async Task<IResult> InquiryAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        SqlConnectionFactory connections,
        bool? sumsOnly,
        bool? useDate,
        string? dateFrom,
        string? dateTo,
        bool? useTime,
        string? timeFrom,
        string? timeTo,
        bool? useStation,
        int? stationNumber,
        bool? useItem,
        bool? matchItem,
        string? itemNumber,
        bool? useName,
        string? itemName,
        bool? useUser,
        string? userId,
        bool? useCustomer,
        string? customerNumber,
        bool? useCustomerName,
        string? customerName,
        bool? useSaleNumber,
        string? saleNumber,
        bool? useBranch,
        string? branchName,
        bool? useClass,
        string? classifications,
        bool? modifiedOnly,
        int? skip,
        int? take,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var seeEarns = CanSeeEarns(actor.User!);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var options = await OptionsAsync(connection, actor.Context!.BranchId, cancellationToken);
        var optionPayload = OptionsPayload(options);
        if (useBranch == true && BranchUnavailable(options, branchName))
        {
            return Results.Ok(new InquiryResponse
            {
                total = 0,
                salesTotal = 0m,
                seeEarns = seeEarns,
                unavailable = true,
                message = "بيانات الفرع المحدد غير متاحة من نقطة البيع الحالية.",
                lines = [],
                options = optionPayload,
            });
        }

        if (ReversedDates(useDate, dateFrom, dateTo))
        {
            return Results.Json(new { message = "تاريخ البداية أكبر من تاريخ النهاية." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var filter = BuildFilter(actor.Context!, useDate, dateFrom, dateTo, useTime, timeFrom, timeTo, useStation, stationNumber, useItem, matchItem, itemNumber, useName, itemName, useUser, userId, useCustomer, customerNumber, useCustomerName, customerName, useSaleNumber, saleNumber, useClass, classifications, modifiedOnly);
        var (total, salesTotal, earnTotal) = await SummariesAsync(connection, filter, seeEarns, cancellationToken);
        var lines = new List<InquiryLine>();
        if (sumsOnly != true)
        {
            lines = await LinesAsync(connection, filter, seeEarns, Math.Max(0, skip ?? 0), Math.Clamp(take ?? PageSize, 1, PageSize), cancellationToken);
        }

        return Results.Ok(new InquiryResponse
        {
            total = total,
            salesTotal = salesTotal,
            earnTotal = seeEarns ? earnTotal : null,
            seeEarns = seeEarns,
            lines = lines,
            options = optionPayload,
        });
    }

    private static async Task<IResult> ExportAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        SqlConnectionFactory connections,
        bool? useDate,
        string? dateFrom,
        string? dateTo,
        bool? useTime,
        string? timeFrom,
        string? timeTo,
        bool? useStation,
        int? stationNumber,
        bool? useItem,
        bool? matchItem,
        string? itemNumber,
        bool? useName,
        string? itemName,
        bool? useUser,
        string? userId,
        bool? useCustomer,
        string? customerNumber,
        bool? useCustomerName,
        string? customerName,
        bool? useSaleNumber,
        string? saleNumber,
        bool? useBranch,
        string? branchName,
        bool? useClass,
        string? classifications,
        bool? modifiedOnly,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (ReversedDates(useDate, dateFrom, dateTo))
        {
            return Results.Json(new { message = "تاريخ البداية أكبر من تاريخ النهاية." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var seeEarns = CanSeeEarns(actor.User!);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var options = await OptionsAsync(connection, actor.Context!.BranchId, cancellationToken);
        if (useBranch == true && BranchUnavailable(options, branchName))
        {
            return Results.Json(new { message = "بيانات الفرع المحدد غير متاحة من نقطة البيع الحالية." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var filter = BuildFilter(actor.Context!, useDate, dateFrom, dateTo, useTime, timeFrom, timeTo, useStation, stationNumber, useItem, matchItem, itemNumber, useName, itemName, useUser, userId, useCustomer, customerNumber, useCustomerName, customerName, useSaleNumber, saleNumber, useClass, classifications, modifiedOnly);
        var (total, _, _) = await SummariesAsync(connection, filter, false, cancellationToken);
        var lines = await LinesAsync(connection, filter, seeEarns, 0, ExportCap, cancellationToken);
        var truncated = total > lines.Count;
        var branchLabel = options.Branches.FirstOrDefault() ?? "";
        var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, BusinessZone()).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var range = useDate == true ? $"{dateFrom} → {dateTo}" : "بدون تاريخ";
        var headers = new List<string> { "رقم الطلبية", "رقم الصنف", "اسم الصنف", "الكمية", "السعر الأصلي", "سعر الزبون", "السعر المنفذ" };
        if (seeEarns) headers.Add("تكلفة الوحدة");
        headers.Add("الإجمالي");
        if (seeEarns) headers.AddRange(["التكلفة", "الربح"]);
        headers.AddRange(["التاريخ", "الوقت", "رقم الزبون", "اسم الزبون", "النقطة", "الموظف", "ملاحظات", "تم التعديل"]);
        var rows = lines.Select(line =>
        {
            var cells = new List<string>
            {
                line.saleNumber.ToString(CultureInfo.InvariantCulture),
                line.itemNumber,
                line.itemName,
                line.quantity.ToString(CultureInfo.InvariantCulture),
                line.originalPrice.ToString(CultureInfo.InvariantCulture),
                line.customerPrice.ToString(CultureInfo.InvariantCulture),
                line.executedPrice.ToString(CultureInfo.InvariantCulture),
            };
            if (seeEarns) cells.Add(line.unitCost?.ToString(CultureInfo.InvariantCulture) ?? "");
            cells.Add(line.total.ToString(CultureInfo.InvariantCulture));
            if (seeEarns)
            {
                cells.Add(line.cost?.ToString(CultureInfo.InvariantCulture) ?? "");
                cells.Add(line.earn?.ToString(CultureInfo.InvariantCulture) ?? "");
            }

            var local = line.completedAt is DateTime completed ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(completed, DateTimeKind.Utc), BusinessZone()) : (DateTime?)null;
            cells.Add(local?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
            cells.Add(local?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "");
            cells.Add(line.customerNumber ?? "");
            cells.Add(line.customerName ?? "");
            cells.Add(line.stationNumber.ToString(CultureInfo.InvariantCulture));
            cells.Add(line.cashier);
            cells.Add("—");
            cells.Add(line.modified ? "نعم" : "لا");
            return (IReadOnlyList<string>)cells;
        }).ToList();
        var meta = new List<string>
        {
            "مراجعة المبيعات",
            "نطاق التاريخ: " + range,
            "الفرع: " + branchLabel,
            "وقت الإنشاء: " + when,
            "عدد النتائج: " + total.ToString(CultureInfo.InvariantCulture),
            truncated ? "تم الاقتطاع بعد " + ExportCap.ToString(CultureInfo.InvariantCulture) + " سطراً." : "التصدير كامل.",
        };
        var bytes = Workbook(meta, headers, rows);
        http.Response.Headers["X-Export-Truncated"] = truncated ? "true" : "false";
        http.Response.Headers["X-Export-Total"] = total.ToString(CultureInfo.InvariantCulture);
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "sales-review.xlsx");
    }

    private static async Task<IResult> DocumentAsync(Guid saleId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var header = new SqlCommand(
            """
            SELECT sale.SaleNumber, customer.CustomerNumber, customer.Name, branch.BranchName, station.StationNumber,
                   cashier.DisplayName, sale.CompletedAt, sale.Subtotal, sale.DiscountTotal, sale.Total
            FROM dbo.Sales AS sale
            INNER JOIN dbo.Branches AS branch ON branch.BranchID = sale.BranchID
            INNER JOIN dbo.POSStations AS station ON station.StationID = sale.POSStationID
            INNER JOIN dbo.Users AS cashier ON cashier.UserID = sale.CashierUserID
            LEFT JOIN dbo.Customers AS customer ON customer.CustomerID = sale.CustomerID
            WHERE sale.SaleID = @sale AND sale.BranchID = @branch AND sale.BusinessID = @business AND sale.Status = N'Completed';
            """, connection);
        header.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        header.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = actor.Context!.BranchId;
        header.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = actor.Context.BusinessId;
        await using var reader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound(new { message = "الطلبية غير موجودة." });
        var body = new
        {
            saleNumber = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
            customerNumber = reader.IsDBNull(1) ? null : reader.GetString(1),
            customerName = reader.IsDBNull(2) ? "زبون عادي" : reader.GetString(2),
            branchName = reader.GetString(3),
            posNumber = reader.GetInt32(4),
            cashier = reader.GetString(5),
            when = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
            subtotal = reader.GetDecimal(7),
            discount = reader.GetDecimal(8),
            total = reader.GetDecimal(9),
        };
        await reader.CloseAsync();
        var lines = new List<object>();
        await using var lineCommand = new SqlCommand(
            """
            SELECT LineNumber, ItemNameSnapshot, Quantity, UnitPrice, LineTotal
            FROM dbo.SaleItems WHERE SaleID = @sale ORDER BY LineNumber;
            """, connection);
        lineCommand.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var lineReader = await lineCommand.ExecuteReaderAsync(cancellationToken);
        while (await lineReader.ReadAsync(cancellationToken))
        {
            lines.Add(new { number = lineReader.GetInt32(0), name = lineReader.GetString(1), quantity = lineReader.GetDecimal(2), unitPrice = lineReader.GetDecimal(3), total = lineReader.GetDecimal(4) });
        }

        return Results.Ok(new { body.saleNumber, body.customerNumber, body.customerName, body.branchName, body.posNumber, body.cashier, body.when, body.subtotal, body.discount, body.total, lines });
    }

    private static async Task<(int Total, decimal Sales, decimal? Earn)> SummariesAsync(SqlConnection connection, Filter filter, bool seeEarns, CancellationToken cancellationToken)
    {
        var sumSql = seeEarns
            ? "SELECT COUNT(*), COALESCE(SUM(line.LineTotal), 0), COALESCE(SUM(CASE WHEN line.LineCostSnapshot IS NULL THEN 0 ELSE line.LineTotal - line.LineCostSnapshot END), 0) "
            : "SELECT COUNT(*), COALESCE(SUM(line.LineTotal), 0) ";
        await using var sums = new SqlCommand(sumSql + From + filter.Where, connection);
        foreach (var parameter in Clone(filter.Parameters)) sums.Parameters.Add(parameter);
        await using var reader = await sums.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0m, null);
        return (reader.GetInt32(0), reader.GetDecimal(1), seeEarns ? reader.GetDecimal(2) : null);
    }

    private static async Task<List<InquiryLine>> LinesAsync(SqlConnection connection, Filter filter, bool seeEarns, int skip, int take, CancellationToken cancellationToken)
    {
        await using var rows = new SqlCommand(
            """
            SELECT sale.SaleID, sale.SaleNumber, line.ItemNumberSnapshot, line.ItemNameSnapshot, line.Quantity,
                   COALESCE(line.ReferenceBasePrice, line.UnitPrice), COALESCE(line.FinalSellingPrice, line.UnitPrice), line.UnitPrice,
                   line.UnitCostSnapshot, line.LineTotal, line.LineCostSnapshot,
                   CASE WHEN line.LineCostSnapshot IS NULL THEN NULL ELSE line.LineTotal - line.LineCostSnapshot END,
                   sale.CompletedAt, customer.Name, customer.CustomerNumber, COALESCE(cashier.DisplayName, N'—'), COALESCE(station.StationNumber, 0),
                   CASE WHEN line.PriceOverrideMode <> N'None' THEN 1 ELSE 0 END
            """ + From + filter.Where + " ORDER BY sale.CompletedAt DESC, sale.SaleNumber DESC, line.LineNumber OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", connection);
        rows.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = skip });
        rows.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
        foreach (var parameter in Clone(filter.Parameters)) rows.Parameters.Add(parameter);
        var lines = new List<InquiryLine>();
        await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new InquiryLine
            {
                saleId = reader.GetGuid(0),
                saleNumber = reader.GetInt64(1),
                itemNumber = reader.GetString(2),
                itemName = reader.GetString(3),
                quantity = reader.GetDecimal(4),
                originalPrice = reader.GetDecimal(5),
                customerPrice = reader.GetDecimal(6),
                executedPrice = reader.GetDecimal(7),
                unitCost = seeEarns && !reader.IsDBNull(8) ? reader.GetDecimal(8) : null,
                total = reader.GetDecimal(9),
                cost = seeEarns && !reader.IsDBNull(10) ? reader.GetDecimal(10) : null,
                earn = seeEarns && !reader.IsDBNull(11) ? reader.GetDecimal(11) : null,
                completedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                customerName = reader.IsDBNull(13) ? null : reader.GetString(13),
                customerNumber = reader.IsDBNull(14) ? null : reader.GetString(14),
                cashier = reader.GetString(15),
                stationNumber = reader.GetInt32(16),
                modified = reader.GetInt32(17) == 1,
            });
        }

        return lines;
    }

    private const string From =
        """
        FROM dbo.SaleItems AS line
        INNER JOIN dbo.Sales AS sale ON sale.SaleID = line.SaleID
        LEFT JOIN dbo.Users AS cashier ON cashier.UserID = sale.CashierUserID
        LEFT JOIN dbo.POSStations AS station ON station.StationID = sale.POSStationID
        LEFT JOIN dbo.Customers AS customer ON customer.CustomerID = sale.CustomerID
        WHERE 
        """;

    private static Filter BuildFilter(
        PosDeviceContext context,
        bool? useDate, string? dateFrom, string? dateTo,
        bool? useTime, string? timeFrom, string? timeTo,
        bool? useStation, int? stationNumber,
        bool? useItem, bool? matchItem, string? itemNumber,
        bool? useName, string? itemName,
        bool? useUser, string? userId,
        bool? useCustomer, string? customerNumber,
        bool? useCustomerName, string? customerName,
        bool? useSaleNumber, string? saleNumber,
        bool? useClass, string? classifications,
        bool? modifiedOnly)
    {
        var clauses = new List<string> { "sale.BranchID = @branch", "sale.Status = N'Completed'", "sale.BusinessID = @business" };
        var parameters = new List<SqlParameter>
        {
            new("@branch", SqlDbType.UniqueIdentifier) { Value = context.BranchId },
            new("@business", SqlDbType.UniqueIdentifier) { Value = context.BusinessId },
        };
        if (useDate == true && DateOnly.TryParse(dateFrom, out var from) && DateOnly.TryParse(dateTo, out var to) && from <= to)
        {
            clauses.Add($"CAST({LocalSale} AS date) >= @dateFrom AND CAST({LocalSale} AS date) <= @dateTo");
            parameters.Add(new("@dateFrom", SqlDbType.Date) { Value = from.ToDateTime(TimeOnly.MinValue) });
            parameters.Add(new("@dateTo", SqlDbType.Date) { Value = to.ToDateTime(TimeOnly.MinValue) });
        }

        if (useTime == true && TimeOnly.TryParse(timeFrom, out var startTime) && TimeOnly.TryParse(timeTo, out var endTime))
        {
            clauses.Add(startTime <= endTime
                ? $"CAST({LocalSale} AS time) >= @timeStart AND CAST({LocalSale} AS time) <= @timeEnd"
                : $"(CAST({LocalSale} AS time) >= @timeStart OR CAST({LocalSale} AS time) <= @timeEnd)");
            parameters.Add(new("@timeStart", SqlDbType.Time) { Value = startTime.ToTimeSpan() });
            parameters.Add(new("@timeEnd", SqlDbType.Time) { Value = endTime.ToTimeSpan() });
        }

        if (useStation == true && stationNumber is int station)
        {
            clauses.Add("station.StationNumber = @station");
            parameters.Add(new("@station", SqlDbType.Int) { Value = station });
        }

        if (useItem == true && !string.IsNullOrWhiteSpace(itemNumber))
        {
            parameters.Add(new("@item", SqlDbType.NVarChar, 100) { Value = itemNumber.Trim() });
            if (matchItem == true)
            {
                clauses.Add("line.ItemNumberSnapshot = @item");
            }
            else
            {
                clauses.Add(
                    """
                    (
                      line.ItemNumberSnapshot = @item
                      OR line.ItemID IN (
                        SELECT root.ItemID FROM dbo.Items AS root WHERE root.ItemNumber = @item
                        UNION
                        SELECT child.ItemID FROM dbo.Items AS root INNER JOIN dbo.Items AS child ON child.ParentItemID = root.ItemID WHERE root.ItemNumber = @item
                        UNION
                        SELECT root.ParentItemID FROM dbo.Items AS root WHERE root.ItemNumber = @item AND root.ParentItemID IS NOT NULL
                        UNION
                        SELECT unit.InternalUnitItemID FROM dbo.Items AS root INNER JOIN dbo.ItemUnits AS unit ON unit.ItemID = root.ItemID WHERE root.ItemNumber = @item AND unit.InternalUnitItemID IS NOT NULL
                        UNION
                        SELECT unit.ItemID FROM dbo.Items AS root INNER JOIN dbo.ItemUnits AS unit ON unit.InternalUnitItemID = root.ItemID WHERE root.ItemNumber = @item
                      )
                      OR line.ItemNumberSnapshot IN (
                        SELECT unit.InternalUnitItemNumber
                        FROM dbo.Items AS root
                        INNER JOIN dbo.ItemUnits AS unit ON unit.ItemID = root.ItemID OR unit.InternalUnitItemID = root.ItemID
                        WHERE root.ItemNumber = @item AND unit.InternalUnitItemNumber IS NOT NULL
                      )
                    )
                    """);
            }
        }

        if (useName == true && !string.IsNullOrWhiteSpace(itemName))
        {
            clauses.Add("line.ItemNameSnapshot LIKE @name");
            parameters.Add(new("@name", SqlDbType.NVarChar, 500) { Value = "%" + EscapeLike(itemName.Trim()) + "%" });
        }

        if (useUser == true && Guid.TryParse(userId, out var cashierId))
        {
            clauses.Add("sale.CashierUserID = @cashierId");
            parameters.Add(new("@cashierId", SqlDbType.UniqueIdentifier) { Value = cashierId });
        }

        if (useCustomer == true && !string.IsNullOrWhiteSpace(customerNumber))
        {
            clauses.Add("customer.CustomerNumber = @customer");
            parameters.Add(new("@customer", SqlDbType.NVarChar, 50) { Value = customerNumber.Trim() });
        }

        if (useCustomerName == true && !string.IsNullOrWhiteSpace(customerName))
        {
            clauses.Add("customer.Name LIKE @customerName");
            parameters.Add(new("@customerName", SqlDbType.NVarChar, 300) { Value = "%" + EscapeLike(customerName.Trim()) + "%" });
        }

        if (useSaleNumber == true && long.TryParse(saleNumber, out var saleNo))
        {
            clauses.Add("sale.SaleNumber = @saleNo");
            parameters.Add(new("@saleNo", SqlDbType.BigInt) { Value = saleNo });
        }

        if (useClass == true && !string.IsNullOrWhiteSpace(classifications))
        {
            var names = classifications.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var namesSql = new List<string>();
            for (var index = 0; index < names.Length; index++)
            {
                var name = "@class" + index;
                namesSql.Add(name);
                parameters.Add(new(name, SqlDbType.NVarChar, 200) { Value = names[index] });
            }

            if (namesSql.Count > 0) clauses.Add("EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemID = line.ItemID AND item.Classification IN (" + string.Join(", ", namesSql) + "))");
        }

        if (modifiedOnly == true) clauses.Add("line.PriceOverrideMode <> N'None'");
        return new Filter(string.Join(" AND ", clauses), parameters);
    }

    private static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static bool ReversedDates(bool? useDate, string? dateFrom, string? dateTo) =>
        useDate == true && DateOnly.TryParse(dateFrom, out var from) && DateOnly.TryParse(dateTo, out var to) && from > to;

    private static bool BranchUnavailable(InquiryOptions options, string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return false;
        var current = options.Branches.FirstOrDefault();
        return current is not null && !string.Equals(current.Trim(), branchName.Trim(), StringComparison.Ordinal);
    }

    private static async Task<InquiryOptions> OptionsAsync(SqlConnection connection, Guid branchId, CancellationToken cancellationToken)
    {
        var stations = await Texts(connection, "SELECT CONVERT(nvarchar(20), StationNumber) FROM dbo.POSStations WHERE BranchID = @branch AND IsActive = 1 ORDER BY StationNumber;", branchId, cancellationToken);
        var branches = await Texts(connection, "SELECT BranchName FROM dbo.Branches WHERE BranchID = @branch;", branchId, cancellationToken);
        var classes = await Texts(connection, "SELECT DISTINCT Classification FROM dbo.Items WHERE Classification IS NOT NULL AND Classification <> N'' ORDER BY Classification;", null, cancellationToken);
        var users = new List<UserOption>();
        await using (var command = new SqlCommand("SELECT UserID, DisplayName FROM dbo.Users WHERE IsActive = 1 ORDER BY DisplayName;", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) users.Add(new UserOption(reader.GetGuid(0), reader.GetString(1)));
        }

        return new InquiryOptions(stations, users.ToArray(), classes, branches);
    }

    private static object OptionsPayload(InquiryOptions options) => new
    {
        options.Stations,
        users = options.Users.Select(user => new { userId = user.UserId, displayName = user.DisplayName }),
        options.Classes,
        branches = options.Branches,
    };

    private static async Task<string[]> Texts(SqlConnection connection, string sql, Guid? branchId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        if (branchId is Guid branch) command.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branch;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static TimeZoneInfo BusinessZone()
    {
        foreach (var id in new[] { "Asia/Hebron", "Israel Standard Time", "Jordan Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }

    private static bool CanSeeEarns(CurrentUserResponse user) =>
        user.Roles.Any(role => role.RoleCode is "Admin" or "Manager" or "Supervisor" || role.RoleName is "مشرف")
        || user.Permissions.Contains(PermissionCodes.SystemAdminister);

    private static SqlParameter[] Clone(IEnumerable<SqlParameter> parameters) =>
        parameters.Select(parameter => new SqlParameter(parameter.ParameterName, parameter.SqlDbType) { Value = parameter.Value, Size = parameter.Size }).ToArray();

    private static async Task<(PosDeviceContext? Context, CurrentUserResponse? User, IResult? Error)> ActorAsync(HttpContext http, AuthService auth, PermissionService permissions, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (null, null, Results.Unauthorized());
        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.PosSell, cancellationToken)) return (null, null, Results.Forbid());
        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return actor is null ? (null, null, Results.Unauthorized()) : (actor, current, null);
    }

    private static byte[] Workbook(IReadOnlyList<string> meta, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Write(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Write(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Write(zip, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="البيانات" sheetId="1" r:id="rId1"/>
                    <sheet name="المبيعات" sheetId="2" r:id="rId2"/>
                  </sheets>
                </workbook>
                """);
            Write(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Write(zip, "xl/worksheets/sheet1.xml", Sheet(meta.Select(line => (IReadOnlyList<string>)[line]).ToList(), []));
            Write(zip, "xl/worksheets/sheet2.xml", Sheet(rows, headers));
        }

        return stream.ToArray();
    }

    private static string Sheet(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> headers)
    {
        var body = new StringBuilder();
        body.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        var rowIndex = 1;
        if (headers.Count > 0)
        {
            body.Append(Row(rowIndex++, headers));
        }

        foreach (var row in rows) body.Append(Row(rowIndex++, row));
        body.Append("</sheetData></worksheet>");
        return body.ToString();
    }

    private static string Row(int index, IReadOnlyList<string> cells)
    {
        var builder = new StringBuilder();
        builder.Append("<row r=\"").Append(index).Append("\">");
        for (var column = 0; column < cells.Count; column++)
        {
            var cell = cells[column] ?? "";
            var reference = ColumnName(column) + index.ToString(CultureInfo.InvariantCulture);
            builder.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                .Append(SafeText(cell)).Append("</t></is></c>");
        }

        builder.Append("</row>");
        return builder.ToString();
    }

    private static string SafeText(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return new XText(value).ToString();
    }

    private static string ColumnName(int index)
    {
        var name = "";
        var current = index + 1;
        while (current > 0)
        {
            current--;
            name = (char)('A' + (current % 26)) + name;
            current /= 26;
        }

        return name;
    }

    private static void Write(ZipArchive zip, string name, string contents)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private sealed record Filter(string Where, List<SqlParameter> Parameters);

    private sealed record UserOption(Guid UserId, string DisplayName);

    private sealed record InquiryOptions(string[] Stations, UserOption[] Users, string[] Classes, string[] Branches);

    private sealed class InquiryResponse
    {
        public int total { get; init; }
        public decimal salesTotal { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public decimal? earnTotal { get; init; }
        public bool seeEarns { get; init; }
        public bool unavailable { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? message { get; init; }
        public List<InquiryLine> lines { get; init; } = [];
        public object? options { get; init; }
    }

    private sealed class InquiryLine
    {
        public Guid saleId { get; init; }
        public long saleNumber { get; init; }
        public string itemNumber { get; init; } = "";
        public string itemName { get; init; } = "";
        public decimal quantity { get; init; }
        public decimal originalPrice { get; init; }
        public decimal customerPrice { get; init; }
        public decimal executedPrice { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public decimal? unitCost { get; init; }
        public decimal total { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public decimal? cost { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public decimal? earn { get; init; }
        public DateTime? completedAt { get; init; }
        public string? customerName { get; init; }
        public string? customerNumber { get; init; }
        public string cashier { get; init; } = "";
        public int stationNumber { get; init; }
        public bool modified { get; init; }
    }
}
