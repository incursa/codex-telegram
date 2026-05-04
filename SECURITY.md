# Security

Incursa.Codex.Telegram runs local Codex work on the same machine as the bot process. Treat the Telegram allowlist as the main access boundary.

## Required Controls

- Set `TelegramBot:AllowedUserIds` before enabling the bot; all bot control requires an allowed user.
- Keep `TelegramBot:AllowedChatIds` empty unless you intentionally want group or forum-topic access. Group and forum messages require both an allowed user and an allowed chat.
- Keep `CodexTelegram:Workspace:WorkspaceRoots` narrow; project paths outside those roots are rejected.
- Store bot tokens and OpenAI API keys in user secrets, environment variables, or another secret store. Do not commit `appsettings.Local.json`.
- Review Codex sandbox and approval settings before using the bot on sensitive repositories.

## Voice Notes

Voice-note transcription sends audio to OpenAI's transcription API using the configured `OpenAI:ApiKey`. The app deletes temporary Telegram audio and transcoded files after processing, but operators should still treat received audio as sensitive while the process is running.

## Reporting

Until this repository has a public security contact, report security issues privately to the repository owner.
