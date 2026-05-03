param(
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\publish\$Runtime"
}

dotnet publish (Join-Path $repoRoot "src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj") `
    -c $Configuration `
    -r $Runtime `
    -o $OutputDirectory `
    /p:PublishSingleFile=true `
    /p:SelfContained=true

Write-Host "Published Incursa.Codex.Telegram to $OutputDirectory"
