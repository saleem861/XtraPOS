$ErrorActionPreference = "Stop"
Set-Location D:\Google\XtraPOS
$serial = Get-Date -Format "yyyyMMdd-HHmmss"
$env:VITE_RELEASE_SERIAL = $serial
$publish = Join-Path $PWD "artifacts\branch-agent-release-$serial"
$zip = Join-Path $PWD "artifacts\XtraPOS-BranchAgent.zip"
New-Item -ItemType Directory -Force -Path $publish | Out-Null
dotnet publish (Join-Path $PWD "src\XtraPOS.Api\XtraPOS.Api.csproj") -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw "API publish failed with $LASTEXITCODE." }
Push-Location (Join-Path $PWD "src\XtraPOS.Web")
try {
  if (-not (Test-Path "node_modules\lucide-react\package.json")) {
    if (Test-Path "node_modules") { Remove-Item "node_modules" -Recurse -Force }
    npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw "Web dependencies failed with $LASTEXITCODE." }
  }
  npm.cmd run build
  if ($LASTEXITCODE -ne 0) { throw "Web build failed with $LASTEXITCODE." }
}
finally {
  Pop-Location
}
$www = Join-Path $publish "wwwroot"
if (Test-Path $www) { Remove-Item $www -Recurse -Force }
Copy-Item (Join-Path $PWD "src\XtraPOS.Web\dist") $www -Recurse
Get-ChildItem $publish -Include *.pdb,*.xml -Recurse -File | Remove-Item -Force
$localData = Join-Path $publish "App_Data"
if (Test-Path $localData) { Remove-Item $localData -Recurse -Force -ErrorAction SilentlyContinue }
$settings = Join-Path $publish "appsettings.json"
if (Test-Path $settings) {
  $prepared = Get-Content $settings -Raw | ConvertFrom-Json
  if (-not $prepared.Membership) { $prepared | Add-Member -MemberType NoteProperty -Name Membership -Value ([pscustomobject]@{}) }
  $prepared.Membership.Enabled = $true
  $prepared.Membership.GoogleLoginEnabled = $true
  $prepared.Membership.AppleLoginEnabled = $false
  $prepared.Membership.RequireVerificationForSpecialPrices = $false
  if ([string]::IsNullOrWhiteSpace([string]$prepared.Membership.GoogleClientId)) {
    $prepared.Membership | Add-Member -MemberType NoteProperty -Name GoogleClientId -Value "998725042091-t1p0fv71s7ajkbs104s62if1c1oviqmd.apps.googleusercontent.com" -Force
  }
  if ([string]::IsNullOrWhiteSpace([string]$prepared.Membership.PublicOrigin)) {
    $prepared.Membership | Add-Member -MemberType NoteProperty -Name PublicOrigin -Value "https://test-pos.sharbelfashion.com" -Force
  }
  $json = $prepared | ConvertTo-Json -Depth 8
  $written = $false
  foreach ($attempt in 1..8) {
    try {
      $temp = "$settings.tmp"
      [System.IO.File]::WriteAllText($temp, $json)
      [System.IO.File]::Copy($temp, $settings, $true)
      Remove-Item $temp -Force -ErrorAction SilentlyContinue
      $written = $true
      break
    } catch {
      if ($attempt -eq 8) { throw }
      Start-Sleep -Seconds 1
    }
  }
  if (-not $written) { throw "Could not write appsettings.json." }
}
$deviceZip = Join-Path $PWD "artifacts\branch-agent.zip"
if (Test-Path $deviceZip) { Remove-Item $deviceZip -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-PackageZip([string]$destination, [bool]$includeSettings) {
  $archive = [System.IO.Compression.ZipFile]::Open($destination, "Create")
  try {
    Get-ChildItem $publish -Recurse -File | Where-Object {
      $_.FullName -notlike "*\App_Data\*" -and (
        ($includeSettings -and $_.Name -eq "appsettings.json") -or ($_.Name -notlike "appsettings*.json")
      )
    } | ForEach-Object {
      $relative = $_.FullName.Substring($publish.Length).TrimStart("\")
      $entry = $archive.CreateEntry($relative.Replace("\", "/"))
      $output = $entry.Open()
      try {
        $source = [System.IO.File]::Open($_.FullName, "Open", "Read", "ReadWrite")
        try { $source.CopyTo($output) } finally { $source.Close() }
      } finally { $output.Close() }
    }
  } finally { $archive.Dispose() }
}
Write-PackageZip $zip $false
Write-PackageZip $deviceZip $true
$tag = "branch-agent-" + $serial
gh release create $tag $zip --repo saleem861/XtraPOS --title "XtraPOS Branch Agent $tag" --notes "Published branch agent package for the login update button and new device setup."
if ($LASTEXITCODE -ne 0) { throw "GitHub release was not created." }
Write-Host "Published $tag"
$hash = (Get-FileHash $deviceZip -Algorithm SHA256).Hash.ToLowerInvariant()
$deviceDir = Join-Path $PWD "artifacts\branch-agent-downloads"
if (Test-Path $deviceDir) { Remove-Item $deviceDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $deviceDir | Out-Null
Copy-Item $deviceZip (Join-Path $deviceDir "branch-agent.zip") -Force
Set-Content -Path (Join-Path $deviceDir "branch-agent.sha256") -Value $hash -Encoding ascii
Set-Content -Path (Join-Path $deviceDir "branch-agent.json") -Value (@{ version = "0.3.0"; sha256 = $hash } | ConvertTo-Json) -Encoding ascii
$bash = "C:\Program Files\Git\bin\bash.exe"
$key = Join-Path $PWD ".ssh\xtrapos_staging_deploy"
if (-not (Test-Path $bash)) { throw "Git Bash is required to update the new-device package. The update button release $tag is already published." }
if (-not (Test-Path $key)) { throw "Staging SSH key was not found. The update button release $tag is already published." }
function To-GitBashPath([string]$path) {
  $full = [IO.Path]::GetFullPath($path)
  return "/" + $full.Substring(0, 1).ToLower() + ($full.Substring(2) -replace '\\', '/')
}
$keyBash = To-GitBashPath $key
$dirBash = To-GitBashPath $deviceDir
$target = "xtrapos-deploy@69.197.178.239"
$writable = & $bash -lc "ssh -i '$keyBash' -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=20 $target 'test -w /opt/xtrapos-staging/current/web && echo WRITABLE || echo DENIED'"
if ($writable -ne "WRITABLE") { throw "The setup site cannot be updated. The update button release $tag is already published." }
& $bash -lc "ssh -i '$keyBash' -o BatchMode=yes -o IdentitiesOnly=yes $target 'mkdir -p /opt/xtrapos-staging/current/web/downloads'"
if ($LASTEXITCODE -ne 0) { throw "The new-device folder was not created. The update button release $tag is already published." }
& $bash -lc "scp -i '$keyBash' -o BatchMode=yes -o IdentitiesOnly=yes '$dirBash/branch-agent.zip' '$dirBash/branch-agent.sha256' '$dirBash/branch-agent.json' '${target}:/opt/xtrapos-staging/current/web/downloads/'"
if ($LASTEXITCODE -ne 0) { throw "The new-device package was not copied. The update button release $tag is already published." }
& $bash -lc "ssh -i '$keyBash' -o BatchMode=yes -o IdentitiesOnly=yes $target 'chmod 644 /opt/xtrapos-staging/current/web/downloads/branch-agent.zip /opt/xtrapos-staging/current/web/downloads/branch-agent.sha256 /opt/xtrapos-staging/current/web/downloads/branch-agent.json'"
Write-Host "New device package updated"
Get-ChildItem (Join-Path $PWD "artifacts") -Directory -Filter "branch-agent-release*" | Where-Object { $_.FullName -ne $publish } | ForEach-Object {
  Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}
