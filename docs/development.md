# Development

This guide is for contributors who want to build, test, package, or change the source code. If you only want to install and use the bot, start with [README.md](../README.md).

## Prerequisites

1. .NET 10 SDK matching [global.json](../global.json).
2. A local `codex` executable if you want end-to-end manual testing.
3. A Telegram bot token and numeric Telegram user ID for live manual checks.
4. `OPENAI_API_KEY` and `ffmpeg` only if you are testing voice transcription.

## Restore, Build, And Test

```powershell
dotnet restore CodexTelegram.slnx
dotnet build CodexTelegram.slnx
dotnet test CodexTelegram.slnx
```

Run the app from source:

```powershell
dotnet run --project src\Incursa.Codex.Telegram
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

When running from source, `appsettings.Local.json` resolves beside the built executable under `bin` by default. If that file is missing and the launch directory has `appsettings.Local.json`, the app uses the launch-directory file.

## Publish Locally

```powershell
.\scripts\Publish.ps1 -Runtime win-x64
```

The output is written under:

```text
artifacts\publish\win-x64
```

The publish script writes:

1. The self-contained binary.
2. A `.sha256` checksum file.
3. `LICENSE.txt`.
4. `appsettings.Local.json` when one already exists in the publish output, or when an ignored repository-root local settings file exists.

Other runtime identifiers can be passed with `-Runtime`, for example `linux-x64` or `osx-arm64`, when the .NET SDK has the required runtime packs.

If the published executable is already running from the output directory, Windows will lock the existing binary and `dotnet publish` cannot replace it. Stop that process first, or opt in to the script-managed stop:

```powershell
.\scripts\Publish.ps1 -Runtime win-x64 -StopRunningProcess
```

`-StopRunningProcess` only targets a process whose executable path exactly matches the publish output binary.

## Release-Readiness Gate

Run this before release validation, release tags, and pushes that affect runtime behavior:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

That gate includes:

1. Release build.
2. Unit tests.
3. Telegram fuzz corpus.
4. Format verification for this repository's source and tests.
5. Package vulnerability reporting.
6. Tracked-file secret scan.
7. Publish verification unless `-SkipPublish` is used.

Run the tracked-file secret scan directly when needed:

```powershell
.\scripts\Test-TrackedSecretScan.ps1
```

## Fuzz And Mutation Checks

Run the deterministic Telegram fuzz corpus when changing command parsing, message chunking, attachment mapping, or emoji/Unicode handling:

```powershell
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release
```

Run scoped mutation testing when changing Telegram routing, parser, chunker, attachment, queueing, sender, or live-output behavior:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Profile core
.\scripts\Test-TelegramMutation.ps1 -Profile handler
.\scripts\Test-TelegramMutation.ps1 -Profile queue
```

Mutation testing is advisory and slower than the normal gate. Use the profile that matches the changed surface.

## GitHub Actions

Pull requests and pushes to `main` run build, format, vulnerability-report, unit-test, and fuzz-corpus validation on Windows, Linux, and macOS.

Pushes to `main` also publish short-retention artifacts for Windows x64, Linux x64, and macOS arm64.

Tag pushes that start with `v` create a GitHub Release and upload the published artifacts with generated release notes.

## Documentation Expectations

Update docs when a change affects:

1. Setup or configuration.
2. Bot commands or day-to-day workflow.
3. Security posture.
4. Release packaging.
5. Manual Telegram validation.
6. Public support boundaries.

Keep the README user-facing. Put source, test, and contribution workflow details here or in [CONTRIBUTING.md](../CONTRIBUTING.md).
