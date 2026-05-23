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

1. Start the app without an existing `appsettings.Local.json` beside the executable and confirm the first-run wizard opens.
2. Paste a BotFather token and confirm the wizard validates it without printing the token.
3. Let the wizard show a setup code and wait for a private Telegram message, send that exact code to the bot, and confirm the wizard captures the user ID.
4. Confirm `TelegramBot:AllowedUserIds` contains the captured user ID after setup.
5. Send `/version` and confirm the running app version matches the binary or commit being tested.
6. Send `/projects` and confirm the response does not expose unrelated local paths.
7. Send `/project add <absolute repository path>` for a directory under an allowed workspace root.
8. Send `/project current` and confirm the selected project is correct.
9. Send `/doctor` and confirm it explains access, routing, active project/session state, workspace roots, queue state, and a plausible next action.
10. Send `/new` and confirm the reply uses an auto-generated project-based session name without noisy status/model/thinking buttons; if Codex rate limits are available, confirm it includes a compact `Rate limits` line.
11. Send a normal text prompt and confirm live output returns to the private chat.
12. Send `/tail`, `/status`, `/usage`, `/model`, `/thinking`, and `/goal` and confirm each command is understandable without stale or truncated buttons; `/status`, `/model`, and `/thinking` should include compact rate limits when available.
13. Send `/status` during and after a turn and confirm the card distinguishes Codex work from Telegram delivery drain.
14. After a completed prompt, confirm Telegram does not append a bare successful `Turn completed` message, `/status` shows the last turn closeout, and `/tail` includes useful recent session events when available.
15. Send `/stop` and confirm pending queued messages for the session are cleared.

## Authorization

1. From an unallowlisted Telegram user, send `/help` and confirm the bot ignores it.
2. From an unallowlisted Telegram user, send `/whoami` and confirm the ID discovery response still works.
3. In an untrusted group, send `/sessions` from an allowlisted user and confirm the bot asks for `/trust`.
4. In that same group, send `/trust` from the allowlisted user and confirm the bot trusts the chat.
5. In a trusted group, send a command from an unallowlisted user and confirm it is ignored.
6. In a trusted group, send a command from an allowlisted user and confirm it works.

## Group And Forum Topics

1. Add the bot to a normal group and record the group chat ID with `/whoami`.
2. Send `/trust` in the group from an allowlisted admin account.
3. Send `/doctor` in the group root and confirm it says trusted group-root messages can auto-route.
4. Select or add a project in the group root, then send plain text and confirm it starts or continues the root chat session.
5. With privacy mode enabled, confirm `/send <text>` works and plain text behavior matches Telegram privacy expectations.
6. If ordinary group text is part of the demo, disable privacy mode in BotFather, re-add the bot if needed, and confirm ordinary text routes only in the intended trusted group or topic.
7. In a forum-enabled supergroup, run `/topic new <name>` with the bot missing topic-management rights and confirm the error is understandable.
8. Grant the needed topic rights and rerun `/topic new <name>`.
9. Send messages in two topics and confirm each topic remains bound to its own session.
10. Close, delete, or otherwise invalidate a test topic when practical and confirm topic-scoped output is not retried in the group root.
11. Restart the process and confirm topic/session bindings rehydrate from local state.

## Voice And Attachments

1. With `OpenAI:ApiKey` missing, send a voice note and confirm the failure explains the missing key.
2. With `OpenAI:FfmpegPath` invalid, send a voice note that requires transcoding and confirm the failure identifies `ffmpeg`, explains that voice transcription is optional, and does not route the failed audio to Codex.
3. With `TelegramInput:DefaultCaptureMode` set to `BundleWhenActiveOrMedia`, start a turn, then send a short voice note and confirm the transcript appears in an editable input bundle card.
4. Send a near-empty or zero-duration voice note and confirm the bot rejects it before download/transcription.
5. Send or simulate audio longer than `TelegramBot:MaxAudioDurationSeconds` and confirm the bot rejects it before download/transcription.
6. Send an image attachment with a prompt while the bundle is open and confirm the existing bundle card is edited in place rather than a separate Codex turn being started.
7. Tap `Steer current turn` and confirm Codex receives the transcript/text plus image input items.
8. Repeat with `Queue next` and confirm attachment files are copied under the configured data root before the queued bundle is persisted.
9. Tap `Clear` on a bundle with text and attachments and confirm the card remains open, content count resets, and durable attachment copies are removed.
10. Tap `Cancel` on a bundle with attachments and confirm the durable attachment files are deleted.
11. Delete the editable bundle card in Telegram, send another note into the same open bundle, and confirm a replacement card appears and future bundle updates edit the replacement instead of creating repeated duplicates.
12. Simulate or force a bundle send/steer failure and confirm the bundle remains open for retry with its attachments intact.
13. Send a Telegram album with multiple images/documents and confirm one bundle card appears with the first caption, all attachments, and the grouped source messages after the media-group debounce window.
14. Send an audio file larger than the OpenAI transcription limit and confirm the failure is clear.

## Queueing And Long Output

1. Send a prompt that produces a long response and confirm the Telegram chunks arrive in order.
2. While a turn is active, send another prompt and confirm the bot shows an input bundle card with Steer current turn and Queue next choices.
3. In two topics, trigger overlapping long responses and confirm one topic does not starve the other.
4. Send `/queue` during the active turn and confirm queued prompts appear in FIFO order with Send now, Edit, and Delete buttons.
5. Edit one queued prompt with `/queue edit <id> <new text>` and confirm the queued preview updates.
6. Delete one queued prompt and confirm only that queued item is removed.
7. Use Send now on one queued prompt while the turn is active and confirm it is sent as steering rather than waiting for normal queue drain.
8. Send `/outbound` during a backlog and confirm pending destination and chunk counts are plausible.
9. Enable `/trace on`, reproduce a long output, then send `/trace latest` and confirm diagnostics answer whether Telegram received the input, whether it was bundled/sent/queued/steered, whether Codex send/plan started, whether Codex saw a terminal event, how many assistant-output characters were captured, how many Telegram chunks were queued and sent, whether chunks are pending, and whether compaction, rate limits, timeouts, or send failures occurred.
10. Simulate a Telegram send failure or rate limit and confirm `/status` does not report delivery complete while messages or chunks remain pending.

## Restart And Persistence

1. Stop the process during idle state and restart it.
2. Confirm `projects.json`, `telegram-state.json`, and thread manifests are still loaded from the expected data root.
3. Send `/project current` and `/status` in the private chat and in any tested topic.
4. Stop the process during a live turn and restart it.
5. Confirm the bot reports a sane state. Mid-turn resume is not expected unless that behavior is explicitly implemented later.
6. Queue a bundle with an attachment, restart the process before it sends, and confirm the queued attachment path still exists under the configured data root and can be sent or cancelled.
7. If a queued attachment file is manually deleted, confirm the bot reports the missing attachment and does not send a text-only prompt that silently drops it.

## Release Gate

1. Run the automated build, test, publish, format, and package-vulnerability checks.
2. Confirm `scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release` is covered by the automated gate or run it directly.
3. Run this manual checklist for private chat.
4. Run group/forum checks only if the release notes claim group/forum support.
5. Record any skipped manual checks with the reason.
6. Do not publish a release if token, allowlist, path, or restart behavior is unclear.
