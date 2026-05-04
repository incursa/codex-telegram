param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "CodexTelegram.slnx"
$testProjectPath = Join-Path $repoRoot "tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj"
$publishOutput = Join-Path $repoRoot "artifacts\verify\release-readiness-$Runtime"

Push-Location $repoRoot
try {
    dotnet build $solutionPath -c $Configuration -m:1
    dotnet test $testProjectPath -c $Configuration --no-build --no-restore -m:1
    dotnet format $solutionPath --verify-no-changes --no-restore
    dotnet list $solutionPath package --vulnerable --include-transitive

    if (-not $SkipPublish) {
        & (Join-Path $repoRoot "scripts\Publish.ps1") -Runtime $Runtime -Configuration $Configuration -OutputDirectory $publishOutput
    }
}
finally {
    Pop-Location
}
