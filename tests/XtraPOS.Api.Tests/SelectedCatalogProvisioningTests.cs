using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class SelectedCatalogProvisioningTests
{
    private const string DisposableName = "XtraPOS_Branch_96";

    [Fact]
    public async Task Disposable_catalog_is_created_on_the_selected_server_and_removed()
    {
        await using var master = new SqlConnection(
            "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15");
        await master.OpenAsync();
        var shop1Before = await ScalarAsync(master, "SELECT CASE WHEN DB_ID(N'shop1') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0) END;");
        var stagingBefore = await ScalarAsync(master, "SELECT DB_ID(@name);", "XtraPOS_Branch_Test_Staging");
        if (await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName) is not null and not DBNull)
        {
            throw new InvalidOperationException("The disposable catalog already exists.");
        }

        var settings = new SqlEndpointSettings(
            DatabaseRole.Branch,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            DisposableName,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);
        var runner = new MigrationRunner(
            new SqlConnectionFactory(Options.Create(new SqlServerOptions
            {
                Server = "unused-staging-host",
                Database = "XtraPOS_Branch_Test_Staging",
                User = "xtrapos_staging",
                Password = "not-used",
                ConnectTimeoutSeconds = 30,
            })),
            NullLogger<MigrationRunner>.Instance);
        var provisioner = new SelectedDatabaseProvisioner(runner);
        try
        {
            await provisioner.ProvisionSchemaAsync(settings, CancellationToken.None);
            await provisioner.ProvisionSchemaAsync(settings, CancellationToken.None);

            Assert.NotNull(await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName));
            Assert.Equal(stagingBefore, await ScalarAsync(master, "SELECT DB_ID(@name);", "XtraPOS_Branch_Test_Staging"));
            await using var branch = new SqlConnection(settings.BuildConnectionString(DisposableName));
            await branch.OpenAsync();
            await using var sales = new SqlCommand("SELECT COUNT(*) FROM dbo.Sales;", branch);
            Assert.Equal(0, (int)(await sales.ExecuteScalarAsync() ?? 0));
            await using var history = new SqlCommand("SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');", branch);
            Assert.NotNull(await history.ExecuteScalarAsync());
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var drop = new SqlCommand(
                """
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    ALTER DATABASE [XtraPOS_Branch_96] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [XtraPOS_Branch_96];
                END
                """,
                master);
            drop.Parameters.AddWithValue("@name", DisposableName);
            drop.CommandTimeout = 60;
            await drop.ExecuteNonQueryAsync();
        }

        Assert.True(await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName) is null or DBNull);
        Assert.Equal(shop1Before, await ScalarAsync(master, "SELECT CASE WHEN DB_ID(N'shop1') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0) END;"));
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
