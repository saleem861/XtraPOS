# Remove local runtime secrets and state from a staging release package.
# This only cleans the package directory. It does not open SSH or touch a server.
function Remove-StagingRuntimeFiles {
    param([Parameter(Mandatory = $true)][string]$ReleaseRoot)

    if ([string]::IsNullOrWhiteSpace($ReleaseRoot) -or -not (Test-Path -LiteralPath $ReleaseRoot -PathType Container)) {
        throw "Release directory was not found."
    }

    $resolved = [System.IO.Path]::GetFullPath($ReleaseRoot)
    if ($resolved -notlike "*\artifacts\staging\*" -and $resolved -notlike "*/artifacts/staging/*") {
        throw "Refusing to clean a directory outside artifacts/staging."
    }

    $exactNames = @(
        "central-connection.json",
        "device-databases.json",
        "sync-status.json",
        "branch-sql-secrets.json",
        "installation-configured",
        "setup-authorization.token",
        "onboarding-authorization.token",
        "secrets.json",
        "dev-sql.env",
        "staging.env",
        "staging.env.local",
        "id_rsa",
        "id_ed25519"
    )

    Get-ChildItem -LiteralPath $resolved -Recurse -Force -File -ErrorAction SilentlyContinue |
        Where-Object {
            $exactNames -contains $_.Name -or
            $_.Name -like "appsettings*.json" -or
            $_.Name -like "key-*.xml" -or
            $_.Name -eq ".env" -or
            $_.Name -like ".env.*" -or
            $_.Extension -in ".pem", ".pfx", ".p12", ".ppk"
        } |
        Remove-Item -Force

    Get-ChildItem -LiteralPath $resolved -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "App_Data" } |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force
}
