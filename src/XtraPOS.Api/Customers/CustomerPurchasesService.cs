using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Customers;

public sealed record CustomerPurchaseQuery(Guid CustomerId, DateOnly? From, DateOnly? To, string? ItemNumber, int Skip, int Take);

/// <summary>
/// «مشتريات الزبون»: فواتير الزبون من `Sales`/`SaleItems` في كل الفروع (المركز أولاً بمهلة قصيرة، ثم الفرع)،
/// مع النجوم من `CustomerStarThresholds`. الصفوف تُجلب صفحة صفحة، والمجاميع والنجوم باستعلامات تجميع.
/// الجهاز المنفرد (بلا مركز محفوظ) يقرأ قاعدته فقط ولا يرى أي ملاحظة.
/// </summary>
public sealed class CustomerPurchasesService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    ILogger<CustomerPurchasesService> logger)
{
    public const string ThresholdsOperationType = "CustomerStarThresholds";
    public const int PageSize = 50;
    internal static readonly TimeSpan CentralTimeout = TimeSpan.FromSeconds(6);

    internal const string CentralUnreachableNotice =
        "تعذر الوصول إلى الإدارة المركزية. تظهر مشتريات الزبون في هذا الفرع فقط.";

    // ------------------------------------------------------------------
    // المشتريات
    // ------------------------------------------------------------------

