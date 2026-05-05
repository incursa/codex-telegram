param(
    [string]$Configuration = "Release",
    [ValidateSet("core", "handler", "queue", "all")]
    [string]$Profile = "core"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repoRoot "src\Incursa.Codex.Telegram"
$configByProfile = @{
    core = "stryker-config.json"
    handler = "stryker-handler-config.json"
    queue = "stryker-queue-config.json"
}

Push-Location $repoRoot
try {
    dotnet tool restore
}
finally {
    Pop-Location
}

Push-Location $sourceDirectory
try {
    $profiles = if ($Profile -eq "all") { @("core", "handler", "queue") } else { @($Profile) }
    foreach ($selectedProfile in $profiles) {
        $configFile = $configByProfile[$selectedProfile]
        Write-Host "Running Telegram mutation profile '$selectedProfile' with $configFile."
        dotnet stryker `
            --config-file $configFile `
            --configuration $Configuration

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet stryker profile '$selectedProfile' failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}
