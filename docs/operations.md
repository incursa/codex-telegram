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

From source:

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

Without `--run`, an interactive terminal opens the bootstrap/admin menu.

## Stop

In an interactive terminal, press Ctrl+C.

If the app is supervised by a service manager, stop it through that service manager.

## Restart

1. Stop the process.
2. Start it again from the same working directory.
3. Confirm it loads the expected `appsettings.Local.json`.
4. Confirm it loads the expected `CodexTelegram:Workspace:DataRoot`.
5. In Telegram, run `/project current` and `/status`.

The app rehydrates conversation-to-session follows from `telegram-state.json` on startup. It does not resume an in-progress Codex turn after a process restart.

## Local State

The important local files are under `CodexTelegram:Workspace:DataRoot`:

1. `projects.json`
2. `telegram-state.json`
3. Per-thread manifest files

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
