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
            $log = Join-Path $env:TEMP 'xtrapos-release-update.log'
            $zip = Join-Path $env:TEMP 'XtraPOS-BranchAgent.zip'
            $stage = Join-Path $env:TEMP 'XtraPOS-BranchAgent-stage'
            function Write-Log($message) {
              Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
            }
            Write-Log 'release update started'
            try {
              Start-Sleep -Seconds 2
              Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
              Write-Log 'package downloaded'
              & sc.exe stop XtraPOSBranchAgent | Out-Null
              $deadline = (Get-Date).AddSeconds(40)
              do {
                Start-Sleep -Seconds 1
                $query = & sc.exe query XtraPOSBranchAgent 2>$null | Out-String
                if ($query -match 'STOPPED') { break }
              } while ((Get-Date) -lt $deadline)
              Get-Process -Name 'XtraPOS.Api' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
              Start-Sleep -Seconds 1
              if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
              Expand-Archive -Path $zip -DestinationPath $stage -Force
              & robocopy $stage $dest /E /R:2 /W:2 /NFL /NDL /NJH /NJS /XD App_Data /XF appsettings.json appsettings.Development.json appsettings.Staging.json appsettings.Production.json 'appsettings*.json'
              if ($LASTEXITCODE -ge 8) { throw "robocopy $LASTEXITCODE" }
              Write-Log 'files copied'
            } catch {
              Write-Log $_.Exception.Message
              throw
            } finally {
              & sc.exe start XtraPOSBranchAgent | Out-Null
              Write-Log 'service start requested'
            }
            """;
    }

    public static void StartUpdater(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), "xtrapos-release-update.ps1");
        File.WriteAllText(path, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
