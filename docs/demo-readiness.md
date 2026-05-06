# Demo Readiness Scorecard

Use this scorecard before showing the repository publicly, recording a release candidate, or switching the repository to public visibility.

## Current Readiness

| Area | Status | Evidence | Release Meaning |
| --- | --- | --- | --- |
| Build and unit tests | Automated | `scripts\Test-ReleaseReadiness.ps1` and GitHub `CI` | Required before demo. |
| Positive and negative tests | Automated | `tests/Incursa.Codex.Telegram.Tests` | Covers command parsing, authorization, session/project state, raw Telegram update adaptation, queueing, attachments, sender failures, and setup diagnostics. |
| Fuzz corpus | Automated | `scripts\Test-TelegramFuzzCorpus.ps1` and `fuzz/corpus` | Deterministic seed corpus for Telegram-like text, Unicode, emoji, command-like input, chunking, and attachment mapping. |
| Mutation tests | Advisory | `scripts\Test-TelegramMutation.ps1` | Run the changed profile after behavior changes; run all profiles for release-candidate evidence when time permits. |
| Publish artifacts | Automated | `scripts\Publish.ps1` and GitHub `Publish` | Required for a binary-based demo. |
| Repository governance | Automated | GitHub rulesets and `CODEOWNERS` | `main` requires pull requests, Code Owner review, current status checks, squash merge, and the contributor-agreement status. |
| Contributor agreement | Automated | `.github/workflows/contributor-agreement.yml` and `incursa/contributor-agreement-action` | Required for outside pull requests after the workflow is proven with a non-allowlisted contributor. |
| Live Telegram behavior | Manual | `docs/manual-test-plan.md` | Required for any public claim involving a real Telegram bot, group, forum topic, voice note, image, or document. |
| Owner evidence | Manual | `docs/release-owner-actions.md` results log | Required before repository visibility changes or tagged releases. |

## Safe Public Claims

The following claims are safe only after the automated gate passes on the release candidate:

- The repository builds and tests on the supported .NET SDK.
- The standalone app can be published as a single-file console binary.
- Telegram command parsing, authorization checks, menu button routing, queueing, and attachment mapping are covered by automated tests.
- The deterministic fuzz corpus is part of the normal CI and release-readiness path.
- Mutation testing exists as a repo-native advisory quality gate.

The following claims require owner-run manual evidence:

- A real Telegram bot was configured successfully through BotFather.
- A private Telegram chat works end to end with the published binary.
- Voice transcription works with the configured OpenAI key and `ffmpeg`.
- Image, document, sticker, video, or audio handling works against Telegram's live file API.
- Groups and forum topics work with the selected Telegram privacy-mode and permission settings.
- Restart behavior was checked with the selected `CodexTelegram:Workspace:DataRoot`.

## Go/No-Go Checklist

Go only if all required items are true:

- `git status --short --branch` is clean except intentional release artifacts that are ignored.
- `scripts\Test-ReleaseReadiness.ps1 -Runtime <target-runtime>` passes.
- The relevant mutation profile passes, or the skipped profile is recorded with a reason.
- The latest GitHub `CI` workflow is green for the release branch or commit.
- The latest GitHub `Publish` workflow is green for the release branch, commit, or tag when binary artifacts are part of the demo.
- `docs/manual-test-plan.md` private-chat checks have been run against the actual bot used for the demo.
- `docs/release-owner-actions.md` records the commit or asset, OS, Codex CLI version, bot privacy-mode setting, and skipped manual checks.
- Demo workspace roots are narrow and do not expose unrelated local paths.
- No tracked file contains a Telegram bot token, OpenAI API key, personal `appsettings.Local.json`, private transcript, or private screenshot.

No-go if any of these are true:

- The app can silently ignore likely operator mistakes without an explanatory reply.
- The support boundary for private chat, groups, forum topics, voice, or attachments is unclear.
- The demo relies on a local path, token, or screenshot that should not be public.
- A failed live Telegram check is treated as "probably fine" without being recorded.
- CI or publish status is unknown for the commit being shown.

## Mutation Profiles

Run the smallest profile that matches the changed surface:

| Profile | Command | Use When |
| --- | --- | --- |
| `core` | `scripts\Test-TelegramMutation.ps1 -Profile core` | Parser, chunker, attachment mapping, sender, or conversation-scope behavior changed. |
| `handler` | `scripts\Test-TelegramMutation.ps1 -Profile handler` | Command handler, menu routing, callback handling, diagnostics, or raw Telegram update adaptation changed. |
| `queue` | `scripts\Test-TelegramMutation.ps1 -Profile queue` | Outbound queueing, queued prompt dispatch, live output relay, or scheduler behavior changed. |
| `all` | `scripts\Test-TelegramMutation.ps1 -Profile all` | Release-candidate evidence or broad Telegram behavior changes. |

Mutation is advisory rather than part of the fast release-readiness script. A failed mutation profile should either be fixed or recorded as a known quality gap before a public release candidate is shown.

## Demo Script

Use this minimum script for a professional private-chat demo:

1. Start the published binary or run `dotnet run --project src/Incursa.Codex.Telegram -- --run`.
2. Send `/whoami` and show the Telegram user and chat identifiers.
3. Send `/projects` and show that only intended workspace roots are visible.
4. Send `/project add <demo repository path>`.
5. Send `/doctor` and show the current access, routing, project, session, workspace, queue, and next action.
6. Send `/new <short demo name>`.
7. Send one practical prompt that produces visible Codex output.
8. Send `/status` or `/tail` to show session state without relying on Telegram scrollback.
9. Optional: send an image or document only if `docs/manual-test-plan.md` attachment checks passed for the same bot.
10. Optional: show a group or forum topic only if group/forum checks passed for the same bot and privacy-mode setting.

## Known Boundaries

- Private chat is the minimum supported demo path.
- Groups and forum topics add Telegram allowlist, privacy-mode, and topic-permission complexity.
- Mid-turn process restart does not resume an in-flight Codex turn.
- Voice transcription depends on an operator-supplied OpenAI API key and usable `ffmpeg`.
- Mutation evidence is profile-based and advisory; it is not a claim of exhaustive correctness.
