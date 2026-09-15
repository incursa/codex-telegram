---
title: "Operations"
---

# Operations

This app is a standalone console process. It does not restart itself from Telegram. Use the terminal, scheduled task, service manager, or container/runtime supervisor that starts the process.

For normal project/session usage after the process is running, use [usage.md](usage.md).

## Operating Modes And Two Instances

Set `CodexTelegram:Mode` to `GeneralPurpose` for a multi-repository personal launcher, or `Repository` for an instance pinned to `CodexTelegram:RepositoryRoot`. For two instances, give each one a different BotFather token, executable/settings folder, and `CodexTelegram:Workspace:DataRoot`; do not let two processes share a Telegram token or local state files. `CodexTelegram:InstanceId` can distinguish default state locations, but an explicit data root is the auditable choice.

Configuration is layered in this order: `appsettings.json`, executable-local `appsettings.Local.json` (then launch-directory fallback), user secrets, `CODEX_TELEGRAM_` environment variables, and command-line arguments. Later values win. The direct `TELEGRAM_*`, `OPENAI_API_KEY`, and `CODEX_PATH` variables are empty-value fallbacks only.

Only one process may poll a given Telegram bot token. The application takes a local receiver lock (the lock name is a token hash, never the token itself) before hosted polling or setup pairing begins. If startup reports that another local receiver already owns the token, stop the duplicate process or configure a different BotFather token; do not run two pollers against one bot.

## Start

From a published Windows binary:

```powershell
.\artifacts\publish\win-x64\codex-telegram.exe --run
```

For a Linux release, install the binary and the matching `codex-telegram-linux-x64-webroot.tar.gz` from the same GitHub release. Extract the archive beside the binary so the installed layout contains `wwwroot/index.html` before starting the service:

```bash
mkdir -p ~/tools/codex-telegram
mv codex-telegram-linux-x64 ~/tools/codex-telegram/codex-telegram
tar -xzf codex-telegram-linux-x64-webroot.tar.gz -C ~/tools/codex-telegram
chmod +x ~/tools/codex-telegram/codex-telegram
cd ~/tools/codex-telegram
./codex-telegram --run
```

The archive is generated from the same published commit as the binary. Keep the installation directory read-only under the fleet service; application state belongs in the configured writable data root, not beside the executable or under `wwwroot`.

From source:

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

Without `--run`, an interactive terminal opens the bootstrap/admin menu.

## Mini App Companion Surface

The optional Mini App host listens on `http://127.0.0.1:5287` by default. The static page is available locally, while its live API is disabled for Telegram use until `TelegramMiniApp:Enabled` is set to `true`. It is an evidence-focused supervision dashboard for attention, recent sessions, task detail, bounded Codex-reported changes, review packets, safe artifact metadata, runtime state, usage, and saved projects. An authenticated user may acknowledge an exact task/run/review packet or prepare the existing `/handoff [sessionId]` command; message and Codex control actions remain in Telegram. Local sample data requires the explicit `?preview=1` query string.

For a Telegram test, set `TelegramMiniApp:ListenUrl` to the local listener and place it behind an HTTPS Cloudflare Tunnel. With the bot running, a temporary test tunnel is:

```bash
cloudflared tunnel --url http://127.0.0.1:5287
```

Put the resulting HTTPS URL in `TelegramMiniApp:PublicUrl`, configure the same URL as the bot menu button through BotFather, and keep the tunnel running while testing. Use a named Cloudflare Tunnel and stable hostname for ongoing operation. `PublicUrl` is operator documentation/configuration; the application does not create the Cloudflare route or BotFather menu button automatically. If `cloudflared` runs in a separate container, route it to the bot container and bind `ListenUrl` to an address reachable from that container rather than `127.0.0.1`.

Never publish the local listener directly, and do not enable the feature without a populated `TelegramBot:AllowedUserIds` allowlist.

Mini App requests must include Telegram's signed initialization data. The host rejects missing, stale, tampered, or non-allowlisted identities. Treat the public URL and all displayed session/workspace data as private operator data.

