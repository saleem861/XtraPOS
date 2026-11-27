namespace XtraPOS.Api.Inventory;

/// <summary>يوقظ عامل ترحيل الجرد فور ضغط «تثبيت»، دون انتظار دورة الاستطلاع.</summary>
public sealed class InventoryFinalizeSignal
{
    private readonly SemaphoreSlim gate = new(0, 1);

    public void Pulse()
    {
        if (gate.CurrentCount == 0) gate.Release();
    }

    public async Task WaitAsync(TimeSpan idle, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(idle, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // انتهت مهلة الانتظار.
        }
    }
}

/// <summary>
/// يرحّل جلسات الجرد التي حالتها Finalizing سطراً سطراً على المركز.
/// فشل سطر يُعاد في الدورة التالية، وPostedAt يمنع التكرار.
/// </summary>
public sealed class InventoryFinalizeWorker(
    IServiceScopeFactory scopes,
    InventoryFinalizeSignal signal,
    ILogger<InventoryFinalizeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                var counts = scope.ServiceProvider.GetRequiredService<InventoryCountService>();
                worked = await counts.PostFinalizingWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Inventory finalize did not finish a line; it will retry.");
            }

            if (!worked)
            {
                await signal.WaitAsync(Idle, stoppingToken);
            }
        }
    }
}
