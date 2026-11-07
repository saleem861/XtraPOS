# Build the local XtraPOS release and copy it to staging over SSH.
# This script does not use GitHub. It stops when the staging account still
# cannot write /opt/xtrapos-staging or the isolated central database is not selected.
param(
    [switch]$WriteCentral,
    [string]$CentralServer,
    [int]$CentralPort = 0,
    [string]$CentralUsername = "xtrapos_staging_central"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$bash = "C:\Program Files\Git\bin\bash.exe"
$key = Join-Path $env:USERPROFILE ".ssh\xtrapos_staging_deploy"
$target = "xtrapos-deploy@69.197.178.239"
if (-not (Test-Path $key)) { throw "Staging SSH key was not found." }
if (-not (Test-Path $bash)) { throw "Git Bash is required for the release scan and SSH copy." }

$sshBase = @("-i", $key, "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "ConnectTimeout=20", $target)
$writable = & $bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=20 $target 'test -w /opt/xtrapos-staging/releases && echo WRITABLE || echo DENIED'"
if ($writable -ne "WRITABLE") {
    throw "xtrapos-deploy cannot write /opt/xtrapos-staging/releases. An administrator must run deploy/staging-admin-setup.sh first. No files were copied."
}

$sha = (& git -C $root rev-parse HEAD).Trim()
if ($sha.Length -ne 40) { throw "The local git commit SHA is required for the release manifest." }
$version = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($sha.Substring(0, 7))"
$release = Join-Path $root "artifacts\staging\$version"
$apiOut = Join-Path $release "api"
$webOut = Join-Path $release "web"

Push-Location (Join-Path $root "src\XtraPOS.Web")
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    npm test
    if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed." }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
}
finally { Pop-Location }

New-Item -ItemType Directory -Force -Path $apiOut, $webOut | Out-Null
Copy-Item (Join-Path $root "src\XtraPOS.Web\dist\*") $webOut -Recurse -Force
dotnet publish (Join-Path $root "src\XtraPOS.Api\XtraPOS.Api.csproj") -c Release -o $apiOut --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "API publish failed." }
. (Join-Path $PSScriptRoot "Remove-StagingRuntimeFiles.ps1")
Remove-StagingRuntimeFiles -ReleaseRoot $release

$manifest = @{
    version = $version
    sha = $sha
    createdAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    migrations = "not-applied"
} | ConvertTo-Json
Set-Content -Path (Join-Path $release "release.json") -Value $manifest -Encoding ascii
Set-Content -Path (Join-Path $webOut "release.json") -Value $manifest -Encoding ascii
& $bash -lc "bash /d/Google/XtraPOS/deploy/scan-release.sh '/d/Google/XtraPOS/artifacts/staging/$version'"
if ($LASTEXITCODE -ne 0) { throw "Release scan failed." }

& $bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes $target 'mkdir -p /opt/xtrapos-staging/releases/$version && test -d /opt/xtrapos-staging/releases/$version'"
if ($LASTEXITCODE -ne 0) { throw "Could not create the remote release directory." }
& $bash -lc "scp -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes -r '/d/Google/XtraPOS/artifacts/staging/$version/.' '${target}:/opt/xtrapos-staging/releases/$version/'"
if ($LASTEXITCODE -ne 0) { throw "SCP failed. The current release was not changed." }

if ($WriteCentral) {
    if (-not $CentralServer -or $CentralPort -lt 1) { throw "WriteCentral requires -CentralServer and -CentralPort." }
    $secure = Read-Host "Password for $CentralUsername" -AsSecureString
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if ([string]::IsNullOrWhiteSpace($password)) { throw "Password was empty." }
    $password | & $bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes $target 'sudo -n /usr/local/sbin/xtrapos-staging-activate write-central $version $CentralServer $CentralPort $CentralUsername'"
    $password = $null
    if ($LASTEXITCODE -ne 0) { throw "Central connection was not saved. The release pointer was not changed." }
}

& $bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes $target 'sudo -n /usr/local/sbin/xtrapos-staging-activate activate $version'"
if ($LASTEXITCODE -ne 0) { throw "Activation failed. The helper restores the previous staging pointer when the new service does not become healthy." }
& $bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes $target 'sed -i s/not-applied/applied-on-activate/ /opt/xtrapos-staging/current/release.json /opt/xtrapos-staging/current/web/release.json'"
if ($LASTEXITCODE -ne 0) { throw "The release is active, but release.json still says migrations were not applied." }
Write-Host "Staging release $version is active. SHA $sha"
