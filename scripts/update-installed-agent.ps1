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
& robocopy $source $dest /E /XD App_Data /R:1 /W:1
if ($LASTEXITCODE -ge 8) {
    throw "Copy failed with robocopy code $LASTEXITCODE."
}
& sc.exe start XtraPOSBranchAgent
Start-Sleep -Seconds 5
$exe = Join-Path $dest "XtraPOS.Api.exe"
Push-Location $dest
try {
    & $exe --apply-branch-migrations --confirm-database XtraPOS_Branch
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Branch migrations were not applied. The service is running."
    }
}
finally {
    Pop-Location
}
& sc.exe query XtraPOSBranchAgent
