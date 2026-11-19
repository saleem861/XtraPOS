$ErrorActionPreference = "Stop"
Set-Location D:\Google\XtraPOS
$serial = Get-Date -Format "yyyyMMdd-HHmmss"
$env:VITE_RELEASE_SERIAL = $serial
$publish = Join-Path $PWD "artifacts\branch-agent-release"
$zip = Join-Path $PWD "artifacts\XtraPOS-BranchAgent.zip"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
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
Get-ChildItem $publish -Filter "appsettings*.json" -File | Remove-Item -Force
Get-ChildItem $publish -Include *.pdb,*.xml -Recurse -File | Remove-Item -Force
$localData = Join-Path $publish "App_Data"
if (Test-Path $localData) { Remove-Item $localData -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, "Create")
try {
  Get-ChildItem $publish -Recurse -File | Where-Object { $_.FullName -notlike "*\App_Data\*" } | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length).TrimStart("\")
    $entry = $archive.CreateEntry($relative.Replace("\", "/"))
    $output = $entry.Open()
    try {
      $source = [System.IO.File]::Open($_.FullName, "Open", "Read", "ReadWrite")
      try { $source.CopyTo($output) } finally { $source.Close() }
    } finally { $output.Close() }
  }
} finally { $archive.Dispose() }
if (-not (Test-Path $zip)) { throw "Package zip was not created." }
$tag = "branch-agent-" + $serial
gh release create $tag $zip --repo saleem861/XtraPOS --title "XtraPOS Branch Agent $tag" --notes "Published branch agent package for the login update button."
if ($LASTEXITCODE -ne 0) { throw "GitHub release was not created." }
Write-Host "Published $tag"
