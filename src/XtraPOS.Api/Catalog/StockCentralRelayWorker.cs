using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Catalog;

/// <summary>
/// يرسل حركات الكمية إلى المركز فور كتابتها (إشارة من StockPostingService)، وكل بضع ثوانٍ احتياطاً،
/// لكل قاعدة فرع على هذا الجهاز. البيع لا ينتظره: بلا مركز تبقى الحركات في السجل حتى يعود الاتصال.
/// </summary>
public sealed class StockCentralRelayWorker(IServiceProvider services, ILogger<StockCentralRelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _signal = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StockCentralRelay.MovementWritten += Signal;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = Idle;
                try
                {
                    if (!await RunOnceAsync(stoppingToken)) delay = AfterFailure;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Stock movements were not sent to central; they stay in the log and retry.");
                    delay = AfterFailure;
                }

                try
                {
                    await _signal.WaitAsync(delay, stoppingToken);
                    // الحركة تُكتب داخل معاملة لم تُحفظ بعد: مهلة قصيرة لتُحفظ قبل القراءة.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            StockCentralRelay.MovementWritten -= Signal;
        }
    }

    private void Signal()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <returns>false عند تعذّر الإرسال لأي قاعدة، فتطول المهلة قبل المحاولة التالية.</returns>
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<DeviceDatabaseDirectory>();
        var databases = new List<string?> { null };
        databases.AddRange(directory.Databases());
        var healthy = true;
        foreach (var database in databases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            BranchDatabaseOverride.Set(database);
            try
            {
                var branchConnection = scope.ServiceProvider.GetRequiredService<SqlConnectionFactory>().TryCreateBranchConnectionString();
                if (branchConnection is null) continue;
                await using var local = new SqlConnection(branchConnection);
                await local.OpenAsync(cancellationToken);
                if (!await HasPendingAsync(local, cancellationToken)) continue;

                var central = await scope.ServiceProvider.GetRequiredService<XtraPOS.Api.Inventory.InventoryCountService>().OpenCentralAsync(cancellationToken);
                if (central is null)
                {
                    healthy = false;
                    continue;
                }

                await using var lease = central;
                if (!await CentralReadyAsync(central, cancellationToken))
                {
                    healthy = false;
                    continue;
                }

                var sent = 0;
                StockCentralRelay.RelayBatch batch;
                do
                {
                    batch = await StockCentralRelay.RelayAsync(local, central, cancellationToken);
                    sent += batch.Sent;
                }
                while (batch.More && !cancellationToken.IsCancellationRequested);

                if (sent > 0)
                {
                    logger.LogInformation("Stock relay: sent {Sent} movement(s) to central.", sent);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                healthy = false;
                logger.LogWarning(exception, "Stock movements were not sent to central; they stay in the log and retry.");
            }
            finally
            {
                BranchDatabaseOverride.Set(null);
            }
        }

        return healthy;
    }

    private static async Task<bool> HasPendingAsync(SqlConnection local, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF COL_LENGTH(N'dbo.StockMovements', N'CentralRelayedAt') IS NULL
                SELECT 0;
            ELSE
                EXEC (N'SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.StockMovements WHERE CentralRelayedAt IS NULL) THEN 1 ELSE 0 END;');
            """,
            local);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> CentralReadyAsync(SqlConnection central, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(N'dbo.BranchStockMovements', N'U') IS NOT NULL
                         AND COL_LENGTH(N'dbo.BranchStockSnapshots', N'LastMovementAt') IS NOT NULL
                        THEN 1 ELSE 0 END;
            """,
            central);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
