# Changelog

## 1.0.60 - 2026-09-19

- Added automatic reconciliation for a request left queued when an operator completes the installation through ordinary APT.
- Added `/update cancel confirm` for safely discarding a queued update without racing an updater that already owns the shared update lock.
- Added explicit status outcomes for manual update detection, cancellation, and refusal to cancel an active updater.

## 1.0.59 - 2026-09-19

- Added explicit Telegram notifications before an external host update or rollback starts and after the restarted service passes its health check.
- Added a bounded updater wait for the pre-update notification acknowledgement, with timeout-based continuation if the bot is unavailable.

## 1.0.58 - 2026-09-19

- Bumped the package and application version to exercise the Telegram-controlled update and rollback flow.

## 1.0.57 - 2026-09-18

- Added live Telegram transcription progress cards and retained long-running transcription support up to the configured ten-minute audio limit with a 15-minute default API timeout.
- Added Telegram-controlled host update requests with private-chat confirmation, worker drain/idle gates, durable state, and completion notifications.
- Added the Debian package and separate root-owned updater service, including APT index refresh, last-known-good package caching, post-restart health checks, automatic rollback, and explicit Telegram rollback.
- Kept Debian installs on a stable dpkg-owned path so ordinary `apt update && apt upgrade` remains independent of the Telegram updater.
- Added the `/health` endpoint, Debian setup wrapper, package smoke checks, and release artifact publication for the amd64 `.deb`.
- Changed special-message and live-card closing boundaries to use `--- end <boundary> ---`, preventing Telegram from interpreting the closing line as a slash command.

## 1.0.55 - 2026-09-15

- Allowed OpenAI transcription requests to run up to 15 minutes by default instead of inheriting HttpClient's 100-second timeout.
- Added bounded `OpenAI:RequestTimeoutSeconds` configuration, setup-menu support, timeout diagnostics, and long-transcription test coverage.
- Kept the default Telegram audio duration limit at 10 minutes and documented both limits.

## 1.0.54 - 2026-09-15

- Reconciled the compact input-bundle card and button-clearing updates into the release source.
- Changed special-message and live-card closing boundaries to use `--- end <boundary> ---`, preventing Telegram from interpreting the closing line as a slash command.

## 1.0.53 - 2026-09-15

- Kept editable input bundles as the default while compacting their cards to show the prompt once with only the useful controls and timing.
- Replaced the post-dispatch bundle card contents with a short `Sent to Codex` acknowledgement instead of repeating the prompt and live-update preamble.

## 1.0.52 - 2026-09-15

- Reframed the Telegram Mini App as the current conversation's Codex session control panel, with an explicit no-session state and no alternate prompt workflow.
- Added observed activity status, editable session naming, Codex-backed goal lifecycle controls, next-turn model and reasoning settings, and owning-worker catalog projection.
- Added persisted account quota history for compact usage sparklines, plus a shared global-instructions profile with explicit safe-boundary application messaging.
- Added authenticated, conversation-scoped Mini App settings actions and documented the distinction between saved settings and runtime acceptance.

## 1.0.51 - 2026-09-15

- Recovered input-bundle auto-dispatch when Codex reports that the selected thread has no rollout, replacing the stale session and retrying the bundle once.

## 1.0.50 - 2026-09-15

- Fixed narrow Mini App layouts so worker and fleet-rollout metadata shrink and ellipsize instead of creating horizontal overflow.
- Verified the combined Needs-attention, Workers, and Fleet rollouts view at compact, full-height, and fullscreen mobile-sized viewports.

## 1.0.49 - 2026-09-15

- Added owner-scoped fleet rollout plans with persisted per-worker staging, activation, health-failure, and rollback evidence.
- Added one-worker-at-a-time advancement with capability, admitted-worker, drain, and zero-active-lease compatibility gates.
- Added authenticated remote worker update status/control contracts; package activation remains external-installer-owned.
- Added Mini App rollout planning and explicitly confirmed advance, finalize, and rollback actions.

## 1.0.48 - 2026-09-15

- Added bounded Mini App handoff evidence containing review status, changed-file metadata, and artifact metadata from the same redacted task packet.
- Added a copyable handoff evidence panel while preserving the existing Telegram command as the only execution and approval path.
- Added explicit unavailable-runtime handoff behavior and bounded handoff projection tests.

## 1.0.47 - 2026-09-15

- Added explicit Mini App worker drain/resume actions for local and admitted remote workers.
- Added authenticated coordinator-to-worker control relay with worker identity, readiness, and stale-worker checks.
- Kept drain non-disruptive for active sessions and required confirmation for all worker state changes.
- Added worker control endpoint and relay coverage for authentication, confirmation, identity, and outcome evidence.

## 1.0.46 - 2026-09-15

- Added a user-scoped durable Mini App acknowledgement for an exact task, run, and review packet.
- Added authenticated Mini App task actions for review acknowledgement and bounded Telegram handoff preparation.
- Kept prompts, approvals, steering, retries, cancellation, and other execution-changing actions in Telegram.
- Added stale-run, authentication, persistence, idempotency, and handoff contract coverage.

