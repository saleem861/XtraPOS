# Checks a staging URL. Does nothing when STAGING_WEB_URL is empty.
$ErrorActionPreference = "Stop"
$url = $env:STAGING_WEB_URL
if (-not $url) {
    Write-Host "STAGING_WEB_URL is not set. Staging is not verified."
    exit 2
}
$web = curl.exe -fsS -o NUL -w "%{http_code}" --max-time 20 $url
$health = curl.exe -fsS --max-time 20 "$url/health"
Write-Host "Web HTTP $web"
Write-Host $health
if ($web -ne "200" -or $health -notmatch "Healthy") { throw "Staging check failed." }
Write-Host "Staging responded."
