# Points the staging current.txt back to previous.txt. Does not migrate databases or restart port 5080.
$ErrorActionPreference = "Stop"
if (-not $env:STAGING_ROOT) { throw "STAGING_ROOT is required." }
$root = $env:STAGING_ROOT
$previousFile = Join-Path $root "previous.txt"
$currentFile = Join-Path $root "current.txt"
if (-not (Test-Path $previousFile)) { throw "No previous staging release is recorded." }
$previous = (Get-Content $previousFile -Raw).Trim()
$release = Join-Path $root "releases\$previous"
if (-not (Test-Path $release)) { throw "Previous release folder is missing: $previous" }
if (Test-Path $currentFile) {
    $now = (Get-Content $currentFile -Raw).Trim()
    Set-Content -Path $previousFile -Value $now -Encoding ascii
}
Set-Content -Path $currentFile -Value $previous -Encoding ascii
Write-Host "Rolled staging pointer back to $previous. Restart only the staging site process, not port 5080."
