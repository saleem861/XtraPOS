$ErrorActionPreference = "Stop"
Set-Location D:\Google\XtraPOS
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
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip
$tag = "branch-agent-" + (Get-Date -Format "yyyyMMdd-HHmmss")
gh release create $tag $zip --repo saleem861/XtraPOS --title "XtraPOS Branch Agent $tag" --notes "Published branch agent package for the login update button."
Write-Host "Published $tag"
