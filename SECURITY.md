# Security

Incursa.Codex.Telegram runs local Codex work on the same machine as the bot process. Treat the Telegram allowlist as the main access boundary.

## Required Controls

- Set `TelegramBot:AllowedUserIds` before enabling the bot; all bot control requires an allowed user.
- Keep `TelegramBot:AllowedChatIds` empty unless you intentionally want group or forum-topic access. Group and forum messages require both an allowed user and an allowed chat.
- Keep `CodexTelegram:Workspace:WorkspaceRoots` narrow; project paths outside those roots are rejected.
- Set explicit workspace roots and a default working directory before enabling polling. If these are omitted, the app falls back to the process current directory.
- Store bot tokens and OpenAI API keys in user secrets, environment variables, or another secret store. Do not commit `appsettings.Local.json`.
- Review Codex sandbox and approval settings before using the bot on sensitive repositories.

## Voice Notes

Voice-note transcription sends audio to OpenAI's transcription API using the configured `OpenAI:ApiKey`. The app deletes temporary Telegram audio and transcoded files after processing, but operators should still treat received audio as sensitive while the process is running.

## Reporting

Do not report secrets, exploit details, private transcripts, or local credential paths in a public issue.

Use GitHub private vulnerability reporting if it is enabled for the repository. If it is unavailable, contact security@incursa.com.

For general open-source project questions, contact oss@incursa.com.

For pre-1.0 or unreleased builds, only the latest `main` branch or current release candidate is expected to receive security fixes.
