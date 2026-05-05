param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    $trackedFiles = git ls-files -z |
        ForEach-Object { $_ -split "`0" } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $patterns = @(
        [pscustomobject]@{
            Name = "Telegram bot token"
            Regex = '(?<![A-Za-z0-9_])[0-9]{8,12}:[A-Za-z0-9_-]{35,}(?![A-Za-z0-9_-])'
        },
        [pscustomobject]@{
            Name = "OpenAI API key"
            Regex = '(?<![A-Za-z0-9_])sk-(?:proj-)?[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])'
        },
        [pscustomobject]@{
            Name = "GitHub token"
            Regex = '(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9_]{30,}(?![A-Za-z0-9_])'
        },
        [pscustomobject]@{
            Name = "Slack token"
            Regex = '(?<![A-Za-z0-9_])xox[baprs]-[A-Za-z0-9-]{20,}(?![A-Za-z0-9-])'
        },
        [pscustomobject]@{
            Name = "Private key"
            Regex = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        }
    )

    $findings = New-Object System.Collections.Generic.List[object]

    foreach ($relativePath in $trackedFiles) {
        $fullPath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        try {
            $content = [System.IO.File]::ReadAllText($fullPath)
        }
        catch {
            continue
        }

        foreach ($pattern in $patterns) {
            $matches = [regex]::Matches($content, $pattern.Regex)
            foreach ($match in $matches) {
                $lineNumber = 1
                for ($index = 0; $index -lt $match.Index; $index++) {
                    if ($content[$index] -eq "`n") {
                        $lineNumber++
                    }
                }

                $findings.Add([pscustomobject]@{
                    File = $relativePath
                    Line = $lineNumber
                    Type = $pattern.Name
                })
            }
        }
    }

    if ($findings.Count -gt 0) {
        $findings | Format-Table -AutoSize
        throw "Tracked-file secret scan found potential secrets."
    }

    Write-Host "Tracked-file secret scan passed."
}
finally {
    Pop-Location
}
