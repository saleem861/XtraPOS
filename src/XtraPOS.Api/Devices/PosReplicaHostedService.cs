namespace XtraPOS.Api.Devices;

public sealed class PosReplicaHostedService(IServiceProvider services, ILogger<PosReplicaHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CentralPosReplicaSync>().PullAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "POS replica sync failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }
}
