---
title: "Development"
---

# Development

This guide is for contributors who want to build, test, package, or change the source code. If you only want to install and use the bot, start with [README.md](../README.md).

## Prerequisites

1. .NET 10 SDK matching [global.json](../global.json).
2. A local `codex` executable if you want end-to-end manual testing.
3. A Telegram bot token and numeric Telegram user ID for live manual checks.
4. `OPENAI_API_KEY` and `ffmpeg` only if you are testing voice transcription.

The app does not own Codex authentication. Development tests should use a disposable or explicitly selected Codex account/context; do not add Codex auth files to the repository or test artifacts. When testing two bot instances, isolate their settings/state roots and, if needed, their Codex auth contexts.

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
4. The published `wwwroot` directory used by a local run.
5. `appsettings.Local.json` when one already exists in the publish output, or when an ignored repository-root local settings file exists.

Other runtime identifiers can be passed with `-Runtime`, for example `linux-x64` or `osx-arm64`, when the .NET SDK has the required runtime packs.

Validate the Linux release-style package, including the matching webroot archive, clean installation, static HTTP serving, and the intended non-root `ProtectSystem=strict` service boundary:

```powershell
.\scripts\Test-LinuxReleasePackaging.ps1
```

On Windows this uses the configured WSL distribution; on Linux it requires `systemd-run`, `curl`, `tar`, and permission to start a transient service. The check is intentionally separate from the local `Publish.ps1` output because the fleet installer receives the binary and webroot archive as separate release assets.

If the published executable is already running from the output directory, Windows will lock the existing binary and `dotnet publish` cannot replace it. Stop that process first, or opt in to the script-managed stop:

```powershell
.\scripts\Publish.ps1 -Runtime win-x64 -StopRunningProcess
```

`-StopRunningProcess` only targets a process whose executable path exactly matches the publish output binary.

The Mini App is a static frontend under `src/Incursa.Codex.Telegram/wwwroot`. Its GET API projects existing Codex threads and turns; its only current write actions acknowledge an exact review packet or prepare a Telegram handoff, not Codex execution. It is not a competing task store. When changing its layout or Telegram WebApp integration, validate the actual served page at compact, full-height, and fullscreen viewport sizes. Check horizontal overflow, dynamic safe-area padding, the viewport badge, the fullscreen control, activation/reopen refresh behavior, task-detail navigation, stale-data handling, and keyboard access with a real browser harness when available; a successful .NET publish does not establish frontend behavior. The staged product contract is in [mini-app-roadmap.md](mini-app-roadmap.md).

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

This is repository evidence, not live integration evidence. It does not validate a real Telegram token, BotFather profile/privacy settings, group delivery, or Codex account authentication. Add the result of [manual-test-plan.md](manual-test-plan.md) separately when making those claims.

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

Tag pushes that start with `v` create a GitHub Release and upload the published artifacts with generated release notes. Linux tag builds additionally archive the published `wwwroot` directory as `codex-telegram-linux-x64-webroot.tar.gz`, attest the archive, and verify the release asset set before creation.

## Documentation Expectations

Update docs when a change affects:

1. Setup or configuration.
2. Bot commands or day-to-day workflow.
3. Security posture.
4. Release packaging.
5. Manual Telegram validation.
6. Public support boundaries.

Keep the README user-facing. Put source, test, and contribution workflow details here or in [CONTRIBUTING.md](../CONTRIBUTING.md).

Ownership boundaries matter when changing docs:

1. The app owns command parsing, callback behavior, and `/help`.
2. Guided setup may apply the app-owned command list and menu button through the Bot API once; BotFather remains the manual path for profile text, privacy/group settings, and skipped/failed operations. There is no background synchronization.
3. `CodexTelegram:Mode` owns workspace scope (`GeneralPurpose` or `Repository`), while `TelegramOutput:PresentationMode` owns Telegram output presentation.
4. Use slash-command fallbacks in examples when a button or command picker may be unavailable.
