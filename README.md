# Incursa Codex Telegram

Incursa Codex Telegram lets you talk to a local Codex CLI session from a private Telegram chat. It runs on your own machine, stores state locally, and only accepts messages from allowlisted Telegram users.

Use it when you want to start, steer, and inspect Codex work from your phone without exposing your whole machine to Telegram users.

An optional, disabled-by-default Telegram Mini App companion provides the current Telegram conversation's Codex session control panel: live status, session name, goal, model/thinking settings, account quota sparklines, and shared instructions. It adapts to Telegram's compact, full-height, and true fullscreen webviews, including viewport and safe-area changes when the app is minimized or reopened. Chat remains the conversation and approval surface; see [getting started](docs/getting-started.md#optional-telegram-mini-app-preview) before exposing the Mini App through an HTTPS URL.

The broader supervision-workspace direction, authority boundaries, and staged delivery contract are documented in the [Mini App roadmap](docs/mini-app-roadmap.md).

## Demo

Watch a two-minute private-chat demo showing a local Codex session controlled from Telegram with project selection, text prompts, and voice input.

[![Watch the Codex Telegram demo](docs/assets/codex-telegram-demo-thumbnail.png)](https://github.com/incursa/codex-telegram/raw/main/docs/assets/codex-telegram-demo.mp4)

For the available Telegram buttons and menus, see the [menus and button reference](docs/menus.md).

## Runtime Modes

The service has two workspace modes. Set `CodexTelegram:Mode` to one of these values:

| Mode | Behavior | Required settings |
| --- | --- | --- |
| `GeneralPurpose` (default) | Browse the configured `CodexTelegram:Workspace:WorkspaceRoots` and select projects per Telegram conversation. | A workspace root is strongly recommended; the runtime otherwise falls back to its process directory. |
| `Repository` | Pin the host to one repository and use it as the default project boundary. | `CodexTelegram:RepositoryRoot`; `RepositoryDisplayLabel` is optional. |

Use `GeneralPurpose` for a personal launcher that works across several repositories. Use `Repository` for a dedicated bot instance whose Telegram users should stay within one repository. In repository mode, keep `RepositoryRoot` explicit and validate it locally before enabling polling.

The service also has launch modes:

1. Start it with no arguments in an interactive terminal to open the bootstrap/admin menu.
2. Start it with `--run` to skip the menu and run the hosted services directly.
3. Start it with `--menu` to force the bootstrap/admin menu.
4. Use private chat first, then trusted group roots or forum topics only after the private flow works.
5. Use [docs/usage.md](docs/usage.md) for the user-facing output modes: `Compact`, `Verbose`, `LiveCard`, `Balanced`, and `FinalOnly`.

`CodexTelegram:Mode` controls workspace scope; `TelegramOutput:PresentationMode` controls how turn output is presented. They are independent settings.

## Configuration And Secrets

The main configuration file is `src/Incursa.Codex.Telegram/appsettings.json`. Keep local values in one of these places:

1. `appsettings.Local.json`
2. User secrets
3. Environment variables
4. An operator-managed secret store

Keep these out of version control:

1. Telegram bot tokens.
2. OpenAI API keys.
3. Local Codex auth state.
4. `appsettings.Local.json`.
5. Local debug traces and private transcripts.

The app does not own or synchronize Codex authentication. Codex authentication belongs to the local Codex installation and the OS/user context that runs it. For separate bot instances, use separate OS identities or Codex-supported per-instance auth homes when you need auth isolation; never copy auth files into a repository or Telegram settings file. Verify each instance by running its configured `codex` executable in the same account/context before starting the bot.

## Repository Layout

The main folders and files are:

1. `src/Incursa.Codex.Telegram`: the console host, Telegram routing, Codex integration, configuration, and runtime services.
2. `tests/Incursa.Codex.Telegram.Tests`: unit and integration-style tests for commands, state, queueing, output, and Telegram behavior.
3. `docs/`: source-authored operator, maintainer, and command documentation.
4. `scripts/`: local release, publish, fuzz, mutation, and secret-scan scripts.
5. `specs/`: repository-native requirements and architecture notes.
6. `fuzz/corpus/`: checked-in Telegram fuzz seeds.
7. `docs.site.json` and `.github/workflows/sync-docs.yml`: the docs mirror manifest and sync workflow.

The Linux Debian package is built with `scripts/Build-DebianPackage.ps1`. It contains both the bot and the separate root-owned updater service; ordinary `apt upgrade` remains the independent recovery path if the updater ever needs repair.

## Download

Download the latest release binary for your operating system:

| Platform | Download | Checksum |
| --- | --- | --- |
| Windows x64 | [codex-telegram-win-x64.exe](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-win-x64.exe) | [sha256](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-win-x64.exe.sha256) |
| Linux x64 binary | [codex-telegram-linux-x64](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64) | [sha256](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64.sha256) |
| Linux x64 Mini App webroot | [codex-telegram-linux-x64-webroot.tar.gz](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64-webroot.tar.gz) | — |
| macOS arm64 | [codex-telegram-osx-arm64](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-osx-arm64) | [sha256](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-osx-arm64.sha256) |

All releases are listed at [GitHub Releases](https://github.com/incursa/codex-telegram/releases).

## What You Need

Before starting, have these ready:

1. A Telegram account.
2. A Telegram bot token from `@BotFather`.
3. A local Codex CLI installation that already works in a terminal.
4. At least one local repository or workspace directory you want Codex to use.
5. Optional: an OpenAI API key for voice-note transcription. Install `ffmpeg` only if Telegram audio must be transcoded; Telegram voice notes commonly need it.

This app does not bundle Codex, Telegram credentials, or OpenAI credentials. If you use voice notes, any required audio transcoder must already exist on the machine running the bot.

For Codex CLI setup, use OpenAI's official [Codex CLI docs](https://developers.openai.com/codex/cli).

## Quick Start

Start by creating a Telegram bot, then follow the complete setup path for your operating system.

### Create A Telegram Bot

In Telegram:

1. Open a chat with `@BotFather`.
2. Send `/newbot`.
3. Choose a display name.
4. Choose a username ending in `bot`.
5. Copy the bot token.

Keep the token private. Anyone with the token can control the bot account.

Recommended BotFather settings for a first private-chat release:

1. Use `/setdescription` and `/setabouttext` to explain that this bot controls a local Codex installation.
2. Keep group joins disabled unless you intentionally want group support.
3. Keep privacy mode enabled unless you intentionally need ordinary group text routed to Codex.
4. Add commands later after the private-chat flow works.

Copy-paste BotFather text, command lists, and privacy recommendations are in [BotFather setup](docs/botfather.md).

BotFather gives you the bot token, but it cannot give you your personal Telegram user ID. On first run, Codex Telegram can validate the bot token, show a random setup code in the terminal, wait for one private message containing that code, and save your user ID automatically.

### Windows

Use this path if the bot will run on Windows x64.

1. Download `codex-telegram-win-x64.exe` and `codex-telegram-win-x64.exe.sha256` from the [latest release](https://github.com/incursa/codex-telegram/releases/latest).
2. Optional but recommended: verify the checksum before renaming or moving the file.

```powershell
Get-FileHash .\codex-telegram-win-x64.exe -Algorithm SHA256
Get-Content .\codex-telegram-win-x64.exe.sha256
```

3. Put the binary in a stable folder.

```powershell
New-Item -ItemType Directory -Force C:\tools\codex-telegram | Out-Null
Move-Item .\codex-telegram-win-x64.exe C:\tools\codex-telegram\codex-telegram.exe
Set-Location C:\tools\codex-telegram
```

4. Confirm Codex works locally before involving Telegram.

```powershell
codex --version
codex
```

5. Start the setup menu from the app folder.

```powershell
.\codex-telegram.exe
```

6. Complete the first-run wizard.

Use these values as a starting point:

```text
Telegram bot token: <token from BotFather>
Telegram polling: enabled
Admin user ID: let the wizard capture it by sending one private Telegram message to the bot
Codex executable path: leave blank if codex is on PATH, otherwise set the full codex.exe path
Workspace root: C:\src
Default working directory: C:\src\your-repo
OpenAI transcription: only if you want voice notes
Local data root: leave blank unless you need a custom state folder
```

The app writes `appsettings.Local.json` beside the executable by default. Keep that file local and untracked. If the executable-local file is missing and your launch directory has `appsettings.Local.json`, the app uses the launch-directory file.

7. Start normal operation with the menu skipped.

```powershell
.\codex-telegram.exe --run
```

Keep that terminal open, or run the app under your preferred Windows service manager.

8. In the private Telegram chat, run the first private Codex session.

```text
/doctor
/projects
/project add C:\src\your-repo
/new release-demo
Summarize this repository and tell me the next safest setup check to run.
/tail
```

At this point you have a working private Telegram chat connected to a local Codex session.

### Run two isolated instances

Each long-polling instance needs its own BotFather token and its own local state root. Keep each executable in a separate folder so the executable-local settings file is unambiguous:

```text
C:\tools\codex-telegram-general\codex-telegram.exe
C:\tools\codex-telegram-repo\codex-telegram.exe
```

Example settings differences:

`codex-telegram-general\appsettings.Local.json`:

Proposed configuration example:

```json
{
  "TelegramBot": { "Token": "<general-bot-token>", "AllowedUserIds": [123456789] },
  "CodexTelegram": {
    "Mode": "GeneralPurpose",
    "InstanceId": "general",
    "Workspace": { "DataRoot": "C:\\data\\codex-telegram-general", "WorkspaceRoots": ["C:\\src"] }
  }
}
```

`codex-telegram-repo\appsettings.Local.json`:

```json
{
  "TelegramBot": { "Token": "<repo-bot-token>", "AllowedUserIds": [123456789] },
  "CodexTelegram": {
    "Mode": "Repository",
    "InstanceId": "repo-docs",
    "RepositoryRoot": "C:\\src\\docs-repo",
    "RepositoryDisplayLabel": "Docs repository",
    "Workspace": { "DataRoot": "C:\\data\\codex-telegram-repo", "WorkspaceRoots": ["C:\\src\\docs-repo"] }
  }
}
```

Do not run two processes with the same Telegram token or the same `DataRoot`. A separate `InstanceId` helps partition the default state location, but an explicit `DataRoot` is the clearest isolation boundary.

### Linux

Use this path if the bot will run on Linux x64.

1. Download `codex-telegram-linux-x64`, `codex-telegram-linux-x64-webroot.tar.gz`, and `codex-telegram-linux-x64.sha256` from the [same latest release](https://github.com/incursa/codex-telegram/releases/latest). The webroot archive is required for every Linux install because the host uses the published static webroot even when the Mini App API is disabled.
2. Or download all three files directly with `curl`.

```bash
curl -fL -o codex-telegram-linux-x64 https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64
curl -fL -o codex-telegram-linux-x64-webroot.tar.gz https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64-webroot.tar.gz
curl -fL -o codex-telegram-linux-x64.sha256 https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64.sha256
```

3. Optional but recommended: verify the checksum before renaming or moving the file.

```bash
shasum -a 256 -c ./codex-telegram-linux-x64.sha256
```

4. Put the binary and the matching published webroot tree in a stable folder and mark the binary executable.

```bash
mkdir -p ~/tools/codex-telegram
mv ./codex-telegram-linux-x64 ~/tools/codex-telegram/codex-telegram
tar -xzf ./codex-telegram-linux-x64-webroot.tar.gz -C ~/tools/codex-telegram
chmod +x ~/tools/codex-telegram/codex-telegram
cd ~/tools/codex-telegram
```

5. Confirm Codex works locally before involving Telegram.

```bash
codex --version
codex
```

6. Start the setup menu from the app folder.

```bash
./codex-telegram
```

7. Complete the first-run wizard.

Use these values as a starting point:

```text
Telegram bot token: <token from BotFather>
Telegram polling: enabled
Admin user ID: let the wizard capture it by sending one private Telegram message to the bot
Codex executable path: leave blank if codex is on PATH, otherwise set the full codex path
Workspace root: /home/you/src
Default working directory: /home/you/src/your-repo
OpenAI transcription: only if you want voice notes
Local data root: leave blank unless you need a custom state folder
```

The app writes `appsettings.Local.json` beside the executable by default. Keep that file local and untracked. If the executable-local file is missing and your launch directory has `appsettings.Local.json`, the app uses the launch-directory file.

8. Start normal operation with the menu skipped.

```bash
./codex-telegram --run
```

Keep that terminal open, or run the app under systemd, tmux, screen, or another process supervisor.

9. In the private Telegram chat, run the first private Codex session.

```text
/doctor
/projects
/project add /home/you/src/your-repo
/new release-demo
Summarize this repository and tell me the next safest setup check to run.
/tail
```

At this point you have a working private Telegram chat connected to a local Codex session.

### macOS

Use this path if the bot will run on Apple Silicon macOS.

1. Download `codex-telegram-osx-arm64` and `codex-telegram-osx-arm64.sha256` from the [latest release](https://github.com/incursa/codex-telegram/releases/latest).
2. Or download both files directly with `curl`.

```bash
curl -fL -o codex-telegram-osx-arm64 https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-osx-arm64
curl -fL -o codex-telegram-osx-arm64.sha256 https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-osx-arm64.sha256
```

3. Optional but recommended: verify the checksum before renaming or moving the file.

```bash
shasum -a 256 -c ./codex-telegram-osx-arm64.sha256
```

4. Put the binary in a stable folder and mark it executable.

```bash
mkdir -p ~/tools/codex-telegram
mv ./codex-telegram-osx-arm64 ~/tools/codex-telegram/codex-telegram
chmod +x ~/tools/codex-telegram/codex-telegram
cd ~/tools/codex-telegram
```

5. If macOS blocks the binary because it was downloaded from the internet, verify the checksum first. If you trust the release, remove the quarantine attribute.

```bash
xattr -d com.apple.quarantine ~/tools/codex-telegram/codex-telegram
```

6. Confirm Codex works locally before involving Telegram.

```bash
codex --version
codex
```

7. Start the setup menu from the app folder.

```bash
./codex-telegram
```

8. Complete the first-run wizard.

Use these values as a starting point:

```text
Telegram bot token: <token from BotFather>
Telegram polling: enabled
Admin user ID: let the wizard capture it by sending one private Telegram message to the bot
Codex executable path: leave blank if codex is on PATH, otherwise set the full codex path
Workspace root: /Users/you/src
Default working directory: /Users/you/src/your-repo
OpenAI transcription: only if you want voice notes
Local data root: leave blank unless you need a custom state folder
```

The app writes `appsettings.Local.json` beside the executable by default. Keep that file local and untracked. If the executable-local file is missing and your launch directory has `appsettings.Local.json`, the app uses the launch-directory file.

9. Start normal operation with the menu skipped.

```bash
./codex-telegram --run
```

Keep that terminal open, or run the app under launchd, tmux, screen, or another process supervisor.

10. In the private Telegram chat, run the first private Codex session.

```text
/doctor
/projects
/project add /Users/you/src/your-repo
/new release-demo
Summarize this repository and tell me the next safest setup check to run.
/tail
```

At this point you have a working private Telegram chat connected to a local Codex session.

## Voice Notes

Voice notes are optional. The bot downloads Telegram audio, transcribes it with OpenAI, shows one editable live progress card, and sends only the transcribed text to the active Codex session. Codex does not receive raw Telegram audio.

Voice note requirements:

1. `OpenAI:ApiKey` or `OPENAI_API_KEY`.
2. A transcription-capable `OpenAI:Model`.
3. `ffmpeg` only when the downloaded audio is not in a format OpenAI accepts directly. Telegram voice notes commonly arrive as OGG/OPUS, so install `ffmpeg` or configure `OpenAI:FfmpegPath` for reliable voice-note support.

After the bot is running, an allowlisted administrator can configure the local key from the private Telegram chat with `/setup openai-key`. The bot accepts the next key message, deletes that source message before saving the key to local settings, and never routes the key to Codex. Telegram deletion is best-effort and cannot undo a notification or a client that already received the message, so use the CLI or an environment/secret-store configuration when that exposure matters.

The default maximum Telegram audio duration is 10 minutes (`TelegramBot:MaxAudioDurationSeconds: 600`). The OpenAI request waits up to 15 minutes by default (`OpenAI:RequestTimeoutSeconds: 900`), so a long note can finish even when transcription takes longer than the standard 100-second `HttpClient` timeout. Both settings can be changed within the application's safety bounds.

If `ffmpeg` is missing when a voice note needs conversion, the bot leaves the Codex session untouched and replies with setup guidance instead of failing silently.

Suggested first voice test:

```text
Please review the current project and tell me the three most important setup risks. Keep it concise and do not edit files.
```

After a successful test, you should see the transcription in Telegram before the Codex response starts.

## Day-To-Day Commands

| Command | Use |
| --- | --- |
| `/doctor` | Explain authorization, routing, active project/session, workspace roots, queue state, and next action. |
| `/help` | Show the built-in command summary. |
| `/whoami` | Show Telegram user, chat, and topic IDs for setup and troubleshooting. |
| `/version` | Show the running app version. |
| `/trust` | Trust the current group or forum chat for allowlisted users. |
| `/setup openai-key` | Configure the local OpenAI transcription key from a private chat; the next key message is deleted before saving. |
| `/projects` | List known local project directories. |
| `/project add <path>` | Add and select a repository or workspace. |
| `/project current` | Confirm the active project for this Telegram conversation. |
| `/new [name]` | Create and select a fresh Codex session; omit the name to auto-generate one from the active project. |
| `/sessions` | Show active and Telegram-managed sessions. |
| `/sessions all [count]` | Show older Codex history. |
| `/use <sessionId>` | Resume an existing session. |
| `/send <text>` | Explicitly send text when privacy mode or chat type prevents normal auto-routing. |
| `/steer <text>` | Add guidance to a currently active turn. |
| `/queue` | View queued prompts for the conversation, then edit, delete, or send one now. |
| `/model` | Show or change the active session model. |
| `/thinking` | Show or change reasoning effort. |
| `/goal` | Show or change the active session goal. |
| `/status` | Show active session status, including compact Codex usage when available. |
| `/usage` | Show five-hour and weekly Codex usage, with reset times. |
| `/tail [lines]` | Show recent output and keep following the session; defaults to 40 lines. |
| `/outbound` | Inspect delayed or batched Telegram output. |
| `/stop` | Gracefully stop a session. |
| `/restart confirm` | Show standalone-process restart guidance. |
| `/update status` / `/update confirm` | Request or inspect an external host update. |
| `/topic ...` | Manage forum-topic sessions in allowed supergroups. |

For a fuller operator guide, see [docs/usage.md](docs/usage.md).
For every command, parameter, and expected behavior, see [docs/command-reference.md](docs/command-reference.md).

## How Output Delivery Works

Telegram output is rate-limited and batched so active Codex sessions do not flood your chat.

Practical rules:

1. Use `/tail` before assuming Telegram scrollback contains the full transcript.
2. Use `/outbound` if messages seem delayed.
3. Use `/usage` when you need current five-hour and weekly Codex usage percentages and reset times.
4. Batched messages are concatenated with simple spacing and preserve multi-line content, including numbered lists and headings.
5. If the local outbound buffer is compacted, the bot sends an explicit compaction notice.
6. Terminal turn events are tracked internally; the bot no longer emits a standalone completion marker into the chat.

Output presentation and text formatting are separate settings. `TelegramOutput:PresentationMode` defaults to `Compact`; `Balanced` adds concise lifecycle/tool milestones while keeping routine progress to sparse still-working pulses. `TelegramOutput:TextFormat` defaults to `PlainText`, which preserves literal Codex text. Set it to `SafeMarkdownV2` only when you want the constrained safe formatter for headings, emphasis, links, and code; unsupported or malformed markup is escaped as literal text. If a formatted message would split across an unsafe MarkdownV2 boundary, its chunks fall back to plain text. A transport that cannot apply formatting also falls back to plain text.

```json
{
  "TelegramOutput": {
    "PresentationMode": "Balanced",
    "TextFormat": "SafeMarkdownV2"
  }
}
```

The runtime equivalent is `/output mode balanced`; use `/output mode reset` to return to the configured presentation mode. Text format is configuration-backed and is not changed by that runtime presentation override.

## Local State And Safety

The app stores local state under `CodexTelegram:Workspace:DataRoot`. By default, that is the user's application data folder.

State includes:

1. `projects.json`
2. `telegram-state.json`
3. Per-thread manifests

Secrets should stay in `appsettings.Local.json`, user secrets, environment variables, or another secret store. Secrets are not supposed to be written to the state files.

Security rules:

1. Keep `TelegramBot:AllowedUserIds` narrow.
2. Keep `TelegramBot:AllowedChatIds` empty unless you intentionally want config-managed group or forum-topic access.
3. Set explicit workspace roots and a default working directory before enabling polling.
4. Review Codex sandbox and approval settings before exposing sensitive repositories.
5. Rotate the BotFather token if it is exposed.

## Supported Modes

Private chat is the recommended first setup path. Trusted groups and forum topics are also supported conversation scopes.

Groups and forum topics require:

1. An allowed Telegram user.
2. A trusted group chat, either from `TelegramBot:AllowedChatIds` or `/trust` sent in that chat by an allowlisted user.
3. BotFather privacy settings that match the desired behavior.
4. Topic-management rights if the bot should create forum topics.

Start privately first. Then use a trusted group root as a single project/session lane, or use forum topics when one group needs multiple independent sessions.

Private chat is the least complicated authorization path: the user must be allowlisted. A group root requires both an allowlisted user and a trusted/allowlisted chat. A forum topic additionally requires a forum-enabled supergroup and the bot permissions needed for topic operations. With Telegram privacy mode enabled, ordinary group text may not arrive; use commands, mentions/replies, or `/send` as the manual fallback.

## Local Validation

Run these commands from the repository root:

```powershell
dotnet restore CodexTelegram.slnx
dotnet build CodexTelegram.slnx -c Release -m:1 --no-restore
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore -m:1
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64 -SkipPublish
git diff --check
```

These commands are local evidence only. They prove build/test/format and repository checks; they do not prove that Telegram delivered an update, that BotFather settings are correct, or that the configured Codex account can authenticate. For those claims, run the live checklist in [docs/manual-test-plan.md](docs/manual-test-plan.md) against the exact commit or published binary and record the result.

## Release And Versioning

The repository publishes self-contained release binaries through `scripts\Publish.ps1` and `.github/workflows/publish.yml`.

Release tags that start with `v` produce GitHub Releases with Windows x64, Linux x64, and macOS arm64 assets, plus SHA-256 checksum files and a copied `LICENSE.txt`. Linux releases also include `codex-telegram-linux-x64-webroot.tar.gz`; it contains the complete published static tree rooted at `wwwroot/`, including `wwwroot/index.html`, and must be generated from the same commit as the binary.

Before a public release, run the repo-native release gate and the live Telegram checklist:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

Then follow [docs/manual-test-plan.md](docs/manual-test-plan.md) against the exact commit or published asset being released.

## Documentation Ownership

The `docs/` tree is source-authored documentation.

The docs sync workflow uses `docs.site.json` and `.github/workflows/sync-docs.yml` to mirror that tree into `incursa-docs/src/content/docs/open-source/codex-telegram/`. Edit the source files in this repository, not the mirrored copies in `incursa-docs`.

The app owns command parsing and built-in `/help`. During guided setup it can optionally apply the app-owned command list and Telegram menu button through the Bot API; this is a one-time setup action, not background synchronization. BotFather remains the manual owner for description/about text, group-join setting, privacy setting, and any command/profile operation the setup pass skips or cannot apply. Use `/help` or the command reference if Telegram's picker is stale or unavailable.

## Known Gaps

1. Live Telegram behavior still needs a real bot account and real credentials for validation.
2. Group and forum-topic support is supported but higher risk than private chat; validate it separately before calling a release ready.
3. Voice-note support depends on OpenAI and, when transcoding is needed, `ffmpeg`.
4. The mirrored documentation tree is generated from this repository and should not be edited directly in the central docs repo.
5. Automated tests and synthetic fixtures do not replace live Telegram, BotFather, or Codex-auth verification.

## Support And Security

For general open-source project questions, contact oss@incursa.com.

For security issues, use GitHub private vulnerability reporting when it is enabled for this repository. If that is unavailable, contact security@incursa.com. Do not include secrets, private transcripts, exploit details, or local credential paths in public issues.

## More Documentation

User and operator docs:

- [Getting started guide](docs/getting-started.md)
- [Documentation index](docs/README.md)
- [Day-to-day usage](docs/usage.md)
- [Operations](docs/operations.md)
- [BotFather setup](docs/botfather.md)
- [Command reference](docs/command-reference.md)
- [Menus and button reference](docs/menus.md)
- [Security](SECURITY.md)

Developer and maintainer docs:

- [Development guide](docs/development.md)
- [Maintainer readiness](docs/maintainer-readiness.md)
- [Contributing](CONTRIBUTING.md)
- [Code of conduct](CODE_OF_CONDUCT.md)
- [Testing and quality](docs/testing.md)
- [Manual Telegram test plan](docs/manual-test-plan.md)
