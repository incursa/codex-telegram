---
title: "Day-To-Day Usage"
---

# Day-To-Day Usage

Use this guide after the bot is configured and the private-chat smoke test works.
For first-time setup, start with [getting-started.md](getting-started.md).
For every command, parameter, and expected behavior, use [command-reference.md](command-reference.md).
For Telegram buttons and menus, use [menus.md](menus.md).

## Optional Mini App

When enabled by the operator, open the bot's Telegram menu button to view a mobile-friendly, Incursa UI Kit supervision dashboard. It shows `Needs attention`, recent activity, runtime/session/project context, and read-only task detail with bounded timeline, Codex-reported changes, and safe artifact metadata. Continue using this chat for prompts, approvals, steering, and session/project changes. Use `/handoff` to emit a bounded task/review context packet into the authorized conversation; it does not transfer a workspace or replay an uncertain command. If the surface says `Preview`, it is explicit local preview mode (`?preview=1`) and is not connected to a Telegram identity or live Codex data. If it says `Stale`, the last confirmed live snapshot is being shown while the host is unavailable.

The Mini App only receives project/task display labels rather than raw local filesystem paths. Review data is deterministic and bounded: at most 200 changed-file previews and 100 artifact records are returned, while artifact paths, generated results, and other raw metadata remain server-side.

For isolated parallel work, use /task new [name] [| baseRef]. It provisions a task-owned Git worktree, branch, development port, and database namespace, then starts a new Codex session in that worktree. Inspect it with /task status; stop the session before release, clean release requires confirmation, and dirty-worktree discard is a separate explicit command.

Telegram may open the profile Main Mini App and the chat menu Mini App with different initial presentation. The dashboard supports compact, full-height, and true fullscreen webviews. In compact mode it requests the maximum available height when the client allows that transition; use the `Fullscreen` button for a user-initiated true fullscreen transition when available. The `Webview` badge reflects the current client state, and returning to the app after minimizing or closing refreshes the dashboard data.

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

## Workspace Modes And Instance Boundaries

`CodexTelegram:Mode` selects the workspace contract, not the Telegram delivery mode:

| Mode | Day-to-day behavior |
| --- | --- |
| `GeneralPurpose` | `/projects` and `/project add` operate over the configured workspace roots. Select the project before creating or continuing work. |
| `Repository` | The instance is pinned to `CodexTelegram:RepositoryRoot`; use the configured label, when present, to distinguish it in operator messages. |

For a two-instance setup, use separate BotFather tokens, executable folders, and `CodexTelegram:Workspace:DataRoot` values. A separate `CodexTelegram:InstanceId` is useful when the default data-root convention is used, but an explicit data root is easier to audit. Do not run two pollers with the same bot token.

Configuration precedence is, from lowest to highest: `appsettings.json`, executable-local `appsettings.Local.json` (or launch-directory fallback), user secrets, `CODEX_TELEGRAM_` environment variables, then command-line arguments. The direct variables `TELEGRAM_BOT_TOKEN`, `TELEGRAM_ALLOWED_USER_IDS`, `TELEGRAM_ALLOWED_CHAT_IDS`, `OPENAI_API_KEY`, and `CODEX_PATH` fill only empty settings; they do not override a value already supplied by those layered sources.

## Daily Checklist

Use this short checklist at the start of a real work session:

1. Confirm you are talking to the intended bot account.
2. Send `/doctor` if authorization, routing, project, session, workspace, or queue state is unclear.
3. Send `/project current` before asking Codex to edit files.
4. Send `/new` when the work should not continue an older thread; add a short name only when it helps.
5. Send `/status` before assuming a long-running turn is stuck.
6. Send `/usage` when you need current five-hour and weekly Codex usage percentages and reset times.
7. Send `/tail` before assuming Telegram scrollback contains the complete transcript.

For a middle ground between quiet output and full progress, use `/output mode balanced`. Balanced publishes concise lifecycle/milestone messages, keeps routine progress represented by sparse still-working pulses, and leaves final output durable. It is a presentation mode; it does not change the text representation configured by `TelegramOutput:TextFormat`.

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

Scope rules are deliberately cumulative:

| Chat scope | Required access | Normal fallback |
| --- | --- | --- |
| Private chat | Allowlisted user | `/send <text>` for explicit dispatch. |
| Trusted group root | Allowlisted user plus trusted/allowlisted chat | Mention/reply or `/send` when privacy mode hides ordinary text. |
| Forum topic | All group-root requirements plus a forum-enabled supergroup and topic permissions | `/topic current`, `/topics`, or `/send`; fix trust/permissions before treating silence as a Codex failure. |

