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

function Add-RestartManagerType {
    if ("RestartManagerMethods" -as [type]) {
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct RM_UNIQUE_PROCESS
{
    public int dwProcessId;
    public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
}

public enum RM_APP_TYPE
{
    RmUnknownApp = 0,
    RmMainWindow = 1,
    RmOtherWindow = 2,
    RmService = 3,
    RmExplorer = 4,
    RmConsole = 5,
    RmCritical = 1000
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct RM_PROCESS_INFO
{
    public RM_UNIQUE_PROCESS Process;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string strAppName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string strServiceShortName;

    public RM_APP_TYPE ApplicationType;
    public uint AppStatus;
    public uint TSSessionId;

    [MarshalAs(UnmanagedType.Bool)]
    public bool bRestartable;
}

public static class RestartManagerMethods
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint pSessionHandle);
}
"@
}

function Get-RestartManagerLockingProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        return
    }

    Add-RestartManagerType

    $sessionHandle = [uint32] 0
    $sessionKey = [Guid]::NewGuid().ToString("N")
    $result = [RestartManagerMethods]::RmStartSession([ref] $sessionHandle, 0, $sessionKey)
    if ($result -ne 0) {
        return
    }

    try {
        $files = [string[]] @((Get-NormalizedPath -Path $TargetPath))
        $result = [RestartManagerMethods]::RmRegisterResources($sessionHandle, [uint32] $files.Length, $files, 0, $null, 0, $null)
        if ($result -ne 0) {
            return
        }

        $needed = [uint32] 0
        $count = [uint32] 0
        $rebootReasons = [uint32] 0
        $result = [RestartManagerMethods]::RmGetList($sessionHandle, [ref] $needed, [ref] $count, $null, [ref] $rebootReasons)
        if ($result -ne 234 -or $needed -eq 0) {
            return
        }

        $count = $needed
        $processInfo = New-Object "RM_PROCESS_INFO[]" $count
        $result = [RestartManagerMethods]::RmGetList($sessionHandle, [ref] $needed, [ref] $count, $processInfo, [ref] $rebootReasons)
        if ($result -ne 0) {
            return
        }

        for ($index = 0; $index -lt $count; $index++) {
            $info = $processInfo[$index]
            $process = Get-Process -Id $info.Process.dwProcessId -ErrorAction SilentlyContinue
            if ($process) {
                $process
            }
            else {
                [PSCustomObject]@{
                    Id = [int] $info.Process.dwProcessId
                    ProcessName = $info.strAppName
                    Path = $null
                }
            }
        }
    }
    finally {
        [void] [RestartManagerMethods]::RmEndSession($sessionHandle)
    }
}

function Get-RunningPublishProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    $target = Get-NormalizedPath -Path $TargetPath
    $matchedProcessIds = [System.Collections.Generic.HashSet[int]]::new()

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $processPath = $null
        try {
            $processPath = $process.Path
        }
        catch {
            $processPath = $null
        }

        if (-not [string]::IsNullOrWhiteSpace($processPath) -and
            [string]::Equals((Get-NormalizedPath -Path $processPath), $target, [System.StringComparison]::OrdinalIgnoreCase)) {
            [void] $matchedProcessIds.Add($process.Id)
            $process
        }
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT -and
        (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
                    [string]::Equals((Get-NormalizedPath -Path $_.ExecutablePath), $target, [System.StringComparison]::OrdinalIgnoreCase) -and
                    -not $matchedProcessIds.Contains([int] $_.ProcessId)
            } |
            ForEach-Object {
                [void] $matchedProcessIds.Add([int] $_.ProcessId)
                $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                if ($process) {
                    $process
                }
                else {
                    [PSCustomObject]@{
                        Id = [int] $_.ProcessId
                        ProcessName = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
                        Path = $_.ExecutablePath
                    }
                }
            }
    }

    foreach ($process in Get-RestartManagerLockingProcess -TargetPath $TargetPath) {
        if (-not $matchedProcessIds.Contains([int] $process.Id)) {
            [void] $matchedProcessIds.Add([int] $process.Id)
            $process
        }
    }
}

function Format-RunningPublishProcessList {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Processes
    )

    return ($Processes | ForEach-Object {
            $label = "PID $($_.Id)"
            if (-not [string]::IsNullOrWhiteSpace($_.ProcessName)) {
                $label = "$label ($($_.ProcessName))"
            }

            $label
        }) -join ", "
}

