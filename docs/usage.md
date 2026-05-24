# Day-To-Day Usage

Use this guide after the bot is configured and the private-chat smoke test works.
For first-time setup, start with [getting-started.md](getting-started.md).
For every command, parameter, and expected behavior, use [command-reference.md](command-reference.md).
For Telegram buttons and menus, use [menus.md](menus.md).

## Normal Start

1. Start the bot from its app folder, or use the full executable path; settings resolve beside the executable by default. If that file is missing and the launch directory has `appsettings.Local.json`, the app uses the launch-directory file.
2. Use `--run` for normal unattended operation.
3. Confirm the process stays running.
4. In Telegram, send `/doctor` if anything feels unclear.

Published Windows example:

```powershell
.\artifacts\publish\win-x64\codex-telegram.exe --run
```

Source example:

```powershell
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

## Daily Checklist

Use this short checklist at the start of a real work session:

1. Confirm you are talking to the intended bot account.
2. Send `/doctor` if authorization, routing, project, session, workspace, or queue state is unclear.
3. Send `/project current` before asking Codex to edit files.
4. Send `/new` when the work should not continue an older thread; add a short name only when it helps.
5. Send `/status` before assuming a long-running turn is stuck.
6. Send `/usage` when you need current five-hour and weekly Codex usage percentages and reset times.
7. Send `/tail` before assuming Telegram scrollback contains the complete transcript.

## Daily Private-Chat Flow

1. Send `/projects` to see known repositories.
2. Send `/project current` to confirm the active repository.
3. If needed, send `/project add <absolute repository path>`.
4. Send `/new` for a fresh Codex session, or `/new <short name>` when you want a specific label.
5. Send normal messages to continue the active session.
6. Use `/tail` when Telegram scrollback is not enough.
7. Use `/status` when you need the current session state.
8. Use `/usage` when you need five-hour or weekly Codex reset timing.

Private chat is the primary setup workflow. Trusted group roots and forum topics are useful once you understand Telegram privacy mode, permissions, and chat allowlists.

## Sending Work

Plain text in a private chat, trusted group root, or forum topic normally goes to the active session.
Use `/send <text>` when Telegram privacy mode or an unsupported chat type prevents normal auto-routing.

Useful examples:

```text
/send summarize the current repository state
/send run the smallest relevant tests and report failures
/steer focus on the failing test first
```

Use `/steer <text>` only while a turn is active. It is for steering an in-progress Codex turn, not for starting ordinary work.

## Model And Thinking Controls

Use `/model` and `/thinking` to inspect or change the selected session's Codex model settings.
Session, status, model, and thinking replies include a compact `Rate limits` line when Codex account data is available. Use `/usage` for full details and setup errors.

Common flow:

1. Send `/model` to view the current model and available model buttons.
2. Tap a model button or send `/model <model>`.
3. Send `/thinking` to view reasoning-effort choices.
4. Send `/thinking high` or `/thinking xhigh` when you intentionally want more reasoning.
5. Use the bootstrap `Codex runtime` menu to set a separate Plan mode thinking default when you want plan turns to use a different effort from normal turns.

You can also include an inline control phrase in a prompt:

```text
Codex settings model gpt-5.4 thinking high: inspect this repository and summarize the safest next setup check
```

## Goal Controls

Use `/goal` to inspect or change the selected session's Codex goal when the connected Codex app-server supports thread goals.

Common flow:

1. Send `/goal` to view the current goal.
2. Send `/goal <objective>` or `/goal set <objective>` to set the session goal.
3. Add a token budget with `/goal set <objective> --budget <tokens>` when you want Codex to track a budget.
4. Send `/goal pause`, `/goal resume`, `/goal complete`, or `/goal clear` to change goal state.

If the bot says goals are unavailable, update Codex and confirm the app-server backend is being used.

## Reading Output

Telegram output is rate-limited so busy sessions do not flood a chat, but final assistant output and queued text items are delivered as separate Telegram messages. Cards are the live control surface; the final answer is not edited in place.

Expect these behaviors:

1. The default `TelegramOutput:PresentationMode` is `LiveCard`: progress and updates refresh one editable turn card, while the final response is sent as durable Telegram message chunks.
2. Long individual messages may split across multiple Telegram messages.
3. The bot does not combine unrelated queued text items into one visible Telegram message.
4. If the local outbound buffer is compacted, the bot sends an explicit compaction notice instead of silently pretending older updates are still present.
5. A completed turn emits a standalone `~~ fin ~~` marker after the final output so Telegram scrollback has an explicit end-of-turn signal.
6. `/output mode` shows or changes the process-level output presentation mode.
7. `/turn updates`, `/turn full`, `/turn progress`, and `/turn final` show operational turn history retained by the bot.
8. `/tail` is the best source when you suspect scrollback is incomplete.
9. `/outbound` shows delayed outbound Telegram messages and chunks.

If output looks incomplete, send `/tail` first. If `/tail` has the missing text, the issue is Telegram delivery. If `/tail` is missing it too, inspect the Codex session itself.
Do not use Telegram scrollback alone as evidence that Codex lost content.

Output modes:

1. `Verbose` sends progress, update, and final messages as durable Telegram messages according to the normal filters. Use it when watching the full process is useful.
2. `LiveCard` summarizes progress and updates into an editable live turn card. The card keeps a stable `Latest` line for assistant-visible output and a separate `Activity` line for ephemeral internal work. Final responses, errors, approval requests, artifacts, and the `~~ fin ~~` marker remain durable messages. The card does not show the internal Codex turn ID.
3. `FinalOnly` suppresses normal progress/update chatter and sends only final output, errors, approval requests, artifacts, and terminal summaries that need attention.

Operational turn history is normalized and user-facing. It is separate from debug capture: history supports buttons such as `Show Updates`, `Show Full Turn`, and `Final`, while `/debug capture full on` records raw interface traffic for deeper diagnostics.

## Queueing

If you send a new prompt while a session has an active turn, the bot queues the prompt instead of racing the active Codex turn.
This is intentional. It preserves session order and prevents two prompts from writing through the same Codex session at the same time.

Useful checks:

1. `/status` shows the selected session and compact rate-limit state when available.
2. `/outbound` shows pending Telegram output.
3. `/tail` shows recent session output.
4. `/queue` shows your queued prompts for the current conversation with Send now, Edit, and Delete buttons.
5. `/usage` shows five-hour and weekly Codex usage and reset times.
6. `/version` confirms which app binary is answering in Telegram.
7. `/stop` clears pending queued messages for the stopped session.

Queue controls:

1. Tap `Send now` to remove a queued prompt and steer the currently active turn with it. If no turn is active, the prompt stays queued.
2. Tap `Edit` to get the exact `/queue edit <id> <new text>` command for replacing prompt text.
3. Tap `Delete` to remove one queued prompt and clean up any temporary attachment files.
4. Send `/queue all` to see your queued prompts across conversations.

Queued prompts are editable until they are drained or sent now. Once a prompt has been steered into an active turn, the bot cannot edit or recall that steering message.

Queueing is per session and per Telegram conversation. A trusted group root and each forum topic can continue independently when they are bound to different sessions.

## Attachments And Voice

Images and documents can be sent with a prompt. Voice notes are transcribed before they are sent to Codex; Codex receives the transcript, not raw Telegram audio.

When the bot captures text, voice transcripts, images, or documents into an input bundle, each new item resets the bundle's idle timer. By default, the bundle automatically sends or queues after 25 seconds with no additional input, so a forgotten Send tap does not leave the transcript stranded. Use the buttons when you want to send, queue, steer, clear, or cancel earlier.

Voice requirements:

1. `OpenAI:ApiKey` or `OPENAI_API_KEY` must be configured.
2. `OpenAI:Model` must name a transcription-capable model.
3. `ffmpeg` must be available only when transcoding is needed. Telegram voice notes commonly need it because they often arrive as OGG/OPUS.
4. Audio must fit the configured duration limits and OpenAI upload limits.

If `ffmpeg` is missing when conversion is needed, the bot replies with setup guidance and does not send the audio message to Codex.

If voice fails, send `/doctor`, then check `OpenAI:ApiKey`, `OpenAI:Model`, and `OpenAI:FfmpegPath` if the audio format needs transcoding.

## Groups And Forum Topics

For groups and forum topics:

1. Add the individual Telegram user ID to `TelegramBot:AllowedUserIds`.
2. Send `/trust` in the group from that allowlisted user, or add the group chat ID to `TelegramBot:AllowedChatIds`.
3. Keep privacy mode enabled unless ordinary group text should route to Codex.
4. Use a trusted group root for one project/session lane.
5. Prefer forum topics when one group needs multiple concurrent Codex sessions.

Forum-topic flow:

```text
/topic new release-readiness
/topic current
/topic attach <sessionId>
```

If `/topic new` fails, confirm the chat is a forum-enabled supergroup and the bot has the required topic-management rights.

## Shutdown And Restart

Stop the console process with Ctrl+C, or stop it through the service manager that owns it.

After restart:

1. Start from the same working directory, or keep `appsettings.Local.json` beside the executable.
2. Confirm the expected `appsettings.Local.json` is loaded.
3. Confirm the same `CodexTelegram:Workspace:DataRoot` is loaded.
4. Send `/project current`.
5. Send `/status`.

Conversation/session bindings rehydrate from `telegram-state.json`. A mid-turn Codex execution does not resume after process restart.

## Safe Operating Habits

1. Keep workspace roots narrow.
2. Keep `appsettings.Local.json` untracked.
3. Do not paste bot tokens, OpenAI keys, or private transcripts into public issues.
4. Start new workflows in private chat before using groups.
5. Use `/doctor` before changing config blindly.
6. Rotate the BotFather token if it is exposed.
7. Set explicit workspace roots and a default working directory before shared or recorded use; otherwise the runtime fallback may use the process current directory.