## 1.0.45 - 2026-09-15

- Added authenticated remote model and reasoning controls plus goal lifecycle operations for owner-bound tasks.
- Added bounded remote attachment transfer for normal prompts, with worker-side materialization and no coordinator path leakage.
- Added queued remote prompt dispatch so attachments remain usable when a task is waiting behind another turn or Telegram delivery.
- Kept remote Plan mode with attachments explicitly unsupported and preserved worker-local Codex execution and authorization boundaries.

## 1.0.44 - 2026-09-15

- Added authenticated remote Mini App task detail projection for worker-owned Codex threads.
- Kept raw Codex detail and worker filesystem paths on the owning worker while preserving bounded review and artifact evidence.
- Added coordinator relay and worker contract tests for exact task ownership, redaction, and provenance.

## 1.0.43 - 2026-09-15

- Added authenticated remote task workspace status and confirmed release/discard operations.
- Kept remote filesystem paths on the worker while returning bounded branch, port, database, and lifecycle evidence.
- Completed remote text/Plan, steering, stop/kill, and workspace lifecycle routing for the supported task controls.

## 1.0.42 - 2026-09-15

- Added authenticated remote steering and stop/kill controls for explicitly selected tasks.
- Added worker-side command receipts and bounded outcome states for remote control operations, including interruption during worker drain.
- Kept remote attachments, model/goal controls, and workspace lifecycle operations out of this slice.

## 1.0.41 - 2026-09-15

- Added authenticated remote text and Plan mode relay to the worker that owns a task.
- Added bounded worker-to-coordinator turn event forwarding and supervision state reconciliation.
- Added durable duplicate command detection at the worker boundary; attachments and remote session controls remain separate follow-up capabilities.

## 1.0.40 - 2026-09-15

- Added explicit coordinator-selected remote task provisioning through an authenticated worker endpoint.
- Added worker-local worktree/session creation with lease, repository, recipe, and supervision ownership checks.
- Persisted a bounded coordinator task projection without transferring paths, credentials, prompts, or Codex transcripts.

## 1.0.39 - 2026-09-15

- Added an authenticated external-installer completion endpoint for staged worker updates.
- Added version, SHA-256, readiness, and health verification before recording an update as active.
- Added explicit health-failure and rollback-active states while retaining the protected installation boundary.

## 1.0.38 - 2026-09-15

- Added authenticated coordinator-issued worker lease handoff with fresh-worker selection, capability/capacity checks, five-minute grants, and idempotent task reuse.
- Added worker-side lease acceptance that re-checks identity, readiness, draining, capabilities, and local lease capacity before acquisition.
- Persisted bounded coordinator lease outcome evidence and exposed a private worker control-plane URL configuration.

## 1.0.37 - 2026-09-15

- Added drain-aware, hash- and version-verified worker package staging with persisted outcome evidence.
- Added operator-confirmed worker update status, staging, and rollback staging commands without overwriting the protected running installation.
- Added required capability checks and rollback artifact capture for externally applied worker updates.

## 1.0.36 - 2026-09-15

- Added the optional authenticated coordinator worker-heartbeat endpoint and outbound worker registration service.
- Added bounded persisted remote-worker admission with exact bearer-token authentication, optional worker ID allowlisting, capacity limits, and stale-heartbeat projection.
- Enforced local worker leases during task workspace creation and release, and exposed local/remote worker state in the read-only Mini App.

## 1.0.35 - 2026-09-15

- Added persisted local worker identity, readiness, bounded task leases, and drain/resume state for the task-workspace boundary.
- Added `/worker status`, `/worker drain confirm`, and `/worker resume confirm`; drain/resume is restricted to an authorized private chat and never interrupts existing leases.
- Added a read-only Mini App Workers card with worker capabilities, readiness, lease capacity, heartbeat, and bounded issue status.
- Added inspectable built-in or configured task recipes with immutable ID/version snapshots and optional Codex session policy for `/task new`.

## 1.0.34 - 2026-09-15

- Added opt-in standalone browser pairing for the read-only Mini App using short-lived Telegram-approved codes and revocable sessions.
- Persisted only pairing/session hashes and kept browser access scoped to the same read-only projection as Telegram Mini App access.
- Added `/pair <code>`, `/pair status`, and `/pair revoke` guidance and browser connection UI.

## 1.0.33 - 2026-09-15

- Added `/task new`, `/task status`, `/task release`, and `/task discard` Telegram flows for creating and inspecting task-owned Codex worktrees.
- Registered explicitly created task identities in the durable supervision ledger with conversation and user ownership checks.
- Prevented release of worktrees whose Codex session is still running and cleared the active session after a successful release.

## 1.0.32 - 2026-09-15

- Added a bounded task workspace allocator that provisions Git worktrees and task branches from explicit repositories and base refs.
- Added persisted per-task development-port reservations and safe database namespace allocation with idempotent active-task creation.
- Added clean release and explicit discard semantics for task worktrees; no existing Codex session is moved or replayed implicitly.

## 1.0.31 - 2026-09-15

