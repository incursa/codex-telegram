# Menus And Buttons

This guide explains the Telegram buttons and interactive menus shown by Incursa Codex Telegram.

Use [README.md](../README.md) for first setup and [command-reference.md](command-reference.md) for command syntax.

## Bootstrap/Admin Menu

The terminal menu appears when the app starts without `--run` in an interactive terminal.

Use it to configure:

1. Telegram token and admin allowlist.
2. Optional chat allowlist for groups/forums.
3. OpenAI transcription key and model.
4. Codex executable path.
5. Codex default model and thinking effort.
6. Workspace roots and default working directory.
7. Local state root.

Behavior:

1. The menu writes `appsettings.Local.json` in the current working directory.
2. Stored secret values are not displayed back in plain text.
3. `!clear` clears fields when the prompt says clearing is supported.
4. When Codex is reachable, the menu can use live Codex model and effort choices.
5. If Codex is not reachable, the menu falls back to curated examples.

Callout:

> Start the app from the folder that should own `appsettings.Local.json`. If the menu writes config in the wrong place, stop the app, change directories, and launch it again.

## Navigation Buttons

Most command replies include a final row of navigation buttons:

| Button | Action |
| --- | --- |
| `Sessions` | Opens the session list. |
| `Projects` | Opens the project list. |
| `Help` | Shows the built-in help text. |

Behavior:

1. Buttons run callback actions in the same Telegram conversation.
2. The bot edits the original message when possible.
3. If a reply is split into multiple Telegram messages, buttons appear on the final chunk.

## Project Buttons

`/projects` adds `Use` buttons when projects are available.

Button labels:

```text
Use
Use 1
Use 2
```

Behavior:

1. If there is only one project, the button is labeled `Use`.
2. If there are multiple projects, buttons are numbered to match the visible list.
3. Tapping a button selects that project for the current conversation.
4. Project selection does not automatically create a Codex session; use `/new <name>` or send a message after selecting.

## Session Buttons

`/sessions`, `/status`, and `/tail` can include session buttons.

Button labels:

```text
Use
Use 1
Use 2
```

Behavior:

1. If there is only one listed session, the button is labeled `Use`.
2. If there are multiple sessions, buttons are numbered to match the visible list.
3. Tapping a button selects that session for the current conversation.
4. Selecting a session also follows live output for that session.

Session replies created by `/new` or `/use` usually suppress redundant `Use` buttons because the session is already selected.

## Model Menu

Open the model menu:

```text
/model
```

Expected layout:

```text
Model settings:
Session: release-demo
Model: gpt-5.4
Thinking: high
Rate limits (pro): 5-hour block: 83%, resets 8:30 AM; weekly block: 52%, resets May 10 6:00 AM
Available thinking: low, medium, high, xhigh
Use /model <model> thinking <effort>. Examples:
- /model gpt-5.4 thinking high
- /model gpt-5.4-mini thinking medium
Voice phrase: Codex settings model gpt-5.4-mini thinking high: <prompt>
```

Button behavior:

1. Model buttons show available Codex models when the Codex gateway reports them.
2. The selected model is marked with `[x]`.
3. Up to eight model buttons are shown.
4. Tapping a model updates the active session and redraws the model menu.
5. `Back` returns to the session status view.

The `Rate limits` line is compact by design. Use `/usage` for full details and setup errors.

Manual equivalent:

```text
/model gpt-5.4 thinking high
```

## Thinking Menu

Open the thinking menu:

```text
/thinking
```

Button behavior:

1. Thinking buttons show available reasoning efforts when known.
2. The selected effort is marked with `[x]`.
3. Tapping an effort updates only the active session thinking effort.
4. `Back` returns to the session status view.

Manual equivalent:

```text
/thinking high
```

Supported common values:

```text
minimal
low
medium
high
xhigh
```

The exact available values can depend on the selected Codex model.

## Status Buttons

Open status:

```text
/status
```

Expected content:

1. Session name.
2. Session status.
3. Working directory.
4. Model and thinking effort.
5. Compact rate limits when available.
6. Created and last-activity age.
7. Short `/use` command.
8. Exit code or last error when present.

Buttons:

1. `Use` when viewing a non-selected session.
2. Navigation buttons for sessions, projects, and help.

## Tail Output

Open recent output:

```text
/tail
```

Behavior:

1. Shows recent output from the selected session.
2. Starts following the session in the current Telegram conversation.
3. Splits long output into multiple Telegram messages when needed.
4. Adds buttons to the final chunk only.

Callout:

> Use `/tail` when Telegram scrollback is incomplete. It is the operator-facing way to inspect recent Codex output without relying on every live update being visible in chat history.

## Topic Buttons

Topic commands are for forum-enabled supergroups.

Common commands:

```text
/topic new docs polish
/topic current
/topic attach <sessionId>
/topics
```

Button behavior:

1. `/topics` lists chat roots and forum topics known to the bot.
2. Topic list entries can include session status, project name, and queued prompt count.
3. Session buttons in topic views bind or select the relevant session for that topic.

Use private chat first. Forum topics add Telegram group allowlist, privacy-mode, and permission complexity.

## Outbound Queue View

Open queue status:

```text
/outbound
```

Expected content:

1. Pending destinations.
2. Pending messages.
3. Pending chunks.
4. Pending characters.
5. Oldest waiting destination when present.
6. Global or chat-specific backoff when Telegram rate limits are active.

## Queued Prompt View

Open queued prompt controls:

```text
/queue
```

Expected content:

1. Queued prompts submitted by you for the current conversation.
2. FIFO order, short queue IDs, session names, age, prompt preview, and attachment count.
3. `Send now`, `Edit`, and `Delete` buttons per queued item.
4. Edit instructions that point to `/queue edit <id> <new text>`.
5. `/queue all` when you need to inspect your queued prompts across conversations.

Use this when output seems delayed, batched, or missing.
