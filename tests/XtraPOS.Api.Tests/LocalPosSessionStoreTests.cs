using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class LocalPosSessionStoreTests
{
    [Fact]
    public void Setup_completion_keeps_the_device_session_for_the_local_login_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtrapos-pos-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalPosSessionStore(new EphemeralDataProtectionProvider(), new RootEnvironment(directory));
            var deviceId = Guid.NewGuid();
            store.Save(new DeviceRegistrationResponse
            {
                DeviceId = deviceId,
                BusinessId = Guid.NewGuid(),
                BranchId = Guid.NewGuid(),
                StationId = Guid.NewGuid(),
                DeviceName = "صندوق 1",
                DeviceType = "POS",
                DeviceToken = "device-token",
                RegisteredAt = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc),
                BusinessName = "منشأة الشربل",
                BranchName = "الفرع الرئيسي",
                BranchType = "Retail",
                StationName = "كاونتر 1",
                StationNumber = 1,
            });

            var saved = store.Read();
            Assert.NotNull(saved);
            Assert.Equal(deviceId, saved!.DeviceId);
            Assert.Equal("device-token", saved.DeviceToken);
            Assert.DoesNotContain("device-token", File.ReadAllText(Path.Combine(directory, "App_Data", "local-pos-session.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RootEnvironment(string path) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = path;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
