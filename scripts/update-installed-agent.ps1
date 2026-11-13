$ErrorActionPreference = "Stop"
$source = "D:\Google\XtraPOS\artifacts\branch-agent-publish"
$dest = "C:\Program Files\XtraPOS\BranchAgent"
if (-not (Test-Path (Join-Path $source "XtraPOS.Api.exe"))) {
    throw "Agent publish was not found."
}
if (-not (Test-Path (Join-Path $source "wwwroot\index.html"))) {
    throw "Local web files were not found."
}

& sc.exe stop XtraPOSBranchAgent
Start-Sleep -Seconds 4
& robocopy $source $dest /E /XD App_Data
if ($LASTEXITCODE -ge 8) {
    throw "Copy failed with robocopy code $LASTEXITCODE."
}
& sc.exe start XtraPOSBranchAgent
Start-Sleep -Seconds 5
& sc.exe query XtraPOSBranchAgent
