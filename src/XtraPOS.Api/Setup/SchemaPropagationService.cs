using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record SchemaPropagationResult(string DatabaseName, string Status, string? Detail);

public sealed class SchemaPropagationService(MigrationRunner migrations)
{
    public async Task<SchemaPropagationResult> PropagateBranchAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        if (!OperationalDatabaseGuard.IsProductionCatalog(databaseName))
        {
            return new SchemaPropagationResult(databaseName, "Skipped", "قاعدة محمية أو غير إنتاجية. لم يُنفذ أي تغيير.");
        }

        try
        {
            await migrations.ApplyPendingToAsync(connectionString, cancellationToken);
            await RecordAsync(connectionString, databaseName, "Applied", SchemaConvergence.ApprovedVersion, cancellationToken);
            return new SchemaPropagationResult(databaseName, "Applied", SchemaConvergence.ApprovedVersion);
        }
        catch (Exception exception)
        {
            var detail = exception.Message.Length > 380 ? exception.Message[..380] : exception.Message;
            try
            {
                await RecordAsync(connectionString, databaseName, "Failed", detail, cancellationToken);
            }
            catch (Exception)
            {
                // The target is offline or the history table is not available yet. The next pass retries.
            }

            return new SchemaPropagationResult(databaseName, "Failed", detail);
        }
    }

    public static async Task RecordAsync(
        string connectionString,
        string databaseName,
        string status,
        string? detail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.SchemaUpdateHistory', N'U');", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.SchemaUpdateHistory (UpdateID, DatabaseName, SchemaVersion, Status, Detail)
            VALUES (@id, @database, @version, @status, @detail);
            """,
            connection);
        insert.Parameters.AddWithValue("@id", Guid.NewGuid());
        insert.Parameters.AddWithValue("@database", databaseName);
        insert.Parameters.AddWithValue("@version", SchemaConvergence.ApprovedVersion);
        insert.Parameters.AddWithValue("@status", status);
        insert.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SchemaPropagationWorker(
    OperationalBranchConnection operational,
    SchemaPropagationService propagation,
    IServiceScopeFactory scopes,
    ILogger<SchemaPropagationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                var current = operational.Current;
                if (current is null || !OperationalDatabaseGuard.IsProductionCatalog(current.DatabaseName))
                {
                    continue;
                }

                var result = await propagation.PropagateBranchAsync(
                    current.BuildConnectionString(current.DatabaseName),
                    current.DatabaseName,
                    stoppingToken);
                if (result.Status == "Failed")
                {
                    logger.LogWarning("Schema propagation for {Database} will retry. {Detail}", result.DatabaseName, result.Detail);
                }

                await using var scope = scopes.CreateAsyncScope();
                var remote = await scope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
                    .ReadSettingsAsync(XtraPOS.Api.Data.DatabaseRole.BranchRemote, stoppingToken);
                if (remote is not null)
                {
                    var remoteResult = await propagation.PropagateBranchAsync(
                        remote.BuildConnectionString(remote.DatabaseName),
                        remote.DatabaseName,
                        stoppingToken);
                    if (remoteResult.Status == "Failed")
                    {
                        logger.LogWarning(
                            "Secondary schema propagation for {Database} will retry after reconnection. {Detail}",
                            remoteResult.DatabaseName,
                            remoteResult.Detail);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Schema propagation pass failed and will retry.");
            }
        }
    }
}
