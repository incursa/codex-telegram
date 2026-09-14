param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $OutputPath = ""
)

$ErrorActionPreference = "Stop"

function Get-RelativeArchivePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $relative = [System.IO.Path]::GetRelativePath($Root, $Path)
    return $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/').Replace([System.IO.Path]::AltDirectorySeparatorChar, '/')
}

function Get-ArchiveEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArchivePath
    )

    $tarCommand = Get-Command tar -ErrorAction Stop
    $entries = @(& $tarCommand.Source -tzf $ArchivePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list archive contents: $ArchivePath"
    }

    return @($entries | ForEach-Object { $_.ToString().Trim().Replace('\', '/') } | Where-Object { $_ })
}

$publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$webRoot = Join-Path $publishRoot "wwwroot"

if (-not (Test-Path -LiteralPath $webRoot -PathType Container)) {
    throw "Published webroot directory not found: $webRoot"
}

$indexPath = Join-Path $webRoot "index.html"
if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
    throw "Published webroot must contain wwwroot/index.html: $indexPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $publishRoot "codex-telegram-linux-x64-webroot.tar.gz"
}

$archivePath = [System.IO.Path]::GetFullPath($OutputPath)
$archiveParent = Split-Path -Parent $archivePath
New-Item -ItemType Directory -Force -Path $archiveParent | Out-Null

$tarCommand = Get-Command tar -ErrorAction Stop
& $tarCommand.Source -czf $archivePath -C $publishRoot wwwroot
if ($LASTEXITCODE -ne 0) {
    throw "Could not create webroot archive: $archivePath"
}

$expectedFiles = @(
    Get-ChildItem -LiteralPath $webRoot -File -Recurse |
        ForEach-Object { Get-RelativeArchivePath -Root $publishRoot -Path $_.FullName }
)
$expectedDirectories = @(
    "wwwroot"
    Get-ChildItem -LiteralPath $webRoot -Directory -Recurse |
        ForEach-Object { Get-RelativeArchivePath -Root $publishRoot -Path $_.FullName }
)
$archiveEntries = @(Get-ArchiveEntries -ArchivePath $archivePath)

$invalidEntries = @($archiveEntries | Where-Object {
        $_ -ne "wwwroot" -and
        $_ -ne "wwwroot/" -and
        (-not $_.StartsWith("wwwroot/", [System.StringComparison]::Ordinal) -or $_.Contains("../") -or $_.StartsWith("/", [System.StringComparison]::Ordinal))
    })
if ($invalidEntries.Count -gt 0) {
    throw "Webroot archive contains paths outside wwwroot/: $($invalidEntries -join ', ')"
}

$normalizedArchiveEntries = @($archiveEntries | ForEach-Object { $_.TrimEnd('/') } | Select-Object -Unique)
$missingFiles = @($expectedFiles | Where-Object { $_ -notin $normalizedArchiveEntries })
if ($missingFiles.Count -gt 0) {
    throw "Webroot archive is missing published files: $($missingFiles -join ', ')"
}

$unexpectedFiles = @($normalizedArchiveEntries | Where-Object { $_ -notin $expectedFiles -and $_ -notin $expectedDirectories })
if ($unexpectedFiles.Count -gt 0) {
    throw "Webroot archive contains files that were not in the published webroot: $($unexpectedFiles -join ', ')"
}

if ($normalizedArchiveEntries -notcontains "wwwroot/index.html") {
    throw "Webroot archive does not contain wwwroot/index.html."
}

Write-Host "Created $archivePath"
Write-Host "Archived $($expectedFiles.Count) published webroot files."
Write-Host "Archive contains wwwroot/index.html and only published wwwroot paths."
