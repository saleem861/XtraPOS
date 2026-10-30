# One action: package a versioned staging release.
# Publishing to a host happens only when -Publish is passed and STAGING_ROOT is set.
# This script does not apply database migrations and does not touch port 5080.
param(
    [switch]$Publish,
    [switch]$Remote,
    [switch]$IncludeApiTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sha = (git -C $root rev-parse --short HEAD).Trim()
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$version = "$stamp-$sha"
$release = Join-Path $root "artifacts\staging\$version"
$operational = @("XtraPOS_Branch_01", "XtraPOS_Branch_02", "XtraPOS_Central")

if ($env:SqlServer__Database -and $operational -contains $env:SqlServer__Database) {
    throw "Refusing to package staging against an operational database."
}

Write-Host "Staging release $version"
Push-Location (Join-Path $root "src\XtraPOS.Web")
try {
    npx vitest run src/components/NewCustomerScreen.test.tsx src/components/CustomerBrowseScreen.test.tsx src/components/CustomersMenuScreen.test.tsx
    if ($LASTEXITCODE -ne 0) { throw "Web tests failed." }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Web build failed." }
}
finally { Pop-Location }

$apiOut = Join-Path $release "api"
$webOut = Join-Path $release "web"
New-Item -ItemType Directory -Force -Path $apiOut, $webOut | Out-Null
Copy-Item (Join-Path $root "src\XtraPOS.Web\dist\*") $webOut -Recurse -Force

dotnet publish (Join-Path $root "src\XtraPOS.Api\XtraPOS.Api.csproj") -c Release -o $apiOut --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

if ($IncludeApiTests) {
    dotnet test (Join-Path $root "tests\XtraPOS.Api.Tests\XtraPOS.Api.Tests.csproj") -c Release --filter "FullyQualifiedName~CustomerMasterTests"
    if ($LASTEXITCODE -ne 0) { throw "API tests failed." }
}

$manifest = @{
    version = $version
    sha = $sha
    createdAt = (Get-Date).ToUniversalTime().ToString("o")
    web = "https://test-pos.sharbelfashion.com"
    database = "XtraPOS_Branch_Test_Staging"
    centralDatabase = "XtraPOS_Central_Test_Staging"
    migrations = "not-applied"
} | ConvertTo-Json
Set-Content -Path (Join-Path $release "release.json") -Value $manifest -Encoding utf8
Write-Host "Packaged $release"

if ($Remote) {
    if (-not $env:STAGING_SSH) { throw "STAGING_SSH is required, for example user@69.197.178.239." }
    if ($env:STAGING_SSH -match "5080|Branch_01|Branch_02") { throw "Refusing a remote target that looks operational." }
    $remoteRelease = "/opt/xtrapos-staging/releases/$version"
    scp -r $release "${env:STAGING_SSH}:$remoteRelease"
    if ($LASTEXITCODE -ne 0) { throw "Remote copy failed." }
    ssh $env:STAGING_SSH "printf '%s' '$version' > /opt/xtrapos-staging/current.txt"
    if ($LASTEXITCODE -ne 0) { throw "Remote pointer update failed." }
    Write-Host "Copied $version to $env:STAGING_SSH. Restart only xtrapos-staging, then run Check-Staging.ps1."
    return
}

if (-not $Publish) {
    Write-Host "Package only. Pass -Remote after the staging host is approved."
    return
}

if (-not $env:STAGING_ROOT) { throw "STAGING_ROOT is required for -Publish." }
$target = $env:STAGING_ROOT
if ($target -match "5080|Branch_01|Branch_02|XtraPOS_Central") { throw "Refusing a staging path that looks operational." }
$releases = Join-Path $target "releases"
New-Item -ItemType Directory -Force -Path $releases | Out-Null
$dest = Join-Path $releases $version
Copy-Item $release $dest -Recurse -Force
$current = Join-Path $target "current.txt"
if (Test-Path $current) {
    Copy-Item $current (Join-Path $target "previous.txt") -Force
}
Set-Content -Path $current -Value $version -Encoding ascii
Write-Host "Staged $version at $dest. Switch the staging site root to this folder, then run Check-Staging.ps1."
