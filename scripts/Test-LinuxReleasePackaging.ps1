param(
    [string] $Configuration = "Release",

    [string] $PublishDirectory = "",

    [string] $ArchivePath = "",

    [string] $WslDistribution = "Ubuntu"
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

function Convert-ToBashSingleQuoted {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $singleQuote = [string][char]39
    $escapedQuote = $singleQuote + [string][char]92 + $singleQuote + $singleQuote
    return $singleQuote + $Value.Replace($singleQuote, $escapedQuote) + $singleQuote
}

function Convert-ToWslPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath.Length -lt 3 -or $fullPath[1] -ne ':') {
        throw "WSL path conversion requires a Windows drive path: $Path"
    }

    $drive = $fullPath.Substring(0, 1).ToLowerInvariant()
    $rest = $fullPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

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

function Find-LinuxBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    $binary = Get-ChildItem -LiteralPath $Directory -File |
        Where-Object {
            $_.Name -in @("codex-telegram-linux-x64", "codex-telegram", "Incursa.Codex.Telegram") -or
            ($_.BaseName -eq "codex-telegram" -and ($_.Extension -eq ".exe" -or [string]::IsNullOrEmpty($_.Extension)))
        } |
        Select-Object -First 1

    if (-not $binary) {
        throw "Linux release binary not found in $Directory"
    }

    return $binary
}

$temporaryRoot = $null
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "codex-telegram-linux-release-$([Guid]::NewGuid().ToString('N'))"
    $PublishDirectory = Join-Path $temporaryRoot "publish"
    New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $projectPath = Join-Path $repoRoot "src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj"
    Invoke-RequiredCommand -FilePath "dotnet" -ArgumentList @(
        "publish",
        $projectPath,
        "-c", $Configuration,
        "-r", "linux-x64",
        "-o", $PublishDirectory,
        "/p:PublishSingleFile=true",
        "/p:SelfContained=true"
    )

    $publishedBinary = Find-LinuxBinary -Directory $PublishDirectory
    $releaseBinaryPath = Join-Path $PublishDirectory "codex-telegram-linux-x64"
    if ($publishedBinary.FullName -ne $releaseBinaryPath) {
        Move-Item -LiteralPath $publishedBinary.FullName -Destination $releaseBinaryPath -Force
    }

    $checksum = Get-FileHash -LiteralPath $releaseBinaryPath -Algorithm SHA256
    "$($checksum.Hash.ToLowerInvariant())  codex-telegram-linux-x64" | Set-Content -LiteralPath "$releaseBinaryPath.sha256" -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $PublishDirectory "codex-telegram-linux-x64-LICENSE.txt") -Force
}

try {
    $PublishDirectory = Get-NormalizedPath -Path $PublishDirectory
    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
        throw "Publish directory not found: $PublishDirectory"
    }

    $binary = Find-LinuxBinary -Directory $PublishDirectory
    $releaseBinaryPath = Join-Path $PublishDirectory "codex-telegram-linux-x64"
    if ($binary.Name -ne "codex-telegram-linux-x64") {
        Copy-Item -LiteralPath $binary.FullName -Destination $releaseBinaryPath -Force
        $binary = Get-Item -LiteralPath $releaseBinaryPath
    }

    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $ArchivePath = Join-Path $PublishDirectory "codex-telegram-linux-x64-webroot.tar.gz"
    }
    else {
        $ArchivePath = Get-NormalizedPath -Path $ArchivePath
    }

    & (Join-Path $PSScriptRoot "New-LinuxWebRootArchive.ps1") -PublishDirectory $PublishDirectory -OutputPath $ArchivePath
    if ($LASTEXITCODE -ne 0) {
        throw "New-LinuxWebRootArchive.ps1 failed with exit code $LASTEXITCODE."
    }

    $checksumPath = "$releaseBinaryPath.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        $hash = Get-FileHash -LiteralPath $releaseBinaryPath -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  codex-telegram-linux-x64" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    }

    $checksumLine = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $actualHash = (Get-FileHash -LiteralPath $releaseBinaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $checksumLine.StartsWith("$actualHash  codex-telegram-linux-x64", [System.StringComparison]::Ordinal)) {
        throw "Linux release checksum does not match codex-telegram-linux-x64."
    }

    $licensePath = Join-Path $PublishDirectory "codex-telegram-linux-x64-LICENSE.txt"
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) "LICENSE") -Destination $licensePath -Force
    }

    if ($IsWindows) {
        $wslBinaryPath = Convert-ToWslPath -Path $releaseBinaryPath
        $wslArchivePath = Convert-ToWslPath -Path $ArchivePath
        $serviceUser = (& wsl.exe -d $WslDistribution -- bash -lc "id -un" 2>$null | Select-Object -Last 1).ToString().Trim()
        if ([string]::IsNullOrWhiteSpace($serviceUser)) {
            throw "Could not determine the WSL non-root service user."
        }
    }
    elseif ($IsLinux) {
        $wslBinaryPath = $releaseBinaryPath
        $wslArchivePath = $ArchivePath
        $serviceUser = (& id -un).Trim()
    }
    else {
        throw "Linux release packaging smoke requires Linux or Windows with WSL."
    }

    $linuxCommand = @'