Browser access is opt-in. Set `TelegramMiniApp:BrowserPairingEnabled` to `true`, open the public URL in a normal browser, then send the displayed `/pair <code>` command to the bot from the authorized private chat. Pairing codes expire quickly, browser sessions expire separately, and `/pair revoke` invalidates all browser sessions for that Telegram user. The browser receives the same user-scoped evidence projection and bounded acknowledgement/handoff actions; prompts, approvals, steering, and other control actions remain in Telegram.

The local worker is registered in `codex-worker-state.json` under `CodexTelegram:Workspace:DataRoot`. It contains a generated or configured stable worker ID, display name, heartbeat, drain state, and bounded task lease metadata; it does not contain prompts, transcripts, credentials, or authorization headers. `/worker status` is a read-only diagnostic. `/worker drain confirm` prevents new task leases while existing sessions continue, and `/worker resume confirm` re-enables claims. Use the private authorized chat for both lifecycle changes. For coordinator routing, configure `CodexTelegram:Worker:ControlPlaneUrl` to the worker's private HTTP(S) endpoint; the URL is advertised in bounded heartbeats and receives only token-authenticated lease grants. Keep that endpoint on the operator-controlled private network.

Task recipes are configured under `CodexTelegram:Recipes`. With no definitions, the host exposes the built-in `investigate-tests`, `review-branch`, and `implement-issue` recipes. Each definition has a required stable `Id`, `Version`, `DisplayName`, and `Objective`, plus optional Codex instruction/model fields and bounded `ExpectedOutputs`/`RequiredCapabilities` lists. `/recipe list` and `/recipe <id>` are inspectable read-only commands. `/task new [name] [| baseRef] [| recipeId]` copies the selected recipe into the new session context and supervision record; a task keeps its original recipe ID/version after configuration changes.

For a coordinator deployment, set `CodexTelegram:Coordinator:Enabled` and a private `AuthenticationToken` on the coordinator host. Set `WorkerRegistrationEnabled`, the coordinator `Url`, and the same token on each worker; optionally list exact `AllowedWorkerIds` on the coordinator. Workers send bounded health heartbeats to `/api/coordinator/v1/workers/heartbeat` over HTTP(S). The endpoint accepts only the exact bearer token and bounded worker snapshots; stale workers appear unavailable in the read-only Mini App. Keep the URL private or behind the existing operator-controlled HTTPS boundary, and rotate the token as an operator-managed secret. A coordinator-issued lease handoff selects a fresh, ready, capacity-available worker, sends a five-minute grant to its configured `ControlPlaneUrl`, and records issued/accepted/rejected evidence. `/task remote <workerId> [name] [| baseRef] [| recipeId]` then sends bounded provisioning metadata to that worker's authenticated `/api/worker/v1/tasks/provision` endpoint. The worker re-validates identity, repository admission, recipe version, readiness, and its local lease before creating the worktree and Codex session. After provisioning, `/send <text>` and normal prompts with bounded attachments use `/api/worker/v1/sessions/send`; `/steer`, `/stop`, and confirmed `/kill` use `/api/worker/v1/sessions/control`; model/reasoning and goal controls use `/api/worker/v1/sessions/settings`; `/task status` and confirmed release/discard use `/api/worker/v1/tasks/workspace`. Every worker endpoint re-checks task ownership and command/lease identity, executes locally, and returns only bounded metadata. Worker turn events continue to `/api/coordinator/v1/worker-events`; stop/kill remain available while a worker is draining if it is still ready. Plan mode with attachments is rejected. The coordinator stores only bounded ownership/resource identifiers and event projections; attachment content is materialized on the worker and coordinator filesystem paths never cross the boundary.

