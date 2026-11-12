using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
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
        await BranchOperationLog.PurgeAsync(branch, cancellationToken);
        var processor = scope.ServiceProvider.GetRequiredService<CreditOutboxProcessor>();
        var collection = scope.ServiceProvider.GetRequiredService<CreditCollectionService>();
        var reversal = scope.ServiceProvider.GetRequiredService<CreditReversalService>();
        var head = await BranchOperationLog.HeadTypeAsync(branch, cancellationToken);
        object? outcome = head switch
        {
            "CreditSale" => await processor.ProcessOnceAsync(central, branch, Environment.MachineName, cancellationToken),
            "DebtCollection" => await collection.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken),
            "CollectionReversal" => await reversal.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken),
            "CustomerUpsert" => await scope.ServiceProvider.GetRequiredService<BranchCardOutboxSync>().RelayCustomerAsync(central, branch, cancellationToken),
            null => await processor.ProcessOnceAsync(central, branch, Environment.MachineName, cancellationToken)
                ?? (object?)await collection.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken)
                ?? await reversal.RecoverOnceAsync(central, branch, Environment.MachineName, cancellationToken),
            _ => null,
        };
        if (outcome is null)
        {
            return TimeSpan.FromSeconds(30);
        }

        logger.LogInformation("Credit outbox recovery finished with {Outcome}.", outcome);
        return TimeSpan.FromSeconds(2);
    }
}
