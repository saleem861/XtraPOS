# Publish the built Vite frontend to XtraPOS staging without touching the API.
# Nginx serves /opt/xtrapos-staging/current/web, and current also selects current/api.
# This script never replaces that symlink, never publishes the API, and never
# calls xtrapos-staging-activate. The default mode is a read-only dry run.
param(
    [ValidateSet("DryRun", "Deploy", "Rollback")]
    [string]$Mode = "DryRun",
    [string]$ExpectedJs = "index-CAc0yqwN.js",
    [string]$ExpectedSha256 = "E71BD4738704C1F3F48C3E00431CEBCC6F0DC81045C110AFBD58311BBAD31FEA",
    [string]$RollbackId = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Bash = "C:\Program Files\Git\bin\bash.exe"
$Key = Join-Path $env:USERPROFILE ".ssh\xtrapos_staging_deploy"
$Target = "xtrapos-deploy@69.197.178.239"
$StagingRoot = "/opt/xtrapos-staging"
$LiveWeb = "$StagingRoot/current/web"
$FrontendRoot = "$StagingRoot/frontend-releases"
$BlockedPrefixes = @(
    "/opt/xtrapos-api",
    "/var/www",
    "/etc/nginx",
    "/etc/systemd",
    "/etc/xtrapos",
    "$StagingRoot/current/api",
    "$StagingRoot/shared"
)

function Assert-SafePath([string]$Path) {
    $normalized = $Path.Replace("\", "/")
    if ($normalized -match "\.\.") { throw "Refusing a path that contains '..': $Path" }
    foreach ($prefix in $BlockedPrefixes) {
        if ($normalized -eq $prefix -or $normalized.StartsWith("$prefix/")) {
            throw "Refusing a path outside the frontend write scope: $Path"
        }
    }
    if ($normalized -match "/api(/|$)") { throw "Refusing an API path: $Path" }
}

function Invoke-Staging([string]$RemoteCommand) {
    if ($RemoteCommand -match "systemctl|xtrapos-staging-activate|apply-branch-migrations|apply-central-migrations|nginx -s|sudo") {
        throw "Refusing a remote command outside the frontend-only scope."
    }
    $file = Join-Path $env:TEMP ("xtrapos-frontend-" + [guid]::NewGuid().ToString("n") + ".sh")
    $text = $RemoteCommand -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText($file, $text)
    $bashPath = $file.Replace("\", "/")
    if ($bashPath -match "^([A-Za-z]):") { $bashPath = "/$($Matches[1].ToLower())$($bashPath.Substring(2))" }
    & $Bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=20 $Target bash -s < '$bashPath'"
    if ($LASTEXITCODE -ne 0) { throw "Staging SSH command failed." }
}

function Copy-Staging([string]$LocalPath, [string]$RemotePath) {
    Assert-SafePath $RemotePath
    $local = $LocalPath.Replace("\", "/")
    if ($local -match "^([A-Za-z]):") { $local = "/$($Matches[1].ToLower())$($local.Substring(2))" }
    & $Bash -lc "scp -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes -r '$local/.' '${Target}:${RemotePath}/'"
    if ($LASTEXITCODE -ne 0) { throw "Frontend copy failed before the live web root was changed." }
}

Assert-SafePath $LiveWeb
Assert-SafePath $FrontendRoot
Assert-SafePath "$FrontendRoot/frontend-example"
try { Assert-SafePath "$StagingRoot/current/api"; throw "API path guard failed." }
catch { if ($_.Exception.Message -notmatch "frontend write scope") { throw } }
try { Assert-SafePath "/opt/xtrapos-api"; throw "Production path guard failed." }
catch { if ($_.Exception.Message -notmatch "frontend write scope") { throw } }

if (-not (Test-Path $Key)) { throw "Staging SSH key was not found." }
if (-not (Test-Path $Bash)) { throw "Git Bash is required." }

$dist = Join-Path $Root "src\XtraPOS.Web\dist"
$js = Join-Path $dist "assets\$ExpectedJs"
if (-not (Test-Path $js)) { throw "Verified frontend bundle was not found: $js" }
$actualSha = (Get-FileHash $js -Algorithm SHA256).Hash
if ($actualSha -ne $ExpectedSha256) {
    throw "Verified bundle hash mismatch for ${ExpectedJs}."
}

$gitSha = (& git -C $Root rev-parse HEAD).Trim()
$dirty = [bool](& git -C $Root status --porcelain)
$assetFiles = @(Get-ChildItem (Join-Path $dist "assets") -File)
$assetHashes = foreach ($file in $assetFiles) {
    "{0} {1}" -f (Get-FileHash $file.FullName -Algorithm SHA256).Hash, $file.Name
}
$identity = "frontend-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$($gitSha.Substring(0, 7))-$($ExpectedJs.Substring(6, 8))"

Write-Host "MODE=$Mode"
Write-Host "GIT_SHA=$gitSha"
Write-Host "TREE_DIRTY=$dirty"
Write-Host "FRONTEND_ID=$identity"
Write-Host "BUNDLE=$ExpectedJs"
Write-Host "SHA256=$actualSha"
Write-Host "LIVE_WEB=$LiveWeb"
Write-Host "FRONTEND_STORE=$FrontendRoot"
$assetHashes | ForEach-Object { Write-Host "ASSET $_" }

$probe = @'
set -e
test -d /opt/xtrapos-staging/current/api
test -d /opt/xtrapos-staging/current/web/assets
current=$(readlink -f /opt/xtrapos-staging/current)
case "$current" in
  /opt/xtrapos-staging/releases/*) ;;
  *) echo CURRENT_OUTSIDE_RELEASES; exit 1 ;;
esac
test -d "$current/api"
test -d "$current/web"
echo "CURRENT=$current"
nginx_root=$(grep -F "root /opt/xtrapos-staging/current/web;" /etc/nginx/sites-enabled/default)
echo "NGINX_ROOT=$nginx_root"
live_index=$(grep -o "/assets/index-[A-Za-z0-9_-]*\.js" /opt/xtrapos-staging/current/web/index.html)
echo "LIVE_INDEX=$live_index"
if test -w /opt/xtrapos-staging/current/web; then echo WEB_DIR_IS_WRITABLE; else echo WEB_DIR_NOT_WRITABLE; exit 1; fi
if test -e /opt/xtrapos-staging/frontend-releases; then echo FRONTEND_STORE_EXISTS; else echo FRONTEND_STORE_ABSENT; fi
if test -w /opt/xtrapos-api; then echo PRODUCTION_WRITABLE; exit 1; fi
echo PRODUCTION_NOT_WRITABLE
echo PROBE_OK
'@
Invoke-Staging $probe

Write-Host "WRITE_SCOPE=$FrontendRoot and index.html favicon.svg sw.js manifest.webmanifest frontend-release.json plus new files in $LiveWeb/assets"
Write-Host "PRESERVED=$StagingRoot/current symlink, $StagingRoot/current/api, $LiveWeb/release.json, $StagingRoot/shared, /opt/xtrapos-api, /etc/nginx"
Write-Host "ROLLBACK=atomic restore of the previous frontend root files from $FrontendRoot; hashed assets are not deleted"

$remoteApply = @'
set -euo pipefail
id="$1"
action="$2"
store="/opt/xtrapos-staging/frontend-releases/$id"
web="/opt/xtrapos-staging/current/web"
case "$id" in
    frontend-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-*) ;;
    *) echo BAD_ID; exit 1 ;;
esac
case "$store" in
    /opt/xtrapos-staging/frontend-releases/frontend-*) ;;
    *) echo BAD_STORE; exit 1 ;;
esac
test -d "$store"
test -d "$web"
test -d /opt/xtrapos-staging/current/api
current=$(readlink -f /opt/xtrapos-staging/current)
case "$current" in
    /opt/xtrapos-staging/releases/*) ;;
    *) echo CURRENT_OUTSIDE_RELEASES; exit 1 ;;
esac
test ! -e "$store/api"
test ! -e "$store/appsettings.json"
if [ "$action" = "deploy" ] && [ ! -f /opt/xtrapos-staging/frontend-previous.txt ]; then
    baseline="frontend-00000000-000000-baseline"
    base="/opt/xtrapos-staging/frontend-releases/$baseline"
    mkdir -p "$base/assets"
    for name in index.html favicon.svg sw.js manifest.webmanifest release.json; do
        if [ -f "$web/$name" ]; then cp "$web/$name" "$base/$name"; fi
    done
    : >"$base/asset-hashes.txt"
    for file in "$web/assets"/*; do
        if [ -f "$file" ]; then
            name=$(basename "$file")
            cp "$file" "$base/assets/$name"
            hash=$(sha256sum "$file" | cut -d " " -f 1)
            printf "%s %s\n" "$hash" "$name" >>"$base/asset-hashes.txt"
        fi
    done
    printf "%s\n" "{\"id\":\"$baseline\",\"kind\":\"frontend-baseline\"}" >"$base/frontend-release.json"
    printf "%s\n" "$baseline" >/opt/xtrapos-staging/frontend-previous.txt
elif [ "$action" = "deploy" ] && [ -f /opt/xtrapos-staging/frontend-current.txt ]; then
    tr -d "\r\n" </opt/xtrapos-staging/frontend-current.txt >/opt/xtrapos-staging/frontend-previous.txt
fi
while read -r hash name; do
    printf "%s  %s\n" "$hash" "$store/assets/$name" | sha256sum -c -
done <"$store/asset-hashes.txt"
mkdir -p "$web/assets"
for file in "$store/assets"/*; do
    base=$(basename "$file")
    dest="$web/assets/$base"
    if [ -e "$dest" ]; then
        cmp -s "$file" "$dest" || { echo "ASSET_CONFLICT $base"; exit 1; }
    else
        cp "$file" "$dest"
    fi
done
for name in index.html favicon.svg sw.js manifest.webmanifest frontend-release.json; do
    tmp="$web/.$name.$id"
    cp "$store/$name" "$tmp"
    mv -Tf "$tmp" "$web/$name"
done
printf "%s\n" "$id" >/opt/xtrapos-staging/frontend-current.txt
echo "FRONTEND_ACTIVE=$id"
'@

function Invoke-FrontendApply([string]$Id, [string]$Action) {
    Assert-SafePath "$FrontendRoot/$Id"
    $apply = Join-Path $env:TEMP "xtrapos-frontend-apply.sh"
    Set-Content -Path $apply -Value ($remoteApply -replace "`r`n", "`n") -Encoding ascii
    $bashApply = $apply.Replace("\", "/")
    if ($bashApply -match "^([A-Za-z]):") { $bashApply = "/$($Matches[1].ToLower())$($bashApply.Substring(2))" }
    & $Bash -lc "ssh -i '/c/Users/Saleem Jayousi/.ssh/xtrapos_staging_deploy' -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=20 $Target 'bash -s -- $Id $Action' < '$bashApply'"
    if ($LASTEXITCODE -ne 0) { throw "Frontend $Action failed. The shared current symlink was not changed." }
}

if ($Mode -eq "DryRun") {
    Write-Host "DRY_RUN_OK no files were uploaded and no symlink was changed"
    exit 0
}

if ($Mode -eq "Rollback") {
    if (-not $RollbackId) {
        $RollbackId = (Invoke-Staging "tr -d '\r\n' < /opt/xtrapos-staging/frontend-previous.txt").Trim()
    }
    if ($RollbackId -notmatch "^frontend-[0-9]{8}-[0-9]{6}-") { throw "Rollback id is not a frontend release id." }
    Invoke-Staging "test -s '$FrontendRoot/$RollbackId/asset-hashes.txt'"
    Invoke-FrontendApply $RollbackId "rollback"
    exit 0
}

$package = Join-Path $Root "artifacts\frontend-only\$identity"
New-Item -ItemType Directory -Force -Path (Join-Path $package "assets") | Out-Null
foreach ($name in @("index.html", "favicon.svg", "sw.js", "manifest.webmanifest")) {
    Copy-Item (Join-Path $dist $name) (Join-Path $package $name)
}
foreach ($file in $assetFiles) {
    Copy-Item $file.FullName (Join-Path $package "assets\$($file.Name)")
}
Set-Content -Path (Join-Path $package "asset-hashes.txt") -Value ($assetHashes -join "`n") -Encoding ascii
$manifest = @{
    id = $identity
    kind = "frontend-only"
    gitSha = $gitSha
    treeDirty = $dirty
    bundle = $ExpectedJs
    sha256 = $actualSha
    createdAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
} | ConvertTo-Json
Set-Content -Path (Join-Path $package "frontend-release.json") -Value $manifest -Encoding ascii
Invoke-Staging "mkdir -p '$FrontendRoot/$identity' && test -d '$FrontendRoot/$identity'"
Copy-Staging $package "$FrontendRoot/$identity"
Invoke-FrontendApply $identity "deploy"
