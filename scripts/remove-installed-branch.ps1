# Full removal of a locally installed XtraPOS branch:
# stop and delete the XtraPOSBranchAgent service, drop the local XtraPOS_Branch
# database when appsettings points at a local server, then delete the install folder.
$install = 'C:\Program Files\XtraPOS\BranchAgent'
$settingsPath = Join-Path $install 'appsettings.json'
$server = 'localhost'
$database = 'XtraPOS_Branch'
if (Test-Path $settingsPath) {
  $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
  if ($settings.SqlServer.Server) { $server = [string]$settings.SqlServer.Server }
  if ($settings.SqlServer.Database) { $database = [string]$settings.SqlServer.Database }
}
$localHost = ($server -split '\\')[0].Trim()
$isLocal = $localHost -in @('localhost', '.', '(local)', '127.0.0.1')
& sc.exe stop XtraPOSBranchAgent | Out-Null
for ($i = 0; $i -lt 40; $i++) {
  $state = & sc.exe query XtraPOSBranchAgent 2>&1 | Out-String
  if ($state -match 'FAILED 1060' -or $state -match 'STOPPED') { break }
  Start-Sleep -Seconds 1
}
& sc.exe delete XtraPOSBranchAgent | Out-Null
if ($database -eq 'XtraPOS_Branch' -and $isLocal) {
  sqlcmd -S $server -E -b -Q "IF DB_ID(N'XtraPOS_Branch') IS NOT NULL BEGIN ALTER DATABASE [XtraPOS_Branch] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [XtraPOS_Branch]; END"
}
if (Test-Path $install) { Remove-Item $install -Recurse -Force }
& sc.exe query XtraPOSBranchAgent
