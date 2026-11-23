# Installs the XtraPOS updater once on a branch computer. Run as Administrator.
# Copies xtrapos-updater.ps1 to C:\ProgramData\XtraPOS\Updater, locks the folder to SYSTEM and
# Administrators, and registers the scheduled task XtraPOSUpdater (SYSTEM, at startup and every minute).
# Uses schtasks.exe with an XML definition so it works even when WMI is broken.
# Keep this file ASCII: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
$ErrorActionPreference = 'Stop'
$TaskName = 'XtraPOSUpdater'
$source = Join-Path $PSScriptRoot 'xtrapos-updater.ps1'
$base = Join-Path $env:ProgramData 'XtraPOS\Updater'
$target = Join-Path $base 'xtrapos-updater.ps1'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this script as Administrator.'
}
if (-not (Test-Path $source)) { throw "xtrapos-updater.ps1 was not found next to this script ($PSScriptRoot)." }

New-Item -ItemType Directory -Force -Path $base | Out-Null
& icacls.exe $base /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set permissions on the updater folder.' }

& cmd.exe /c "schtasks.exe /End /TN $TaskName >nul 2>&1"
Copy-Item -LiteralPath $source -Destination $target -Force

$start = (Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
$arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $target + '"'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>XtraPOS branch updater. Applies the published release when the login screen asks for it.</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
    <TimeTrigger>
      <StartBoundary>$start</StartBoundary>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT75M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>$([System.Security.SecurityElement]::Escape($arguments))</Arguments>
    </Exec>
  </Actions>
</Task>
"@
$xmlFile = Join-Path $base 'task.xml'
[System.IO.File]::WriteAllText($xmlFile, $xml, [System.Text.Encoding]::Unicode)
& schtasks.exe /Create /F /TN $TaskName /XML $xmlFile | Out-Null
if ($LASTEXITCODE -ne 0) { throw "schtasks could not register $TaskName (exit $LASTEXITCODE)." }
Remove-Item $xmlFile -Force -ErrorAction SilentlyContinue

$heartbeat = Join-Path $base 'heartbeat.txt'
Remove-Item $heartbeat -Force -ErrorAction SilentlyContinue
& schtasks.exe /Run /TN $TaskName | Out-Null
$deadline = (Get-Date).AddSeconds(20)
while (-not (Test-Path $heartbeat) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $heartbeat)) { throw "$TaskName was registered but did not start. Check $base\updater.log." }
Write-Host "XtraPOS updater installed and running."