The app owns command parsing and `/help`. Guided setup can apply the app-owned command list and menu button through the Bot API, but it does not maintain a background sync. BotFather remains the manual path for profile text, group/privacy settings, and skipped or failed setup operations. If the picker is stale, use the manual command forms in [command-reference.md](command-reference.md).

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

The bot does not create or isolate Codex credentials. Confirm the configured Codex executable and authentication context in the same OS account/environment used by the process. Keep Codex auth files outside repositories and local Telegram configuration. Separate instances that require separate Codex identities need separate OS identities or a Codex-supported auth-home mechanism.

## Reading Output

Telegram output is rate-limited so busy sessions do not flood a chat, but final assistant output and queued text items are delivered as separate Telegram messages. Cards are the live control surface; the final answer is not edited in place.

Expect these behaviors:

1. The default `TelegramOutput:PresentationMode` is `Compact`: final output is durable, the typing indicator stays active while work is running, and sparse still-working pulses appear when a turn stays quiet.
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

1. `Compact` sends final output durably and publishes throttled still-working pulses while a turn is active and otherwise quiet.
2. `Verbose` sends progress, update, and final messages as durable Telegram messages according to the normal filters. Use it when watching the full process is useful.
3. `LiveCard` summarizes progress and updates into an editable live turn card. The card keeps a stable `Latest` line for assistant-visible output and a separate `Activity` line for ephemeral internal work. Final responses, errors, approval requests, artifacts, and the `~~ fin ~~` marker remain durable messages. The card does not show the internal Codex turn ID, and if Codex retries or restarts internally the same card is edited in place.
4. `Balanced` publishes concise lifecycle/tool milestones and sparse still-working pulses for routine progress; final output, errors, approvals, and artifacts remain durable.
5. `FinalOnly` suppresses normal progress/update chatter and sends only final output, errors, approval requests, artifacts, and terminal summaries that need attention.

Operational turn history is normalized and user-facing. It is separate from debug capture: history supports buttons such as `Show Updates`, `Show Full Turn`, and `Final`, while `/debug capture full on` records raw interface traffic for deeper diagnostics.

## Text Formatting

`TelegramOutput:TextFormat` controls text representation independently of the presentation mode:

| Value | Behavior |
| --- | --- |
| `PlainText` (default) | Sends Codex text literally using the legacy plain-text path. |
| `SafeMarkdownV2` | Converts a constrained Markdown subset to Telegram MarkdownV2, including headings, emphasis, HTTP(S) links, inline/fenced code, and list prefixes. Special characters are escaped. |

The safe formatter does not accept arbitrary Markdown or HTML. Unsupported, malformed, or unsafe links are emitted as escaped literal text. Long output is chunked before each chunk is formatted; if a chunk boundary would leave a fence, link, or other supported marker incomplete, delivery falls back to plain-text chunks so Telegram does not reject the message. If the active transport has no formatted-message capability, the effective format is also `PlainText`. File captions and command/control notices retain their existing plain-text path.

Example configuration:

```json
{
  "TelegramOutput": {
    "PresentationMode": "Balanced",
    "TextFormat": "SafeMarkdownV2"
  }
}
```

Compatibility example (observed in synthetic formatter tests): input `**bold** [docs](https://example.test)` stays literal in `PlainText`; with `SafeMarkdownV2`, it is converted to Telegram-safe emphasis and an HTTPS link. This is synthetic evidence, not a Telegram-live rendering claim.

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

By default, the bot captures text, voice transcripts, images, and documents into an input bundle before starting Codex. Each new item resets the bundle's idle timer. The bundle automatically sends or queues after 25 seconds with no additional input, so a forgotten Send tap does not leave the transcript stranded. Use the buttons when you want to send, queue, steer, clear, or cancel earlier.

Very long plain-text messages still open an input bundle even if `TelegramInput:DefaultCaptureMode` is changed to `ImmediateText`, which keeps Telegram-split prompts together instead of sending the first chunk immediately.

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

Do not infer live readiness from local output alone. A successful `/doctor` proves the bot's reported local state; it does not prove that an unobserved group update arrived or that a Codex turn was authenticated. Use `/tail`, `/outbound`, and the manual checklist when diagnosing delivery, then record whether the evidence was automated, synthetic, local live, or real Telegram live.

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
