using XtraPOS.Api.Data;
using XtraPOS.Api.Devices;

namespace XtraPOS.Api.Sync;

public sealed class CustomerReplicaSyncWorker(IServiceProvider services, SyncStatusStore status, ILogger<CustomerReplicaSyncWorker> logger) : BackgroundService
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
            if (BranchDatabaseOverride.Database is null)
            {
                status.Report("Unlinked", "customer-replica", "غير مرتبط بالمركز", null);
            }
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

        var reportsStatus = BranchDatabaseOverride.Database is null;
        if (reportsStatus)
        {
            status.Report("InProgress", "customer-replica", "جاري المزامنة", null);
        }
        var result = await sync.PullAsync(view.EndpointId, settings, stoppingToken);
        if (!result.CentralReachable)
        {
            if (state == SharedDataSync.ReplicaReady)
            {
                await sync.SetReplicaStateAsync(SharedDataSync.ReplicaStale, stoppingToken);
            }

            if (reportsStatus)
            {
                status.Report("Error", "customer-replica", result.Error ?? "تعذر تحديث البيانات المركزية.", null);
            }
            logger.LogWarning("Central master sync left the local copy unchanged: {Error}", result.Error);
            return;
        }

        if (reportsStatus)
        {
            status.Report("Success", "customer-replica", null, null);
        }

        if (state == SharedDataSync.ReplicaStale)
        {
            await sync.SetReplicaStateAsync(SharedDataSync.ReplicaReady, stoppingToken);
        }

        logger.LogInformation("Central master sync applied {Applied} customer and item changes.", result.Applied);
    }
}
