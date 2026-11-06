using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Credit;

public sealed class FinancialInboxWorker(IServiceScopeFactory scopes, ILogger<FinancialInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Financial inbox sync paused.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task PullOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var branchFactory = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
        var branchConnection = branchFactory.TryCreateBranchConnectionString();
        var centralSettings = await scope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
            .ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (branchConnection is null || centralSettings is null)
        {
            return;
        }

        await using var branch = new SqlConnection(branchConnection);
        await using var central = new SqlConnection(centralSettings.BuildConnectionString(centralSettings.DatabaseName));
        await branch.OpenAsync(cancellationToken);
        await central.OpenAsync(cancellationToken);
        if (!await ExistsAsync(branch, "BranchFinancialInbox", cancellationToken) || !await ExistsAsync(central, "CentralFinancialEvents", cancellationToken))
        {
            return;
        }

        await FinancialInboxSync.PullAsync(central, branch, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.AddWithValue("@name", "dbo." + table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
