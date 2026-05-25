# Changelog

## Unreleased

- Updated the fallback `Incursa.OpenAI.Codex` package reference to 2.2.0 so published Telegram builds consume the SDK observable turn stream and plan-mode configuration surface.

## 1.0.20 - 2026-05-23

- Added session-pinned live cards that keep one editable card per conversation, hide internal turn IDs, and reuse the same card across internal turn restarts.
- Stopped publishing bare successful `Turn completed` messages; successful terminal events now only flush real assistant text.
- Tightened empty-output retry handling so turns that complete without assistant response text retry instead of treating tool, background-agent, or marker-only output as the response.
- Added an in-memory session event projection so `/status` and `/tail` can show the last turn closeout, including a warning when assistant text reached Telegram but Codex ended without a final response item.
- Added Telegram input bundles with editable draft cards for active-turn and media input, including send, queue, steer, cancel, and trace buttons.
- Added Telegram album/media-group debouncing so multiple images/documents from the same album are captured as one bundle candidate.
- Added local trace diagnostics for Telegram inbound, bundle, Codex turn, and outbound delivery state so cut-off output can be separated into Codex terminal, queue, compaction, rate-limit, timeout, and send-failure causes.
- Changed `/status` into a session status card that separates Codex completion from Telegram delivery drain and exposes trace/debug buttons.
- Hardened editable card recovery, durable queued/bundled attachment storage, real bundle clearing, and missing-attachment diagnostics.

## 1.0.15 - 2026-05-10

- Added `/goal` controls for active Telegram Codex sessions, including show, set, token budget, pause, resume, complete, and clear actions.
- Updated `Incursa.OpenAI.Codex` to 1.2.1 so the Telegram app can use Codex thread-goal APIs.
- Documented `/goal` in the usage guide, command reference, setup guide, BotFather command list, and manual test plan.
- Added regression coverage for `/goal` command parsing, gateway/session-manager wiring, goal status formatting, and queued-prompt test doubles.

## 1.0.14 - 2026-05-06

- Fixed published `--run` launches that missed `appsettings.Local.json` when the settings file existed in the launch directory but not beside the binary.
- Preserved local settings during `scripts/Publish.ps1` so refreshing a publish folder does not strand Telegram token, allowlist, or workspace configuration.
- Added shared-chat `/whoami` regression coverage for group and supergroup setup diagnostics.
- Aligned application version defaults and issue-template placeholders for the 1.0.14 release.

## 1.0.13 - 2026-05-06

- Added trusted group-root sessions so allowlisted users can route plain text, audio, and attachments in trusted group roots.
- Updated `/trust`, `/doctor`, and command guidance for group-root project/session behavior.
- Changed `/new` to allow an omitted name and use a project-based default session name.

## 1.0.12 - 2026-05-06

- Added `/queue` and `/queued` for viewing queued prompts, editing queued text, deleting queued items, and sending a queued item now as active-turn steering.
- Added first-run setup onboarding that validates the Telegram bot token, captures the admin user ID from a private bot message, and writes settings beside the executable by default.
- Added macOS `curl` download instructions and updated setup documentation for executable-folder settings and workspace-root selection.

## 1.0.11 - 2026-05-06

- Enabled invariant globalization for release binaries so Linux self-contained builds do not require system ICU packages at startup.
- Aligned application version defaults and issue-template placeholders for the 1.0.11 release.

## 1.0.10 - 2026-05-06

- Updated `Incursa.OpenAI.Codex` to 1.1.0 and switched `/usage` to the SDK account rate-limit API.
- Changed `/status` to show a compact five-hour and weekly Codex usage line without being suppressed by a cached inline-usage miss.
- Updated BotFather and command documentation for the `/usage` command and compact status usage text.
- Added a `Maintainer Review` workflow that routes outside-authored pull requests to Samuel and gates merges on Samuel's current-head approval.
- Refined the pull request template so reviewer notes replace command-output validation prompts.

## 1.0.8 - 2026-05-06

- Aligned the application version, default Codex client version, setup defaults, and issue-template examples for release.

## 1.0.7 - 2026-05-06

- Updated `Incursa.OpenAI.Codex` to 1.0.20.
- Updated Microsoft.Extensions runtime package references to 10.0.7.
- Updated test infrastructure packages for Microsoft.NET.Test.Sdk and coverlet collector.
- Added a project code of conduct and linked it from contributor-facing docs.
- Added Dependabot configuration for GitHub Actions and NuGet.
- Added CodeQL workflow configuration.
- Added tracked-file secret scanning to GitHub Actions CI and publish workflows.
- Pinned third-party GitHub Actions to commit SHAs.
- Configured publish-time artifact attestations for release binaries.

## 1.0.6 - 2026-05-05

- Added a standalone bootstrap/admin menu for local configuration.
- Added live Codex model discovery for setup, with curated fallback examples.
- Added durable local project, Telegram state, and thread-manifest storage under `CodexTelegram:Workspace:DataRoot`.
- Added startup rehydration for Telegram conversation-to-session follow state.
- Simplified Telegram inline buttons so single-session replies no longer show noisy numbered controls.
- Tightened group and forum authorization so messages require both an allowed user and an allowed chat.
- Added manual release validation docs for private chat, authorization, groups, forum topics, voice, attachments, queueing, and restart behavior.
- Added tests for button labels, authorization, outbound queue behavior, local settings, state persistence, and OpenAI transcription error boundaries.
- Added checked-in Telegram fuzz corpus coverage for command-like text, emoji intent, Unicode boundaries, formatting-like input, chunking, and attachment mapping.
- Added a scoped Telegram mutation-testing script and Stryker configuration for parser, chunker, attachment, sender, and topic-scope seams.
- Added `/doctor` in-chat diagnostics for authorization, routing, project/session state, workspace roots, outbound queue status, and the next best action.
- Added group-root plain-text guidance so allowed users are not left with a silent no-op when they accidentally send outside a topic.
- Changed stale/deleted topic send failures to fail closed instead of retrying Codex output in the group root.
- Reduced model/thinking update latency by avoiding a redundant model-list lookup after settings changes.
- Preserved full multi-line content in batched Telegram output instead of reducing each queued update to its first line.
- Reworked the README as a product-facing setup and download guide, with developer workflow details moved to a dedicated development guide.
- Added BotFather, command-reference, and menu/button documentation for first-time public users.
- Aligned model-setting examples on the canonical `/model <model> thinking <effort>` form.
- Removed `---` separators from batched Telegram output so queued multi-line content reads as one continuous update.
- Removed the batched-output `/tail 100` footer and redundant successful `Turn completed.` text before the `~~ fin ~~` marker.
- Removed the batched-output update-count and session-ID header so grouped Telegram sends start directly with Codex content.
- Changed the `~~ fin ~~` turn marker to send as one standalone Telegram message after terminal turn content.
- Recovered from empty or unreadable selected Codex thread transcripts by clearing the stale Telegram session binding, starting a fresh session, and retrying the prompt once.
- Made model and thinking button callbacks edit the tapped menu immediately while settings are loading or updating.
- Added XML documentation and named limit/default constants across the Telegram outbound queue, bot options, Codex option contracts, DTO contracts, and small Telegram routing/value-object surfaces.
- Enabled XML documentation generation for the app project and tightened non-configuration app seams to internal visibility.

Known boundaries:

- Private chat is the primary supported operating mode.
- Group and forum-topic support require explicit chat allowlisting and Telegram permissions.
- A process restart rehydrates stored conversation/session bindings, but does not resume a mid-turn Codex execution.
- The app does not bundle Codex, Telegram credentials, OpenAI credentials, or `ffmpeg`.
