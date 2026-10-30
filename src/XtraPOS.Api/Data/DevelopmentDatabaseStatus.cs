using System.Data;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Data;

public static class DevelopmentDatabaseStatus
{
    public const string CommandName = "DevelopmentDatabaseStatus";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static async Task<int> WriteAsync(
        SqlConnectionFactory connections,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!connections.IsSafeConfiguration(out var problem))
        {
            await output.WriteLineAsync(problem);
            return 1;
        }

        await output.WriteLineAsync($"Database={connections.ConfiguredDatabase}");
        await output.WriteLineAsync($"Server={connections.ConfiguredServer}");
        await output.WriteLineAsync(connections.UsesIntegratedSecurity
            ? "Authentication=Integrated"
            : "Authentication=SqlLogin");

        await using var master = new SqlConnection(connections.CreateStatusConnectionString("master"));
        await master.OpenAsync(cancellationToken);
        await using (var identity = new SqlCommand(
            """
            SELECT
                @@SERVERNAME,
                CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)),
                CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128));
            """,
            master))
        await using (var identityReader = await identity.ExecuteReaderAsync(cancellationToken))
        {
            await identityReader.ReadAsync(cancellationToken);
            await output.WriteLineAsync($"ServerName={ReadString(identityReader, 0)}");
            await output.WriteLineAsync($"MachineName={ReadString(identityReader, 1)}");
            await output.WriteLineAsync($"InstanceName={ReadString(identityReader, 2)}");
        }

        await using (var databases = new SqlCommand(
            """
            SELECT name, create_date, state_desc
            FROM sys.databases
            WHERE name LIKE N'XtraPOS%'
            ORDER BY name;
            """,
            master))
        await using (var databaseReader = await databases.ExecuteReaderAsync(cancellationToken))
        {
            var names = new List<string>();
            while (await databaseReader.ReadAsync(cancellationToken))
            {
                names.Add($"{databaseReader.GetString(0)}|{databaseReader.GetDateTime(1):yyyy-MM-dd}|{databaseReader.GetString(2)}");
            }

            await output.WriteLineAsync("XtraPOSDatabases=" + (names.Count == 0 ? "(none)" : string.Join(", ", names)));
        }

        await using var located = new SqlCommand(
            """
            SELECT create_date, state_desc, collation_name
            FROM sys.databases
            WHERE name = @name;
            """,
            master);
        located.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = connections.ConfiguredDatabase;
        await using var locatedReader = await located.ExecuteReaderAsync(cancellationToken);
        if (!await locatedReader.ReadAsync(cancellationToken))
        {
            await output.WriteLineAsync("DatabaseExists=false");
            return 0;
        }

        await output.WriteLineAsync($"Created={locatedReader.GetDateTime(0):yyyy-MM-dd HH:mm:ss}");
        await output.WriteLineAsync($"State={locatedReader.GetString(1)}");
        await output.WriteLineAsync($"Collation={ReadString(locatedReader, 2)}");
        await locatedReader.DisposeAsync();

        await using var branch = new SqlConnection(connections.CreateStatusConnectionString(connections.ConfiguredDatabase));
        await branch.OpenAsync(cancellationToken);
        await using (var current = new SqlCommand("SELECT DB_NAME();", branch))
        {
            await output.WriteLineAsync($"ConnectedDatabase={await current.ExecuteScalarAsync(cancellationToken)}");
        }

        var tables = new List<string>();
        await using (var tableCommand = new SqlCommand("SELECT name FROM sys.tables ORDER BY name;", branch))
        await using (var tableReader = await tableCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await tableReader.ReadAsync(cancellationToken))
            {
                tables.Add(tableReader.GetString(0));
            }
        }

        await output.WriteLineAsync("Tables=" + (tables.Count == 0 ? "(none)" : string.Join(", ", tables)));

        var migrations = new List<string>();
        if (tables.Contains("DatabaseSchemaHistory"))
        {
            await using var migrationCommand = new SqlCommand(
                "SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY AppliedAt, MigrationID;",
                branch);
            await using var migrationReader = await migrationCommand.ExecuteReaderAsync(cancellationToken);
            while (await migrationReader.ReadAsync(cancellationToken))
            {
                migrations.Add(migrationReader.GetString(0));
            }
        }

        await output.WriteLineAsync("AppliedMigrations=" + (migrations.Count == 0 ? "(none)" : string.Join(", ", migrations)));

        var itemsExist = tables.Contains("Items");
        await output.WriteLineAsync($"ItemsTableExists={itemsExist}");
        await output.WriteLineAsync($"ItemsCount={await CountAsync(branch, itemsExist, "dbo.Items", cancellationToken)}");
        await output.WriteLineAsync($"ActiveItemsCount={await CountAsync(branch, itemsExist, "dbo.Items WHERE IsActive = 1", cancellationToken)}");
        await output.WriteLineAsync($"BusinessesCount={await CountAsync(branch, tables.Contains("Businesses"), "dbo.Businesses", cancellationToken)}");
        await output.WriteLineAsync($"BranchesCount={await CountAsync(branch, tables.Contains("Branches"), "dbo.Branches", cancellationToken)}");
        await output.WriteLineAsync($"UsersCount={await CountAsync(branch, tables.Contains("Users"), "dbo.Users", cancellationToken)}");
        return 0;
    }

    private static async Task<string> CountAsync(
        SqlConnection connection,
        bool exists,
        string source,
        CancellationToken cancellationToken)
    {
        if (!exists)
        {
            return "n/a";
        }

        await using var command = new SqlCommand($"SELECT COUNT(*) FROM {source};", connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "0";
    }

    private static string ReadString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
}
