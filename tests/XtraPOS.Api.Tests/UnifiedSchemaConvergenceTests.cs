using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[CollectionDefinition("UnifiedSchemaSerial", DisableParallelization = true)]
public sealed class UnifiedSchemaSerialCollection;

[Collection("UnifiedSchemaSerial")]
public sealed class UnifiedSchemaConvergenceTests
{
    private const string PrimaryName = "XtraPOS_Branch_94";
    private const string SecondaryName = "XtraPOS_Branch_95";
    private const string CentralName = "XtraPOS_Central_Test_LocalOps";

    [Fact]
    public async Task Isolated_databases_converge_without_touching_protected_databases()
    {
        await using var master = new SqlConnection(Master());
        await master.OpenAsync();
        var shop1Before = await ScalarAsync(master, "SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0;");
        var protectedCentral = await ScalarAsync(master, "SELECT DB_ID(N'XtraPOS_Central');");
        var protectedBranch = await ScalarAsync(master, "SELECT DB_ID(N'XtraPOS_Branch_01');");

        var runner = new MigrationRunner(
            new SqlConnectionFactory(Options.Create(new SqlServerOptions
            {
                Server = "localhost",
                Database = PrimaryName,
                ConnectTimeoutSeconds = 30,
            })),
            NullLogger<MigrationRunner>.Instance);
        await runner.ApplyPendingToAsync(Database(PrimaryName), CancellationToken.None);
        await runner.ApplyPendingToAsync(Database(SecondaryName), CancellationToken.None);
        await runner.ApplyPendingToAsync(Database(PrimaryName), CancellationToken.None);
        var centralRunner = new CentralMigrationRunner();
        await centralRunner.ApplyAsync(Database(CentralName), CancellationToken.None);
        await centralRunner.ApplyAsync(Database(CentralName), CancellationToken.None);

        Assert.Equal(1, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'043_UnifiedSchemaObjects';"));
        Assert.Equal(1, await CountAsync(CentralName, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'029_UnifiedSchemaObjects';"));
        Assert.Equal("branch-045+central-031", SchemaConvergence.ApprovedVersion);

        var primaryTables = await NamesAsync(PrimaryName, "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name;");
        var secondaryTables = await NamesAsync(SecondaryName, "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name;");
        var centralTables = await NamesAsync(CentralName, "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name;");
        Assert.Equal(primaryTables, secondaryTables);
        Assert.Equal(primaryTables, centralTables);

        var primaryColumns = await NamesAsync(PrimaryName, ColumnSignature());
        var centralColumns = await NamesAsync(CentralName, ColumnSignature());
        Assert.Equal(primaryColumns, centralColumns);

        var primaryKeys = await NamesAsync(PrimaryName, KeySignature("is_primary_key = 1"));
        var centralKeys = await NamesAsync(CentralName, KeySignature("is_primary_key = 1"));
        Assert.Equal(primaryKeys, centralKeys);

        var primaryIndexes = await NamesAsync(PrimaryName, KeySignature("is_primary_key = 0 AND i.type > 0"));
        var centralIndexes = await NamesAsync(CentralName, KeySignature("is_primary_key = 0 AND i.type > 0"));
        var indexGap = primaryIndexes.Except(centralIndexes).Concat(centralIndexes.Except(primaryIndexes)).ToArray();
        Assert.Empty(indexGap);

        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Sales;"));
        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Customers;"));
        Assert.Equal(1, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.Cashboxes;"));
        Assert.Equal(0, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.Sales;"));
        Assert.False(BranchRemoteSync.IsImplemented);
        Assert.Equal(shop1Before, await ScalarAsync(master, "SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0;"));
        Assert.Equal(protectedCentral, await ScalarAsync(master, "SELECT DB_ID(N'XtraPOS_Central');"));
        Assert.Equal(protectedBranch, await ScalarAsync(master, "SELECT DB_ID(N'XtraPOS_Branch_01');"));
    }

    [Fact]
    public async Task Branch_price_wins_and_duplicates_and_ledger_updates_are_rejected()
    {
        await using var connection = new SqlConnection(Database(PrimaryName));
        await connection.OpenAsync();

        await using (var priceTransaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            try
            {
                await using (var setup = new SqlCommand(
                    """
                    DECLARE @branch uniqueidentifier = (SELECT TOP (1) BranchID FROM dbo.Branches);
                    DECLARE @type uniqueidentifier = (SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0');
                    DECLARE @item uniqueidentifier = NEWID();
                    INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName) VALUES (@item, N'PX-TEMP', N'سعر مؤقت');
                    INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                    VALUES
                        (NEWID(), @item, @type, NULL, 10, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                        (NEWID(), @item, @type, @branch, 7, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                    SELECT TOP (1) Price
                    FROM dbo.ItemPrices
                    WHERE ItemID = @item AND PriceTypeID = @type AND IsActive = 1 AND (BranchID = @branch OR BranchID IS NULL)
                    ORDER BY CASE WHEN BranchID = @branch THEN 0 ELSE 1 END;
                    """,
                    connection,
                    priceTransaction))
                {
                    Assert.Equal(7m, Convert.ToDecimal(await setup.ExecuteScalarAsync()));
                }

                await using var duplicate = new SqlCommand(
                    """
                    DECLARE @branch uniqueidentifier = (SELECT TOP (1) BranchID FROM dbo.Branches);
                    DECLARE @type uniqueidentifier = (SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = N'Price0');
                    DECLARE @item uniqueidentifier = (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'PX-TEMP');
                    INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
                    VALUES (NEWID(), @item, @type, NULL, 11, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """,
                    connection,
                    priceTransaction);
                var error = await Assert.ThrowsAsync<SqlException>(() => duplicate.ExecuteNonQueryAsync());
                Assert.Contains(error.Number, new[] { 2601, 2627 });
            }
            finally
            {
                await priceTransaction.RollbackAsync();
            }
        }

        await using (var ledgerTransaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            try
            {
                await using (var setup = new SqlCommand(
                    """
                    DECLARE @business uniqueidentifier = (SELECT TOP (1) BusinessID FROM dbo.Businesses);
                    DECLARE @branch uniqueidentifier = (SELECT TOP (1) BranchID FROM dbo.Branches);
                    DECLARE @customer uniqueidentifier = NEWID();
                    INSERT INTO dbo.Customers (CustomerID, BusinessID, Name) VALUES (@customer, @business, N'تحقق دفتر');
                    INSERT INTO dbo.CustomerCreditLedger
                        (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, SourceBranchID, CreatedBy, IdempotencyKey)
                    VALUES (NEWID(), @customer, NEWID(), N'Adjustment', 1, @branch, NEWID(), N'temp-ledger');
                    """,
                    connection,
                    ledgerTransaction))
                {
                    await setup.ExecuteNonQueryAsync();
                }

                await using var update = new SqlCommand(
                    "UPDATE dbo.CustomerCreditLedger SET Amount = 2 WHERE IdempotencyKey = N'temp-ledger';",
                    connection,
                    ledgerTransaction);
                var ledgerError = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync());
                Assert.Equal(51011, ledgerError.Number);
            }
            finally
            {
                await ledgerTransaction.RollbackAsync();
            }
        }

        Assert.Equal(0, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.ItemPrices;"));
        Assert.Equal(0, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger;"));
        Assert.Equal(0, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.Sales;"));
        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Sales;"));
    }

    private static string ColumnSignature() =>
        """
        SELECT t.name + N'|' + c.name + N'|' + ty.name + N'|' + CONVERT(nvarchar(10), c.max_length)
             + N'|' + CONVERT(nvarchar(10), c.precision) + N'|' + CONVERT(nvarchar(10), c.scale)
             + N'|' + CONVERT(nvarchar(1), c.is_nullable)
        FROM sys.tables t
        JOIN sys.columns c ON c.object_id = t.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0
        ORDER BY t.name, c.name;
        """;

    private static string KeySignature(string filter) =>
        $"""
        SELECT t.name + N'|' + CONVERT(nvarchar(1), i.is_unique) + N'|' + ISNULL(i.filter_definition, N'') + N'|'
             + ISNULL(STUFF((
                SELECT N',' + c.name
                FROM sys.index_columns ic
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                ORDER BY ic.key_ordinal
                FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N''), N'')
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        WHERE t.is_ms_shipped = 0 AND {filter}
        ORDER BY 1;
        """;

    private static string Master() =>
        "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15";

    private static string Database(string name) =>
        $"Server=localhost;Database={name};Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30";

    private static async Task<List<string>> NamesAsync(string database, string sql)
    {
        var names = new List<string>();
        await using var connection = new SqlConnection(Database(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<int> CountAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(Database(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
