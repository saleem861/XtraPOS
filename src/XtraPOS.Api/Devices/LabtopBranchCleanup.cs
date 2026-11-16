using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Devices;

public sealed class LabtopBranchCleanup(CentralConnectionFileStore centrals, SqlConnectionFactory branches, IHostEnvironment environment)
{
    public const string BranchName = "Labtop";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var marker = Path.Combine(environment.ContentRootPath, "App_Data", "labtop-only-branches.done");
        if (File.Exists(marker))
        {
            return;
        }

        var kept = false;
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is not null)
        {
            await using var central = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await central.OpenAsync(cancellationToken);
            kept |= await KeepAsync(central, cancellationToken);
        }

        if (await branches.DatabaseExistsAsync(cancellationToken))
        {
            await using var local = new SqlConnection(branches.CreateBranchConnectionString());
            await local.OpenAsync(cancellationToken);
            kept |= await KeepAsync(local, cancellationToken);
        }

        if (!kept)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task KeepAccessAsync(CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is not null)
        {
            await using var central = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
            await central.OpenAsync(cancellationToken);
            await GrantLabtopAccessAsync(central, cancellationToken);
        }

        if (await branches.DatabaseExistsAsync(cancellationToken))
        {
            await using var local = new SqlConnection(branches.CreateBranchConnectionString());
            await local.OpenAsync(cancellationToken);
            await GrantLabtopAccessAsync(local, cancellationToken);
        }
    }

    public static async Task<bool> KeepAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var find = new SqlCommand("SELECT TOP (1) BranchID FROM dbo.Branches WHERE BranchName = @name;", connection);
        find.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = BranchName;
        if (await find.ExecuteScalarAsync(cancellationToken) is not Guid keep)
        {
            return false;
        }

        await BranchAccessGrant.ApplyAsync(connection, null, cancellationToken);
        await RunAsync(connection, keep, """
            IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL
                DELETE FROM dbo.CentralPosRegistry WHERE BranchID <> @keep;
            """, cancellationToken);
        await RunAsync(connection, keep, """
            IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NOT NULL
                DELETE replica FROM dbo.CentralPosReplica AS replica
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.Branches AS branch
                    WHERE branch.BranchID = replica.BranchID AND branch.BranchName = N'Labtop');
            """, cancellationToken);
        await RunAsync(connection, keep, """
            IF OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL
                DELETE station FROM dbo.POSStations AS station
                WHERE station.BranchID <> @keep
                  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.POSStationID = station.StationID);
            """, cancellationToken);
        await RunAsync(connection, keep, """
            IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Devices', N'BranchID') IS NOT NULL
                DELETE device FROM dbo.Devices AS device
                WHERE device.BranchID <> @keep
                  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.DeviceID = device.DeviceID);
            """, cancellationToken);

        foreach (var statement in await ChildDeletesAsync(connection, cancellationToken))
        {
            await RunAsync(connection, keep, statement, cancellationToken);
        }

        await RunAsync(connection, keep, """
            DELETE branch FROM dbo.Branches AS branch
            WHERE branch.BranchID <> @keep
              AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.BranchID = branch.BranchID);
            """, cancellationToken);
        return true;
    }

    private static async Task GrantLabtopAccessAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await BranchAccessGrant.ApplyAsync(connection, null, cancellationToken);
    }

    private static async Task<List<string>> ChildDeletesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var statements = new List<string>();
        await using var command = new SqlCommand(
            """
            SELECT SCHEMA_NAME(child.schema_id), child.name, columnName.name
            FROM sys.foreign_keys AS link
            INNER JOIN sys.foreign_key_columns AS pair ON pair.constraint_object_id = link.object_id
            INNER JOIN sys.tables AS child ON child.object_id = link.parent_object_id
            INNER JOIN sys.columns AS columnName ON columnName.object_id = child.object_id AND columnName.column_id = pair.parent_column_id
            INNER JOIN sys.tables AS parent ON parent.object_id = link.referenced_object_id
            INNER JOIN sys.columns AS parentColumn ON parentColumn.object_id = parent.object_id AND parentColumn.column_id = pair.referenced_column_id
            WHERE parent.name = N'Branches' AND parentColumn.name = N'BranchID'
              AND child.name NOT IN (N'Sales', N'SaleItems', N'SalePayments', N'POSStations', N'Devices', N'Branches');
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var column = reader.GetString(2);
            statements.Add(
                "DELETE child FROM " + Quote(schema) + "." + Quote(table) + " AS child WHERE child." + Quote(column) + " <> @keep AND child." + Quote(column) + " IS NOT NULL;");
        }

        return statements;
    }

    private static async Task RunAsync(SqlConnection connection, Guid keep, string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@keep", SqlDbType.UniqueIdentifier).Value = keep;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // A commercial foreign key keeps that one table. The Labtop rows stay.
        }
    }

    private static string Quote(string name)
    {
        var builder = new StringBuilder(name.Length + 2);
        builder.Append('[');
        foreach (var character in name)
        {
            builder.Append(character == ']' ? "]]" : character.ToString());
        }

        builder.Append(']');
        return builder.ToString();
    }
}

public sealed class LabtopBranchCleanupService(IServiceProvider services, ILogger<LabtopBranchCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
            using var scope = services.CreateScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<LabtopBranchCleanup>();
            try
            {
                await cleanup.RunOnceAsync(stoppingToken);
                await cleanup.KeepAccessAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Keeping only the Labtop branch did not finish.");
            }

            var connections = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
            var centralStore = scope.ServiceProvider.GetRequiredService<CentralConnectionFileStore>();
            var centralSettings = await centralStore.ReadAsync(stoppingToken);
            if (centralSettings is not null)
            {
                await using var central = new SqlConnection(centralSettings.BuildConnectionString(centralSettings.DatabaseName));
                await central.OpenAsync(stoppingToken);
                await AccountAccessRepair.ApplyAsync(central, null, stoppingToken);
            }

            if (await connections.DatabaseExistsAsync(stoppingToken))
            {
                await using var local = new SqlConnection(connections.CreateBranchConnectionString());
                await local.OpenAsync(stoppingToken);
                await AccountAccessRepair.ApplyAsync(local, null, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Account access repair will retry.");
        }
    }
}
