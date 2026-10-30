using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sync;

public sealed class CustomerReplicaSyncWorker(IServiceProvider services, ILogger<CustomerReplicaSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var directory = scope.ServiceProvider.GetRequiredService<DeviceDatabaseDirectory>();
                var databases = new List<string?> { null };
                databases.AddRange(directory.Databases());
                foreach (var database in databases.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    BranchDatabaseOverride.Set(database);
                    try
                    {
                        await SyncOneAsync(scope.ServiceProvider, stoppingToken);
                    }
                    finally
                    {
                        BranchDatabaseOverride.Set(null);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Customer replica sync did not run.");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    private async Task SyncOneAsync(IServiceProvider provider, CancellationToken stoppingToken)
    {
        var endpoints = provider.GetRequiredService<SyncEndpointStore>();
        var sync = provider.GetRequiredService<SharedDataSync>();
        var view = await endpoints.GetAsync(DatabaseRole.Central, stoppingToken);
        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, stoppingToken);
        var state = await sync.ReadReplicaStateAsync(stoppingToken);
        if (view is null
            || settings is null
            || !string.Equals(view.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return;
        }

        if (state is SharedDataSync.ReplicaNotInitialized or SharedDataSync.ReplicaBootstrapping)
        {
            state = await sync.EnsureCustomerReplicaAsync(view.EndpointId, settings, stoppingToken);
            logger.LogInformation("Central customer replica resumed at state {State}.", state);
        }

        if (state is not (SharedDataSync.ReplicaReady or SharedDataSync.ReplicaStale))
        {
            return;
        }

        var result = await sync.PullAsync(view.EndpointId, settings, stoppingToken);
        if (!result.CentralReachable)
        {
            if (state == SharedDataSync.ReplicaReady)
            {
                await sync.SetReplicaStateAsync(SharedDataSync.ReplicaStale, stoppingToken);
            }

            logger.LogWarning("Central master sync left the local copy unchanged: {Error}", result.Error);
            return;
        }

        if (state == SharedDataSync.ReplicaStale)
        {
            await sync.SetReplicaStateAsync(SharedDataSync.ReplicaReady, stoppingToken);
        }

        logger.LogInformation("Central master sync applied {Applied} customer and item changes.", result.Applied);
    }
}
