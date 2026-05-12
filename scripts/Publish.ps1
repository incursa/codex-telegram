param(
    [string] $Runtime = "win-x64",
    [string] $Configuration = "Release",
    [string] $OutputDirectory = "",
    [switch] $StopRunningProcess
)

$ErrorActionPreference = "Stop"

function Get-NormalizedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Get-RunningPublishProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    $target = Get-NormalizedPath -Path $TargetPath
    $processName = [System.IO.Path]::GetFileNameWithoutExtension($TargetPath)
    Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Where-Object {
            $processPath = $null
            try {
                $processPath = $_.Path
            }
            catch {
                $processPath = $null
            }

            -not [string]::IsNullOrWhiteSpace($processPath) -and
                [string]::Equals((Get-NormalizedPath -Path $processPath), $target, [System.StringComparison]::OrdinalIgnoreCase)
        }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\publish\$Runtime"
}

$targetBinaryName = if ($Runtime -like "win-*") { "codex-telegram.exe" } else { "codex-telegram" }
$targetBinaryPath = Join-Path $OutputDirectory $targetBinaryName

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

$runningProcesses = @(Get-RunningPublishProcess -TargetPath $targetBinaryPath)
if ($runningProcesses.Count -gt 0) {
    if (-not $StopRunningProcess) {
        $processList = ($runningProcesses | ForEach-Object { "PID $($_.Id)" }) -join ", "
        throw "Cannot publish because $targetBinaryPath is currently running ($processList). Stop that process first, or rerun this script with -StopRunningProcess to stop only the process launched from this publish target."
    }

    foreach ($process in $runningProcesses) {
        Write-Host "Stopping running publish target PID $($process.Id): $($process.Path)"
        Stop-Process -Id $process.Id -ErrorAction Stop
        Wait-Process -Id $process.Id -Timeout 15 -ErrorAction Stop
    }
}

try {
    dotnet publish (Join-Path $repoRoot "src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj") `
        -c $Configuration `
        -r $Runtime `
        -o $OutputDirectory `
        /p:AssemblyName=codex-telegram `
        /p:PublishSingleFile=true `
        /p:SelfContained=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

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
