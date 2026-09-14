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

## Output presentation and formatting coverage

When the output surface changes, keep these checks separate:

- `TelegramTextFormatterTests` are `synthetic`/automated coverage for `PlainText` compatibility, constrained `SafeMarkdownV2` headings/emphasis/links/lists/code, escaping, malformed markup, unsafe links, and chunk-boundary fallback.
- Relay tests cover `Balanced` milestone durability, routine still-working pulses, high-priority events, and the existing `Compact`, `Verbose`, `LiveCard`, and `FinalOnly` behavior.
- Command-handler tests cover `balanced`, `milestones`, `milestone`, and `/output mode reset`, including the distinction between configured mode and runtime override.

Proposed synthetic before/after fixture:

```text
Proposed before (PlainText): **bold** [docs](https://example.test)
Proposed after (SafeMarkdownV2): *bold* [docs](https://example.test)
```

Observed here means only a local formatter/test-double result. It is not Telegram-live rendering or proof that a real bot received the message. Do not report a live formatting result unless the private-chat manual check was actually run.

The canonical requirement IDs for Codex testability and validation live in [`specs/requirements/codex-telegram/_index.md`](../specs/requirements/codex-telegram/_index.md).

## Linux Release Packaging

Run the Linux-specific packaging test for a release-style proof:

```powershell
.\scripts\Test-LinuxReleasePackaging.ps1
```

The test publishes `linux-x64` when no publish directory is supplied, creates `codex-telegram-linux-x64-webroot.tar.gz` from that published `wwwroot`, compares archive files with the complete published static tree, and requires `wwwroot/index.html`. It then creates a clean staging installation without a pre-existing webroot, extracts only the release archive, and starts the actual binary as a non-root systemd service with `ProtectSystem=strict`. The service must serve `/` and `/app.js`, remain active, and leave the extracted webroot unchanged; a missing-directory creation failure is therefore not hidden by a writable install directory.

The test is local/synthetic packaging evidence. It does not prove Telegram authorization, BotFather configuration, or Codex authentication.

## Mini App presentation coverage

The Mini App frontend must be checked separately from the .NET release gate. Use a browser harness against the served `wwwroot` and exercise three Telegram WebApp contracts: compact (`isExpanded=false`), full-height (`isExpanded=true`), and true fullscreen (`isFullscreen=true`), including a user-initiated `requestFullscreen`/`exitFullscreen` transition. Assert that the page has no horizontal overflow, that the viewport and safe-area styles are applied, that `/` and static assets render, and that `activated` causes a fresh bootstrap request after the app is reopened. Record browser validation as `synthetic` unless it used an actual Telegram client; automated checks do not prove BotFather launch configuration or Telegram-live behavior.
