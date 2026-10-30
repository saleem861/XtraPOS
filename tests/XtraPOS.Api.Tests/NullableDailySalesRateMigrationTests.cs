using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class NullableDailySalesRateMigrationTests
{
    private const string DatabaseName = "XtraPOS_Branch_Test_SalesRate";

    [Fact]
    public async Task Migration012_MakesDailySalesRateNullableAndPreservesStoredValues()
    {
        var name = TestDatabase.Require(DatabaseName);
        await DropAsync(name);
        try
        {
            await CreateAsync(name);
            var connectionString = ConnectionString(name);
            await ApplyThroughAsync(connectionString, "011_ItemEditPermissions");
            await ExecuteAsync(connectionString, """
                DECLARE @business uniqueidentifier = NEWID();
                DECLARE @branch uniqueidentifier = NEWID();
                DECLARE @kept uniqueidentifier = NEWID();
                DECLARE @zero uniqueidentifier = NEWID();
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
                VALUES (@business, N'Rate test', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES (@branch, @business, N'01', N'Rate', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited, IsPublishedOnline, CreatedAt, UpdatedAt)
                VALUES
                    (@kept, N'RATE-KEPT', N'Kept', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (@zero, N'RATE-ZERO', N'Zero', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, DailySalesRate, IsAvailable, IsActive)
                VALUES
                    (@branch, @kept, 4.5, 1, 1),
                    (@branch, @zero, 0, 1, 1);
                """);
            await ApplyOneAsync(connectionString, MigrationRunner.DailySalesRateMigrationId);
            await ApplyOneAsync(connectionString, MigrationRunner.DailySalesRateMigrationId);

            Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'DailySalesRate', N'AllowsNull');"));
            Assert.Equal(0, await ScalarAsync(connectionString, """
                SELECT COUNT(*)
                FROM sys.default_constraints AS defaultConstraint
                INNER JOIN sys.columns AS rateColumn
                    ON rateColumn.object_id = defaultConstraint.parent_object_id
                   AND rateColumn.column_id = defaultConstraint.parent_column_id
                WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.ItemBranchInventory')
                  AND rateColumn.name = N'DailySalesRate';
                """));
            Assert.Equal(4.5m, await DecimalAsync(connectionString, "SELECT DailySalesRate FROM dbo.ItemBranchInventory WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'RATE-KEPT');"));
            Assert.Equal(0m, await DecimalAsync(connectionString, "SELECT DailySalesRate FROM dbo.ItemBranchInventory WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'RATE-ZERO');"));
            await ExecuteAsync(connectionString, """
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited, IsPublishedOnline, CreatedAt, UpdatedAt)
                VALUES (NEWID(), N'RATE-NULL', N'Null', 1, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.ItemBranchInventory (BranchID, ItemID, IsAvailable, IsActive)
                SELECT TOP (1) branch.BranchID, item.ItemID, 1, 1
                FROM dbo.Branches AS branch
                CROSS JOIN dbo.Items AS item
                WHERE item.ItemNumber = N'RATE-NULL';
                """);
            Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.ItemBranchInventory WHERE DailySalesRate IS NULL;"));
            Assert.Equal(4.5m, await DecimalAsync(connectionString, "SELECT DailySalesRate FROM dbo.ItemBranchInventory WHERE ItemID IN (SELECT ItemID FROM dbo.Items WHERE ItemNumber = N'RATE-KEPT');"));
        }
        finally
        {
            await DropAsync(name);
        }
    }

    private static async Task ApplyThroughAsync(string connectionString, string lastId)
    {
        foreach (var id in new[]
        {
            "001_InitialSchema",
            "002_UsersRolesPermissions",
            "003_NormalizeSystemRoles",
            "004_ItemsInventoryFoundation",
            "005_CatalogModelRefinement",
            "006_InventoryOwnership",
            "007_RemoveSkuOnHandQuantity",
            "008_ItemsViewPermission",
            "009_DataImportHistory",
            "010_SyncFoundation",
            "011_ItemEditPermissions",
        })
        {
            await ApplyOneAsync(connectionString, id);
            if (id == lastId)
            {
                return;
            }
        }
    }

    private static async Task ApplyOneAsync(string connectionString, string id)
    {
        var sql = ReadSql(id);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadSql(string id)
    {
        var resource = "XtraPOS.Api.Data.Migrations." + id + ".sql";
        using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing migration " + id);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task CreateAsync(string name)
    {
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{name}] COLLATE {BranchDatabase.Collation};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        TestDatabase.Require(name);
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """,
            connection)
        {
            CommandTimeout = 60,
        };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal> DecimalAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static string ConnectionString(string name) => new SqlConnectionStringBuilder
    {
        DataSource = "localhost",
        InitialCatalog = name,
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
    }.ConnectionString;

    private static string Master() =>
        "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";
}
