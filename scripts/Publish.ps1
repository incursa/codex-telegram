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

$settingsFileName = "appsettings.Local.json"
$outputSettingsPath = Join-Path $OutputDirectory $settingsFileName
$repoSettingsPath = Join-Path $repoRoot $settingsFileName
$settingsBackupPath = $null
$settingsSourcePath = $null

if (Test-Path -LiteralPath $outputSettingsPath) {
    $settingsBackupPath = Join-Path ([System.IO.Path]::GetTempPath()) "codex-telegram-appsettings-$([Guid]::NewGuid().ToString("N")).json"
    Copy-Item -LiteralPath $outputSettingsPath -Destination $settingsBackupPath -Force
    $settingsSourcePath = $settingsBackupPath
}
elseif (Test-Path -LiteralPath $repoSettingsPath) {
    $settingsSourcePath = $repoSettingsPath
}

try {
    dotnet publish (Join-Path $repoRoot "src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj") `
        -c $Configuration `
        -r $Runtime `
        -o $OutputDirectory `
        /p:AssemblyName=codex-telegram `
        /p:PublishSingleFile=true `
        /p:SelfContained=true

    if ($settingsSourcePath) {
        Copy-Item -LiteralPath $settingsSourcePath -Destination $outputSettingsPath -Force
        Write-Host "Wrote local settings to $outputSettingsPath"
    }

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
}
finally {
    if ($settingsBackupPath -and (Test-Path -LiteralPath $settingsBackupPath)) {
        Remove-Item -LiteralPath $settingsBackupPath -Force
    }
}

Write-Host "Published codex-telegram to $OutputDirectory"
Write-Host "Wrote SHA256 checksum to $checksumPath"
