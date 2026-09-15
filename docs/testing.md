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

For Telegram update-boundary changes, automated coverage must prove atomic duplicate exclusion, completed replay suppression after state reload, stale in-flight lease reclamation, bounded retention, compatibility with legacy state files, retry after handler failure, and propagation of the transport `UpdateId` without treating it as an application `CommandId`.

For supervision-ledger changes, automated coverage must prove distinct Task/Run/Command identities, task scoping by Telegram user and conversation, replay rejection without a second Codex send, queue-to-running identity preservation, terminal Codex transition projection, explicit non-terminal `Unknown` outcomes, restart reconciliation of in-flight runs, bounded redacted persistence, and unchanged legacy Telegram state behavior. The ledger must preserve `CodexThreadId` and `CodexTurnId` as provenance rather than overwriting them with application IDs. Delivery coverage must prove one acknowledgement across a multi-chunk message and independent failure/unknown states; approval coverage must prove explicit grant/deny decisions and safe defaults; claim coverage must prove lease ownership and cross-user exclusion; recovery coverage must prove requested/applied/unknown transitions and child-command identity for replacement sessions.

For Mini App changes, keep these evidence categories separate. Review-packet checks must also prove deterministic packet IDs, Codex thread/turn provenance, bounded change/artifact counts, sanitized paths, binary/unsupported evidence labels, and the `/handoff` identity boundary. Mini App task-action checks must prove signed or paired-browser authentication, exact user/task/run/packet scope, durable idempotent acknowledgement, stale-run rejection, attention reappearance for a newer run, and handoff preparation without Telegram delivery or Codex execution.

For coordinator changes, automated coverage must prove exact bearer-token authentication, worker-ID admission, bounded registration capacity, persistence/reload, stale-heartbeat projection, and that the worker heartbeat contains only the bounded worker snapshot. A coordinator smoke test is synthetic unless it uses two separately configured processes over an operator-controlled private network; it does not prove real multi-container Codex execution or credentials sharing. Task dispatch remains a separate test obligation and must prove coordinator-issued lease ownership, worker selection, stale-worker exclusion, and cross-worker isolation.

- `automated`: projection, authorization, bounded payload, and no-manifest-write tests.
- `synthetic`: scripted Codex runtime and signed-init-data fixtures; these do not prove a real Telegram account or Codex authentication.
- `browser`: served-page checks at compact, full-height, and fullscreen-sized viewports, including no horizontal overflow, no framework overlay, console health, task-detail activation, and refresh/reopen behavior.
- `Telegram-live`: only a real private-chat check with the configured allowlist and HTTPS Mini App URL.

Live refresh failures must render as unavailable or stale data. A failed API call must never be verified by accepting invented preview sessions unless the test explicitly opts into `?preview=1`.

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
