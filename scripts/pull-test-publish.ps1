# Pull, test, and publish in one run. Stops at the first step that fails and says which one.
#
#   powershell -ExecutionPolicy Bypass -File .\scripts\pull-test-publish.ps1 -DotnetFilter "FullyQualifiedName~PosDeviceSettings" -WebTests src/components/DeviceInitialValuesScreen.test.tsx
#
# -DotnetFilter  dotnet test filter (omit to skip server tests)
# -WebTests      vitest files under src/XtraPOS.Web (omit to skip web tests)
# -SkipPublish   stop after the tests
#
# Keep this file ASCII: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
param(
  [string]$DotnetFilter = "",
  [string[]]$WebTests = @(),
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Step([string]$name, [scriptblock]$body) {
  Write-Host ""
  Write-Host "=== $name" -ForegroundColor Cyan
  $global:LASTEXITCODE = 0
  try {
    & $body
  } catch {
    Write-Host "STOPPED at: $name" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
  }
  if ($LASTEXITCODE -ne 0) {
    Write-Host "STOPPED at: $name (exit code $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
  }
  Write-Host "OK: $name" -ForegroundColor Green
}

# Google Drive drops desktop.ini files inside .git, which breaks git ("bad object refs/desktop.ini").
Invoke-Step "Clean desktop.ini inside .git" {
  $found = @(Get-ChildItem (Join-Path $root '.git') -Recurse -Force -Filter desktop.ini -ErrorAction SilentlyContinue)
  if ($found.Count -gt 0) {
    $to = 'D:\XtraPOS-duplicates\git-desktop-ini'
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $i = 0
    foreach ($file in $found) {
      $i++
      $name = "$stamp-$i-" + ($file.DirectoryName -replace '[\\:]', '_') + '.ini'
      Move-Item -LiteralPath $file.FullName -Destination (Join-Path $to $name) -Force
    }
    Write-Host "Moved $($found.Count) desktop.ini file(s) to $to"
  }
}

Invoke-Step "git pull" { git -c gc.auto=0 pull --ff-only }

if ($DotnetFilter) {
  Invoke-Step "Server build and tests" {
    dotnet test (Join-Path $root 'tests\XtraPOS.Api.Tests\XtraPOS.Api.Tests.csproj') --filter $DotnetFilter --nologo -v q
  }
}

if ($WebTests.Count -gt 0) {
  Invoke-Step "Web tests" {
    Push-Location (Join-Path $root 'src\XtraPOS.Web')
    try { npx vitest run @WebTests --silent } finally { Pop-Location }
  }
}

if (-not $SkipPublish) {
  Invoke-Step "Publish" {
    powershell -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\publish-branch-release.ps1')
  }
}

Write-Host ""
Write-Host "ALL STEPS PASSED" -ForegroundColor Green
