param(
    [string] $Configuration = "Release",
    [switch] $NoRestore,
    [switch] $NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj"
$corpusRoot = Join-Path $repoRoot "fuzz\corpus"

$seedFiles = Get-ChildItem -Path $corpusRoot -Recurse -File |
    Where-Object { $_.Extension -in ".txt", ".bin" } |
    Sort-Object FullName

if ($seedFiles.Count -eq 0) {
    throw "No Telegram fuzz corpus seed files were found under $corpusRoot."
}

Push-Location $repoRoot
try {
    if (-not $NoRestore) {
        dotnet restore $projectPath
    }

    if (-not $NoBuild) {
        dotnet build $projectPath -c $Configuration --no-restore
    }

    dotnet test $projectPath -c $Configuration --no-build --no-restore --filter "FullyQualifiedName~TelegramFuzzCorpusTests"
}
finally {
    Pop-Location
}
