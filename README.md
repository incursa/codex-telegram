# Incursa.Codex.Telegram

Incursa.Codex.Telegram is a lightweight Telegram bot host for a local Codex installation. It runs as a console app, stores state locally, and lets an allowlisted Telegram user create/select Codex sessions for local repositories.

The app does not bundle Codex, Telegram credentials, OpenAI credentials, or `ffmpeg`. Operators provide those on the machine that runs the bot.

## Prerequisites

- .NET 10 SDK for source builds, or a published self-contained binary.
- Local `codex` installed, on `PATH` or configured via `Codex:CodexPathOverride`, and already authenticated.
- A Telegram bot token from BotFather.
- Your numeric Telegram user ID. Start with `/whoami` while the bot is temporarily reachable, or use another trusted method to get it.
- `OPENAI_API_KEY` or `OpenAI:ApiKey` for voice-note transcription.
- `ffmpeg` on `PATH` if you want Telegram voice notes and unsupported audio formats transcoded before upload.

OpenAI's transcription API supports `whisper-1`, `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`, and related transcription models. File uploads are limited to 25 MB, with supported formats documented in the [speech-to-text guide](https://platform.openai.com/docs/guides/speech-to-text?lang=curl) and [audio API reference](https://platform.openai.com/docs/api-reference/audio/createTranscription.class).

## Configuration

The default startup path is an interactive bootstrap/admin menu:

```powershell
.\artifacts\publish\win-x64\Incursa.Codex.Telegram.exe
```

Use the menu to set the Telegram bot token, admin user IDs, optional chat allowlist, OpenAI transcription key/model, Codex executable path, Codex defaults, workspace roots, and local state root. The menu writes `appsettings.Local.json` in the current directory and never displays stored secret values.

After configuration, choose `Start bot` from the menu, or use `--run` for quiet service-style startup:

```powershell
.\artifacts\publish\win-x64\Incursa.Codex.Telegram.exe --run
```

Other supported app switches:

```powershell
.\artifacts\publish\win-x64\Incursa.Codex.Telegram.exe --menu
.\artifacts\publish\win-x64\Incursa.Codex.Telegram.exe --help
```

You can still use these configuration approaches:

- User secrets while developing:

```powershell
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:Enabled" "true"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:Token" "<telegram-bot-token>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "TelegramBot:AllowedUserIds:0" "<your-user-id>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "OpenAI:ApiKey" "<openai-api-key>"
dotnet user-secrets set --project src\Incursa.Codex.Telegram "CodexTelegram:Workspace:WorkspaceRoots:0" "C:\src"
```

- Environment variables for a published binary:

```powershell
$env:TELEGRAM_BOT_TOKEN = "<telegram-bot-token>"
$env:TELEGRAM_ALLOWED_USER_IDS = "<your-user-id>"
$env:OPENAI_API_KEY = "<openai-api-key>"
$env:CODEX_PATH = "C:\path\to\codex.exe"
$env:CODEX_TELEGRAM_TelegramBot__Enabled = "true"
$env:CODEX_TELEGRAM_CodexTelegram__Workspace__WorkspaceRoots__0 = "C:\src"
```

- `appsettings.Local.json` next to the working directory or binary. Use `appsettings.Local.example.json` as the shape, but keep the real local file untracked.

Important settings:

- `TelegramBot:AllowedUserIds`: required allowlist for private control.
- `TelegramBot:AllowedChatIds`: optional group allowlist.
- `CodexTelegram:Workspace:WorkspaceRoots`: directories users may add as projects.
- `CodexTelegram:Context:WorkingDirectory`: default Codex working directory.
- `CodexTelegram:Workspace:DataRoot`: local JSON state root. Defaults to the user's application data folder.
- `Codex:CodexPathOverride` or `CODEX_PATH`: optional path to the local `codex` executable.
- `OpenAI:Model`: defaults to `whisper-1`.

## Run From Source

```powershell
dotnet run --project src\Incursa.Codex.Telegram
```

That opens the bootstrap/admin menu. To start directly without the menu:

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

The running bot keeps the process alive in the terminal without framework log noise. Stop it with Ctrl+C.

## Publish A Single Binary

```powershell
.\scripts\Publish.ps1 -Runtime win-x64
```

The output is:

```text
artifacts\publish\win-x64\Incursa.Codex.Telegram.exe
```

Other runtime identifiers can be passed with `-Runtime`, for example `linux-x64` or `osx-arm64`, when the .NET SDK has the required runtime packs.

## First Run

1. Start the process.
2. In the menu, set the Telegram bot token and enable Telegram polling.
3. If you do not know your numeric Telegram user ID, start once and send `/whoami`; the app allows that command before the admin allowlist is configured.
4. Return to the menu and add your numeric Telegram user ID under `Telegram and admins`.
5. Set the OpenAI API key if you want voice transcription.
6. Set the Codex executable path if `codex` is not already on `PATH`.
7. Set workspace roots and a default working directory.
8. Choose `Start bot`, then send `/projects` or `/project add <absolute-directory>`.
9. Send `/new <session name>`, then send normal messages to continue the active Codex session.

Useful commands:

- `/projects`, `/project add <path>`, `/project current`
- `/new <name>`, `/sessions`, `/use <sessionId>`
- `/status`, `/tail [lines]`, `/stop`
- `/model`, `/thinking`
- `/topic new <name>` in forum-enabled supergroups
- Voice notes are transcribed and sent to the active session.

## Local State

State is stored under `CodexTelegram:Workspace:DataRoot`, defaulting to the user's application data folder:

- `projects.json`
- `telegram-state.json`
- per-thread manifests

Secrets are not written to those state files.

## Development

```powershell
dotnet build CodexTelegram.slnx
dotnet test CodexTelegram.slnx
dotnet publish src\Incursa.Codex.Telegram\Incursa.Codex.Telegram.csproj -c Release -r win-x64 -o artifacts\publish\win-x64
```

This repository is intentionally console-only. The ASP.NET Core web console, SignalR UI, and MCP endpoint belong in the separate `codex-remote` project.
