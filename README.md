# Incursa Codex Telegram

Incursa Codex Telegram lets you talk to a local Codex CLI session from a private Telegram chat. It runs on your own machine, stores state locally, and only accepts messages from allowlisted Telegram users.

Use it when you want to start, steer, and inspect Codex work from your phone without exposing your whole machine to Telegram users.

## Demo

Watch a two-minute private-chat demo showing a local Codex session controlled from Telegram with project selection, text prompts, and voice input.

[![Watch the Codex Telegram demo](docs/assets/codex-telegram-demo-thumbnail.png)](https://github.com/incursa/codex-telegram/raw/main/docs/assets/codex-telegram-demo.mp4)

For the available Telegram buttons and menus, see the [menus and button reference](docs/menus.md).

## Download

Download the latest release binary for your operating system:

| Platform | Download | Checksum |
| --- | --- | --- |
| Windows x64 | [codex-telegram-win-x64.exe](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-win-x64.exe) | [sha256](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-win-x64.exe.sha256) |
| Linux x64 | [codex-telegram-linux-x64](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64) | [sha256](https://github.com/incursa/codex-telegram/releases/latest/download/codex-telegram-linux-x64.sha256) |
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

This path gets you from a downloaded binary to a working private Telegram chat.

### 1. Optional: Verify The Download

Download the matching `.sha256` file next to the binary before you rename or move the binary.

Windows:

```powershell
Get-FileHash .\codex-telegram-win-x64.exe -Algorithm SHA256
Get-Content .\codex-telegram-win-x64.exe.sha256
```

Linux:

```bash
shasum -a 256 -c ./codex-telegram-linux-x64.sha256
```

macOS:

```bash
shasum -a 256 -c ./codex-telegram-osx-arm64.sha256
```

### 2. Put The Binary In A Stable Folder

Windows example:

```powershell
New-Item -ItemType Directory -Force C:\tools\codex-telegram | Out-Null
Move-Item .\codex-telegram-win-x64.exe C:\tools\codex-telegram\codex-telegram.exe
Set-Location C:\tools\codex-telegram
```

Linux example:

```bash
mkdir -p ~/tools/codex-telegram
mv ./codex-telegram-linux-x64 ~/tools/codex-telegram/codex-telegram
chmod +x ~/tools/codex-telegram/codex-telegram
cd ~/tools/codex-telegram
```

macOS arm64 example:

```bash
mkdir -p ~/tools/codex-telegram
mv ./codex-telegram-osx-arm64 ~/tools/codex-telegram/codex-telegram
chmod +x ~/tools/codex-telegram/codex-telegram
cd ~/tools/codex-telegram
```

If macOS blocks the binary because it was downloaded from the internet, verify the checksum first. If you trust the release, remove the quarantine attribute:

```bash
xattr -d com.apple.quarantine ~/tools/codex-telegram/codex-telegram
```

### 3. Confirm Codex Works Locally

Run Codex once in a normal terminal before involving Telegram:

```powershell
codex --version
codex
```

If `codex` is not on `PATH`, keep the full path handy. The setup menu can store a Codex executable path override.

### 4. Create A Telegram Bot

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

## First Launch

Run the app from the folder that should own its local `appsettings.Local.json`.

Windows:

```powershell
.\codex-telegram.exe
```

Linux/macOS:

```bash
./codex-telegram
```

The default startup path opens an interactive bootstrap/admin menu.

In the menu:

1. Set the Telegram bot token.
2. Enable Telegram polling.
3. Set the Codex executable path if `codex` is not on `PATH`.
4. Set at least one workspace root, such as `C:\src` or `/home/you/src`.
5. Set the default working directory to the repository you want to use first.
6. Set OpenAI transcription settings only if you want voice notes.
7. Leave the local data root blank unless you need a custom state folder.

The menu writes `appsettings.Local.json` in the current directory. Keep that file local and untracked.

## Find Your Telegram User ID

The bot uses an allowlist. You need your numeric Telegram user ID before normal use.

Bootstrap path:

1. Start the bot with the token configured.
2. Leave the user allowlist empty only long enough to discover your ID.
3. In a private chat with the bot, send:

```text
/whoami
```

4. Copy the numeric user ID.
5. Stop the bot with Ctrl+C.
6. Start the app again, open the menu, and add your user ID under Telegram/admin settings.
7. Start the bot again.

After the allowlist is configured, unauthorized users are ignored.

## Start The Bot

For normal operation, run with `--run` so the menu is skipped.

Windows:

```powershell
.\codex-telegram.exe --run
```

Linux/macOS:

```bash
./codex-telegram --run
```

Keep that terminal open, or run the app under your preferred service manager.

## Your First Private Codex Chat

In the private Telegram chat:

1. Confirm setup:

```text
/doctor
```

2. List projects:

```text
/projects
```

3. Add or select a project:

```text
/project add C:\src\your-repo
```

Use a Unix path on Linux/macOS, for example:

```text
/project add /home/you/src/your-repo
```

4. Start a new Codex session:

```text
/new release-demo
```

5. Send a normal message:

```text
Summarize this repository and tell me the next safest setup check to run.
```

6. Inspect recent output:

```text
/tail
```

At this point you have a working private Telegram chat connected to a local Codex session.

## Voice Notes

Voice notes are optional. The bot downloads Telegram audio, transcribes it with OpenAI, shows the transcript, and sends only the transcribed text to the active Codex session. Codex does not receive raw Telegram audio.

Voice note requirements:

1. `OpenAI:ApiKey` or `OPENAI_API_KEY`.
2. A transcription-capable `OpenAI:Model`.
3. `ffmpeg` only when the downloaded audio is not in a format OpenAI accepts directly. Telegram voice notes commonly arrive as OGG/OPUS, so install `ffmpeg` or configure `OpenAI:FfmpegPath` for reliable voice-note support.

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
| `/projects` | List known local project directories. |
| `/project add <path>` | Add and select a repository or workspace. |
| `/project current` | Confirm the active project for this Telegram conversation. |
| `/new <name>` | Create and select a fresh Codex session. |
| `/sessions` | Show active and Telegram-managed sessions. |
| `/sessions all [count]` | Show older Codex history. |
| `/use <sessionId>` | Resume an existing session. |
| `/send <text>` | Send text when plain text is not automatically routed, especially in groups. |
| `/steer <text>` | Add guidance to a currently active turn. |
| `/model` | Show or change the active session model. |
| `/thinking` | Show or change reasoning effort. |
| `/status` | Show active session status. |
| `/tail [lines]` | Show recent output and keep following the session; defaults to 40 lines. |
| `/outbound` | Inspect delayed or batched Telegram output. |
| `/stop` | Gracefully stop a session. |
| `/restart confirm` | Show standalone-process restart guidance. |
| `/topic ...` | Manage forum-topic sessions in allowed supergroups. |

For a fuller operator guide, see [docs/usage.md](docs/usage.md).
For every command, parameter, and expected behavior, see [docs/command-reference.md](docs/command-reference.md).

## How Output Delivery Works

Telegram output is rate-limited and batched so active Codex sessions do not flood your chat.

Practical rules:

1. Use `/tail` before assuming Telegram scrollback contains the full transcript.
2. Use `/outbound` if messages seem delayed.
3. Batched messages are concatenated with simple spacing and preserve multi-line content, including numbered lists and headings.
4. If the local outbound buffer is compacted, the bot sends an explicit compaction notice.
5. A final `~~ fin ~~` marker means the turn reached a terminal event.

## Local State And Safety

The app stores local state under `CodexTelegram:Workspace:DataRoot`. By default, that is the user's application data folder.

State includes:

1. `projects.json`
2. `telegram-state.json`
3. Per-thread manifests

Secrets should stay in `appsettings.Local.json`, user secrets, environment variables, or another secret store. Secrets are not supposed to be written to the state files.

Security rules:

1. Keep `TelegramBot:AllowedUserIds` narrow.
2. Keep `TelegramBot:AllowedChatIds` empty unless you intentionally want group or forum-topic access.
3. Set explicit workspace roots and a default working directory before enabling polling.
4. Review Codex sandbox and approval settings before exposing sensitive repositories.
5. Rotate the BotFather token if it is exposed.

## Supported Modes

Private chat is the primary supported operating mode and the recommended first setup path.

Groups and forum topics are available for advanced workflows, but they require:

1. An allowed Telegram user.
2. An allowed group chat ID.
3. BotFather privacy settings that match the desired behavior.
4. Topic-management rights if the bot should create forum topics.

Start privately first. Add groups only after the private flow works.

## Support And Security

For general open-source project questions, contact oss@incursa.com.

For security issues, use GitHub private vulnerability reporting when it is enabled for this repository. If that is unavailable, contact security@incursa.com. Do not include secrets, private transcripts, exploit details, or local credential paths in public issues.

## More Documentation

User and operator docs:

- [Getting started guide](docs/getting-started.md)
- [Day-to-day usage](docs/usage.md)
- [Operations](docs/operations.md)
- [BotFather setup](docs/botfather.md)
- [Command reference](docs/command-reference.md)
- [Menus and button reference](docs/menus.md)
- [Security](SECURITY.md)

Developer and maintainer docs:

- [Development guide](docs/development.md)
- [Contributing](CONTRIBUTING.md)
- [Code of conduct](CODE_OF_CONDUCT.md)
- [Testing and quality](docs/testing.md)
- [Manual Telegram test plan](docs/manual-test-plan.md)
