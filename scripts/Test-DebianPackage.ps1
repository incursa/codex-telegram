param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = "Stop"

if (-not $IsLinux) {
    throw "Debian package smoke tests must run on Linux."
}
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Debian package not found: $PackagePath"
}
if (-not (Get-Command dpkg-deb -ErrorAction SilentlyContinue)) {
    throw "dpkg-deb is required for Debian package smoke tests."
}

$packageInfo = & dpkg-deb --info $PackagePath 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "dpkg-deb could not inspect $PackagePath.`n$packageInfo"
}

$requiredPaths = @(
    "./usr/lib/codex-telegram/codex-telegram",
    "./usr/lib/codex-telegram/codex-telegram-updater",
    "./usr/bin/codex-telegram",
    "./usr/lib/codex-telegram/wwwroot/index.html",
    "./etc/codex-telegram/appsettings.Local.json",
    "./etc/codex-telegram/updater.json",
    "./usr/lib/systemd/system/codex-telegram.service",
    "./usr/lib/systemd/system/codex-telegram-updater.service",
    "./usr/lib/systemd/system/codex-telegram-updater.path"
)
$contents = @(& dpkg-deb --contents $PackagePath)
if ($LASTEXITCODE -ne 0) {
    throw "dpkg-deb could not list $PackagePath."
}
foreach ($requiredPath in $requiredPaths) {
    if (-not ($contents -match [System.Text.RegularExpressions.Regex]::Escape($requiredPath) + '$')) {
        throw "Debian package is missing $requiredPath."
    }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "codex-telegram-deb-test-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
    & dpkg-deb --extract $PackagePath $temporaryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dpkg-deb could not extract $PackagePath."
    }

    $updater = Join-Path $temporaryRoot "usr/lib/codex-telegram/codex-telegram-updater"
    & $updater --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The extracted updater did not start successfully."
    }

    foreach ($scriptName in @("postinst", "prerm", "postrm")) {
        $scriptPath = Join-Path $PSScriptRoot "../packaging/debian/$scriptName"
        & sh -n $scriptPath
        if ($LASTEXITCODE -ne 0) {
            throw "Maintainer script failed shell syntax validation: $scriptName"
        }
    }

    Write-Host "Debian package smoke passed: $PackagePath"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
