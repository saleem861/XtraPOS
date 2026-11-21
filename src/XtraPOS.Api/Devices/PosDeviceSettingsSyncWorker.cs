using XtraPOS.Api.Data;

namespace XtraPOS.Api.Devices;

/// <summary>
/// كل دقيقة: يرفع تعديلات الفرع المعلّقة إلى المركز، ثم يسحب تعديلات المركز على نقاط هذا الفرع.
/// مستقل عن نسخة الزبائن، فلا يتوقف إذا لم تكن نسخة الزبائن المحلية جاهزة.
/// </summary>
public sealed class PosDeviceSettingsSyncWorker(IServiceProvider services, ILogger<PosDeviceSettingsSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
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
                        var result = await scope.ServiceProvider
                            .GetRequiredService<PosDeviceSettingsService>()
                            .SyncAsync(stoppingToken);
                        if (result.Pushed > 0 || result.Pulled > 0)
                        {
                            logger.LogInformation(
                                "POS device settings sync: pushed {Pushed}, pulled {Pulled}.",
                                result.Pushed,
                                result.Pulled);
                        }

                        if (result.Error is not null)
                        {
                            logger.LogWarning("POS device settings sync left local values unchanged: {Error}", result.Error);
                        }
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
                logger.LogWarning(exception, "POS device settings sync did not run.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
