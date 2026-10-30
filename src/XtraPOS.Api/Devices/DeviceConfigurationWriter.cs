using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Devices;

public static class DeviceConfigurationWriter
{
    public static async Task SaveAsync(BranchDbContext db, Guid deviceId, Guid branchId, string deviceName, int? posNumber, CancellationToken cancellationToken)
    {
        var json = DeviceConfigDocument.Merge(null, new Dictionary<string, string?>
        {
            ["deviceId"] = deviceId.ToString(),
            ["branchId"] = branchId.ToString(),
            ["posName"] = deviceName,
            ["posNumber"] = posNumber?.ToString(),
        });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NOT NULL
            BEGIN
                MERGE dbo.POSDeviceConfigurations AS target
                USING (SELECT {deviceId} AS DeviceID) AS source
                ON target.DeviceID = source.DeviceID
                WHEN MATCHED THEN UPDATE SET
                    POSNumber = {posNumber},
                    BranchID = {branchId},
                    DeviceName = {deviceName},
                    ConfigurationJson = {json},
                    ConfigVersion = target.ConfigVersion + 1,
                    IsSetupCompleted = 1,
                    UpdatedAtUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (DeviceID, POSNumber, BranchID, DeviceName, ConfigurationJson, SchemaVersion, ConfigVersion, IsSetupCompleted, UpdatedAtUtc)
                    VALUES ({deviceId}, {posNumber}, {branchId}, {deviceName}, {json}, 1, 1, 1, SYSUTCDATETIME());
            END
            """,
            cancellationToken);
    }
}
