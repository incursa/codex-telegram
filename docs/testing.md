---
title: "Testing"
---

# Testing

Automated release readiness is intentionally split into a normal gate and a deeper mutation gate.

## Normal Gate

Run this before release validation, release tags, and pushes that affect runtime behavior:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

That gate builds, tests, formats this repository's source and tests, audits packages, runs a tracked-file secret scan, runs the checked-in Telegram fuzz corpus, and publishes unless `-SkipPublish` is passed.

Evidence boundary: this gate is automated repository evidence. It does not prove a real Telegram update was received, a BotFather command/profile setting was applied, a group or forum topic is trusted, or the configured Codex account authenticated. Record those as separate live evidence from [manual-test-plan.md](manual-test-plan.md).

## Telegram Fuzz Corpus

Run this directly when changing Telegram command parsing, message chunking, attachment mapping, or emoji/Unicode handling:

```powershell
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release
```

The seed corpus lives under `fuzz/corpus` and is exercised by `TelegramFuzzCorpusTests`.

## Mutation Gate

Run scoped mutation testing when changing Telegram routing, parser, chunker, attachment, queueing, or sender behavior:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Configuration Release
```

The default `core` profile uses the repo-local `dotnet-stryker` tool and `src/Incursa.Codex.Telegram/stryker-config.json`.

Use a narrower profile when the change is concentrated in one surface:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Configuration Release -Profile core
.\scripts\Test-TelegramMutation.ps1 -Configuration Release -Profile handler
.\scripts\Test-TelegramMutation.ps1 -Configuration Release -Profile queue
```

Use all mutation profiles before a release candidate when time permits:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Configuration Release -Profile all
```

The profiles are:

- `core`: parser, chunker, attachment mapping, sender behavior, and conversation scope.
- `handler`: Telegram command handling and raw Telegram update adaptation.
- `queue`: outbound queueing, queued prompt dispatch, and turn output relay behavior.

Latest local mutation evidence from the May 5, 2026 release-readiness pass:

- `core`: 82.35%, improved from 64.71% after adding sender failure/rate-limit/button coverage plus parser and attachment display-name edge cases.
- `handler`: 38.51%, improved from 15.22% after adding command, callback, audio, topic, project, session, model, thinking, status, tail, outbound, lifecycle, authorization, attachment, and raw update-adapter coverage.
- `queue`: 70.71%, improved from 13.11% after adding queue scheduler, queued prompt processor, hosted service, turn output relay, runtime option update, cancellation, backoff boundary, compaction, and relay cleanup coverage.

Mutation testing is not part of the normal release gate because it is slower and best used as focused quality evidence after meaningful Telegram behavior changes.
Treat mutation scores as advisory evidence. The broader `handler` and `queue` profiles intentionally include large surfaces that are not exhaustively covered by unit tests, so record the score and investigate material survivors instead of presenting a passing Stryker run as full behavioral proof.

When documenting a verification result, label it as `automated`, `synthetic`, `local-live`, or `Telegram-live`. A scripted Codex runtime is synthetic evidence; it cannot establish Codex authentication. Keep auth state outside the repository and run any local-live check under the same OS account/environment as the bot.

For workspace-mode changes, cover both `CodexTelegram:Mode=GeneralPurpose` (workspace-root browsing/project selection) and `CodexTelegram:Mode=Repository` (explicit `RepositoryRoot` boundary). For two-instance tests, use distinct Telegram tokens and `DataRoot` values so one process cannot consume the other's updates or state.

The canonical requirement IDs for Codex testability and validation live in [`specs/requirements/codex-telegram/_index.md`](../specs/requirements/codex-telegram/_index.md).
