# Getting Started

This is the operator guide for a fresh install of `Incursa.Codex.Telegram`.
It is intentionally detailed. If you are new to the repo, read it straight through once before you start changing settings.

The flow is:

1. Create a Telegram bot with BotFather.
2. Start the app once so you can discover your Telegram IDs.
3. Add your user ID to the allowlist.
4. Configure Codex, workspaces, and optional OpenAI transcription.
5. Start the bot in private chat first.
6. Add group and forum-topic support only after the private flow works.

After that first setup succeeds, use [usage.md](usage.md) for the normal day-to-day operator workflow.

## What This App Does

`Incursa.Codex.Telegram` is a console host for a local Codex installation.

It can:

1. Accept Telegram messages from allowlisted users and optional allowlisted chats.
2. Create and select Codex sessions.
3. Route plain text, attachments, and voice notes into the active session.
4. Keep per-conversation session state so a private chat, trusted group root, or forum topic can continue independently.
5. Stream and queue Codex session updates back to Telegram.

It does not:

1. Bundle the `codex` executable.
2. Bundle Telegram credentials.
3. Bundle an OpenAI API key.
4. Bundle optional audio tools such as `ffmpeg`.
5. Remove the need for a local machine where the bot will run.

## Before You Start

Have these ready before you touch the config:

1. A Telegram account.
2. A Telegram bot token from BotFather.
3. Your numeric Telegram user ID.
4. A local Codex CLI installation that already works in a terminal.
5. An OpenAI API key if you want voice-note transcription.
6. `ffmpeg` on `PATH` if you want reliable voice-note support and Telegram audio must be transcoded.
7. At least one directory you want to use as a workspace root.

If you plan to use a group or forum supergroup, also decide whether the bot should be allowed to see plain text messages in that group.

## Create Your Telegram Bot

Use Telegram's official bot management account, `@BotFather`.

Telegram's official docs for bot setup and privacy behavior are here:

