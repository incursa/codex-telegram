# Manual Test Plan

Use this checklist before a demo, release tag, or release note that claims live Telegram behavior.

Record the date, operator, OS, published asset or commit SHA, Codex CLI version, and Telegram bot privacy-mode setting with the test result.

## Preconditions

1. `scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64` or the target-runtime equivalent passes.
2. If publish is intentionally skipped, `scripts\Test-ReleaseReadiness.ps1 -SkipPublish` passes and the selected publish path is validated separately.
3. Any CI or publish workflow result used for release evidence is recorded in the pull request or release notes.
4. The real `appsettings.Local.json` is untracked.
5. The bot token and OpenAI API key are not present in tracked files.
6. The configured `CodexTelegram:Workspace:WorkspaceRoots` are narrow enough for the demo.

## Private Chat

1. Start the bot with Telegram polling enabled and no admin allowlist long enough to run `/whoami`.
2. Send `/whoami` in a private chat and record the user ID.
3. Add the user ID to `TelegramBot:AllowedUserIds` and restart or relaunch.
4. Send `/projects` and confirm the response does not expose unrelated local paths.
5. Send `/project add <absolute repository path>` for a directory under an allowed workspace root.
6. Send `/project current` and confirm the selected project is correct.
7. Send `/doctor` and confirm it explains access, routing, active project/session state, workspace roots, queue state, and a plausible next action.
8. Send `/new <short name>` and confirm the reply summarizes the session without noisy status/model/thinking buttons.
9. Send a normal text prompt and confirm live output returns to the private chat.
10. Send `/tail`, `/status`, `/usage`, `/model`, and `/thinking` and confirm each command is understandable without stale or truncated buttons.
11. Send `/stop` and confirm pending queued messages for the session are cleared.

## Authorization

1. From an unallowlisted Telegram user, send `/help` and confirm the bot ignores it.
2. From an unallowlisted Telegram user, send `/whoami` and confirm the ID discovery response still works.
3. In a group not listed in `TelegramBot:AllowedChatIds`, send a command from an allowlisted user and confirm it is ignored.
4. In an allowed group, send a command from an unallowlisted user and confirm it is ignored.
5. In an allowed group, send a command from an allowlisted user and confirm it works.

## Group And Forum Topics

1. Add the bot to a normal group and record the group chat ID with `/whoami`.
2. Add that chat ID to `TelegramBot:AllowedChatIds`.
3. Send `/doctor` in the group root and confirm it clearly says root messages do not auto-route.
4. Send plain text to the group root and confirm the bot explains that it was not sent to Codex.
5. With privacy mode enabled, confirm `/send <text>` works and plain text behavior matches Telegram privacy expectations.
6. If plain group text is part of the demo, disable privacy mode in BotFather, re-add the bot if needed, and confirm ordinary text routes only in the intended topic or private chat.
7. In a forum-enabled supergroup, run `/topic new <name>` with the bot missing topic-management rights and confirm the error is understandable.
8. Grant the needed topic rights and rerun `/topic new <name>`.
9. Send messages in two topics and confirm each topic remains bound to its own session.
10. Close, delete, or otherwise invalidate a test topic when practical and confirm topic-scoped output is not retried in the group root.
11. Restart the process and confirm topic/session bindings rehydrate from local state.

## Voice And Attachments

1. With `OpenAI:ApiKey` missing, send a voice note and confirm the failure explains the missing key.
2. With `OpenAI:FfmpegPath` invalid, send a voice note that requires transcoding and confirm the failure identifies `ffmpeg`, explains that voice transcription is optional, and does not route the failed audio to Codex.
3. With a valid key and `ffmpeg`, send a short voice note and confirm the transcript is sent to the active session.
4. Send a near-empty or zero-duration voice note and confirm the bot rejects it before download/transcription.
5. Send or simulate audio longer than `TelegramBot:MaxAudioDurationSeconds` and confirm the bot rejects it before download/transcription.
6. Send an image attachment with a prompt and confirm Codex receives both.
7. Send a document attachment with a prompt and confirm Codex receives both.
8. Send an audio file larger than the OpenAI transcription limit and confirm the failure is clear.

## Queueing And Long Output

1. Send a prompt that produces a long response and confirm the Telegram chunks arrive in order.
2. While a turn is active, send another prompt and confirm it is queued rather than racing the active turn.
3. In two topics, trigger overlapping long responses and confirm one topic does not starve the other.
4. Send `/outbound` during a backlog and confirm pending destination and chunk counts are plausible.

## Restart And Persistence

1. Stop the process during idle state and restart it.
2. Confirm `projects.json`, `telegram-state.json`, and thread manifests are still loaded from the expected data root.
3. Send `/project current` and `/status` in the private chat and in any tested topic.
4. Stop the process during a live turn and restart it.
5. Confirm the bot reports a sane state. Mid-turn resume is not expected unless that behavior is explicitly implemented later.

## Release Gate

1. Run the automated build, test, publish, format, and package-vulnerability checks.
2. Confirm `scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release` is covered by the automated gate or run it directly.
3. Run this manual checklist for private chat.
4. Run group/forum checks only if the release notes claim group/forum support.
5. Record any skipped manual checks with the reason.
6. Do not publish a release if token, allowlist, path, or restart behavior is unclear.
