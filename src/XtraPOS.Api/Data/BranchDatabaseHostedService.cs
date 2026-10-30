using XtraPOS.Api.Data.Migrations;

namespace XtraPOS.Api.Data;

public sealed class BranchDatabaseHostedService(
    SqlConnectionFactory connections,
    MigrationRunner migrations,
    IConfiguration configuration,
    ILogger<BranchDatabaseHostedService> logger) : IHostedService
{
    private readonly CancellationTokenSource _shutdown = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => MigrateIfPresentAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        return Task.CompletedTask;
    }

    private async Task MigrateIfPresentAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!connections.IsSafeConfiguration(out _))
            {
                return;
            }

            if (!await connections.DatabaseExistsAsync(cancellationToken))
            {
                return;
            }

            if (configuration is IConfigurationRoot root)
            {
                string? providerName = null;
                foreach (var provider in root.Providers)
                {
                    if (provider.TryGet("SqlServer:Database", out _))
                    {
                        providerName = provider.ToString();
                    }
                }

                logger.LogInformation(
                    "SqlServer:Database {Database} on server {Server} is supplied by configuration provider {Provider}.",
                    connections.ConfiguredDatabase,
                    connections.ConfiguredServer,
                    providerName ?? "(none)");
            }

            var pending = await migrations.ListPendingAsync(cancellationToken);
            if (pending.Count == 0)
            {
                logger.LogInformation(
                    "Branch schema on server {Server} database {Database} has no pending migrations. None were applied.",
                    connections.ConfiguredServer,
                    connections.ConfiguredDatabase);
                return;
            }

            logger.LogWarning(
                "Pending branch migrations on server {Server} database {Database} were not applied: {Pending}. Apply them with --apply-branch-migrations --confirm-database {Database}.",
                connections.ConfiguredServer,
                connections.ConfiguredDatabase,
                string.Join(", ", pending),
                connections.ConfiguredDatabase);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not apply branch migrations on startup.");
        }
    }
}
