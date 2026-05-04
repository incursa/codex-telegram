param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repoRoot "src\Incursa.Codex.Telegram"

Push-Location $repoRoot
try {
    dotnet tool restore
}
finally {
    Pop-Location
}

Push-Location $sourceDirectory
try {
    dotnet stryker `
        --config-file "stryker-config.json" `
        --configuration $Configuration

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet stryker failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