1. [Telegram Bot Features](https://core.telegram.org/bots/features)
2. [Telegram Bot API](https://core.telegram.org/bots/api)
3. [Telegram Bot FAQ](https://core.telegram.org/bots/faq)

Follow this sequence:

1. Open a chat with `@BotFather`.
2. Send `/newbot`.
3. Pick a display name.
4. Pick a username that is unique and ends in `bot`.
5. Copy the token BotFather gives you.
6. Keep that token private. Anyone with the token can control the bot.

The app now syncs its command list, description, about text, and conservative group-admin defaults automatically on startup, so you usually do not need to set those by hand.

Optional BotFather steps that are still worth doing:

1. `/setuserpic` to give the bot a recognizable avatar.
2. `/revoke` if you ever leak the token and need to rotate it.

Copy-paste BotFather text, command lists, and recommended first-release settings are in [botfather.md](botfather.md).

If you expect to use the bot in groups or forums, also review:

1. `/setjoingroups` to control whether the bot can be added to groups.
2. `/setprivacy` to control whether the bot can read ordinary group text.

Privacy mode matters most in groups and forum supergroups:

1. Private chats are fine either way.
2. In groups, privacy mode usually limits the bot to commands, mentions, replies, and service messages.
3. If you need the bot to read normal group text, disable privacy or make sure the group setup matches your intended workflow.
4. If you change privacy settings, Telegram may require you to re-add the bot for the change to fully take effect.

## Discover Your Telegram IDs

The app trusts allowlisted users in private chats. Groups and forum topics also need a trusted chat, either from `TelegramBot:AllowedChatIds` or from `/trust` sent in that chat by an allowlisted user.

BotFather gives you the bot token, but it cannot give you your personal Telegram user ID. Most Telegram clients also do not show the numeric user ID directly.

The easiest bootstrap path is the first-run wizard:

1. Start the app with no local settings file beside the executable.
2. Paste the BotFather token when the wizard asks for it.
3. Let the wizard show a random setup code and wait for one private message to the bot.
4. Send that exact setup code to the bot in a private chat.
5. Confirm that the wizard captured and saved your numeric user ID.
6. If you want the bot in a group, add the bot to the group and send `/trust` there from the allowlisted admin account.

The manual fallback is still available:

1. Leave `TelegramBot:AllowedUserIds` empty only long enough to complete the setup-code capture or run `/whoami`.
2. Copy the numeric user ID from the reply.
3. Add that user ID to the allowlist.

Important details:

1. `/whoami` is deliberately reachable before the allowlist exists, so you can discover your ID.
2. Once the allowlist is populated, unauthorized users are ignored.
3. Telegram user IDs are numeric.
4. Telegram chat IDs are numeric too, and group chat IDs are often negative.
5. Forum topics also have a thread ID, which `/whoami` will show when you run it inside the topic.

## Choose a Configuration Method

You can configure the app in four ways.

### 1. `appsettings.Local.json`

This is the simplest path for a single machine.

The file is resolved beside the executable by default. If the executable-local file is missing and the shell's current working directory contains `appsettings.Local.json`, that launch-directory file is used.
The interactive bootstrap menu also writes `appsettings.Local.json` beside the executable, so command-line launches from another directory still use the same local settings.

Use this when:

1. You want one local config file.
2. You do not mind keeping secrets in a machine-local JSON file.
3. You are running the bot from a dedicated app folder.

### 2. The interactive bootstrap menu

If you start the app without `--run` and the terminal is interactive, the app opens a menu.

Use this when:

1. You want the app to create or edit `appsettings.Local.json` for you.
2. You want a guided setup path.
3. You do not want to memorize every config key immediately.

When no local settings file exists yet, the first-run wizard asks for the Telegram token, validates it with Telegram, captures your admin user ID from a private bot message, optionally stores an OpenAI key for voice notes, and asks for explicit workspace roots.

The menu has sections for:

1. Telegram and admin allowlists.
2. OpenAI transcription.
3. Codex runtime.
4. Workspaces.

The Workspaces section is where you tell the bot which local folders are safe for project selection. Use a parent source directory such as `C:\src`, `~/src`, or `/Users/you/src` when most repositories live together; use specific repository paths when you want tighter scope. The local data root is separate and stores the persisted project catalog, conversation bindings, queued prompts, and thread manifests.
The model prompts are picker-based for the common cases, so you can choose a known transcription model, a default Codex model, or a thinking-effort preset without typing blind. When Codex is reachable, the picker uses the live model list and the model's reported effort choices; otherwise it falls back to curated examples. Custom values are still allowed when you need them.

The menu understands `!clear` when a field prompt says it can be cleared.

### 3. User secrets

Use this during source-based development when you do not want values in plain JSON.

Example:

```powershell
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:Enabled" "true"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:Token" "<telegram-bot-token>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:AllowedUserIds:0" "<your-user-id>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "OpenAI:ApiKey" "<openai-api-key>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "CodexTelegram:Workspace:WorkspaceRoots:0" "C:\src"
```

### 4. Environment variables

Use this when you want the bot to run like a service, container, or scheduled process.

The configuration sources are layered in this order:

1. `appsettings.json` defaults.
2. `appsettings.Local.json` beside the executable.
3. User secrets.
4. Environment variables with the `CODEX_TELEGRAM_` prefix.
5. Command-line arguments.

Later sources win.

The app also has a few direct environment-variable fallbacks for common secrets:

1. `TELEGRAM_BOT_TOKEN`
2. `TELEGRAM_ALLOWED_USER_IDS`
3. `TELEGRAM_ALLOWED_CHAT_IDS`
4. `OPENAI_API_KEY`
5. `CODEX_PATH`

Examples:

```powershell
$env:TELEGRAM_BOT_TOKEN = "<telegram-bot-token>"
$env:TELEGRAM_ALLOWED_USER_IDS = "<your-user-id>"
$env:TELEGRAM_ALLOWED_CHAT_IDS = "-1001234567890"
$env:OPENAI_API_KEY = "<openai-api-key>"
$env:CODEX_PATH = "C:\path\to\codex.exe"
$env:CODEX_TELEGRAM_TelegramBot__Enabled = "true"
$env:CODEX_TELEGRAM_CodexTelegram__Workspace__WorkspaceRoots__0 = "C:\src"
```

If you use the prefixed form, remember that nested keys use double underscores.

## Minimal Config File

If you prefer to edit JSON by hand, this is a good starting point:

1. Copy `appsettings.Local.example.json` to `appsettings.Local.json`.
2. Replace the placeholders with your real values.
3. Keep the real file untracked.

```json
{
  "Codex": {
    "CodexPathOverride": ""
  },
  "TelegramBot": {
    "Enabled": true,
    "Token": "replace-with-your-telegram-bot-token",
    "AllowedUserIds": [
      123456789
    ],
    "AllowedChatIds": [],
    "DefaultWorkingDirectory": "C:\\src\\your-repo",
    "CodexExecutablePath": "",
    "MinAudioDurationSeconds": 1,
    "MaxAudioDurationSeconds": 600
  },
  "OpenAI": {
    "ApiKey": "replace-with-your-openai-api-key",
    "Model": "whisper-1",
    "FfmpegPath": "ffmpeg"
  },
  "CodexTelegram": {
    "InitializeOnStart": true,
    "Context": {
      "WorkingDirectory": "C:\\src\\your-repo",
      "Sandbox": "workspace-write",
      "ApprovalMode": "on-request"
    },
    "Workspace": {
      "WorkspaceRoots": [
        "C:\\src"
      ]
    }
  }
}
```

Configuration behavior:

1. `TelegramBot.Enabled` must be `true` or the bot will not poll Telegram.
2. `TelegramBot.Token` is the BotFather token.
3. `TelegramBot.AllowedUserIds` should contain your numeric Telegram user ID.
4. `TelegramBot.AllowedChatIds` is the config-managed group/forum allowlist. You can also trust a group from Telegram with `/trust` after the admin user is allowlisted.
5. `TelegramBot.DefaultWorkingDirectory` is the fallback working directory for new sessions that do not already have a project selected.
6. `Codex:CodexPathOverride` is the preferred place to point at a local `codex` executable if it is not on `PATH`.
   The app also accepts `TelegramBot:CodexExecutablePath` and `CODEX_PATH` as fallbacks.
7. `OpenAI:ApiKey` is required for voice-note transcription.
8. `OpenAI:Model` defaults to `whisper-1`.
9. `OpenAI:BaseUrl` defaults to `https://api.openai.com/v1/`.
10. `OpenAI:FfmpegPath` defaults to `ffmpeg`.
11. `TelegramBot:MinAudioDurationSeconds` and `TelegramBot:MaxAudioDurationSeconds` reject suspiciously short or long Telegram audio before download.
12. `CodexTelegram:InitializeOnStart` controls whether the Codex gateway initializes during startup. Leave it `true` for normal bot use.
13. `CodexTelegram:Context:WorkingDirectory` is the default Codex working directory.
14. `CodexTelegram:Workspace:WorkspaceRoots` are the directories users may add as projects.
15. The Codex submenu will query live model names and effort choices when the configured executable is reachable.

## First Launch Checklist

When you launch the app for the first time, work through this list in order.

1. Make sure the `codex` CLI works in a normal terminal session.
2. Make sure the Telegram bot token is configured.
3. Make sure `TelegramBot.Enabled` is `true`.
4. Start with an empty `AllowedUserIds` list only long enough to complete the setup-code capture or run `/whoami`.
5. Put your own user ID into `AllowedUserIds`.
6. Add any group chat IDs you want the bot to trust.
7. Set a workspace root that includes the repositories you want to work on.
8. Set a default working directory for Codex.
9. Configure OpenAI only if you plan to use voice notes.
10. Start the bot.

If the menu shows warnings, do not ignore them casually. They usually mean one of the required pieces is still missing.
If you leave workspace roots or the default working directory unset, the runtime falls back to the process current directory. That fallback exists for development convenience only; public, shared, or recorded setups should use explicit roots and an explicit default working directory.

## Run It

### Run From Source

```powershell
dotnet run --project src\Incursa.Codex.Telegram
```

That normally opens the bootstrap menu.

Use this if you want to edit config interactively before starting the bot.

### Run Directly

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

That skips the menu and starts the bot directly.

### Force The Menu

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --menu
```

### Show Help

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --help
```

### Published Binary

If you are using a published build, the binary is named `codex-telegram` on Unix-like systems and `codex-telegram.exe` on Windows.

Example:

```powershell
.\artifacts\publish\win-x64\codex-telegram.exe --run
```

The GitHub Actions release workflow currently publishes:

1. Windows x64.
2. Linux x64.
3. macOS arm64.

## Set Up Workspaces And Codex Defaults

Workspaces are the directories the bot considers safe and intentional for project selection.

Do this before you start using the bot seriously:

1. Add the parent directories where your real work lives to `CodexTelegram:Workspace:WorkspaceRoots`.
2. Set `CodexTelegram:Context:WorkingDirectory` to the directory you want Codex to start in by default.
3. Set `TelegramBot:DefaultWorkingDirectory` if you want brand-new sessions to fall back to a specific directory when no project is selected yet.
4. If `codex` is not already on `PATH`, set `Codex:CodexPathOverride`, `TelegramBot:CodexExecutablePath`, or `CODEX_PATH`. The startup menu uses that effective executable path for live Codex model discovery.

Useful related settings:

1. `CodexTelegram:Context:Sandbox` defaults to `workspace-write`.
2. `CodexTelegram:Context:ApprovalMode` defaults to `on-request`.
3. `CodexTelegram:Context:Model` lets you pin a default model.
4. `CodexTelegram:Context:ReasoningEffort` lets you pin a default reasoning effort.
5. `CodexTelegram:Context:NetworkAccessEnabled` can override the Codex default network posture.
6. `CodexTelegram:Context:WebSearchEnabled` and `CodexTelegram:Context:WebSearchMode` can be used if your Codex workflow expects web search.
7. `CodexTelegram:Context:AdditionalDirectories` can grant Codex extra read access when needed.
8. `CodexTelegram:Workspace:DataRoot` moves the local state files somewhere other than the default user application data folder.

The bootstrap menu offers direct pickers for the common values in items 3 and 4, which avoids typing model IDs or effort names from memory.

## Verify The Private Chat Flow

After the bot is running, test it in a private chat before you move on to groups.
For release validation, use the fuller checklist in [manual-test-plan.md](manual-test-plan.md).
For everyday operation after this smoke test, use [usage.md](usage.md).

Suggested sequence:

1. Send `/projects`.
2. Add one known repository with `/project add <absolute path>`.
3. Confirm the selected project with `/project current`.
4. Start a session with `/new`, or use `/new <name>` when you want a specific name.
5. Send a plain text message.
6. Confirm the bot replies with active-session output.
7. Try `/doctor`, `/tail`, `/status`, `/model`, and `/thinking`.

If that flow works, the core integration is healthy.

If the bot never replies, stop and check:

1. Token.
2. `TelegramBot.Enabled`.
3. Allowlist.
4. Current working directory.
5. Whether you are talking to the right bot.

## Use The Bot In A Group

If you want the bot in a group chat, add the bot to the group and send `/trust` there from an allowlisted admin account. Group and forum-topic messages require both an allowed user and a trusted chat.

Group behavior:

1. The bot should still be tested privately first.
2. Group IDs are numeric and often negative.
3. A group must be trusted in addition to the individual Telegram user.
4. Plain text in a trusted group root can auto-route to that group's active session, which makes one group per project a simple workflow.
5. If the group is busy, privacy-mode behavior and allowlists matter more than in a private chat.

Recommended group workflow:

1. Add the bot to the group.
2. If necessary, make it an admin with topic-management rights.
3. Send `/trust` in the group from the allowlisted admin account.
4. Decide whether privacy should stay enabled. With privacy enabled, Telegram may only deliver commands and mentions to the bot.
5. Use `/project add <path>` or `/projects` in the group root, then send a normal message or `/new` to start the group's default session.

## Use The Bot In Forum Topics

Forum topics are the cleanest way to run multiple Codex threads inside one supergroup.

The supported flow is:

1. Create or convert the group into a forum-enabled supergroup.
2. Make sure the bot has the rights needed to create or manage topics.
3. Use `/topic new <name>` to create a new topic and matching Codex session.
4. Optionally use `/topic new <name> | <absolute directory path>` to start the topic in a specific project.
5. Use `/topic attach [sessionId]` to bind the current topic to an existing session.
6. Use `/topic current` to see the active binding.
7. Use `/launchpad on` in the group root when you want to spawn several independent topics quickly; then send a plain-text or voice launch message, or use `/launch <name>`, for each new detached worktree-backed lane. Launchpad titles the new topic from the group name plus a lane number so long prompts do not become the topic name.

Behavior to remember:

1. `/topic new` only works in a forum-enabled supergroup.
2. If the bot does not have the needed rights, topic creation will fail.
3. If ordinary text appears to do nothing in a topic, use `/send <text>` or revisit privacy settings.
4. If Telegram rejects a reply to a stale, closed, or deleted topic, the bot does not retry the message in the group root.
5. The topic thread ID is useful when you want to debug or trace where messages are going.
6. Launchpad mode automatically turns off after 10 minutes of inactivity.

## Voice Notes, Audio, And Attachments

The bot handles more than plain text.

1. Voice notes are transcribed before they are sent to Codex; Codex receives the resulting text, not raw Telegram audio.
2. Audio transcription requires an OpenAI API key.
3. `ffmpeg` is used only when the bot needs to transcode downloaded audio into a format OpenAI accepts directly. Telegram voice notes commonly need this because they often arrive as OGG/OPUS.
4. If `ffmpeg` is missing when conversion is needed, the bot replies with setup guidance and does not send the audio message to Codex.
5. The bot rejects audio shorter than `TelegramBot:MinAudioDurationSeconds` or longer than `TelegramBot:MaxAudioDurationSeconds` before download.
6. Images and documents are forwarded to Codex.
7. Large media files still have practical upload and API limits, so keep expectations realistic.

If voice transcription fails, check these first:

1. `OpenAI:ApiKey`.
2. `OpenAI:Model`.
3. `OpenAI:FfmpegPath` if the failing audio format needs transcoding.
4. Whether `ffmpeg` is actually available on the machine that runs the bot.

## Command Reference

The bot's built-in help text is the final authority, but this is the practical summary.
For workflow-oriented usage after setup, see [usage.md](usage.md).
For a complete parameter-by-parameter reference, see [command-reference.md](command-reference.md).

| Command | What it does | When to use it |
| --- | --- | --- |
| `/help` | Shows the supported commands and basic usage. | Start here if you forget the syntax. |
| `/whoami` | Shows your Telegram user ID, chat ID, and topic thread ID. | Use this during bootstrap and debugging. |
| `/version` | Shows the running app version. | Use when Telegram behavior does not match the docs or release notes. |
| `/projects` | Lists configured project directories. | Use before selecting or adding a project. |
| `/project add <path>` | Adds a project directory and selects it. | Use when you want to make a repo available to Codex. |
| `/project <number|name|path>` | Selects a known project. | Use when multiple projects exist. |
| `/project current` | Shows the current project binding. | Use to confirm what the conversation is anchored to. |
| `/topics` | Lists Telegram topics and chat sessions in the conversation. | Use in chats with multiple topic threads. |
| `/topic list` | Same as `/topics`. | Use whichever form is easiest to remember. |
| `/launchpad on` | Arms the group root for repeated plain-text or audio launches and `/launch` commands. | Use when you want to fan out multiple detached worktree-backed topic/session lanes quickly. |
| `/launch <name> \| <path>` | Creates a new topic, session, and detached git worktree while launchpad is armed. | Use with or without an explicit project path. |
| `/topic new <name>` | Creates a new forum topic and a new Codex session. | Use in a forum-enabled supergroup. |
| `/topic new <name> | <path>` | Creates a new topic and session in a specific project directory. | Use when you want the topic tied to a particular repo. |
| `/topic attach [sessionId]` | Binds the current forum topic to an existing Codex session. | Use when you want a topic to resume work instead of creating a new session. |
| `/topic current` | Shows the active topic/session binding. | Use to confirm where the topic is connected. |
| `/sessions` | Shows active and Telegram-managed sessions. | Use to resume or inspect current work. |
| `/sessions all [count]` | Shows recent Codex history. | Use when you need older sessions that are not active. |
| `/new [name]` | Creates and selects a new Codex session. | Omit the name to auto-generate one from the active project. |
| `/use <sessionId>` | Selects an existing session. | Use to continue a previous thread. |
| `/send <text>` | Sends text to the active session. | Use when plain text is not automatically routed. |
| `/steer <text>` | Adds steering text to the active turn. | Use when you want to guide a live session. |
| `/queue` | Shows queued prompts with Send now, Edit, and Delete buttons. | Use when you want to inspect or change prompts waiting behind an active turn. |
| `/model [model] [thinking <effort>]` | Shows or changes the selected session model. | Use when you need to switch the active model. |
| `/thinking <minimal|low|medium|high|xhigh>` | Changes the reasoning effort for the selected session. | Use when you want more or less reasoning budget. |
| `/goal [objective|clear|pause|resume|complete]` | Shows or changes the selected session goal. | Use when you want Codex to keep a thread-level objective. |
| `/tail [count]` | Shows recent output and keeps following the session. | Use while waiting on a live turn. |
| `/status [sessionId]` | Shows session status and compact Codex usage when available. | Use when you want a quick health check. |
| `/usage` | Shows five-hour and weekly Codex usage and reset times. | Use when planning around Codex usage blocks. |
| `/doctor` | Explains authorization, routing, active project/session, workspace roots, outbound queue state, and the next best action. | Use when setup, group routing, or output delivery feels unclear. |
| `/outbound` | Shows outbound Telegram queue status. | Use when messages seem delayed or missing. |
| `/stop [sessionId]` | Gracefully stops a session. | Use when you want to end work cleanly. |
| `/restart confirm` | Explains that restart is managed outside this standalone process. | Use when you need the correct restart procedure for your terminal, service manager, or scheduled task. |
| `/kill <sessionId> confirm` | Hard-stops a session. | Use only when graceful stop is not enough. |
| `/rename <sessionId> <new name>` | Renames a session. | Use to make a session list easier to scan later. |
| `/forget <sessionId>` | Hides a stopped or exited session without deleting logs. | Use when you want to clean up the visible list. |

There are also convenience behaviors that do not require a command:

1. Plain text in a private chat usually continues the active session.
2. Plain text in a topic usually continues that topic's session.
3. If there is no active session yet, the first message can create one automatically.
4. Images, documents, and other attachments are forwarded to Codex.
5. Voice notes are transcribed first.
6. You can also use the inline control phrase `Codex settings model <model> thinking <effort>: <prompt>` if you want to steer a turn in the message text itself.

## Local State

The app keeps state on disk so it can remember projects, sessions, and conversation bindings.

The important files are:

1. `appsettings.Local.json` for local configuration.
2. `projects.json` for the project catalog.
3. `telegram-state.json` for Telegram conversation state.
4. Thread manifest files for Codex session tracking.

The data root defaults to the user's application data folder unless you override `CodexTelegram:Workspace:DataRoot`.
The active topic/session follow map is rehydrated from `telegram-state.json` on startup, but it is still derived state rather than a separate durable file.

Important guarantees:

1. Secret values are not supposed to be written into the local state files.
2. The local state is machine-local.
3. If you move the data root, move it intentionally and keep the old path around until you know the new one works.

For restart, backup, and token-rotation procedures, see [operations.md](operations.md).

## Advanced Runtime Tuning

Most new users can ignore this section.

If you are tuning a busier bot or a noisy session, the outbound queue settings matter:

1. `TelegramBot:Outbound:Enabled`
2. `TelegramBot:Outbound:GroupMinimumSendIntervalSeconds`
3. `TelegramBot:Outbound:PrivateMinimumSendIntervalSeconds`
4. `TelegramBot:Outbound:GlobalMaxMessagesPerSecond`
5. `TelegramBot:Outbound:MaxMessageChars`
6. `TelegramBot:Outbound:MaxBufferedCharsPerDestination`
7. `TelegramBot:Outbound:MaxBufferedMessagesPerDestination`
8. `TelegramBot:Outbound:FlushIntervalMilliseconds`
9. `TelegramBot:Outbound:IncludeProgressMessages`
10. `TelegramBot:Outbound:AgentMessageUpdateMinChars`
11. `TelegramBot:Outbound:AgentMessageUpdateMaxChars`
12. `TelegramBot:Outbound:BatchWindowSeconds`
13. `TelegramBot:Outbound:DropPolicy`

The app also clamps several of those values to safe ranges at startup, so wildly out-of-range values will be normalized rather than trusted blindly.

## Troubleshooting

### The bot does not reply at all

Check these in order:

1. The bot token is correct.
2. `TelegramBot.Enabled` is `true`.
3. The bot process is actually running.
4. Your user ID is in `AllowedUserIds`.
5. The chat is trusted with `/trust`, or the chat ID is in `AllowedChatIds`, if you are in a group or forum topic.
6. You are talking to the right bot account.
7. The executable folder contains the `appsettings.Local.json` file you think it does.

### `/whoami` does not work

Check these first:

1. The bot is running.
2. The token is valid.
3. You started with an empty allowlist only long enough to discover your ID.
4. You did not accidentally talk to a different bot.

### Plain text works in private chat but not in a group or topic

This is usually one of these:

1. Privacy mode is still blocking ordinary group messages.
2. The bot is not allowed in the chat.
3. The chat ID is not on the allowlist.
4. The group is not a forum-enabled supergroup when you are trying to create topics.

Use `/send <text>` when you want to force a message into the active session.

### The bot cannot find `codex`

Check these:

1. `codex` is installed.
2. `codex` is on `PATH`.
3. `Codex:CodexPathOverride` is set if `codex` is not on `PATH`.
4. `TelegramBot:CodexExecutablePath` is set if you prefer that config key.
5. `CODEX_PATH` is correct if you are using the environment-variable fallback.

### Voice notes fail

Check these:

1. `OpenAI:ApiKey` is set.
2. `OpenAI:Model` is valid for transcription.
3. `ffmpeg` is installed.
4. `OpenAI:FfmpegPath` points at the right executable if `ffmpeg` is not on `PATH`.

### `/topic new` fails

Check these:

1. The chat is actually a forum-enabled supergroup.
2. The bot has the rights needed to create topics.
3. The topic title is not empty.
4. The target project path is valid and allowed.

### The menu keeps writing config in the wrong place

Remember that `appsettings.Local.json` is written beside the executable by default.

If you want it in a specific folder, put the binary in that app folder and run that binary.

## Security And Operations

Treat these as secrets:

1. Telegram bot token.
2. OpenAI API key.
3. Any local credential or auth state that belongs to Codex.

Operational rules that keep this repo sane:

1. Keep `appsettings.Local.json` out of version control.
2. Use allowlists instead of trusting all Telegram users.
3. Rotate the Telegram token with BotFather if you leak it.
4. Keep group membership and privacy settings intentional.
5. Start in private chat before you trust a group workflow.

## External References

These are the official Telegram pages most relevant to this app:

1. [Telegram Bot Features](https://core.telegram.org/bots/features)
2. [Telegram Bot API](https://core.telegram.org/bots/api)
3. [Telegram Bot FAQ](https://core.telegram.org/bots/faq)
