$ErrorActionPreference = "Continue"
$root = "D:\Google\XtraPOS"
$log = Join-Path $root "artifacts\update-local.log"
$lock = Join-Path $root "artifacts\update-local.lock"
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
if (Test-Path $lock) {
  $age = (Get-Date) - (Get-Item $lock).LastWriteTime
  if ($age.TotalMinutes -lt 3) { exit 0 }
}
Set-Content -Path $lock -Value (Get-Date -Format o)
function Write-Log($message) {
  Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message)
}

Start-Sleep -Seconds 2
Write-Log "update started"
$listener = Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
  $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
  $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)"
  $command = "$($process.CommandLine) $($parent.CommandLine)"
  if ($command -like "*XtraPOS.Api*") {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    if ($parent.CommandLine -like "*XtraPOS.Api*") {
      Stop-Process -Id $parent.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Write-Log "stopped API $($process.ProcessId)"
  } else {
    Write-Log "port 5080 is not XtraPOS; update stopped"
    exit 1
  }
}

$env:SqlServer__Server = "localhost"
$env:SqlServer__Database = "XtraPOS_Branch_01"
dotnet build (Join-Path $root "src\XtraPOS.Api\XtraPOS.Api.csproj") --nologo
if ($LASTEXITCODE -ne 0) {
  Write-Log "build failed $LASTEXITCODE"
  exit $LASTEXITCODE
}

try {
  Start-Process -FilePath "dotnet" -ArgumentList @(
    "run", "--project", (Join-Path $root "src\XtraPOS.Api\XtraPOS.Api.csproj"),
    "--no-build", "--launch-profile", "http"
  ) -WorkingDirectory $root -WindowStyle Hidden
  Write-Log "API started"
} finally {
  Remove-Item $lock -ErrorAction SilentlyContinue
}
exit 0