For an explicitly controlled worker update, configure `CodexTelegram:Updates` with `Enabled`, the exact local `PackagePath`, the expected `TargetVersion`, the expected `ExpectedSha256`, an operator-owned writable `StageRoot`, and a separate private `InstallerAuthenticationToken`. Optional `RequiredCapabilities` prevents staging on an incompatible worker. The package must match the configured major/minor/build version and the worker must first be drained with zero active leases. `/worker update stage confirm` records a verified staged package and a copy of the current executable as the last-known-good rollback artifact. `/worker update rollback confirm` stages that rollback artifact. These commands do not stop the process or overwrite the protected installation; the service/fleet installer remains responsible for applying the package and starting the service. After the service is running, the installer must POST the running version, SHA-256, and health result to `/api/worker/v1/update/complete` with the installer token. The application records `Active` only after the version, digest, worker readiness, and health flag agree; a failed check becomes `HealthFailed` and leaves explicit rollback staging available. `codex-worker-update-state.json` records bounded outcome evidence without raw paths, credentials, or transcripts.

Task workspaces are provisioned below CodexTelegram:Workspace:TaskWorktreeRoot, or below the DataRoot/task-workspaces directory when that setting is empty. The allocator creates a task-specific Git branch/worktree and records one development port plus one database namespace in codex-task-workspaces.json. Configure a dedicated writable root for these worktrees; do not place it below the protected application installation directory. Releasing a clean worktree is safe; discarding changes requires an explicit operator action and is never inferred from a failed cleanup.

Task detail is a Codex-authoritative read projection. Opening a task validates that the thread is present in the current Codex list and does not create a local thread manifest when one is missing. The browser receives bounded timeline and diff data plus redacted artifact metadata; it does not receive artifact payloads, result URLs, or raw artifact paths.

Telegram's profile Main Mini App and bot menu button are different launch contracts and may use different initial webview presentation. The shipped dashboard is responsive for compact, full-height, and true fullscreen modes. It reports the current mode in the `Webview` badge, responds to viewport and safe-area changes, and reloads after Telegram activates the app again. Keep the static webroot installed beside the binary; this runtime behavior does not require creating directories or writing under the protected installation path.

## Stop

In an interactive terminal, press Ctrl+C.

If the app is supervised by a service manager, stop it through that service manager.

## Restart

1. Stop the process.
2. Start it again from the same working directory.
3. Confirm it loads the expected `appsettings.Local.json`.
4. Confirm it loads the expected `CodexTelegram:Workspace:DataRoot`.
5. In Telegram, run `/project current` and `/status`.

The app rehydrates conversation-to-session follows from `telegram-state.json` on startup. It does not claim to resume an in-progress Codex turn after a process restart. Persisted `Accepted`, `Queued`, `Running`, and `WaitingForInput` supervision runs are reconciled to non-terminal `Unknown` with an explicit restart outcome code, so an operator can distinguish an interrupted process from confirmed Codex completion. The service never blindly resends an ambiguous side-effecting command.

## Local State

The important local files are under `CodexTelegram:Workspace:DataRoot`:

1. `projects.json`
2. `telegram-state.json`
3. Per-thread manifest files
4. `codex-supervision-state.json`
5. `codex-worker-state.json`
6. `codex-worker-update-state.json`

`telegram-state.json` also contains a bounded Telegram update-receipt ledger. It is transport replay protection, not proof that a Codex command executed. A completed receipt is retained for seven days; an interrupted in-flight receipt can be reclaimed after fifteen minutes. Do not edit the JSON state file while the service is running.

`codex-supervision-state.json` contains the separate bounded supervision projection for prompt commands: application-owned task, run, and command IDs, Codex thread/turn provenance, lifecycle states, delivery acknowledgements, approval/input decisions, lease-bound task claims, recovery-action evidence, and safe outcome codes. It does not contain prompt or response bodies, attachment paths, credentials, or authorization headers. Delivery state is independent from execution state, so a failed Telegram send does not rewrite a completed Codex run. `Unknown` means that an external Codex outcome needs explicit reconciliation; it is not a successful completion and is never silently replayed. Claims fail closed when another unexpired user claim owns the task, and all bounded records are scoped back to the authorized Telegram user/conversation. Back up the file with the rest of the data root, and do not edit it while the service is running.

