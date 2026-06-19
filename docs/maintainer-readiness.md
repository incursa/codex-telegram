# Maintainer Readiness

This guide is for maintainers and operators validating `codex-telegram` from a local checkout. Use local commands and manual Telegram evidence as proof; do not rely on GitHub Actions alone.

## Service Purpose

Incursa Codex Telegram is a standalone console service that lets allowlisted Telegram users start, steer, inspect, and stop local Codex sessions from Telegram. The service runs on an operator-owned machine, uses local state, and drives a local Codex runtime.

The service does not provide hosted Codex, Telegram account management, BotFather token management, OpenAI account management, or remote deployment automation.

## Service Boundaries

- `src/Incursa.Codex.Telegram`: application host, configuration binding, Codex integration, Telegram update handling, output delivery, local state, tracing, and transcription integration.
- `tests/Incursa.Codex.Telegram.Tests`: unit and integration-style tests using local test doubles for command handling, authorization, output relay, queueing, state, options, fuzz corpus, and service behavior.
- `scripts`: local release, publish, fuzz, mutation, and tracked-file secret-scan scripts.
- `docs`: user, operator, command, menu, testing, and maintainer guides.
- `specs`: repository-native requirements and architecture notes for selected behavior.
- `fuzz/corpus`: checked-in Telegram seed inputs used by the deterministic fuzz corpus test path.

The app depends on:

- a local `codex` executable and local Codex authentication;
- a Telegram bot token from BotFather;
- allowlisted Telegram user IDs, and optionally allowlisted chat IDs;
- optional `OPENAI_API_KEY` and `ffmpeg` for voice-note transcription.

## Runtime Modes And User-Facing Behavior

Launch modes:

- No arguments in an interactive terminal: open the bootstrap/admin menu.
- `--run`: skip the menu and start the hosted services directly.
- `--menu`: force the bootstrap/admin menu.
- `--help`, `-h`, or `/?`: print command-line help.

Telegram operating scopes:

- Private chat is the primary supported setup and operating path.
- Trusted group-root sessions are supported when an allowlisted user sends from a trusted chat.
- Forum-topic sessions are supported when the bot has the required Telegram permissions and the chat is trusted.

Output behavior:

- Telegram output is queued, rate-limited, chunked, and delivered through the outbound queue.
- `/tail`, `/turn`, `/status`, and `/outbound` are the operator tools for separating Codex output, retained turn history, session state, and Telegram delivery backlog.
- `/output mode` shows or changes the effective presentation mode at runtime. Check `docs/usage.md`, `docs/command-reference.md`, and `src/Incursa.Codex.Telegram/Options/TelegramOutputOptions.cs` before changing or documenting mode behavior.
- Voice notes are transcribed before being sent to Codex. Codex receives transcript text, not raw Telegram audio.

## Architecture

At startup, `Program.cs` builds a generic host, loads configuration from appsettings, local settings, user secrets, environment variables, and command-line arguments, then registers the service graph.

Important hosted services:

- `CodexWarmupHostedService`: initializes Codex when configured.
- `TelegramCodexBotHostedService`: receives Telegram updates through long polling.
- `TelegramInputBundleAutoDispatchHostedService`: dispatches bundled input after the configured delay.
- `TelegramQueuedPromptProcessorHostedService`: processes queued prompts.
- `TelegramTypingHeartbeatHostedService`: keeps Telegram typing indicators active while work is running.
- `OutboundTelegramDeliveryHostedService`: drains the outbound Telegram queue.
- `CodexSessionRuntimeRegistry`: owns active Codex session runtime state and reattachment behavior.

Important local-state files under `CodexTelegram:Workspace:DataRoot`:

- `projects.json`: known project/workspace paths.
- `telegram-state.json`: conversation-to-session follows, trusted chats, queued prompts, and related Telegram state.
- per-thread manifest directories/files: session metadata needed for Telegram-managed Codex sessions.
- input-bundle and trace files when those features are enabled.

Process restart rehydrates stored conversation/session follows and can reattach persisted active-turn state when supported by the Codex SDK. It does not guarantee that a mid-turn Codex execution continues after the process exits.

## Configuration And Secrets

Default configuration lives in `src/Incursa.Codex.Telegram/appsettings.json`. Local secrets belong in `appsettings.Local.json`, user secrets, environment variables, or an operator-managed secret store.

Required runtime settings:

- `TelegramBot:Enabled`
- `TelegramBot:Token` or `TELEGRAM_BOT_TOKEN`
- `TelegramBot:AllowedUserIds` or `TELEGRAM_ALLOWED_USER_IDS`
- `CodexTelegram:Workspace:WorkspaceRoots`
- `TelegramBot:DefaultWorkingDirectory` or `CodexTelegram:Context:WorkingDirectory`
- `TelegramBot:CodexExecutablePath`, `Codex:CodexPathOverride`, `CODEX_PATH`, or a `codex` executable on `PATH`

Optional voice transcription settings:

