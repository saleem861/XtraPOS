using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XtraPOS.Api.Setup;

public static class PublishedRelease
{
    public const string AssetName = "XtraPOS-BranchAgent.zip";
    public const string StagingPackageUrl = "https://test-pos.sharbelfashion.com/downloads/branch-agent.zip";
    public const string LatestApi = "https://api.github.com/repos/saleem861/XtraPOS/releases/latest";

    public static string? FindAssetUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var name) || !string.Equals(name.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static bool IsAllowedDownload(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.Host.Equals("test-pos.sharbelfashion.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/downloads/branch-agent.zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string?> LatestAssetUrlAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("XtraPOS", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync(LatestApi, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var asset = FindAssetUrl(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!string.IsNullOrWhiteSpace(asset))
            {
                return asset;
            }
        }

        using var probe = new HttpRequestMessage(HttpMethod.Get, StagingPackageUrl);
        using var staged = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return staged.IsSuccessStatusCode ? StagingPackageUrl : null;
    }

    public static string BuildUpdaterScript(string installDirectory, string downloadUrl)
    {
        if (!IsAllowedDownload(downloadUrl))
        {
            throw new InvalidOperationException("The published package address is not the staging package or a GitHub release asset.");
        }

        var destination = installDirectory.Replace("'", "''", StringComparison.Ordinal);
        var address = downloadUrl.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $dest = '{{destination}}'
            $url = '{{address}}'
            $log = 'C:\Windows\Temp\xtrapos-release-update.log'
            $zip = 'C:\Windows\Temp\XtraPOS-BranchAgent.zip'
            $stage = 'C:\Windows\Temp\XtraPOS-BranchAgent-stage'
            function Write-Log($message) {
              Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
            }
            Write-Log 'release update started'
            try {
              Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
              Write-Log 'package downloaded'
              & sc.exe stop XtraPOSBranchAgent | Out-Null
              $deadline = (Get-Date).AddSeconds(30)
              do {
                Get-Process -Name 'XtraPOS.Api' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
              } while ((Get-Process -Name 'XtraPOS.Api' -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline))
              if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
              Expand-Archive -Path $zip -DestinationPath $stage -Force
              $copied = 0
              Get-ChildItem $stage -Recurse -File | Where-Object { $_.FullName -notlike '*\App_Data\*' -and $_.Name -notlike 'appsettings*.json' } | ForEach-Object {
                $relative = $_.FullName.Substring($stage.Length).TrimStart('\')
                $target = Join-Path $dest $relative
                $parent = Split-Path $target
                if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
                $copied++
              }
              if ($copied -lt 1) { throw 'no files copied' }
              Write-Log ("files copied " + $copied)
            } catch {
              Write-Log $_.Exception.Message
              throw
            } finally {
              & sc.exe start XtraPOSBranchAgent | Out-Null
              Start-Sleep -Seconds 3
              $query = & sc.exe query XtraPOSBranchAgent 2>$null | Out-String
              if ($query -match 'RUNNING') {
                Write-Log 'service started'
              } else {
                $exe = Join-Path $dest 'XtraPOS.Api.exe'
                if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $dest }
                Write-Log 'program started'
              }
            }
            """;
    }

    public static void StartUpdater(string script)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XtraPOS");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "xtrapos-release-update.ps1");
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A child of the service is killed when the script stops the service, before the files are copied.
        var command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + path;
        var created = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = "/Create /F /TN XtraPOSBranchUpdate /SC ONCE /ST 00:00 /RL HIGHEST /TR \"" + command + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        created?.WaitForExit(15000);
        if (created is null || created.ExitCode != 0)
        {
            throw new InvalidOperationException("تعذر جدولة إيقاف الخدمة وتحديثها.");
        }

        var started = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = "/Run /TN XtraPOSBranchUpdate",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        started?.WaitForExit(15000);
        if (started is null || started.ExitCode != 0)
        {
            throw new InvalidOperationException("تعذر تشغيل تحديث الخدمة.");
        }
    }
}
