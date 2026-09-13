#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$WorkerUser = 'samuel',
    [switch]$RunTests,
    [switch]$VerifyOnly,
    [switch]$SkipCodexUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param([string]$File, [string[]]$Arguments = @(), [string]$WorkingDirectory)
    $previous = $null
    try {
        if ($WorkingDirectory) { $previous = Get-Location; Set-Location -LiteralPath $WorkingDirectory }
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $File $($Arguments -join ' ')" }
    }
    finally { if ($null -ne $previous) { Set-Location -LiteralPath $previous } }
}

function Test-IsRoot { return [int]((& id -u | Out-String).Trim()) -eq 0 }

function Get-WorkerHome {
    $entry = (& getent passwd $WorkerUser | Out-String).Trim()
    if (-not $entry) { throw "Linux user '$WorkerUser' does not exist." }
    return $entry.Split(':')[5]
}

function Invoke-Root {
    param([string]$File, [string[]]$Arguments = @())
    if (Test-IsRoot) { Invoke-Native $File $Arguments; return }
    if (-not (Get-Command sudo -ErrorAction SilentlyContinue)) { throw 'Root privileges or sudo are required.' }
    Invoke-Native sudo (@($File) + $Arguments)
}

function Invoke-AsWorker {
    param([string]$File, [string[]]$Arguments = @(), [string]$WorkingDirectory)
    $currentUser = (& id -un | Out-String).Trim()
    if ($currentUser -eq $WorkerUser) { Invoke-Native $File $Arguments $WorkingDirectory; return }
    if (-not (Test-IsRoot)) { throw "Cannot run worker command as '$WorkerUser' from '$currentUser'." }
    $workerHome = Get-WorkerHome
    $workerPath = "$workerHome/.local/bin:$workerHome/bin:/usr/local/bin:/usr/bin:/bin"
    $previous = $null
    try {
        if ($WorkingDirectory) { $previous = Get-Location; Set-Location -LiteralPath $WorkingDirectory }
        Invoke-Native runuser (@('-u', $WorkerUser, '--', 'env', "HOME=$workerHome", "PATH=$workerPath", $File) + $Arguments)
    }
    finally { if ($null -ne $previous) { Set-Location -LiteralPath $previous } }
}

