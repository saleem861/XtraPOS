using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Tests;

public static class TestDatabase
{
    public const string Name = "XtraPOS_Branch_Test_Integration";

    public static string Require(string? databaseName)
    {
        var name = databaseName?.Trim() ?? "";
        if (string.Equals(name, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to drop, reset, or recreate XtraPOS_Branch from automated tests.");
        }

        if (!name.StartsWith(BranchDatabase.TestNamePrefix + "_", StringComparison.Ordinal)
            || !BranchDatabase.IsTestName(name))
        {
            throw new InvalidOperationException(
                $"Refusing database '{databaseName}'. Automated tests may only use a database whose name starts with {BranchDatabase.TestNamePrefix}.");
        }

        return name;
    }

    public static string ConnectionString()
    {
        var name = Require(Name);
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = name,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "XtraPOS.Tests",
        }.ConnectionString;
    }

    public static async Task DropAsync()
    {
        var name = Require(Name);
        if (string.Equals(name, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(BranchDatabase.TestNamePrefix + "_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop XtraPOS_Branch.");
        }

        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(
            "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
        SqlConnection.ClearAllPools();
    }
}

public sealed class XtraPosWebFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName;

    public XtraPosWebFactory(string? databaseName = null)
    {
        _databaseName = databaseName ?? TestDatabase.Name;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var databaseName = TestDatabase.Require(_databaseName);
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "XtraPOS.Api")));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlServer:Database"] = databaseName,
            });
        });
    }
}
