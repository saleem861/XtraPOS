using System.Text.Json;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class UpdaterChannelTests
{
    [Fact]
    public void Updater_counts_as_running_only_with_a_recent_heartbeat()
    {
        var now = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdaterChannel.IsAlive(now.AddSeconds(-20), now));
        Assert.False(UpdaterChannel.IsAlive(now.AddMinutes(-4), now));
        Assert.False(UpdaterChannel.IsAlive(null, now));
    }

    [Fact]
    public void Request_and_status_round_trip_through_the_updater_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xtrapos-updater-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var now = DateTime.UtcNow;
            var id = UpdaterChannel.Request(folder, now);
            using (var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "request.json"))))
            {
                Assert.Equal(id, request.RootElement.GetProperty("id").GetString());
            }

            File.WriteAllText(Path.Combine(folder, "heartbeat.txt"), now.ToString("o"));
            Assert.True(UpdaterChannel.IsAlive(UpdaterChannel.ReadHeartbeat(folder), now));

            File.WriteAllText(Path.Combine(folder, "status.json"), $$"""{"requestId":"{{id}}","state":"Failed","stage":"Done","detail":"rolled back: no files copied","from":"20261120-224712","to":"20261121-000042","at":"x"}""");
            var status = UpdaterChannel.ReadStatus(folder);
            Assert.NotNull(status);
            Assert.Equal(id, status!.RequestId);
            Assert.Equal("Failed", status.State);
            Assert.Equal("20261121-000042", status.To);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Installed_serial_comes_from_the_release_marker()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xtrapos-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal("", UpdaterChannel.InstalledSerial(folder));
            File.WriteAllText(Path.Combine(folder, "release.json"), """{"serial":"20261121-000042","tag":"branch-agent-20261121-000042"}""");
            Assert.Equal("20261121-000042", UpdaterChannel.InstalledSerial(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Updater_scripts_ship_in_the_repository()
    {
        var root = FindRoot();
        var updater = File.ReadAllText(Path.Combine(root, "deploy", "updater", "xtrapos-updater.ps1"));
        var installer = File.ReadAllText(Path.Combine(root, "deploy", "updater", "install-updater.ps1"));
        Assert.Contains("App_Data", updater, StringComparison.Ordinal);
        Assert.Contains("appsettings*.json", updater, StringComparison.Ordinal);
        Assert.Contains("RollingBack", updater, StringComparison.Ordinal);
        Assert.Contains("Tls12", updater, StringComparison.Ordinal);
        Assert.Contains("schtasks.exe /Create", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Register-ScheduledTask", installer, StringComparison.Ordinal);
        Assert.True(updater.All(character => character < 128), "xtrapos-updater.ps1 must stay ASCII for Windows PowerShell 5.1.");
        Assert.True(installer.All(character => character < 128), "install-updater.ps1 must stay ASCII for Windows PowerShell 5.1.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XtraPOS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