Back up that folder before moving machines or changing the data root.

Transient Telegram audio, downloaded attachments, and outbound media use an instance-specific temporary directory when `DataRoot` or `InstanceId` is configured. The legacy shared temporary location is retained only when neither selector is supplied.

## Token Rotation

1. Use BotFather to revoke or rotate the Telegram bot token.
2. Update `TelegramBot:Token`, `TELEGRAM_BOT_TOKEN`, or the corresponding secret store value.
3. Restart the process.
4. Send `/whoami` from an allowed user to confirm the bot is responding.

If an OpenAI key is rotated, update `OpenAI:ApiKey` or `OPENAI_API_KEY` and restart before testing voice transcription.

## Group And Forum Operations

Private chat is the primary setup mode. A trusted group root can also be used as a project/session lane, and forum topics can split one group into multiple lanes.

For groups and forum topics:

1. Add only trusted users to `TelegramBot:AllowedUserIds`.
2. Trust the group with `/trust` from an allowlisted admin account, or add the group chat ID to `TelegramBot:AllowedChatIds`.
3. Keep Telegram privacy mode enabled unless ordinary group-root text should route to Codex.
4. Grant topic-management rights only if `/topic new` is part of the supported workflow.

The app owns runtime commands and `/help`. Guided setup can apply the app-owned command list and menu button through the Bot API, but there is no background profile/command synchronization. Apply [botfather.md](botfather.md) manually for profile text, privacy/group settings, or skipped/failed operations, and retain slash commands as the fallback when Telegram's picker is stale.

Group and forum messages require both an allowed user and a trusted chat.

## Health Checks

Use these Telegram commands during operation:

1. `/whoami` to confirm user, chat, and topic IDs.
2. `/version` to confirm which app binary is answering in Telegram.
3. `/project current` to confirm the working directory binding.
4. `/status` to confirm the active session state.
5. `/outbound` to inspect delayed Telegram output.
6. `/usage` to inspect five-hour and weekly Codex usage percentages and reset timing.
7. `/output mode` to confirm whether the bot is in `Compact`, `Verbose`, `LiveCard`, `Balanced`, or `FinalOnly` mode and whether a runtime override is active.
8. `/turn updates` or `/turn full` to inspect retained operational turn history.
9. `/tail` to inspect recent session output.

If Telegram output looks delayed or incomplete, use `/status`, `/outbound`, `/turn final`, and `/tail` before changing configuration. Those commands separate Codex completion, retained final-response capture, delivery backlog, and Codex session-output questions.

### Output presentation and formatting

Use `/output mode balanced` when operators need lifecycle and tool milestones without the message volume of `Verbose`. `milestones` and `milestone` are aliases. Use `/output mode reset` to clear a runtime presentation override and return to `TelegramOutput:PresentationMode`.

`TelegramOutput:TextFormat` is independent and defaults to `PlainText`. `SafeMarkdownV2` is a constrained formatter for headings, emphasis, HTTP(S) links, lists, and code. It escapes unsupported or malformed markup. If a formatted payload cannot be split safely, or the active Telegram client does not support formatted sends, the payload falls back to plain-text chunks. A formatted appearance is not proof of delivery; use `/outbound`, `/trace`, and the manual plan for delivery evidence.

Use these local commands before a release or demo:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

Use `-SkipPublish` when you only need the build, test, format, and package-vulnerability checks.

Record evidence by type. Automated build/tests prove repository behavior; synthetic Codex tests prove test-double seams; a local live smoke proves the configured process can reach a real bot/Codex account; and group/forum claims require a real Telegram chat with the relevant BotFather settings. Do not report a skipped live step as passed.

Codex authentication is owned by the local Codex installation, not this app. Verify it under the same OS account and environment as the bot. If instances need distinct Codex identities, isolate them with separate OS accounts or a Codex-supported per-process auth location; never back up or publish auth state with app data.