set -euo pipefail
source_binary=__SOURCE_BINARY__
source_archive=__SOURCE_ARCHIVE__
service_user=__SERVICE_USER__

if [ "$(id -u "$service_user")" -eq 0 ]; then
    echo "The service user must be non-root: $service_user" >&2
    exit 1
fi

if [ "$(id -u)" -eq 0 ]; then
    SUDO=()
else
    SUDO=(sudo -n)
fi

command -v systemd-run >/dev/null
command -v systemctl >/dev/null
command -v curl >/dev/null
command -v tar >/dev/null

smoke_root="$(mktemp -d /tmp/codex-telegram-linux-install.XXXXXX)"
install_root="$smoke_root/install"
data_root="$smoke_root/data"
port="${CODEX_TELEGRAM_SMOKE_PORT:-5287}"
unit="codex-telegram-linux-packaging-$RANDOM"
chmod 0755 "$smoke_root"

cleanup() {
    "${SUDO[@]}" systemctl stop "$unit" >/dev/null 2>&1 || true
    "${SUDO[@]}" systemctl reset-failed "$unit" >/dev/null 2>&1 || true
    rm -rf "$smoke_root"
}
trap cleanup EXIT

install -d -m 0755 "$install_root"
install -d -m 0750 -o "$service_user" -g "$(id -gn "$service_user")" "$data_root"
test ! -e "$install_root/wwwroot"
cp -- "$source_binary" "$install_root/codex-telegram"
cp -- "$source_archive" "$install_root/codex-telegram-linux-x64-webroot.tar.gz"
chmod 0555 "$install_root/codex-telegram"
tar -xzf "$install_root/codex-telegram-linux-x64-webroot.tar.gz" -C "$install_root"
rm -f "$install_root/codex-telegram-linux-x64-webroot.tar.gz"
chmod -R a=rX "$install_root/wwwroot"
test -f "$install_root/wwwroot/index.html"

before_tree="$(find "$install_root/wwwroot" -type f -printf '%P:%s:%T@\n' | sort)"

"${SUDO[@]}" systemd-run \
    --unit="$unit" \
    --collect \
    --property=User="$service_user" \
    --property=Group="$(id -gn "$service_user")" \
    --property=WorkingDirectory="$install_root" \
    --property=ProtectSystem=strict \
    --property=ReadWritePaths="$data_root" \
    --property=NoNewPrivileges=yes \
    -- "$install_root/codex-telegram" \
    --run \
    --TelegramBot:Enabled=false \
    --CodexTelegram:InitializeOnStart=false \
    --CodexTelegram:Workspace:DataRoot="$data_root" \
    --TelegramMiniApp:Enabled=true \
    --TelegramMiniApp:ListenUrl="http://127.0.0.1:$port"

for attempt in $(seq 1 40); do
    if curl --fail --silent --show-error "http://127.0.0.1:$port/" > "$smoke_root/index.html"; then
        break
    fi
    if ! "${SUDO[@]}" systemctl is-active --quiet "$unit"; then
        "${SUDO[@]}" journalctl -u "$unit" --no-pager >&2 || true
        exit 1
    fi
    sleep 0.25
done

grep -F '<html' "$smoke_root/index.html" >/dev/null
curl --fail --silent --show-error "http://127.0.0.1:$port/app.js" > "$smoke_root/app.js"
grep -F 'Telegram' "$smoke_root/app.js" >/dev/null
"${SUDO[@]}" systemctl is-active --quiet "$unit"

after_tree="$(find "$install_root/wwwroot" -type f -printf '%P:%s:%T@\n' | sort)"
test "$before_tree" = "$after_tree"

logs="$("${SUDO[@]}" journalctl -u "$unit" --no-pager -n 200 || true)"
if printf '%s\n' "$logs" | grep -F 'Read-only file system' >/dev/null; then
    printf '%s\n' "$logs" >&2
    exit 1
fi

echo "Clean install smoke passed for non-root user $service_user with ProtectSystem=strict."
echo "Served / and /app.js from the extracted webroot without runtime directory creation."
'@
    $linuxCommand = $linuxCommand.Replace("__SOURCE_BINARY__", (Convert-ToBashSingleQuoted -Value $wslBinaryPath))
    $linuxCommand = $linuxCommand.Replace("__SOURCE_ARCHIVE__", (Convert-ToBashSingleQuoted -Value $wslArchivePath))
    $linuxCommand = $linuxCommand.Replace("__SERVICE_USER__", (Convert-ToBashSingleQuoted -Value $serviceUser))

    if ($IsWindows) {
        $linuxCommand | & wsl.exe -d $WslDistribution -u root -- bash -s
    }
    else {
        $linuxCommand | & bash -s
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Linux clean-install smoke failed with exit code $LASTEXITCODE."
    }

    Write-Host "Linux release packaging smoke passed."
    Write-Host "Release assets: codex-telegram-linux-x64, codex-telegram-linux-x64-webroot.tar.gz, codex-telegram-linux-x64.sha256, codex-telegram-linux-x64-LICENSE.txt"
}
finally {
    if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
