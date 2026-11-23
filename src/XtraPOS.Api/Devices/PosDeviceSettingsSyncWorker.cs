using XtraPOS.Api.Data;

namespace XtraPOS.Api.Devices;

/// <summary>
/// كل دقيقة: يسحب من المركز القيم الأولية لكل النقاط في كل الفروع ويطبّقها على جدول هذا الفرع (dbo.PosDeviceSettings).
/// الاتجاه من الفرع إلى المركز لا يمر من هنا: يمر بسجل الترحيل الموجود (BranchOperationLog) و CreditOutboxWorker.
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
                        var pulled = await scope.ServiceProvider.GetRequiredService<PosDeviceSettingsService>().PullAsync(stoppingToken);
                        if (pulled > 0)
                        {
                            logger.LogInformation("POS initial values: applied {Pulled} central change(s).", pulled);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "POS initial values were not pulled from central.");
                    }

                    try
                    {
                        var classified = await scope.ServiceProvider.GetRequiredService<XtraPOS.Api.Inventory.InventoryCountService>().PullSettingsAsync(stoppingToken);
                        if (classified > 0)
                        {
                            logger.LogInformation("Inventory: copied {Copied} classification row(s) from central.", classified);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Inventory classification was not pulled from central.");
                    }

                    try
                    {
                        var copied = await scope.ServiceProvider.GetRequiredService<XtraPOS.Api.Catalog.QuickItemsService>().PullAsync(stoppingToken);
                        if (copied > 0)
                        {
                            logger.LogInformation("Quick items: copied {Copied} list(s) from central.", copied);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Quick items were not pulled from central.");
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
                logger.LogWarning(exception, "POS initial values sync did not run.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