    public async Task<object> PurchasesAsync(PosDeviceContext actor, bool seeTotals, CustomerPurchaseQuery query, CancellationToken cancellationToken)
    {
        var (connection, source, notice) = await OpenReadAsync(cancellationToken);
        await using var lease = connection;
        var customer = await ReadCustomerAsync(connection, actor.BusinessId, query.CustomerId, cancellationToken);
        if (customer is null && source == "central")
        {
            await using var local = await OpenLocalAsync(cancellationToken);
            customer = await ReadCustomerAsync(local, actor.BusinessId, query.CustomerId, cancellationToken);
        }

        if (customer is null)
        {
            throw new CustomerStarException("الزبون غير موجود.");
        }

        var profit = await ProfitExpressionAsync(connection, cancellationToken);
        var where = new List<string>
        {
            "sale.Status = N'Completed'",
            "sale.BusinessID = @business",
            "sale.CustomerID IN (SELECT c.CustomerID FROM dbo.Customers AS c WHERE c.BusinessID = @business AND (c.CustomerID = @customer OR (@number IS NOT NULL AND c.CustomerNumber = @number)))",
        };
        var parameters = new List<SqlParameter>
        {
            new("@business", SqlDbType.UniqueIdentifier) { Value = actor.BusinessId },
            new("@customer", SqlDbType.UniqueIdentifier) { Value = customer.CustomerId },
            new("@number", SqlDbType.NVarChar, 50) { Value = (object?)customer.CustomerNumber ?? DBNull.Value },
        };
        if (query.From is not null || query.To is not null)
        {
            var (start, end) = SalesInquiryLocal.LocalDayBounds(
                query.From ?? new DateOnly(2000, 1, 1),
                query.To ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
                XtraPOS.Api.Endpoints.SalesInquiryEndpoints.BusinessZone());
            where.Add("sale.CompletedAt >= @fromUtc AND sale.CompletedAt < @toUtc");
            parameters.Add(new("@fromUtc", SqlDbType.DateTime2) { Value = start });
            parameters.Add(new("@toUtc", SqlDbType.DateTime2) { Value = end });
        }

        var item = string.IsNullOrWhiteSpace(query.ItemNumber) ? null : query.ItemNumber.Trim();
        if (item is not null)
        {
            where.Add("EXISTS (SELECT 1 FROM dbo.SaleItems AS hit WHERE hit.SaleID = sale.SaleID AND hit.ItemNumberSnapshot = @item)");
            parameters.Add(new("@item", SqlDbType.NVarChar, 100) { Value = item });
        }

        var filter = string.Join(" AND ", where);

        // المجاميع: استعلام تجميع واحد، لا يقرأ الصفوف.
        int count;
        decimal salesTotal;
        decimal? earnTotal = null;
        decimal? itemQuantity = null;
        decimal? itemTotal = null;
        string? itemName = null;
        await using (var totals = new SqlCommand(
            $"""
             SELECT COUNT(*), COALESCE(SUM(sale.Total), 0)
             FROM dbo.Sales AS sale
             WHERE {filter};
             """,
            connection))
        {
            AddAll(totals, parameters);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            count = reader.GetInt32(0);
            salesTotal = reader.GetDecimal(1);
        }

        if ((seeTotals && profit is not null) || item is not null)
        {
            await using var lineTotals = new SqlCommand(
                $"""
                 SELECT {(seeTotals && profit is not null ? $"SUM({profit})" : "CAST(NULL AS decimal(19, 4))")},
                        {(item is not null ? "SUM(CASE WHEN line.ItemNumberSnapshot = @item THEN line.Quantity ELSE 0 END)" : "CAST(NULL AS decimal(19, 4))")},
                        {(item is not null ? "SUM(CASE WHEN line.ItemNumberSnapshot = @item THEN line.LineTotal ELSE 0 END)" : "CAST(NULL AS decimal(19, 4))")},
                        {(item is not null ? "MAX(CASE WHEN line.ItemNumberSnapshot = @item THEN line.ItemNameSnapshot END)" : "CAST(NULL AS nvarchar(500))")}
                 FROM dbo.SaleItems AS line
                 INNER JOIN dbo.Sales AS sale ON sale.SaleID = line.SaleID
                 WHERE {filter};
                 """,
                connection);
            AddAll(lineTotals, parameters);
            await using var reader = await lineTotals.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                earnTotal = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
                itemQuantity = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                itemTotal = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                itemName = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        // صفحة واحدة من الفواتير، ونطلب سطراً زائداً لنعرف إن بقي المزيد.
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, PageSize);
        var lines = new List<object>();
        var hasMore = false;
        await using (var page = new SqlCommand(
            $"""
             SELECT sale.SaleID, sale.SaleNumber, sale.CompletedAt, branch.BranchName, station.StationNumber, station.StationName,
                    COALESCE(cashier.DisplayName, N''), sale.Total,
                    (SELECT COUNT(*) FROM dbo.SaleItems AS line WHERE line.SaleID = sale.SaleID) AS LineCount,
                    {(seeTotals && profit is not null ? $"(SELECT SUM({profit}) FROM dbo.SaleItems AS line WHERE line.SaleID = sale.SaleID)" : "CAST(NULL AS decimal(19, 4))")} AS Earn,
                    {(item is not null ? "(SELECT SUM(line.Quantity) FROM dbo.SaleItems AS line WHERE line.SaleID = sale.SaleID AND line.ItemNumberSnapshot = @item)" : "CAST(NULL AS decimal(19, 4))")} AS ItemQuantity,
                    {(item is not null ? "(SELECT SUM(line.LineTotal) FROM dbo.SaleItems AS line WHERE line.SaleID = sale.SaleID AND line.ItemNumberSnapshot = @item)" : "CAST(NULL AS decimal(19, 4))")} AS ItemTotal
             FROM dbo.Sales AS sale
             LEFT JOIN dbo.Branches AS branch ON branch.BranchID = sale.BranchID
             LEFT JOIN dbo.POSStations AS station ON station.StationID = sale.POSStationID
             LEFT JOIN dbo.Users AS cashier ON cashier.UserID = sale.CashierUserID
             WHERE {filter}
             ORDER BY sale.CompletedAt DESC, sale.SaleNumber DESC
             OFFSET @skip ROWS FETCH NEXT @takeOne ROWS ONLY;
             """,
            connection))
        {
            AddAll(page, parameters);
            page.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = skip });
            page.Parameters.Add(new SqlParameter("@takeOne", SqlDbType.Int) { Value = take + 1 });
            await using var reader = await page.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (lines.Count == take)
                {
                    hasMore = true;
                    break;
                }

