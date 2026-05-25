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
12. Send `/tail`, `/status`, `/usage`, `/model`, `/thinking`, `/plan`, and `/goal` and confirm each command is understandable without stale or truncated buttons; `/status`, `/model`, and `/thinking` should include compact rate limits when available.
13. Send `/plan <request>` and confirm the bot starts the Plan mode flow; if Codex asks a follow-up question, answer it with `/answer <answer>` and confirm the conversation resumes cleanly.
14. Send `/status` during and after a turn and confirm the card distinguishes Codex work from Telegram delivery drain.
15. After a completed prompt, confirm Telegram appends a standalone `~~ fin ~~` marker rather than a bare `Turn completed` message, `/status` shows the last turn closeout, and `/tail` includes useful recent session events when available.
16. Send `/output mode` and confirm `LiveCard` is the default unless local configuration intentionally changed it.
17. Send `/output mode final`, run a short prompt, and confirm progress/update chatter is suppressed while final output and `~~ fin ~~` are still durable messages.
18. Send `/output mode verbose`, run a short prompt, and confirm update messages are visible as durable Telegram messages.
19. Send `/output mode live`, run a short prompt, and confirm updates edit one live turn card while the final answer is delivered as normal Telegram messages.
20. Use the `Output Mode`, `Show Updates`, `Show Full Turn`, `Final`, `Trace`, and `Diagnostics` buttons from session/live cards when available; slash commands should be fallbacks.
21. Send `/turn updates`, `/turn full`, and `/turn final` after a prompt and confirm retained operational history is available without enabling full debug capture.
22. Delete the live turn card during a run and confirm the next live-card update creates a replacement card without blocking final output delivery.
23. Send `/stop` and confirm pending queued messages for the session are cleared.

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
3. With the default `TelegramInput:DefaultCaptureMode` of `BundleAlways`, send a short text prompt while idle and confirm it opens an editable input bundle card instead of starting a turn immediately.
4. Send a near-empty or zero-duration voice note and confirm the bot rejects it before download/transcription.
5. Send or simulate audio longer than `TelegramBot:MaxAudioDurationSeconds` and confirm the bot rejects it before download/transcription.
6. Send an image attachment with a prompt while the bundle is open and confirm the existing bundle card is edited in place rather than a separate Codex turn being started.
7. Tap `Steer current turn` and confirm Codex receives the transcript/text plus image input items.
8. Repeat with `Queue next` and confirm attachment files are copied under the configured data root before the queued bundle is persisted.
9. Tap `Clear` on a bundle with text and attachments and confirm the card remains open, content count resets, and durable attachment copies are removed.
10. Tap `Cancel` on a bundle with attachments and confirm the durable attachment files are deleted.
11. Delete the editable bundle card in Telegram, send another note into the same open bundle, and confirm a replacement card appears and future bundle updates edit the replacement instead of creating repeated duplicates.
12. Simulate or force a bundle send/steer failure and confirm the bundle remains open for retry with its attachments intact.
13. Simulate or force slow bundle steering acceptance and confirm the bot posts a durable pending message, then later reports success or failure.
14. Send a Telegram album with multiple images/documents and confirm one bundle card appears with the first caption, all attachments, and the grouped source messages after the media-group debounce window.
15. With `TelegramInput:DefaultCaptureMode` temporarily set to `ImmediateText`, send a very long plain-text prompt while idle and confirm it opens an input bundle instead of starting a turn on the first chunk, then send the next chunk and confirm it stays in the same bundle.
16. Leave an input bundle untouched for `TelegramInput:AutoDispatchAfterSeconds` and confirm it auto-sends when idle, auto-queues when the target session is busy, and does not auto-steer without an explicit button tap.
17. Send an audio file larger than the OpenAI transcription limit and confirm the failure is clear.

## Queueing And Long Output

1. Send a prompt that produces a long response and confirm the Telegram chunks arrive in order.
2. While a turn is active, send another prompt and confirm the bot shows an input bundle card with Steer current turn and Queue next choices.
3. In two topics, trigger overlapping long responses and confirm one topic does not starve the other.
4. Send `/queue` during the active turn and confirm queued prompts appear in FIFO order with Send now, Edit, and Delete buttons.
5. Edit one queued prompt with `/queue edit <id> <new text>` and confirm the queued preview updates.
6. Delete one queued prompt and confirm only that queued item is removed.
7. Use Send now on one queued prompt while the turn is active and confirm it is sent as steering rather than waiting for normal queue drain.
8. Simulate or force slow queued steering acceptance and confirm the bot posts a durable pending message, then later reports success or requeues on failure.
9. Send `/outbound` during a backlog and confirm pending destination and chunk counts are plausible.
10. Enable `/debug capture on`, reproduce a long output, then send `/debug capture latest` and confirm diagnostics answer whether Telegram received the input, whether it was bundled/sent/queued/steered, whether Codex send/plan started, whether Codex saw a terminal event, how many assistant-output characters were captured, how many Telegram chunks were queued and sent, whether chunks are pending, and whether compaction, rate limits, timeouts, or send failures occurred.
11. Enable `/debug capture full on 30m`, send a short test prompt, inspect the local trace file, and confirm inbound text, Codex input/final output, and outbound Telegram chunk text are present with obvious secret-looking values redacted. Then send `/debug capture full off`.
12. If `TelegramDebugTrace:CaptureAttachmentCopies` is enabled, send one small image/document during full capture and confirm a copy appears under `telegram-traces/yyyyMMdd/<traceId>.attachments/` and the JSONL event records `attachmentCopyPath.*`.
13. Simulate a Telegram send failure or rate limit and confirm `/status` does not report delivery complete while messages or chunks remain pending.
14. In `LiveCard` mode, simulate a card edit rejection and confirm final-response chunks still enqueue and diagnostics record replacement/failure separately from durable output delivery.
15. In `FinalOnly` mode, confirm progress/update events can still be requested through `/turn full` or `/turn updates` when retained by operational history settings.

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