- Added deterministic, bounded read-only review packets that preserve Codex thread/turn provenance while redacting absolute and unsupported file evidence.
- Added a Mini App review-packet detail surface with explicit evidence status and a Telegram-only decision boundary.
- Added `/handoff [sessionId]` to emit a bounded task, run, command, and review-evidence handoff without transferring a workspace or replaying an uncertain command.

## 1.0.30 - 2026-09-14

- Added bounded durable Telegram delivery records that remain separate from Codex execution state and track multi-chunk sends through accepted, failed, and externally unknown outcomes.
- Added persisted approval/input decisions, task claims with lease expiry and fail-closed ownership, recovery-action evidence, and startup reconciliation that marks in-flight runs unknown instead of claiming completion.
- Recorded approval and replacement-session recovery transitions through the existing Telegram/Codex seams without adding Mini App mutations or changing Telegram authorization semantics.

## 1.0.29 - 2026-09-14

- Added a bounded durable supervision ledger for application-owned task, run, and command identities around Telegram prompt dispatch and queueing.
- Persisted Codex run transitions for queued, running, operator-input, completed, failed, interrupted, and explicitly unknown outcomes while retaining Codex thread/turn provenance separately.
- Added read-only Mini App durable-work projections and task/run identity details without adding browser mutations or changing Telegram authorization semantics.

## 1.0.28 - 2026-09-14

- Added durable, bounded Telegram update receipts with atomic duplicate suppression, stale in-flight lease recovery, retention pruning, legacy-state compatibility, and retry after handler failure.
- Propagated Telegram transport update IDs into inbound message and callback context without treating them as application-owned command IDs.
- Added the supervision workspace authority, identity, recovery, isolation, review, browser, recipe, worker-update, and release contracts in the canonical draft SPEC and architecture artifacts.

## 1.0.27 - 2026-09-14

- Hardened read-only Mini App projections with deterministic ordering, bounded changes and artifacts, safe artifact metadata redaction, and non-path project labels.

## 1.0.26 - 2026-09-14

- Added the authenticated read-only supervision surface for needs-attention items, recent activity, task detail, bounded Codex changes, and safe artifact metadata.
- Added stale/unavailable live states and explicit preview mode while keeping Telegram as the control and approval surface.

## 1.0.25 - 2026-09-14

- Made the read-only Mini App responsive across compact, full-height, and true fullscreen Telegram webviews.
- Added Telegram viewport, safe-area, fullscreen, activation, and deactivation handling so resizing, minimizing, and reopening refresh the surface safely.
- Added a user-initiated fullscreen control and clear live viewport-mode status without changing Telegram authorization semantics.

## 1.0.24 - 2026-09-14

- Fixed Linux Mini App release packaging by publishing `codex-telegram-linux-x64-webroot.tar.gz` from the same published tree as the Linux binary.
- Added clean-install and `ProtectSystem=strict` smoke coverage for the extracted static asset tree, including `/` and `/app.js` serving.
- Added release workflow provenance checks and artifact attestation for the Linux webroot archive.
- Documented the Linux binary plus webroot archive installation contract.

## 1.0.22 - 2026-09-11

- Added `Balanced` Telegram output presentation with durable lifecycle/tool milestones and chronological still-working pulses, preserving the existing four modes and `Compact` default.
- Added opt-in `TelegramOutput:TextFormat=SafeMarkdownV2` rendering for constrained headings, emphasis, links, lists, and code, with escaping, format-aware chunking, and plain-text fallback.
- Threaded text-format metadata through queued delivery and Telegram send/edit paths while retaining raw text in local traces and context records.
- Documented the `Balanced` Telegram presentation mode and independent `PlainText`/`SafeMarkdownV2` text-format configuration, including compatibility and chunk-boundary fallbacks.
- Added configuration examples and synthetic/proposed verification guidance for output presentation and formatting; no Telegram-live result is implied.

## 1.0.21 - 2026-09-10

- Added additive `GeneralPurpose`/`Repository` workspace configuration with validated repository roots, safe labels, automatic repository-bound sessions, read-only `/repo` summaries, and fail-closed cross-repository resume/project actions.
- Extended first-run setup into a mode, storage, Telegram identity, expiring pairing, Codex-readiness, command/menu, and optional profile walkthrough with resumable saves and explicit readiness outcomes.
- Added conversation-scoped queue/input-bundle mutations, stale callback rejection, instance-aware state fallbacks, duplicate polling diagnostics, and the packaged opt-in avatar asset.
- Preserved the existing `Incursa.OpenAI.Codex` 2.3.0 fallback reference while adding the setup and repository-mode surfaces on top of the current SDK integration.
- Documented explicit `GeneralPurpose` and `Repository` workspace modes, repository labels, instance boundaries, and two-instance configuration examples.
- Documented guided setup outcomes and manual fallbacks, configuration precedence, BotFather command/profile ownership, and private/group/forum-topic routing requirements.
- Clarified that Codex authentication remains owned by the local Codex installation and that automated or synthetic checks do not prove live Telegram or Codex readiness.
- Added `docs/ux-assessment.md` with evidence-labeled findings, before/after examples, and follow-up user stories with acceptance criteria.

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
