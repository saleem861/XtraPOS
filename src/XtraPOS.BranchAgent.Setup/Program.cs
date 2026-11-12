using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace XtraPOS.BranchAgent.Setup;

internal static class Program
{
    private const string Site = "https://test-pos.sharbelfashion.com";
    private const string InstallDirectory = @"C:\Program Files\XtraPOS\BranchAgent";
    private const string ServiceName = "XtraPOSBranchAgent";

    [STAThread]
    private static int Main()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("التثبيت متاح على ويندوز فقط.");
            }

            var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            if (!runtime.Contains(".NET 8", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("يلزم تثبيت .NET 8 على هذا الجهاز قبل تثبيت الخدمة.");
            }

            var temp = Path.Combine(Path.GetTempPath(), "xtrapos-branch-agent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var zipPath = Path.Combine(temp, "branch-agent.zip");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            File.WriteAllBytes(zipPath, http.GetByteArrayAsync(Site + "/downloads/branch-agent.zip").GetAwaiter().GetResult());
            var expected = http.GetStringAsync(Site + "/downloads/branch-agent.sha256").GetAwaiter().GetResult().Trim().Split(' ', '\r', '\n')[0];
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath)));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ملف التثبيت لا يطابق البصمة المنشورة. لم يتم تثبيت الخدمة.");
            }

            StopService();
            Directory.CreateDirectory(InstallDirectory);
            Extract(zipPath, InstallDirectory);
            GrantLocalSqlCreate();
            ConfigureService();
            StartService();
            if (!WaitForStatus())
            {
                throw new InvalidOperationException("تم نسخ الخدمة، لكن لم يبدأ الاستماع المحلي. أعد تشغيل المثبّت.");
            }

            MessageBox.Show("تم تثبيت خدمة XtraPOS وتشغيلها. ارجع إلى صفحة الإعداد.", "XtraPOS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "XtraPOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static bool IsSafeZipEntry(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':'))
        {
            return false;
        }

        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part => part != "." && part != "..");
    }

    private static void Extract(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeZipEntry(entry.FullName) || entry.FullName.Replace('\\', '/').StartsWith("App_Data/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void GrantLocalSqlCreate()
    {
        foreach (var instance in LocalInstances())
        {
            try
            {
                using var connection = new SqlConnection($"Server={instance};Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
                    GRANT CREATE ANY DATABASE TO [NT AUTHORITY\SYSTEM];
                    """;
                command.ExecuteNonQuery();
            }
            catch (Exception)
            {
                // The wizard reports the SQL permission error if this grant cannot be applied.
            }
        }
    }

    private static IEnumerable<string> LocalInstances()
    {
        yield return "localhost";
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        if (key is null)
        {
            yield break;
        }

        foreach (var name in key.GetValueNames())
        {
            if (!name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            {
                yield return @"localhost\" + name;
            }
        }
    }

    private static void ConfigureService()
    {
        var exe = Path.Combine(InstallDirectory, "XtraPOS.Api.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException("ملف الخدمة غير موجود داخل الحزمة.");
        }

        var binPath = $"\\\"{exe}\\\" --branch-agent --server {Site}";
        var query = Run("sc.exe", $"query {ServiceName}");
        if (query != 0)
        {
            var created = Run("sc.exe", $"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"XtraPOS Branch Agent\"");
            if (created != 0)
            {
                throw new InvalidOperationException("تعذر تسجيل خدمة ويندوز.");
            }
        }
        else
        {
            var configured = Run("sc.exe", $"config {ServiceName} binPath= \"{binPath}\" start= auto");
            if (configured != 0)
            {
                throw new InvalidOperationException("تعذر تحديث خدمة ويندوز.");
            }
        }
    }

    private static void StopService()
    {
        Run("sc.exe", $"stop {ServiceName}");
        Thread.Sleep(1500);
    }

    private static void StartService()
    {
        var started = Run("sc.exe", $"start {ServiceName}");
        if (started != 0)
        {
            throw new InvalidOperationException("تعذر تشغيل الخدمة بعد التثبيت.");
        }
    }

    private static bool WaitForStatus()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                var body = http.GetStringAsync("http://127.0.0.1:47331/status").GetAwaiter().GetResult();
                if (body.Contains("XtraPOS.BranchAgent", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                Thread.Sleep(1000);
            }
        }

        return false;
    }

    private static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }
}
