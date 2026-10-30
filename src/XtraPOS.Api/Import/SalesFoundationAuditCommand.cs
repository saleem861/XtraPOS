using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Import;

public static partial class SalesFoundationAuditCommand
{
    public const string CommandName = "--audit-sales-foundation";
    private static readonly string[] RemoteCatalogs = ["saleemPOSDB", "Shop1", "Shop2", "swstore"];
    private static readonly HashSet<string> DeepNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sales", "SalesTemp", "Invoices", "SaleItems", "SalesItems", "SaleDetails", "SalesDetails",
        "Receipts", "Payments", "Customers", "POSs", "SalesOfAllBranches",
    };

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static async Task<int> ExecuteGapsAsync(
        SqlEndpointSettings central,
        SqlConnectionFactory connections,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var branchBefore = await CaptureBranchAsync(connections, cancellationToken);
        var centralBefore = await CaptureCentralAsync(central, cancellationToken);
        var oldBefore = await CaptureOldBranchAsync(cancellationToken);
        await using (var remote = await OpenRemoteAsync(central, CentralDatabase.Name, cancellationToken))
        {
            var items = (await ReadRowsAsync(remote, "SELECT ItemNumber FROM dbo.Items;", cancellationToken))
                .Select(row => row[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var catalog in new[] { "Shop1", "Shop2", "swstore" })
            {
                await using var source = await OpenRemoteAsync(central, catalog, cancellationToken);
                var distinct = await ReadRowsAsync(source, """
                    SELECT DISTINCT LTRIM(RTRIM(ItemNumber))
                    FROM dbo.Sales
                    WHERE NULLIF(LTRIM(RTRIM(ItemNumber)), N'') IS NOT NULL;
                    """, cancellationToken);
                var unresolved = distinct.Count(row => !items.Contains(row[0]));
                await output.WriteLineAsync(catalog + " DistinctItems=" + distinct.Count + " UnresolvedItems=" + unresolved);
                await output.WriteLineAsync(catalog + " DebtOrders " + await ReadLinesAsync(source, """
                    SELECT COUNT(DISTINCT OrderID) FROM dbo.Debts;
                    SELECT COUNT(DISTINCT s.OrderID)
                    FROM dbo.Sales AS s
                    INNER JOIN dbo.Debts AS d ON d.OrderID = s.OrderID;
                    SELECT TransactionType, COUNT_BIG(*)
                    FROM dbo.Debts
                    GROUP BY TransactionType
                    ORDER BY COUNT_BIG(*) DESC;
                    SELECT
                        SUM(CASE WHEN NULLIF(LTRIM(RTRIM(InternalUnitItemNumber)), N'') IS NOT NULL THEN 1 ELSE 0 END),
                        SUM(CASE WHEN ISNULL(CountOfInternalUnits, 0) > 1 THEN 1 ELSE 0 END)
                    FROM dbo.Sales;
                    """, cancellationToken));
            }
        }

        var branchAfter = await CaptureBranchAsync(connections, cancellationToken);
        var centralAfter = await CaptureCentralAsync(central, cancellationToken);
        var oldAfter = await CaptureOldBranchAsync(cancellationToken);
        var unchanged = string.Equals(branchBefore, branchAfter, StringComparison.Ordinal)
            && string.Equals(centralBefore, centralAfter, StringComparison.Ordinal)
            && string.Equals(oldBefore, oldAfter, StringComparison.Ordinal);
        await output.WriteLineAsync(unchanged ? "FINGERPRINTS_UNCHANGED" : "FINGERPRINTS_CHANGED");
        return unchanged ? 0 : 2;
    }

    public static async Task<int> ExecuteAsync(
        SqlEndpointSettings central,
        SqlConnectionFactory connections,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(connections.ConfiguredDatabase, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
        {
            await output.WriteLineAsync("Sales foundation audit requires the configured database XtraPOS_Branch_01.");
            return 1;
        }

        var branchBefore = await CaptureBranchAsync(connections, cancellationToken);
        var centralBefore = await CaptureCentralAsync(central, cancellationToken);
        var oldBefore = await CaptureOldBranchAsync(cancellationToken);
        await output.WriteLineAsync("FINGERPRINT_BEFORE");
        await output.WriteLineAsync(branchBefore);
        await output.WriteLineAsync(centralBefore);
        await output.WriteLineAsync(oldBefore);

        await ReportQaUserAsync(connections, central, output, cancellationToken);

        foreach (var catalog in RemoteCatalogs)
        {
            await AuditCatalogAsync(central, catalog, output, cancellationToken);
        }

        await AuditLocalShop1DistinctionAsync(output, cancellationToken);

        var branchAfter = await CaptureBranchAsync(connections, cancellationToken);
        var centralAfter = await CaptureCentralAsync(central, cancellationToken);
        var oldAfter = await CaptureOldBranchAsync(cancellationToken);
        await output.WriteLineAsync("FINGERPRINT_AFTER");
        await output.WriteLineAsync(branchAfter);
        await output.WriteLineAsync(centralAfter);
        await output.WriteLineAsync(oldAfter);
        var unchanged = string.Equals(branchBefore, branchAfter, StringComparison.Ordinal)
            && string.Equals(centralBefore, centralAfter, StringComparison.Ordinal)
            && string.Equals(oldBefore, oldAfter, StringComparison.Ordinal);
        await output.WriteLineAsync(unchanged ? "FINGERPRINTS_UNCHANGED" : "FINGERPRINTS_CHANGED");
        return unchanged ? 0 : 2;
    }

    private static async Task ReportQaUserAsync(
        SqlConnectionFactory connections,
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("QA_USER");
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await output.WriteLineAsync("Branch " + await ReadLinesAsync(branch, QaSql, cancellationToken));
        try
        {
            await using var remote = await OpenRemoteAsync(central, CentralDatabase.Name, cancellationToken);
            await output.WriteLineAsync("Central " + await ReadLinesAsync(remote, QaSql, cancellationToken));
            await output.WriteLineAsync("QA_DECISION Kept inactive. UserAdministrationAudit.TargetUserID references the user, and Users has no test-classification column. The audit rows stay.");
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync("Central QA read failed: " + exception.GetType().Name);
            await output.WriteLineAsync("QA_DECISION Kept inactive on the branch. Central inspection did not complete.");
        }
    }

    private const string QaSql =
        """
        SELECT 'QaUser', u.Username, CAST(u.IsActive AS int)
        FROM dbo.Users AS u
        WHERE u.Username = N'xtrapos.test.6h';
        SELECT 'QaAudit', a.Action, COUNT(*)
        FROM dbo.UserAdministrationAudit AS a
        INNER JOIN dbo.Users AS u ON u.UserID = a.TargetUserID
        WHERE u.Username = N'xtrapos.test.6h'
        GROUP BY a.Action;
        SELECT 'Users', COUNT(*) FROM dbo.Users;
        SELECT fk.name, OBJECT_NAME(fk.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.Users')
          AND OBJECT_NAME(fk.parent_object_id) = N'UserAdministrationAudit';
        """;

    private static async Task AuditCatalogAsync(
        SqlEndpointSettings central,
        string catalog,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync("CATALOG " + catalog);
        SqlConnection connection;
        try
        {
            connection = await OpenRemoteAsync(central, catalog, cancellationToken);
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync("ABSENT " + catalog + " " + exception.GetType().Name);
            return;
        }

        await using (connection)
        {
            var objects = await ReadLinesAsync(connection, """
                SELECT o.type_desc, o.name
                FROM sys.objects AS o
                WHERE o.is_ms_shipped = 0
                  AND o.type IN ('U', 'V', 'P')
                  AND (
                    o.name LIKE N'%Sale%' OR o.name LIKE N'%Invoice%' OR o.name LIKE N'%Receipt%'
                    OR o.name LIKE N'%Payment%' OR o.name LIKE N'%Cash%' OR o.name LIKE N'%Discount%'
                    OR o.name LIKE N'%Customer%' OR o.name LIKE N'%Return%' OR o.name LIKE N'%Refund%'
                    OR o.name LIKE N'%POS%' OR o.name LIKE N'%Held%' OR o.name LIKE N'%Suspend%'
                    OR o.name LIKE N'%Debt%' OR o.name LIKE N'%Credit%' OR o.name = N'Invoices')
                ORDER BY o.type_desc, o.name;
                """, cancellationToken);
            await output.WriteLineAsync(objects);
            var tables = await ReadRowsAsync(connection, """
                SELECT t.name, SUM(p.rows)
                FROM sys.tables AS t
                INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                WHERE t.is_ms_shipped = 0
                  AND (
                    t.name LIKE N'%Sale%' OR t.name LIKE N'%Invoice%' OR t.name LIKE N'%Receipt%'
                    OR t.name LIKE N'%Payment%' OR t.name LIKE N'%Cash%' OR t.name LIKE N'%Discount%'
                    OR t.name LIKE N'%Customer%' OR t.name LIKE N'%Return%' OR t.name LIKE N'%Refund%'
                    OR t.name LIKE N'%POS%' OR t.name LIKE N'%Held%' OR t.name LIKE N'%Suspend%'
                    OR t.name LIKE N'%Debt%' OR t.name LIKE N'%Credit%')
                GROUP BY t.name
                ORDER BY t.name;
                """, cancellationToken);
            foreach (var table in tables)
            {
                await output.WriteLineAsync("TABLE " + table[0] + " PartitionRows=" + table[1]);
            }

            if (tables.Any(row => string.Equals(row[0], "POSs", StringComparison.OrdinalIgnoreCase)))
            {
                await PrintPosEvidenceAsync(connection, output, cancellationToken);
            }

            foreach (var table in tables.Where(row => DeepNames.Contains(row[0])))
            {
                await ProfileTableAsync(connection, table[0], output, cancellationToken);
            }

            if (tables.Any(row => string.Equals(row[0], "Sales", StringComparison.OrdinalIgnoreCase))
                && tables.Any(row => string.Equals(row[0], "SalesTemp", StringComparison.OrdinalIgnoreCase)))
            {
                await CompareSalesTempAsync(connection, output, cancellationToken);
            }

            await DetailAsync(connection, catalog, tables.Select(row => row[0]).ToArray(), output, cancellationToken);
        }
    }

    private static async Task DetailAsync(SqlConnection connection, string catalog, string[] tables, TextWriter output, CancellationToken cancellationToken)
    {
        bool Has(string name) => tables.Any(table => string.Equals(table, name, StringComparison.OrdinalIgnoreCase));
        if (Has("Sales"))
        {
            await output.WriteLineAsync("DETAIL Sales " + catalog);
            await TryDetailAsync(connection, output, "stats", """
                SELECT
                    COUNT(DISTINCT OrderID),
                    SUM(CASE WHEN OrderID IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN ItemNumber IS NULL OR LTRIM(RTRIM(ItemNumber)) = N'' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN ABS(CAST(Price AS decimal(19,4)) - CAST(Quantity AS decimal(19,4)) * CAST(UnitPrice AS decimal(19,4))) < 0.02 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN UnitPrice = OriginalPrice THEN 1 ELSE 0 END),
                    SUM(CASE WHEN UnitPrice < OriginalPrice AND Quantity > 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN UnitPrice = CustomerPrice THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity < 0 AND Price < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity < 0 AND ISNULL(Price, 0) >= 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity > 0 AND Price < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity < 0 AND QuantityAfterSale > QuantityBeforeSale THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity > 0 AND QuantityAfterSale < QuantityBeforeSale THEN 1 ELSE 0 END),
                    SUM(CASE WHEN IsModified = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ParentItemNumber)), N'') IS NOT NULL THEN 1 ELSE 0 END),
                    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(UserShortName)), N''))
                FROM dbo.Sales;
                SELECT BranchNumber, COUNT_BIG(*)
                FROM dbo.Sales
                GROUP BY BranchNumber
                ORDER BY COUNT_BIG(*) DESC;
                SELECT
                    SUM(CASE WHEN Qty < 0 AND Total < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Qty < 0 AND Total >= 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Qty >= 0 AND Total < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Mixed = 1 THEN 1 ELSE 0 END),
                    COUNT_BIG(*)
                FROM (
                    SELECT
                        SUM(CAST(Quantity AS decimal(19,4))) AS Qty,
                        SUM(CAST(Price AS decimal(19,4))) AS Total,
                        CASE WHEN SUM(CASE WHEN Quantity < 0 THEN 1 ELSE 0 END) > 0 AND SUM(CASE WHEN Quantity > 0 THEN 1 ELSE 0 END) > 0 THEN 1 ELSE 0 END AS Mixed
                    FROM dbo.Sales
                    WHERE OrderID IS NOT NULL
                    GROUP BY OrderID
                ) AS orders;
                """, cancellationToken);
            await TryDetailAsync(connection, output, "unresolved-items", """
                SELECT COUNT(DISTINCT s.ItemNumber)
                FROM dbo.Sales AS s
                WHERE NULLIF(LTRIM(RTRIM(s.ItemNumber)), N'') IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM XtraPOS_Central.dbo.Items AS i WHERE i.ItemNumber = s.ItemNumber);
                """, cancellationToken);
            await TryDetailAsync(connection, output, "unresolved-customers", """
                SELECT COUNT(DISTINCT s.CustomerPhoneNumber)
                FROM dbo.Sales AS s
                WHERE NULLIF(LTRIM(RTRIM(s.CustomerPhoneNumber)), N'') IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM dbo.Customers AS c WHERE c.CustomerPhoneNumber = s.CustomerPhoneNumber);
                """, cancellationToken);
            await TryDetailAsync(connection, output, "unresolved-users", """
                SELECT COUNT(DISTINCT s.UserShortName)
                FROM dbo.Sales AS s
                WHERE NULLIF(LTRIM(RTRIM(s.UserShortName)), N'') IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM saleemPOSDB.dbo.UsersTable AS u WHERE u.UserShortName = s.UserShortName);
                """, cancellationToken);
            await TryDetailAsync(connection, output, "top-customers", """
                SELECT TOP 5 LEFT(ISNULL(CustomerName, N''), 40), CustomerType, PriceType, Lines
                FROM (
                    SELECT c.CustomerName, c.CustomerType, c.PriceType, COUNT_BIG(*) AS Lines
                    FROM dbo.Sales AS s
                    INNER JOIN dbo.Customers AS c ON c.CustomerPhoneNumber = s.CustomerPhoneNumber
                    GROUP BY c.CustomerName, c.CustomerType, c.PriceType
                ) AS ranked
                ORDER BY Lines DESC;
                """, cancellationToken);
        }

        if (Has("SalesTemp"))
        {
            await output.WriteLineAsync("DETAIL SalesTemp " + catalog);
            await output.WriteLineAsync(await ReadLinesAsync(connection, """
                SELECT
                    SUM(CASE WHEN ItemNumber IS NULL OR LTRIM(RTRIM(ItemNumber)) = N'' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Quantity IS NOT NULL AND NULLIF(LTRIM(RTRIM(ItemNumber)), N'') IS NOT NULL THEN 1 ELSE 0 END),
                    COUNT(DISTINCT OrderID)
                FROM dbo.SalesTemp;
                """, cancellationToken));
        }

        if (Has("PaymentMethods"))
        {
            await output.WriteLineAsync("DETAIL PaymentMethods " + catalog);
            await output.WriteLineAsync(await ReadLinesAsync(connection, "SELECT * FROM dbo.PaymentMethods;", cancellationToken));
        }

        if (Has("DiscountsSales"))
        {
            await output.WriteLineAsync("DETAIL DiscountsSales " + catalog);
            await output.WriteLineAsync(await ReadLinesAsync(connection, "SELECT * FROM dbo.DiscountsSales;", cancellationToken));
        }

        foreach (var name in new[] { "Debts", "CashDrawerCalculatingRecords", "CustomersAllowedToDebt", "ReceiptData" })
        {
            if (!Has(name))
            {
                continue;
            }

            await output.WriteLineAsync("DETAIL " + name + " " + catalog);
            var columns = await ReadRowsAsync(connection, """
                SELECT c.name, ty.name
                FROM sys.columns AS c
                INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(@table)
                ORDER BY c.column_id;
                """, cancellationToken, ("@table", "dbo." + name));
            await output.WriteLineAsync(string.Join(" | ", columns.Select(row => row[0] + ":" + row[1])));
            var date = columns.FirstOrDefault(row => IsDate(row[1]) && SafeName(row[0]));
            var countSql = "SELECT COUNT_BIG(*)" + (date is null ? "" : ", CONVERT(varchar(19), MIN([" + date[0] + "]), 120), CONVERT(varchar(19), MAX([" + date[0] + "]), 120)") + " FROM dbo.[" + name + "];";
            await output.WriteLineAsync(await ReadLinesAsync(connection, countSql, cancellationToken));
            var method = columns.FirstOrDefault(row => SafeName(row[0]) && GroupName().IsMatch(row[0]) && !row[0].Contains("Connection", StringComparison.OrdinalIgnoreCase) && IsGroupable(row[1]));
            if (method is not null)
            {
                await output.WriteLineAsync(await ReadLinesAsync(connection, "SELECT TOP 8 CONVERT(nvarchar(80), [" + method[0] + "]), COUNT_BIG(*) FROM dbo.[" + name + "] GROUP BY [" + method[0] + "] ORDER BY COUNT_BIG(*) DESC;", cancellationToken));
            }
        }

        if (Has("POSs"))
        {
            await output.WriteLineAsync(await ReadLinesAsync(connection, """
                SELECT POSNumber, POSName, CAST(IsItReference AS int), CAST(IsItServer AS int), CAST(IsItOnlyStore AS int)
                FROM dbo.POSs
                ORDER BY POSNumber;
                """, cancellationToken));
        }
    }

    private static async Task ProfileTableAsync(SqlConnection connection, string table, TextWriter output, CancellationToken cancellationToken)
    {
        if (!SafeName(table))
        {
            return;
        }

        await output.WriteLineAsync("PROFILE " + table);
        var columns = await ReadRowsAsync(connection, """
            SELECT c.name, ty.name, c.is_nullable, c.max_length, c.precision, c.scale
            FROM sys.columns AS c
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table)
            ORDER BY c.column_id;
            """, cancellationToken, ("@table", "dbo." + table));
        foreach (var column in columns)
        {
            await output.WriteLineAsync("COLUMN " + string.Join(" | ", column));
        }

        var keys = await ReadLinesAsync(connection, """
            SELECT i.is_primary_key, i.is_unique, i.name, c.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND ic.is_included_column = 0 AND i.is_hypothetical = 0
            ORDER BY i.is_primary_key DESC, i.name, ic.key_ordinal;
            """, cancellationToken, ("@table", "dbo." + table));
        await output.WriteLineAsync(string.IsNullOrWhiteSpace(keys) ? "KEYS none" : keys);

        var count = await ScalarAsync(connection, "SELECT COUNT_BIG(*) FROM dbo.[" + table + "];", cancellationToken);
        await output.WriteLineAsync("EXACT " + table + " " + count);

        var names = columns.Select(row => row[0]).Where(SafeName).ToArray();
        var types = columns.Where(row => SafeName(row[0])).ToDictionary(row => row[0], row => row[1], StringComparer.OrdinalIgnoreCase);
        var dates = names.Where(name => IsDate(types[name])).ToArray();
        if (dates.Length > 0)
        {
            var sql = "SELECT " + string.Join(", ", dates.Select(name => "CONVERT(varchar(19), MIN([" + name + "]), 120), CONVERT(varchar(19), MAX([" + name + "]), 120)")) + " FROM dbo.[" + table + "];";
            await output.WriteLineAsync("DATES " + string.Join(" | ", dates));
            await output.WriteLineAsync(await ReadLinesAsync(connection, sql, cancellationToken));
        }

        var numbers = names.Where(name => IsNumber(types[name]) && IsMeasure(name)).Take(12).ToArray();
        if (numbers.Length > 0)
        {
            var parts = numbers.Select(name =>
                "SUM(CASE WHEN [" + name + "] < 0 THEN 1 ELSE 0 END), SUM(CASE WHEN [" + name + "] = 0 THEN 1 ELSE 0 END), SUM(CASE WHEN [" + name + "] > 0 THEN 1 ELSE 0 END)");
            await output.WriteLineAsync("SIGNS " + string.Join(" | ", numbers));
            await output.WriteLineAsync(await ReadLinesAsync(connection, "SELECT " + string.Join(", ", parts) + " FROM dbo.[" + table + "];", cancellationToken));
        }

        var quantity = names.FirstOrDefault(name => IsNumber(types[name]) && QuantityName().IsMatch(name));
        var total = names.FirstOrDefault(name => IsNumber(types[name]) && TotalName().IsMatch(name));
        if (quantity is not null && total is not null)
        {
            await output.WriteLineAsync("PATTERN " + quantity + " " + total);
            await output.WriteLineAsync(await ReadLinesAsync(connection, $"""
                SELECT
                    SUM(CASE WHEN [{quantity}] < 0 AND [{total}] < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN [{quantity}] < 0 AND [{total}] >= 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN [{quantity}] >= 0 AND [{total}] < 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN [{quantity}] > 0 AND [{total}] > 0 THEN 1 ELSE 0 END)
                FROM dbo.[{table}];
                """, cancellationToken));
        }

        foreach (var name in names.Where(name => GroupName().IsMatch(name) && IsGroupable(types[name]) && !name.Contains("Connection", StringComparison.OrdinalIgnoreCase) && !name.Contains("Password", StringComparison.OrdinalIgnoreCase)).Take(6))
        {
            await output.WriteLineAsync("GROUP " + name);
            await output.WriteLineAsync(await ReadLinesAsync(connection, $"""
                SELECT TOP 25 CONVERT(nvarchar(100), [{name}]), COUNT_BIG(*)
                FROM dbo.[{table}]
                GROUP BY [{name}]
                ORDER BY COUNT_BIG(*) DESC;
                """, cancellationToken));
        }

        foreach (var name in names.Where(name => CustomerName().IsMatch(name)).Take(4))
        {
            var empty = IsNumber(types[name])
                ? $"[{name}] IS NULL OR [{name}] = 0"
                : $"[{name}] IS NULL OR LTRIM(RTRIM(CONVERT(nvarchar(100), [{name}]))) = N''";
            await output.WriteLineAsync("CUSTOMER " + name);
            await output.WriteLineAsync(await ReadLinesAsync(connection, $"SELECT COUNT_BIG(*), SUM(CASE WHEN {empty} THEN 1 ELSE 0 END) FROM dbo.[{table}];", cancellationToken));
        }
    }

    private static async Task CompareSalesTempAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        var sales = await ColumnSetAsync(connection, "Sales", cancellationToken);
        var temp = await ColumnSetAsync(connection, "SalesTemp", cancellationToken);
        var shared = sales.Intersect(temp, StringComparer.OrdinalIgnoreCase).Where(SafeName).ToArray();
        await output.WriteLineAsync("SHARED_COLUMNS " + string.Join(", ", shared));
        var key = new[] { "ID", "SaleID", "InvoiceID", "InvoiceNumber", "BillNumber", "ReceiptNumber" }
            .FirstOrDefault(name => shared.Contains(name, StringComparer.OrdinalIgnoreCase))
            ?? shared.FirstOrDefault(name => KeyName().IsMatch(name) && !name.Contains("Item", StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            await output.WriteLineAsync("SALES_TEMP_OVERLAP no shared key column");
            return;
        }

        await output.WriteLineAsync("SALES_TEMP_KEY " + key);
        await output.WriteLineAsync(await ReadLinesAsync(connection, $"""
            SELECT
                (SELECT COUNT_BIG(*) FROM dbo.SalesTemp AS t WHERE EXISTS (SELECT 1 FROM dbo.Sales AS s WHERE s.[{key}] = t.[{key}])),
                (SELECT COUNT_BIG(*) FROM dbo.SalesTemp AS t WHERE NOT EXISTS (SELECT 1 FROM dbo.Sales AS s WHERE s.[{key}] = t.[{key}]));
            """, cancellationToken));
    }

    private static async Task PrintPosEvidenceAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        var columns = await ColumnSetAsync(connection, "POSs", cancellationToken);
        if (!columns.Contains("POSNumber"))
        {
            await output.WriteLineAsync("POS_EVIDENCE POSs has no POSNumber");
            return;
        }

        var select = new List<string> { "POSNumber" };
        foreach (var name in new[] { "IsItReference", "POSName", "Name", "BranchName", "DatabaseName" })
        {
            if (columns.Contains(name))
            {
                select.Add(name);
            }
        }

        var hasConnection = columns.Contains("POSConnectionString");
        if (hasConnection)
        {
            select.Add("POSConnectionString");
        }

        var rows = await ReadRowsAsync(connection, "SELECT " + string.Join(", ", select.Select(name => "[" + name + "]")) + " FROM dbo.POSs ORDER BY POSNumber;", cancellationToken);
        foreach (var row in rows)
        {
            var fields = new List<string>();
            for (var index = 0; index < select.Count; index++)
            {
                if (string.Equals(select[index], "POSConnectionString", StringComparison.Ordinal))
                {
                    fields.Add(DescribeConnection(row[index]));
                }
                else
                {
                    fields.Add(select[index] + "=" + row[index]);
                }
            }

            await output.WriteLineAsync("POS " + string.Join(" | ", fields));
        }
    }

    private static string DescribeConnection(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Catalog=(none)";
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            return "DataSource=" + builder.DataSource + " Catalog=" + builder.InitialCatalog;
        }
        catch (ArgumentException)
        {
            return "Catalog=(unparsed)";
        }
    }

    private static async Task AuditLocalShop1DistinctionAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = "shop1",
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ConnectTimeout = 8,
            ApplicationName = "XtraPOS.SalesFoundationAudit",
        };
        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT COUNT_BIG(*) FROM dbo.Sales; SELECT COUNT_BIG(*) FROM dbo.SalesTemp;", connection)
            {
                CommandTimeout = 120,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var sales = reader.GetValue(0);
            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            await output.WriteLineAsync("LOCAL_SHOP1_NOT_REMOTE Sales=" + Convert.ToString(sales, CultureInfo.InvariantCulture) + " SalesTemp=" + Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync("LOCAL_SHOP1_NOT_REMOTE " + exception.GetType().Name);
        }
    }

    private static async Task<HashSet<string>> ColumnSetAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(connection, "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@table);", cancellationToken, ("@table", "dbo." + table));
        return rows.Select(row => row[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> CaptureBranchAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return "Branch " + await ReadLinesAsync(connection, """
            SELECT COUNT(*) FROM dbo.Items;
            SELECT COUNT(*) FROM dbo.ItemBranchInventory;
            SELECT COUNT(*) FROM dbo.BranchStockBalances;
            SELECT COUNT(*) FROM dbo.StockMovements;
            SELECT COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL;
            SELECT COUNT(*) FROM dbo.Users;
            SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;
            """, cancellationToken);
    }

    private static async Task<string> CaptureCentralAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await using var connection = await OpenRemoteAsync(central, CentralDatabase.Name, cancellationToken);
        return "Central " + await ReadLinesAsync(connection, """
            SELECT COUNT(*) FROM dbo.Businesses;
            SELECT COUNT(*) FROM dbo.Branches;
            SELECT COUNT(*) FROM dbo.Items;
            SELECT COUNT(*) FROM dbo.ItemPrices;
            SELECT COUNT(*) FROM dbo.Users;
            SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory;
            """, cancellationToken);
    }

    private static async Task<string> CaptureOldBranchAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = BranchDatabase.Name,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "XtraPOS.SalesFoundationAudit",
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return "OldBranch " + await ReadLinesAsync(connection, """
            SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY MigrationID;
            SELECT COUNT(*) FROM dbo.Businesses;
            SELECT COUNT(*) FROM dbo.Branches;
            SELECT COUNT(*) FROM dbo.Users;
            SELECT CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;
            """, cancellationToken);
    }

    private static async Task<SqlConnection> OpenRemoteAsync(SqlEndpointSettings central, string catalog, CancellationToken cancellationToken)
    {
        if (catalog is not ("saleemPOSDB" or "Shop1" or "Shop2" or "swstore" or "XtraPOS_Central"))
        {
            throw new InvalidOperationException("Sales audit refuses this catalog.");
        }

        var builder = new SqlConnectionStringBuilder(central.BuildConnectionString(catalog))
        {
            ConnectTimeout = 20,
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "XtraPOS.SalesFoundationAudit",
        };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task TryDetailAsync(SqlConnection connection, TextWriter output, string label, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await output.WriteLineAsync(label);
            await output.WriteLineAsync(await ReadLinesAsync(connection, sql, cancellationToken));
        }
        catch (Exception exception)
        {
            await output.WriteLineAsync(label + " FAILED " + exception.GetType().Name);
        }
    }

    private static async Task<string> ReadLinesAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] parameters)
    {
        var rows = await ReadRowsAsync(connection, sql, cancellationToken, parameters);
        return string.Join(Environment.NewLine, rows.Select(row => string.Join(" | ", row)));
    }

    private static async Task<List<string[]>> ReadRowsAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] parameters)
    {
        var rows = new List<string[]>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var fields = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    fields[index] = reader.IsDBNull(index) ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
                }

                rows.Add(fields);
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return rows;
    }

    private static async Task<long> ScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool SafeName(string name) => SafeIdentifier().IsMatch(name);

    private static bool IsDate(string type) =>
        type.Equals("datetime", StringComparison.OrdinalIgnoreCase)
        || type.Equals("smalldatetime", StringComparison.OrdinalIgnoreCase)
        || type.Equals("date", StringComparison.OrdinalIgnoreCase)
        || type.Equals("datetime2", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumber(string type) =>
        type.Equals("int", StringComparison.OrdinalIgnoreCase)
        || type.Equals("bigint", StringComparison.OrdinalIgnoreCase)
        || type.Equals("smallint", StringComparison.OrdinalIgnoreCase)
        || type.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
        || type.Equals("decimal", StringComparison.OrdinalIgnoreCase)
        || type.Equals("numeric", StringComparison.OrdinalIgnoreCase)
        || type.Equals("money", StringComparison.OrdinalIgnoreCase)
        || type.Equals("smallmoney", StringComparison.OrdinalIgnoreCase)
        || type.Equals("float", StringComparison.OrdinalIgnoreCase)
        || type.Equals("real", StringComparison.OrdinalIgnoreCase);

    private static bool IsGroupable(string type) => IsNumber(type) || type.Contains("char", StringComparison.OrdinalIgnoreCase) || type.Equals("bit", StringComparison.OrdinalIgnoreCase);

    private static bool IsMeasure(string name) => MeasureName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    [GeneratedRegex("qty|quantity|qnty|total|price|discount|tax|paid|cash|cost|amount", RegexOptions.IgnoreCase)]
    private static partial Regex MeasureName();

    [GeneratedRegex("qty|quantity|qnty", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityName();

    [GeneratedRegex("total|net|amount", RegexOptions.IgnoreCase)]
    private static partial Regex TotalName();

    [GeneratedRegex("pos|status|type|pay|return|void|cancel|kind", RegexOptions.IgnoreCase)]
    private static partial Regex GroupName();

    [GeneratedRegex("customer|client|account", RegexOptions.IgnoreCase)]
    private static partial Regex CustomerName();

    [GeneratedRegex("id$|number$|no$|code$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyName();
}
