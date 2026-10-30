using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Credit;

public sealed class CreditOutboxWorker(
    IServiceScopeFactory scopes,
    ILogger<CreditOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                delay = await ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Credit outbox recovery paused.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<TimeSpan> ProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var branchFactory = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>();
        var branchConnection = branchFactory.TryCreateBranchConnectionString();
        if (branchConnection is null)
        {
            return TimeSpan.FromMinutes(1);
        }

        var centralSettings = await scope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
            .ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (centralSettings is null)
        {
            return TimeSpan.FromMinutes(1);
        }

        await using var branch = new SqlConnection(branchConnection);
        await using var central = new SqlConnection(centralSettings.BuildConnectionString(centralSettings.DatabaseName));
        await branch.OpenAsync(cancellationToken);
        await central.OpenAsync(cancellationToken);
        var processor = scope.ServiceProvider.GetRequiredService<CreditOutboxProcessor>();
        var outcome = await processor.ProcessOnceAsync(central, branch, Environment.MachineName, cancellationToken);
        var collection = scope.ServiceProvider.GetRequiredService<CreditCollectionService>();
        var collected = await collection.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken);
        var reversal = scope.ServiceProvider.GetRequiredService<CreditReversalService>();
        var reversed = await reversal.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken);
        if (outcome is null && collected is null && reversed is null)
        {
            return TimeSpan.FromSeconds(30);
        }

        logger.LogInformation("Credit outbox recovery finished with {Outcome}.", outcome);
        return TimeSpan.FromSeconds(2);
    }
}
