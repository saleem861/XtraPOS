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

public sealed class LocalProvisionVerificationTests
{
    private const string PrimaryName = "XtraPOS_Branch_94";
    private const string SecondaryName = "XtraPOS_Branch_95";
    private const string CentralName = "XtraPOS_Central_Test_LocalOps";

    [Fact]
    public async Task Localhost_provisions_primary_secondary_and_compares_schema()
    {
        await using var master = new SqlConnection(MasterConnection());
        await master.OpenAsync();
        var shop1Before = await ScalarAsync(master, "SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0;");

        var runner = new MigrationRunner(
            new SqlConnectionFactory(Options.Create(new SqlServerOptions
            {
                Server = "localhost",
                Database = PrimaryName,
                ConnectTimeoutSeconds = 30,
            })),
            NullLogger<MigrationRunner>.Instance);
        var provisioner = new SelectedDatabaseProvisioner(runner);
        await provisioner.ProvisionSchemaAsync(Branch(PrimaryName), CancellationToken.None);
        await provisioner.ProvisionSchemaAsync(Branch(PrimaryName), CancellationToken.None);
        await provisioner.ProvisionSchemaAsync(Branch(SecondaryName), CancellationToken.None);

        if (await ScalarAsync(master, "SELECT DB_ID(@name);", CentralName) is null or DBNull)
        {
            await using var create = new SqlCommand(
                $"CREATE DATABASE [{CentralName}] COLLATE Arabic_CI_AS;",
                master);
            create.CommandTimeout = 60;
            await create.ExecuteNonQueryAsync();
        }

        await new CentralMigrationRunner().ApplyAsync(DatabaseConnection(CentralName), CancellationToken.None);
        await new CentralMigrationRunner().ApplyAsync(DatabaseConnection(CentralName), CancellationToken.None);

        var primaryTables = await TableNamesAsync(PrimaryName);
        var secondaryTables = await TableNamesAsync(SecondaryName);
        var centralTables = await TableNamesAsync(CentralName);
        Assert.Equal(primaryTables, secondaryTables);
        Assert.Contains("Cashboxes", primaryTables);
        Assert.Contains("SchemaUpdateHistory", primaryTables);
        Assert.Contains("Sales", primaryTables);
        Assert.Contains("CentralPosRegistry", centralTables);
        Assert.Contains("Cashboxes", centralTables);
        Assert.Contains("SchemaUpdateHistory", centralTables);

        foreach (var blocked in new[] { "CustomerCreditLedger", "CustomerReceivables", "Customers", "Items" })
        {
            if (primaryTables.Contains(blocked) && centralTables.Contains(blocked))
            {
                Assert.True(SchemaConvergence.IsBlocked(blocked));
            }
        }

        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Sales;"));
        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Customers;"));
        Assert.Equal(0, await CountAsync(SecondaryName, "SELECT COUNT(*) FROM dbo.Cashboxes;"));
        Assert.False(BranchRemoteSync.IsImplemented);

        await ExecuteAsync(
            PrimaryName,
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Businesses)
                INSERT INTO dbo.Businesses (BusinessName, IsActive)
                VALUES (N'تحقق الإعداد', 1);
            DECLARE @business uniqueidentifier = (SELECT TOP (1) BusinessID FROM dbo.Businesses);
            IF NOT EXISTS (SELECT 1 FROM dbo.Branches)
                INSERT INTO dbo.Branches (BusinessID, BranchName, BranchType, IsActive)
                VALUES (@business, N'فرع التحقق', N'Retail', 1);
            DECLARE @branch uniqueidentifier = (SELECT TOP (1) BranchID FROM dbo.Branches);
            IF NOT EXISTS (SELECT 1 FROM dbo.Cashboxes)
                INSERT INTO dbo.Cashboxes (CashboxID, BusinessID, BranchID, CashboxNumber, DisplayName, Status)
                VALUES (NEWID(), @business, @branch, 1, N'صندوق 1', N'Active');
            """);

        var duplicate = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            PrimaryName,
            """
            DECLARE @business uniqueidentifier = (SELECT TOP (1) BusinessID FROM dbo.Businesses);
            DECLARE @branch uniqueidentifier = (SELECT TOP (1) BranchID FROM dbo.Branches);
            INSERT INTO dbo.Cashboxes (CashboxID, BusinessID, BranchID, CashboxNumber, DisplayName, Status)
            VALUES (NEWID(), @business, @branch, 1, N'صندوق مكرر', N'Active');
            """));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        Assert.Equal(1, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.Cashboxes;"));
        Assert.Equal(0, await CountAsync(PrimaryName, "SELECT COUNT(*) FROM dbo.Sales;"));

        var shop1After = await ScalarAsync(master, "SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0;");
        Assert.Equal(shop1Before, shop1After);
    }

    private static SqlEndpointSettings Branch(string name) =>
        new(
            DatabaseRole.Branch,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            name,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);

    private static string MasterConnection() =>
        "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15";

    private static string DatabaseConnection(string name) =>
        $"Server=localhost;Database={name};Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30";

    private static async Task<List<string>> TableNamesAsync(string database)
    {
        var names = new List<string>();
        await using var connection = new SqlConnection(DatabaseConnection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT name FROM sys.tables WHERE is_ms_shipped = 0 ORDER BY name;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<int> CountAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(DatabaseConnection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqlConnection(DatabaseConnection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, string? name = null)
    {
        await using var command = new SqlCommand(sql, connection);
        if (name is not null)
        {
            command.Parameters.AddWithValue("@name", name);
        }

        return await command.ExecuteScalarAsync();
    }
}
