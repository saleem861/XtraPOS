# XtraPOS branch updater.
# Runs as SYSTEM from the scheduled task XtraPOSUpdater, outside the branch service,
# so a broken service build can never block its own replacement.
# The service only drops request.json; this script does the whole update and writes status.json.
# Keep this file ASCII: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
param(
  [int]$RunMinutes = 55
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'XtraPOSBranchAgent'
$Repository = 'saleem861/XtraPOS'
$AssetName = 'XtraPOS-BranchAgent.zip'
$HealthUrl = 'http://127.0.0.1:47335/health'
$Base = Join-Path $env:ProgramData 'XtraPOS\Updater'
$RequestFile = Join-Path $Base 'request.json'
$StatusFile = Join-Path $Base 'status.json'
$HeartbeatFile = Join-Path $Base 'heartbeat.txt'
$LogFile = Join-Path $Base 'updater.log'
$Work = Join-Path $Base 'work'
$Self = $MyInvocation.MyCommand.Path

function Write-Log([string]$message) {
  try {
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 1MB)) {
      Move-Item $LogFile ($LogFile + '.old') -Force
    }
    Add-Content -Path $LogFile -Value ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
  } catch { }
}

function Write-JsonFile([string]$path, $value) {
  $json = $value | ConvertTo-Json -Depth 4
  $temp = $path + '.tmp'
  [System.IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
  [System.IO.File]::Copy($temp, $path, $true)
  Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}

$script:Status = @{}
function Set-Status([string]$state, [string]$stage, [string]$detail) {
  $script:Status.state = $state
  $script:Status.stage = $stage
  $script:Status.detail = $detail
  $script:Status.at = (Get-Date).ToUniversalTime().ToString('o')
  Write-JsonFile $StatusFile $script:Status
  Write-Log ("{0} {1} {2}" -f $state, $stage, $detail)
}

function Get-InstallDirectory {
  $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
  $image = (Get-ItemProperty -Path $key -ErrorAction Stop).ImagePath
  $match = [regex]::Match($image, '^\s*"([^"]+)"|^\s*(\S+)')
  $exe = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
  return (Split-Path $exe -Parent)
}

function Get-InstalledSerial([string]$folder) {
  $file = Join-Path $folder 'release.json'
  if (-not (Test-Path $file)) { return '' }
  try { return [string]((Get-Content $file -Raw | ConvertFrom-Json).serial) } catch { return '' }
}

function Stop-Branch([string]$folder) {
  & sc.exe stop $ServiceName | Out-Null
  $deadline = (Get-Date).AddSeconds(40)
  do {
    Start-Sleep -Seconds 1
    $query = & sc.exe query $ServiceName | Out-String
  } while (($query -notmatch 'STOPPED') -and ((Get-Date) -lt $deadline))
  $exe = Join-Path $folder 'XtraPOS.Api.exe'
  $deadline = (Get-Date).AddSeconds(20)
  do {
    Get-Process -Name 'XtraPOS.Api' -ErrorAction SilentlyContinue |
      Where-Object { (-not $_.Path) -or ($_.Path -ieq $exe) } |
      Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $left = Get-Process -Name 'XtraPOS.Api' -ErrorAction SilentlyContinue | Where-Object { (-not $_.Path) -or ($_.Path -ieq $exe) }
  } while ($left -and ((Get-Date) -lt $deadline))
  if ($left) { throw 'branch program did not stop' }
}

function Start-Branch {
  & sc.exe start $ServiceName | Out-Null
}

function Wait-Healthy([int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  do {
    Start-Sleep -Seconds 2
    try {
      $response = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 5
      if ($response.StatusCode -eq 200) { return $true }
    } catch { }
  } while ((Get-Date) -lt $deadline)
  return $false
}

function Copy-Tree([string]$from, [string]$to, [switch]$KeepLocal) {
  $root = (Get-Item -LiteralPath $from).FullName.TrimEnd('\')
  $count = 0
  Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1)
    if ($KeepLocal -and (($relative -like 'App_Data\*') -or ($_.Name -like 'appsettings*.json') -or ($relative -like 'Updater\*'))) { return }
    $target = Join-Path $to $relative
    $parent = Split-Path $target -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    $count++
  }
  return $count
}

function Invoke-Update([string]$requestId) {
  $script:Status = @{ requestId = $requestId; from = ''; to = '' }
  Set-Status 'Running' 'Checking' ''
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $dest = Get-InstallDirectory
  $installed = Get-InstalledSerial $dest
  $script:Status.from = $installed

  $headers = @{ 'User-Agent' = 'XtraPOS-Updater'; 'Accept' = 'application/vnd.github+json' }
  $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers $headers -TimeoutSec 30
  $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
  if (-not $asset) { throw "release $($release.tag_name) has no $AssetName" }
  $serial = ([string]$release.tag_name) -replace '^branch-agent-', ''
  $script:Status.to = $serial
  if ($installed -and ($installed -eq $serial)) {
    Set-Status 'UpToDate' 'Done' ''
    return
  }

  if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $Work | Out-Null
  $zip = Join-Path $Work 'package.zip'
  $stage = Join-Path $Work 'stage'
  $backup = Join-Path $Work 'backup'

  Set-Status 'Running' 'Downloading' ''
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -TimeoutSec 600
  if ((Get-Item $zip).Length -ne [long]$asset.size) { throw 'downloaded package size does not match the release' }
  Expand-Archive -Path $zip -DestinationPath $stage -Force
  foreach ($required in @('XtraPOS.Api.dll', 'XtraPOS.Api.exe', 'wwwroot\index.html')) {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "package is missing $required" }
  }

  Set-Status 'Running' 'Backup' ''
  $null = Copy-Tree $dest $backup -KeepLocal

  Set-Status 'Running' 'Stopping' ''
  $stopped = $false
  try {
    Stop-Branch $dest
    $stopped = $true
    Set-Status 'Running' 'Copying' ''
    $copied = Copy-Tree $stage $dest -KeepLocal
    if ($copied -lt 1) { throw 'no files copied' }
    $marker = @{ serial = $serial; tag = [string]$release.tag_name; installedAt = (Get-Date).ToUniversalTime().ToString('o') }
    Write-JsonFile (Join-Path $dest 'release.json') $marker
    Set-Status 'Running' 'Starting' ''
    Start-Branch
    if (-not (Wait-Healthy 120)) { throw 'branch service did not answer /health after the update' }
  } catch {
    $reason = $_.Exception.Message
    Write-Log ("update failed, restoring: " + $reason)
    Set-Status 'Running' 'RollingBack' $reason
    try {
      if ($stopped) { Stop-Branch $dest }
      $null = Copy-Tree $backup $dest -KeepLocal
    } catch { Write-Log ("restore error: " + $_.Exception.Message) }
    Start-Branch
    $null = Wait-Healthy 90
    throw ("rolled back: " + $reason)
  }

  $newUpdater = Join-Path $stage 'Updater\xtrapos-updater.ps1'
  if ((Test-Path $newUpdater) -and $Self) {
    $current = (Get-FileHash $Self -Algorithm SHA256).Hash
    $incoming = (Get-FileHash $newUpdater -Algorithm SHA256).Hash
    if ($current -ne $incoming) {
      Copy-Item -LiteralPath $newUpdater -Destination $Self -Force
      $script:RestartSelf = $true
      Write-Log 'updater script replaced; it restarts on the next task run'
    }
  }

  Set-Status 'Succeeded' 'Done' ''
}

New-Item -ItemType Directory -Force -Path $Base | Out-Null
Write-Log 'updater started'
$script:RestartSelf = $false
$until = (Get-Date).AddMinutes($RunMinutes)
while ((Get-Date) -lt $until -and -not $script:RestartSelf) {
  try { [System.IO.File]::WriteAllText($HeartbeatFile, (Get-Date).ToUniversalTime().ToString('o')) } catch { }
  if (Test-Path $RequestFile) {
    $requestId = ''
    try { $requestId = [string]((Get-Content $RequestFile -Raw | ConvertFrom-Json).id) } catch { }
    Remove-Item $RequestFile -Force -ErrorAction SilentlyContinue
    try {
      Invoke-Update $requestId
    } catch {
      $script:Status.requestId = $requestId
      Set-Status 'Failed' 'Done' $_.Exception.Message
    }
  }
  Start-Sleep -Seconds 2
}
Write-Log 'updater loop ended'
