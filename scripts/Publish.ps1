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
    /p:AssemblyName=codex-telegram `
    /p:PublishSingleFile=true `
    /p:SelfContained=true

$binary = Get-ChildItem -Path $OutputDirectory -File |
    Where-Object { $_.BaseName -eq "codex-telegram" -and ($_.Extension -eq ".exe" -or $_.Extension -eq "") } |
    Select-Object -First 1

if (-not $binary) {
    throw "Published binary not found in $OutputDirectory."
}

$checksumPath = "$($binary.FullName).sha256"
$hash = Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $($binary.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

$licenseSource = Join-Path $repoRoot "LICENSE"
if (Test-Path -LiteralPath $licenseSource) {
    Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $OutputDirectory "LICENSE.txt") -Force
}

Write-Host "Published codex-telegram to $OutputDirectory"
Write-Host "Wrote SHA256 checksum to $checksumPath"