function New-PublishTargetRunningMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetPath,

        [Parameter(Mandatory = $true)]
        [object[]] $Processes
    )

    $processList = Format-RunningPublishProcessList -Processes $Processes
    return "Cannot publish because $TargetPath is currently running ($processList). Stop that process first, or rerun this script with -StopRunningProcess to stop only the process launched from this publish target."
}

function New-PublishTargetLockedMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetPath,

        [Parameter(Mandatory = $true)]
        [object[]] $Processes
    )

    $processList = Format-RunningPublishProcessList -Processes $Processes
    return "Cannot publish because $TargetPath is locked by $processList, but none of those processes matched the publish target executable name or path. Stop the locking process and rerun the publish command."
}

function Test-PublishTargetProcessCanStop {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Process,

        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    $target = Get-NormalizedPath -Path $TargetPath
    $targetProcessName = [System.IO.Path]::GetFileNameWithoutExtension($TargetPath)

    $processPath = $null
    try {
        $processPath = $Process.Path
    }
    catch {
        $processPath = $null
    }

    if (-not [string]::IsNullOrWhiteSpace($processPath) -and
        [string]::Equals((Get-NormalizedPath -Path $processPath), $target, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    if (-not [string]::IsNullOrWhiteSpace($Process.ProcessName) -and
        [string]::Equals($Process.ProcessName, $targetProcessName, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT -and
        (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
        $cimProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction SilentlyContinue
        if ($cimProcess) {
            if (-not [string]::IsNullOrWhiteSpace($cimProcess.ExecutablePath) -and
                [string]::Equals((Get-NormalizedPath -Path $cimProcess.ExecutablePath), $target, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }

        }
    }

    return $false
}

function Stop-PublishTargetProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Processes,

        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    $stoppableProcesses = @($Processes | Where-Object { Test-PublishTargetProcessCanStop -Process $_ -TargetPath $TargetPath })
    if ($stoppableProcesses.Count -eq 0) {
        throw (New-PublishTargetLockedMessage -TargetPath $TargetPath -Processes $Processes)
    }

    foreach ($process in $stoppableProcesses) {
        Write-Host "Stopping running publish target PID $($process.Id): $($process.Path)"
        try {
            Stop-Process -Id $process.Id -ErrorAction Stop
        }
        catch {
            throw "Cannot stop publish target PID $($process.Id). $($_.Exception.Message) If this process was started from an elevated console, stop it from that elevated console or run this publish script elevated."
        }

        try {
            Wait-Process -Id $process.Id -Timeout 15 -ErrorAction Stop
        }
        catch {
            if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
                throw
            }
        }
    }
}

function Remove-PublishFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $PublishTargetPath,

        [switch] $StopRunningProcess
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    try {
        Remove-Item -LiteralPath $Path -Force
    }
    catch {
        $runningProcesses = @(Get-RunningPublishProcess -TargetPath $PublishTargetPath)
        if ($runningProcesses.Count -gt 0 -and $StopRunningProcess) {
            Stop-PublishTargetProcesses -Processes $runningProcesses -TargetPath $PublishTargetPath
            Remove-Item -LiteralPath $Path -Force
            return
        }

        if ($runningProcesses.Count -gt 0) {
            throw (New-PublishTargetRunningMessage -TargetPath $PublishTargetPath -Processes $runningProcesses)
        }

        throw "Cannot remove $Path. $($_.Exception.Message)"
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
        throw (New-PublishTargetRunningMessage -TargetPath $targetBinaryPath -Processes $runningProcesses)
    }

    Stop-PublishTargetProcesses -Processes $runningProcesses -TargetPath $targetBinaryPath
}

try {
    Remove-PublishFile -Path $targetBinaryPath -PublishTargetPath $targetBinaryPath -StopRunningProcess:$StopRunningProcess

    $targetChecksumPath = "$targetBinaryPath.sha256"
    Remove-PublishFile -Path $targetChecksumPath -PublishTargetPath $targetBinaryPath -StopRunningProcess:$StopRunningProcess

    dotnet publish (Join-Path $repoRoot "src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj") `
        -c $Configuration `
        -r $Runtime `
        -o $OutputDirectory `
        /p:PublishSingleFile=true `
        /p:SelfContained=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $defaultBinaryName = if ($Runtime -like "win-*") { "Incursa.Codex.Telegram.exe" } else { "Incursa.Codex.Telegram" }
    $defaultBinaryPath = Join-Path $OutputDirectory $defaultBinaryName
    if (Test-Path -LiteralPath $defaultBinaryPath) {
        Move-Item -LiteralPath $defaultBinaryPath -Destination $targetBinaryPath -Force
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