                lines.Add(new
                {
                    saleId = reader.GetGuid(0),
                    saleNumber = reader.GetInt64(1),
                    completedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                    branchName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    stationNumber = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    stationName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    cashier = reader.GetString(6),
                    total = reader.GetDecimal(7),
                    lineCount = reader.GetInt32(8),
                    earn = reader.IsDBNull(9) ? (decimal?)null : reader.GetDecimal(9),
                    itemQuantity = reader.IsDBNull(10) ? (decimal?)null : reader.GetDecimal(10),
                    itemTotal = reader.IsDBNull(11) ? (decimal?)null : reader.GetDecimal(11),
                });
            }
        }

        // النجوم تُحسب مرة مع الصفحة الأولى فقط.
        CustomerStarStanding? stars = null;
        if (skip == 0)
        {
            var thresholds = await ReadThresholdsAsync(connection, actor.BusinessId, cancellationToken) ?? CustomerStarRules.Default;
            stars = await StandingAsync(connection, actor.BusinessId, customer, thresholds, cancellationToken);
            if (!seeTotals)
            {
                // الكاشير يرى النجوم والاتجاه فقط، لا المبالغ.
                stars = stars with { WindowTotal = 0m, PreviousTotal = 0m, NextStarAt = null, NeededForNext = null };
            }
        }

        return new
        {
            source,
            notice,
            customer,
            seeTotals,
            totals = new
            {
                count,
                salesTotal = seeTotals ? salesTotal : (decimal?)null,
                earnTotal = seeTotals ? earnTotal : null,
                itemNumber = item,
                itemName,
                itemQuantity,
                itemTotal = seeTotals ? itemTotal : null,
            },
            stars,
            skip,
            hasMore,
            lines,
        };
    }

    private static async Task<CustomerStarStanding> StandingAsync(
        SqlConnection connection,
        Guid businessId,
        PurchaseCustomer customer,
        CustomerStarThresholds thresholds,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var command = new SqlCommand(
            """
            SELECT COALESCE(SUM(CASE WHEN sale.CompletedAt >= @windowStart THEN sale.Total END), 0),
                   COALESCE(SUM(CASE WHEN sale.CompletedAt < @windowStart THEN sale.Total END), 0)
            FROM dbo.Sales AS sale
            WHERE sale.Status = N'Completed'
              AND sale.BusinessID = @business
              AND sale.CompletedAt >= @previousStart
              AND sale.CustomerID IN (SELECT c.CustomerID FROM dbo.Customers AS c WHERE c.BusinessID = @business AND (c.CustomerID = @customer OR (@number IS NOT NULL AND c.CustomerNumber = @number)));
            """,
            connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@customer", SqlDbType.UniqueIdentifier).Value = customer.CustomerId;
        command.Parameters.Add("@number", SqlDbType.NVarChar, 50).Value = (object?)customer.CustomerNumber ?? DBNull.Value;
        command.Parameters.Add("@windowStart", SqlDbType.DateTime2).Value = now.AddDays(-thresholds.WindowDays);
        command.Parameters.Add("@previousStart", SqlDbType.DateTime2).Value = now.AddDays(-2 * thresholds.WindowDays);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return CustomerStarRules.Standing(reader.GetDecimal(0), reader.GetDecimal(1), thresholds);
    }

    public sealed record PurchaseCustomer(Guid CustomerId, string? CustomerNumber, string Name, string? Phone);

    private static async Task<PurchaseCustomer?> ReadCustomerAsync(SqlConnection connection, Guid businessId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CustomerID, CustomerNumber, Name, Phone FROM dbo.Customers WHERE CustomerID = @id AND BusinessID = @business;",
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = customerId;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PurchaseCustomer(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>ربح السطر كما حُفظ وقت البيع، أو السعر ناقص تكلفة الوحدة المحفوظة.</summary>
    private static async Task<string?> ProfitExpressionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var hasProfit = await ColumnExistsAsync(connection, "dbo.SaleItems", "LineProfitSnapshot", cancellationToken);
        var hasUnitCost = await ColumnExistsAsync(connection, "dbo.SaleItems", "UnitCostSnapshot", cancellationToken);
        return (hasProfit, hasUnitCost) switch
        {
            (true, true) => "COALESCE(line.LineProfitSnapshot, line.LineTotal - line.UnitCostSnapshot * line.Quantity)",
            (true, false) => "line.LineProfitSnapshot",
            (false, true) => "line.LineTotal - line.UnitCostSnapshot * line.Quantity",
            _ => null,
        };
    }

    // ------------------------------------------------------------------
    // حدود النجوم
    // ------------------------------------------------------------------

    public async Task<CustomerStarThresholds> ThresholdsAsync(PosDeviceContext actor, CancellationToken cancellationToken)
    {
        await using var local = await OpenLocalAsync(cancellationToken);
        var central = await OpenCentralAsync(cancellationToken);
        if (central is not null)
        {
            await using (central)
            {
                try
                {
                    await CopyNewestFromCentralAsync(central, local, actor.BusinessId, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogInformation(exception, "Central star thresholds were not readable; using the branch copy.");
                }
            }
        }

        return await ReadThresholdsAsync(local, actor.BusinessId, cancellationToken) ?? CustomerStarRules.Default;
    }

    public async Task<object> SaveThresholdsAsync(PosDeviceContext actor, CustomerStarThresholds wanted, CancellationToken cancellationToken)
    {
        await using var branch = await OpenLocalAsync(cancellationToken);
        if (!await TableExistsAsync(branch, "dbo.CustomerStarThresholds", cancellationToken))
        {
            throw new CustomerStarException("قاعدة هذا الفرع لم تُحدَّث بعد لنجوم الزبائن (الترحيل 052).");
        }

        var id = Guid.NewGuid();
        await using (var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken))
        {
            await InsertThresholdsAsync(branch, transaction, id, actor.BusinessId, wanted, actor.UserId, actor.DisplayName, "Branch", relayed: false, DateTime.UtcNow, cancellationToken);
            await BranchOperationLog.EnqueueAsync(branch, transaction, new BranchOperationDraft(
                id,
                ThresholdsOperationType,
                id.ToString("N"),
                $"حدود نجوم الزبائن — {wanted.WindowDays} يوماً",
                "CustomerStarThresholds",
                id,
                actor.BranchId,
                actor.UserId,
                actor.StationId), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var relayed = false;
        var central = await OpenCentralAsync(cancellationToken);
        if (central is not null)
        {
            await using (central)
            {
                try
                {
                    relayed = await RelayThresholdsAsync(central, branch, id, cancellationToken);
                }
                catch (SqlException exception)
                {
                    logger.LogInformation(exception, "Star thresholds {Id} stay queued for relay.", id);
                }
            }
        }
        else if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null)
        {
            // جهاز منفرد: لا مركز يُرحَّل إليه، فلا تبقى العملية معلّقة في سجل الترحيل.
            await BranchOperationLog.MarkAsync(branch, null, id, "Completed", "جهاز منفرد: حُفظت الحدود على هذا الجهاز فقط.", cancellationToken);
        }

        return new
        {
            thresholdId = id,
            relayed,
            message = relayed ? "تم حفظ حدود النجوم ووصلت إلى الإدارة المركزية." : "تم حفظ حدود النجوم على هذا الجهاز.",
        };
    }

    public static async Task<string?> RelayLoggedAsync(SqlConnection central, SqlConnection branch, CancellationToken cancellationToken)
    {
        Guid id;
        await using (var read = new SqlCommand(
            """
            SELECT TOP (1) OperationID FROM dbo.BranchOperationLog
            WHERE Status NOT IN (N'Completed', N'Cancelled') AND OperationType = @type
            ORDER BY Serial;
            """,
            branch))
        {
            read.Parameters.Add("@type", SqlDbType.NVarChar, 40).Value = ThresholdsOperationType;
            if (await read.ExecuteScalarAsync(cancellationToken) is not Guid found) return null;
            id = found;
        }

        try
        {
            return await RelayThresholdsAsync(central, branch, id, cancellationToken) ? "Completed" : "Blocked";
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            await BranchOperationLog.MarkAsync(branch, null, id, "Blocked", "جدول نجوم الزبائن غير موجود على المركز بعد (الترحيل 037).", cancellationToken);
            return "Blocked";
        }
    }

    private static async Task<bool> RelayThresholdsAsync(SqlConnection central, SqlConnection branch, Guid id, CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(branch, id, cancellationToken);
        if (row is null)
        {
            await BranchOperationLog.MarkAsync(branch, null, id, "Blocked", "حدود النجوم غير موجودة على هذا الفرع.", cancellationToken);
            return false;
        }

        await using (var transaction = (SqlTransaction)await central.BeginTransactionAsync(cancellationToken))
        {
            await InsertThresholdsAsync(central, transaction, id, row.BusinessId, row.Thresholds, row.UserId, row.Thresholds.UpdatedByName, "Branch", relayed: true, row.CreatedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await using (var mark = new SqlCommand("UPDATE dbo.CustomerStarThresholds SET RelayedAt = SYSUTCDATETIME() WHERE ThresholdID = @id AND RelayedAt IS NULL;", branch))
        {
            mark.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await BranchOperationLog.MarkAsync(branch, null, id, "Completed", "تم ترحيل حدود نجوم الزبائن إلى المركز.", cancellationToken);
        return true;
    }

    /// <summary>أحدث صف على المركز يُنسخ إلى الفرع إن لم يكن عنده، فيعمل الفرع بالحدود نفسها حتى بلا اتصال لاحقاً.</summary>
    private static async Task CopyNewestFromCentralAsync(SqlConnection central, SqlConnection local, Guid businessId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(central, "dbo.CustomerStarThresholds", cancellationToken)
            || !await TableExistsAsync(local, "dbo.CustomerStarThresholds", cancellationToken))
        {
            return;
        }

        Guid? newest;
        await using (var read = new SqlCommand(
            "SELECT TOP (1) ThresholdID FROM dbo.CustomerStarThresholds WHERE BusinessID = @business ORDER BY CreatedAt DESC, ThresholdID DESC;",
            central))
        {
            read.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
            newest = await read.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        if (newest is null) return;
        var row = await ReadRowAsync(central, newest.Value, cancellationToken);
        if (row is null) return;
        await using var transaction = (SqlTransaction)await local.BeginTransactionAsync(cancellationToken);
        await InsertThresholdsAsync(local, transaction, newest.Value, businessId, row.Thresholds, row.UserId, row.Thresholds.UpdatedByName, "Central", relayed: true, row.CreatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record ThresholdRow(Guid BusinessId, CustomerStarThresholds Thresholds, Guid? UserId, DateTime CreatedAt);

    private static async Task<ThresholdRow?> ReadRowAsync(SqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT BusinessID, WindowDays, OneStar, TwoStars, ThreeStars, FourStars, FiveStars, CreatedAt, CreatedByUserID, CreatedByName
            FROM dbo.CustomerStarThresholds WHERE ThresholdID = @id;
            """,
            connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var thresholds = new CustomerStarThresholds(
            reader.GetInt32(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6),
            false, reader.GetDateTime(7), reader.IsDBNull(9) ? null : reader.GetString(9));
        return new ThresholdRow(reader.GetGuid(0), thresholds, reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetDateTime(7));
    }

    private static async Task<CustomerStarThresholds?> ReadThresholdsAsync(SqlConnection connection, Guid businessId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "dbo.CustomerStarThresholds", cancellationToken)) return null;
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) WindowDays, OneStar, TwoStars, ThreeStars, FourStars, FiveStars, CreatedAt, CreatedByName
            FROM dbo.CustomerStarThresholds
            WHERE BusinessID = @business
            ORDER BY CreatedAt DESC, ThresholdID DESC;
            """,
            connection);
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CustomerStarThresholds(
            reader.GetInt32(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5),
            false, reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static async Task InsertThresholdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid id,
        Guid businessId,
        CustomerStarThresholds thresholds,
        Guid? userId,
        string? userName,
        string source,
        bool relayed,
        DateTime createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.CustomerStarThresholds WITH (UPDLOCK, HOLDLOCK) WHERE ThresholdID = @id)
                INSERT INTO dbo.CustomerStarThresholds
                    (ThresholdID, BusinessID, WindowDays, OneStar, TwoStars, ThreeStars, FourStars, FiveStars, CreatedAt, CreatedByUserID, CreatedByName, Source, RelayedAt)
                VALUES
                    (@id, @business, @window, @one, @two, @three, @four, @five, @created, @user, @userName, @source,
                     CASE WHEN @relayed = 1 THEN SYSUTCDATETIME() ELSE NULL END);
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@window", SqlDbType.Int).Value = thresholds.WindowDays;
        AddMoney(command, "@one", thresholds.OneStar);
        AddMoney(command, "@two", thresholds.TwoStars);
        AddMoney(command, "@three", thresholds.ThreeStars);
        AddMoney(command, "@four", thresholds.FourStars);
        AddMoney(command, "@five", thresholds.FiveStars);
        command.Parameters.Add("@created", SqlDbType.DateTime2).Value = createdAt;
        command.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@userName", SqlDbType.NVarChar, 200).Value = (object?)userName ?? DBNull.Value;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 20).Value = source;
        command.Parameters.Add("@relayed", SqlDbType.Bit).Value = relayed;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------
    // الاتصال
    // ------------------------------------------------------------------

    /// <summary>
    /// المركز أولاً. بلا مركز محفوظ (جهاز منفرد) تُقرأ القاعدة المحلية دون أي ملاحظة،
    /// ومع مركز محفوظ لا يجيب تظهر ملاحظة الانقطاع.
    /// </summary>
    private async Task<(SqlConnection Connection, string Source, string? Notice)> OpenReadAsync(CancellationToken cancellationToken)
    {
        if (await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken) is null)
        {
            return (await OpenLocalAsync(cancellationToken), "local", null);
        }

        var central = await OpenCentralAsync(cancellationToken);
        return central is null
            ? (await OpenLocalAsync(cancellationToken), "local", CentralUnreachableNotice)
            : (central, "central", null);
    }

    private async Task<SqlConnection> OpenLocalAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<SqlConnection?> OpenCentralAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CentralTimeout);
        try
        {
            var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, timeout.Token);
            if (settings is null) return null;
            var builder = new SqlConnectionStringBuilder(settings.BuildConnectionString(settings.DatabaseName))
            {
                ConnectTimeout = (int)CentralTimeout.TotalSeconds,
            };
            var connection = new SqlConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync(timeout.Token);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception) when (
            exception is SqlException or InvalidOperationException or TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation(exception, "Central was not reachable for customer purchases; using the branch database.");
            return null;
        }
    }

    private static void AddAll(SqlCommand command, IEnumerable<SqlParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SqlParameter(parameter.ParameterName, parameter.SqlDbType, parameter.Size) { Value = parameter.Value });
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@name, N'U');", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COL_LENGTH(@table, @column);", connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 256).Value = table;
        command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = column;
        return await command.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
    }

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 4;
        parameter.Value = value;
    }
}
