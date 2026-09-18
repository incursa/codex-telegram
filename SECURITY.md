# Security

Incursa.Codex.Telegram runs local Codex work on the same machine as the bot process. Treat the Telegram allowlist as the main access boundary.

## Required Controls

- Set `TelegramBot:AllowedUserIds` before enabling the bot; all bot control requires an allowed user.
- Keep `TelegramBot:AllowedChatIds` empty unless you intentionally want group or forum-topic access. Group and forum messages require both an allowed user and an allowed chat.
- Keep `CodexTelegram:Workspace:WorkspaceRoots` narrow; project paths outside those roots are rejected.
- Set explicit workspace roots and a default working directory before enabling polling. If these are omitted, the app falls back to the process current directory.
- Store bot tokens and OpenAI API keys in user secrets, environment variables, or another secret store. Do not commit `appsettings.Local.json`. For convenience, an allowlisted administrator may use `/setup openai-key` in a private Telegram chat; the bot deletes the next key message before saving it locally, but deletion cannot undo a notification or client that already received the message.
- Review Codex sandbox and approval settings before using the bot on sensitive repositories.
- Keep `TelegramMiniApp` disabled unless you intentionally need the companion surface. If enabled, expose it only through an operator-controlled HTTPS proxy or tunnel, keep `TelegramBot:AllowedUserIds` narrow, and do not treat `TelegramMiniApp:PublicUrl` as a secret.
- Mini App API requests are authorized from Telegram's signed `initData`; the server validates the HMAC, freshness window, and allowlisted user ID. Client-side `initDataUnsafe` is not used as an authorization source.
- The Mini App exposes authenticated projections plus bounded settings writes for the conversation's current session: rename, goal lifecycle, session-specific model/thinking selection, secondary session selection, and a shared global-instructions profile. It does not accept prompts, approvals, steering, retries, cancellation, file writes, uploads, commits, merges, or deployment actions. Model/goal writes are routed to the owning worker when applicable and report only explicit acceptance or a conflict. Active-turn model/thinking changes affect the next turn; global-instruction saves never interrupt work or send a hidden prompt.
- Mini App detail data is bounded. Artifact projections expose only safe identifiers, type labels, status, title, and timestamps; raw artifact paths, result URLs, bytes, and payload metadata are not sent to the browser.
- Codex command and file-change approval requests fail closed when no explicit Telegram decision is available. A missing, malformed, expired, or otherwise unresolved approval is rejected; it is never treated as consent.

## Voice Notes

Voice-note transcription sends audio to OpenAI's transcription API using the configured `OpenAI:ApiKey`. The app deletes temporary Telegram audio and transcoded files after processing, but operators should still treat received audio as sensitive while the process is running. Telegram key setup is private-chat-only, requires the existing Telegram authorization boundary, deletes the source message before saving, and deliberately bypasses inbound text tracing, attachment persistence, and Codex routing. If deletion fails, the key is not saved.

## Reporting

Do not report secrets, exploit details, private transcripts, or local credential paths in a public issue.

Use GitHub private vulnerability reporting if it is enabled for the repository. If it is unavailable, contact security@incursa.com.

For general open-source project questions, contact oss@incursa.com.

For unreleased builds, only the latest `main` branch or current release candidate is expected to receive security fixes.