- `OpenAI:ApiKey` or `OPENAI_API_KEY`
- `OpenAI:Model`
- `OpenAI:FfmpegPath`

Security expectations:

- Do not commit `appsettings.Local.json`.
- Do not commit Telegram bot tokens, OpenAI API keys, local Codex auth state, private paths, debug traces, private transcripts, or screenshots from non-demo repositories.
- Keep `TelegramBot:AllowedChatIds` empty unless group or forum support is intentional.
- Keep workspace roots narrow enough that the bot cannot browse or route prompts into unrelated directories.

## Local Development

Restore, build, and test:

```powershell
dotnet restore CodexTelegram.slnx
dotnet build CodexTelegram.slnx -c Release -m:1 --no-restore
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore -m:1
```

Run from source:

```powershell
dotnet run --project src\Incursa.Codex.Telegram
dotnet run --project src\Incursa.Codex.Telegram -- --run
dotnet run --project src\Incursa.Codex.Telegram -- --help
```

Run focused tests:

```powershell
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TelegramCommandHandlerTests
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TelegramTurnOutputRelayTests
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TelegramFuzzCorpusTests
```

Run local quality checks:

```powershell
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release -NoRestore -NoBuild
dotnet format CodexTelegram.slnx --verify-no-changes --no-restore --include src\Incursa.Codex.Telegram tests\Incursa.Codex.Telegram.Tests
dotnet list CodexTelegram.slnx package --vulnerable --include-transitive
.\scripts\Test-TrackedSecretScan.ps1
git diff --check
```

## Release Readiness

The repo-native release gate is:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

That script runs:

1. Release build.
2. Unit tests.
3. Telegram fuzz corpus.
4. Format verification for this repository's source and tests.
5. Package vulnerability report.
6. Tracked-file secret scan.
7. Publish verification unless `-SkipPublish` is passed.

Use the publish script directly when validating packaging or refreshing a local binary:

```powershell
.\scripts\Publish.ps1 -Runtime win-x64
.\scripts\Publish.ps1 -Runtime win-x64 -StopRunningProcess
```

`-StopRunningProcess` is intended for the published executable at the target path. It should not be treated as a general process killer.

Before a tag or public release, also run the manual Telegram checklist in `docs/manual-test-plan.md` against the exact commit or published asset being released.

## Deployment And Operator Checks

This service is deployed by placing the published binary and local settings on an operator-owned machine. It is not a self-updating daemon.

Operator checklist:

1. Verify the published asset checksum.
2. Confirm `codex --version` and an interactive `codex` command work for the same account that runs the bot.
3. Confirm `appsettings.Local.json` is loaded from the intended directory.
4. Confirm `CodexTelegram:Workspace:DataRoot` points to the intended durable state folder.
5. Start with `--run`.
6. In Telegram, run `/whoami`, `/doctor`, `/project current`, `/status`, `/usage`, `/output mode`, `/outbound`, and `/tail`.
7. Run a short private-chat prompt before enabling any group or forum workflow.

For restarts, stop the process through the terminal or supervisor, start it again from the same working directory, then verify `/project current`, `/status`, and `/tail`.

## Troubleshooting

- Bot does not respond: verify `TelegramBot:Enabled`, token, network access, BotFather token validity, and allowed user IDs.
- User is ignored: run `/whoami` and compare the numeric user ID with `TelegramBot:AllowedUserIds`.
- Group or topic is ignored: confirm the user is allowed and the chat is trusted through `/trust` or `TelegramBot:AllowedChatIds`.
- Codex does not start: verify `codex --version`, `CODEX_PATH`, `TelegramBot:CodexExecutablePath`, sandbox settings, and the working directory.
- Output appears missing: inspect `/status`, `/outbound`, `/turn final`, and `/tail` before assuming Codex lost data.
- Voice transcription fails: verify `OpenAI:ApiKey`, model, audio duration limits, file size, and `ffmpeg` availability.
- Publish fails on Windows because the binary is locked: stop the running published executable or rerun `scripts/Publish.ps1` with `-StopRunningProcess`.
- Debug output is insufficient: use `/debug`, `/trace`, and `TelegramDebugTrace` settings, but avoid full capture unless private transcript storage is acceptable.

## Known Gaps And Future Work

- Live Telegram behavior still requires manual evidence with a real bot account and real credentials.
- Group and forum-topic behavior is supported but higher risk than private chat; validate it separately before claiming release readiness.
- Voice transcription depends on external OpenAI and `ffmpeg` behavior.
- Mutation testing is advisory and slower than the normal release gate; use targeted profiles for behavior-sensitive changes.
- The docs and code should stay synchronized when output modes, session cards, completion markers, debug capture, or publish-lock behavior changes.
- Repository hygiene still lacks `.gitattributes`, `.editorconfig`, `SUPPORT.md`, `NOTICE.md`, and `specs/README.md`; add them as focused follow-up work if they become part of the maintenance standard for this service.
