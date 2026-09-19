param(
    [string] $Configuration = "Release",
    [string] $Version = "",
    [string] $OutputDirectory = "",
    [string] $Architecture = "amd64"
)

$ErrorActionPreference = "Stop"

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Find-PublishedBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string[]] $Names
    )

    $binary = Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -in $Names } |
        Select-Object -First 1
    if (-not $binary) {
        throw "Published binary was not found in $Directory. Expected one of: $($Names -join ', ')."
    }

    return $binary
}

if (-not $IsLinux) {
    throw "Debian package builds must run on Linux."
}

if (-not (Get-Command dpkg-deb -ErrorAction SilentlyContinue)) {
    throw "dpkg-deb is required to build the Debian package."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src/Incursa.Codex.Telegram/Incursa.Codex.Telegram.csproj"
$updaterProjectPath = Join-Path $repoRoot "src/Incursa.Codex.Telegram.Updater/Incursa.Codex.Telegram.Updater.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = Get-Content -LiteralPath $projectPath -Raw
    $versionMatch = [System.Text.RegularExpressions.Regex]::Match($projectXml, '<Version>(?<version>[0-9A-Za-z.+~-]+)</Version>')
    if (-not $versionMatch.Success) {
        throw "Could not determine the package version from $projectPath."
    }

    $Version = $versionMatch.Groups["version"].Value
}

if ($Version -notmatch '^[0-9A-Za-z.+~_-]+$') {
    throw "Invalid Debian package version: $Version"
}

if ($Architecture -notin @("amd64", "arm64")) {
    throw "Supported Debian architectures are amd64 and arm64."
}

$runtime = if ($Architecture -eq "arm64") { "linux-arm64" } else { "linux-x64" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/debian"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "codex-telegram-deb-$([Guid]::NewGuid().ToString('N'))"
$publishRoot = Join-Path $temporaryRoot "publish"
$appPublish = Join-Path $publishRoot "app"
$updaterPublish = Join-Path $publishRoot "updater"
$packageRoot = Join-Path $temporaryRoot "package"
$debianControl = Join-Path $packageRoot "DEBIAN"
$installRoot = Join-Path $packageRoot "usr/lib/codex-telegram"
$binRoot = Join-Path $packageRoot "usr/bin"
$systemdRoot = Join-Path $packageRoot "usr/lib/systemd/system"
$etcRoot = Join-Path $packageRoot "etc/codex-telegram"
$docRoot = Join-Path $packageRoot "usr/share/doc/codex-telegram"

try {
    New-Item -ItemType Directory -Force -Path $appPublish, $updaterPublish, $debianControl, $installRoot, $binRoot, $systemdRoot, $etcRoot, $docRoot, $OutputDirectory | Out-Null

    Invoke-RequiredCommand -FilePath "dotnet" -ArgumentList @(
        "publish",
        $projectPath,
        "-c", $Configuration,
        "-r", $runtime,
        "-o", $appPublish,
        "/p:PublishSingleFile=true",
        "/p:SelfContained=true",
        "/p:Version=$Version"
    )
    Invoke-RequiredCommand -FilePath "dotnet" -ArgumentList @(
        "publish",
        $updaterProjectPath,
        "-c", $Configuration,
        "-r", $runtime,
        "-o", $updaterPublish,
        "/p:PublishSingleFile=true",
        "/p:SelfContained=true",
        "/p:Version=$Version"
    )

    $appBinary = Find-PublishedBinary -Directory $appPublish -Names @("Incursa.Codex.Telegram", "codex-telegram")
    $updaterBinary = Find-PublishedBinary -Directory $updaterPublish -Names @("codex-telegram-updater")
    Copy-Item -LiteralPath $appBinary.FullName -Destination (Join-Path $installRoot "codex-telegram")
    Copy-Item -LiteralPath $updaterBinary.FullName -Destination (Join-Path $installRoot "codex-telegram-updater")
    Copy-Item -LiteralPath (Join-Path $appPublish "wwwroot") -Destination $installRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot "src/Incursa.Codex.Telegram/appsettings.json") -Destination (Join-Path $installRoot "appsettings.json")

    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/codex-telegram.service") -Destination $systemdRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/codex-telegram-updater.service") -Destination $systemdRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/codex-telegram-updater.path") -Destination $systemdRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/updater.json") -Destination $etcRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/appsettings.Local.json") -Destination $etcRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/codex-telegram-wrapper") -Destination (Join-Path $binRoot "codex-telegram")
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $docRoot "copyright")

    $control = @"
Package: codex-telegram
Version: $Version
Section: admin
Priority: optional
Architecture: $Architecture
Maintainer: Incursa <oss@incursa.com>
Depends: adduser, apt, ca-certificates, dpkg, systemd
Description: Telegram host for a local Codex installation
 Incursa Codex Telegram provides a local Telegram bot host for an operator-owned
 Codex installation. The package includes a separate root-owned updater service
 that can apply and verify package updates requested from an authorized chat.
"@
    Set-Content -LiteralPath (Join-Path $debianControl "control") -Value $control -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/postinst") -Destination $debianControl
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/prerm") -Destination $debianControl
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/postrm") -Destination $debianControl
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging/debian/conffiles") -Destination $debianControl
    Invoke-RequiredCommand -FilePath "chmod" -ArgumentList @(
        "+x",
        (Join-Path $debianControl "postinst"),
        (Join-Path $debianControl "prerm"),
        (Join-Path $debianControl "postrm"),
        (Join-Path $installRoot "codex-telegram"),
        (Join-Path $installRoot "codex-telegram-updater"),
        (Join-Path $binRoot "codex-telegram")
    )

    $packagePath = Join-Path $OutputDirectory "codex-telegram_${Version}_${Architecture}.deb"
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    Invoke-RequiredCommand -FilePath "dpkg-deb" -ArgumentList @("--build", "--root-owner-group", $packageRoot, $packagePath)
    Write-Host "Built $packagePath"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