function Test-WorkerCommand {
    param([string]$Command)
    $workerHome = Get-WorkerHome
    $workerPath = "$workerHome/.local/bin:$workerHome/bin:/usr/local/bin:/usr/bin:/bin"
    if ((& id -un | Out-String).Trim() -eq $WorkerUser) {
        & env "HOME=$workerHome" "PATH=$workerPath" bash -lc $Command *> $null
    } elseif (Test-IsRoot) {
        & runuser -u $WorkerUser -- env "HOME=$workerHome" "PATH=$workerPath" bash -lc $Command *> $null
    } else { return $false }
    return $LASTEXITCODE -eq 0
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$config = Get-Content (Join-Path $PSScriptRoot 'worker.json') -Raw | ConvertFrom-Json
if ([int]$config.schemaVersion -ne 1) { throw "Unsupported worker schema: $($config.schemaVersion)" }

if (-not (Test-Path /etc/os-release)) { throw 'This bootstrap supports Linux workers only.' }
$os = @{}
Get-Content /etc/os-release | ForEach-Object {
    if ($_ -match '^(?<key>[A-Z0-9_]+)=(?<value>.*)$') { $os[$Matches.key] = $Matches.value.Trim('"', "'") }
}
if ($os.ID -ne $config.platform.os -or $os.VERSION_ID.Split('.')[0] -ne [string]$config.platform.majorVersion) {
    throw "Expected $($config.platform.os) $($config.platform.majorVersion); found $($os.ID) $($os.VERSION_ID)."
}
Get-WorkerHome | Out-Null

if (-not $VerifyOnly -and @($config.dependencies.aptPackages).Count -gt 0) {
    Invoke-Root apt-get @('update')
    Invoke-Root apt-get (@('install', '-y') + @($config.dependencies.aptPackages))
}

$globalJson = Get-Content (Join-Path $repoRoot $config.dependencies.dotnet.globalJson) -Raw | ConvertFrom-Json
$sdkVersion = [string]$globalJson.sdk.version
$installedSdks = @()
$dotnetCommand = if (Test-Path -LiteralPath '/usr/local/bin/dotnet' -PathType Leaf) {
    '/usr/local/bin/dotnet'
} else {
    $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) { $command.Source } else { $null }
}
if (-not [string]::IsNullOrWhiteSpace($dotnetCommand)) {
    $installedSdks = @(& $dotnetCommand --list-sdks 2>$null | ForEach-Object { ($_ -split '\s+')[0] })
}
if ($installedSdks -notcontains $sdkVersion) {
    if ($VerifyOnly) { throw ".NET SDK $sdkVersion is not installed." }
    $installer = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$([guid]::NewGuid().ToString('N')).sh"
    try {
        Invoke-Native curl @('-fsSL', 'https://dot.net/v1/dotnet-install.sh', '-o', $installer)
        Invoke-Root mkdir @('-p', '/usr/local/share/dotnet')
        Invoke-Root bash @($installer, '--version', $sdkVersion, '--install-dir', '/usr/local/share/dotnet', '--no-path')
        Invoke-Root ln @('-sfn', '/usr/local/share/dotnet/dotnet', '/usr/local/bin/dotnet')
        $dotnetCommand = '/usr/local/bin/dotnet'
    } finally { Remove-Item $installer -Force -ErrorAction SilentlyContinue }
}

$hasCodex = Test-WorkerCommand 'command -v codex >/dev/null 2>&1'
if (-not $hasCodex -and $VerifyOnly) { throw "Codex CLI is not installed for '$WorkerUser'." }
if (-not $VerifyOnly -and (-not $hasCodex -or (-not $SkipCodexUpdate -and $config.dependencies.codexCli.updateOnBootstrap))) {
    $installer = Join-Path ([IO.Path]::GetTempPath()) "codex-install-$([guid]::NewGuid().ToString('N')).sh"
    try {
        Invoke-Native curl @('-fsSL', 'https://chatgpt.com/codex/install.sh', '-o', $installer)
        Invoke-Root chmod @('0755', $installer)
        Invoke-AsWorker sh @($installer)
    } finally { Remove-Item $installer -Force -ErrorAction SilentlyContinue }
}
if (-not (Test-WorkerCommand 'command -v codex >/dev/null 2>&1')) { throw "Codex CLI is unavailable for '$WorkerUser' after bootstrap." }

foreach ($command in @($config.dependencies.requiredCommands)) {
    if (-not (Get-Command ([string]$command) -ErrorAction SilentlyContinue)) { throw "Required command '$command' is unavailable." }
}

if ([string]::IsNullOrWhiteSpace($dotnetCommand)) { throw 'The .NET SDK was ensured, but no dotnet executable is available.' }
Write-Host "Worker toolchain ready for $($config.name): .NET $(& $dotnetCommand --version), Codex installed." -ForegroundColor Green
if (-not $VerifyOnly) {
    foreach ($command in @($config.commands.bootstrap)) { Invoke-AsWorker ([string]$command.file) @($command.args) $repoRoot }
    if ($RunTests) { foreach ($command in @($config.commands.test)) { Invoke-AsWorker ([string]$command.file) @($command.args) $repoRoot } }
}

if (Test-WorkerCommand 'codex login status >/dev/null 2>&1') {
    Write-Host 'CODEX_WORKER_STATUS=ready'
} else {
    Write-Warning "Codex authentication is required. Run 'codex login --device-auth' as '$WorkerUser'."
    Write-Host 'CODEX_WORKER_STATUS=ready-needs-codex-auth'
}
