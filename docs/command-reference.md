# Command Reference

This is the detailed reference for Telegram commands supported by Incursa Codex Telegram.

Use [README.md](../README.md) for first setup and [usage.md](usage.md) for day-to-day workflows.

## Syntax Rules

1. Commands are case-insensitive.
2. Arguments are separated by whitespace unless a command says otherwise.
3. Session IDs can be abbreviated if the prefix is unambiguous.
4. Project selectors can be a list number, project key, project name prefix, or full path.
5. Private chats, trusted group roots, and forum topics can auto-route plain text to the active session.
6. Use `/send <text>` when Telegram privacy mode or an unsupported chat type prevents normal auto-routing.
7. Attachments are forwarded to Codex when they are attached to a routed message.
8. Voice notes are transcribed first, then sent to the active session.
9. Groups and forum topics require an allowed user plus either `AllowedChatIds` or `/trust` from an allowed user in that chat.

## Quick Workflow

```text
/doctor
/project add C:\src\your-repo
/new setup-check
Summarize this repository and tell me the next safest setup check to run.
/tail 80
```

Use a Unix path on Linux/macOS:

```text
/project add /home/you/src/your-repo
```

## Commands

### `/help`

Shows the built-in command summary and navigation buttons.

Syntax:

```text
/help
```

Expected behavior:

1. Replies with the supported command list.
2. Adds navigation buttons for `Sessions`, `Projects`, and `Help`.

### `/whoami`

Shows the Telegram identifiers needed for configuration and troubleshooting.

Syntax:

```text
/whoami
```

Expected behavior:

1. Shows Telegram user ID.
2. Shows chat ID.
3. Shows topic thread ID when sent inside a forum topic.
4. Works before the user allowlist is configured so first-time setup can discover IDs.

Do not show `/whoami` in a public video unless you are comfortable exposing the IDs.

### `/version`

Shows the app version for the currently running Telegram process.

Syntax:

```text
/version
```

Expected behavior:

1. Shows the Incursa Codex Telegram assembly version.
2. Helps confirm whether Telegram is talking to the binary you just installed or an older process.

### `/trust`

Trusts the current group or forum chat for allowlisted users without copying a chat ID into configuration.

Syntax:

```text
/trust
/trust chat
/trust remove
```

Expected behavior:

1. Works only for users already listed in `TelegramBot:AllowedUserIds`.
2. In a private chat, explains that no chat trust entry is required.
3. In a group or forum topic, stores the current chat ID in local Telegram state.
4. Allows future commands, callbacks, topic workflows, audio, and attachments from allowlisted users in that chat.
5. Allows the trusted chat root and each forum topic to keep separate active project/session state.
6. `/trust remove` removes Telegram-granted trust for the current chat. If the chat is also listed in `TelegramBot:AllowedChatIds`, configuration still allows it.

### `/doctor`

Explains the current conversation, routing, project, session, workspace, queue state, and next recommended action.

Aliases:

```text
/diag
/diagnostics
```

Syntax:

```text
/doctor
```

Expected behavior:

1. Shows whether the user and chat are allowed.
2. Shows whether plain text can auto-route.
3. Shows known project/session counts and current selections.
4. Shows workspace roots and process directory.
5. Shows outbound queue counts.
6. Ends with a concrete next action.

Use this before changing configuration blindly.

### `/projects`

Lists known local project directories.

Syntax:

```text
/projects
```

Expected behavior:

1. Lists projects stored in local state.
2. Marks the active project for this Telegram conversation.
3. Adds `Use` buttons when projects are available.

### `/project add <path>`

Adds a local directory to the project catalog and selects it for the current conversation.

Syntax:

```text
/project add <absolute directory path>
```

Examples:

```text
/project add C:\src\my-repo
/project add /home/you/src/my-repo
```

Expected behavior:

1. Validates that the directory is under an allowed workspace root.
2. Adds the normalized path to local project state.
3. Selects the project for the current private chat, group, or topic.
4. Rejects paths outside configured workspace roots.

### `/project <number|name|path>`

Selects an existing project.

Syntax:

```text
/project <number>
/project <name>
/project <absolute path>
```

Examples:

```text
/project 1
/project codex-telegram
/project C:\src\my-repo
```

Expected behavior:

1. Selects a matching known project.
2. Rejects ambiguous names and asks for a clearer selector.
3. Stores the selection for the current conversation.

### `/project current`

Shows the active project for the current conversation.

Syntax:

```text
/project current
```

Expected behavior:

1. Shows project name/key/path when a project is selected.
2. Gives selection guidance when no project is active.

### `/new [name]`

Creates and selects a new Codex session in the active project.

Syntax:

```text
/new
/new <session name>
```

Example:

```text
/new release-readiness
```

Expected behavior:

1. Requires an active project.
2. Creates a Codex session with the supplied name, or an auto-generated project-based name when omitted.
3. Selects it for the current conversation.
4. Starts following live output for that session.
5. Includes a compact `Rate limits` line when Codex account data is available quickly.

### Plain Text Message

In a private chat, trusted group root, or forum topic, a normal message continues the active session.

Example:

```text
Review the README and tell me the top three setup gaps. Do not edit files.
```

Expected behavior:

1. Uses the selected session when one exists.
2. Creates a project-based default session if no session is selected.
3. Sends attachments with the prompt when attachments are present.
4. Queues the prompt if a turn is already active.

### `/send <text>`

Sends text to the active session.

Syntax:

```text
/send <text>
```

Example:

```text
/send summarize the current repository state
```

Expected behavior:

1. Routes text to the active session or creates one when allowed.
2. Useful when Telegram privacy mode or an unsupported chat type prevents normal auto-routing.
3. Queues the prompt if a turn is already active.

### `/steer <text>`

Adds guidance to an active turn.

Syntax:

```text
/steer <text>
```

Example:

```text
/steer focus on the failing test first
```

Expected behavior:

1. Requires an active selected session.
2. Sends steering text to the currently active Codex turn.
3. Replies with an error if there is no live turn to steer.

Use `/send` for normal new work. Use `/steer` only while Codex is already working. Steering text is sent immediately and cannot be edited after the bot hands it to Codex; edit queued text first with `/queue edit <id> <new text>`.

### `/queue`

Shows queued prompts submitted by you for the current Telegram conversation.

Syntax:

```text
/queue
/queued
```

Expected behavior:

1. Lists queued prompts in FIFO order for the current private chat, group root, or forum topic.
2. Shows the target session, queued age, short queue item ID, prompt preview, and attachment count.
3. Adds `Send now`, `Edit`, and `Delete` buttons for each listed item.
4. Keeps the list conversation-scoped by default so queued text from other chats or topics is not shown accidentally.

### `/queue all`

Shows your queued prompts across Telegram conversations.

Syntax:

```text
/queue all
```

Expected behavior:

1. Lists queued prompts submitted by your Telegram user ID across conversations.
2. Adds the conversation label for each queued item.
3. Uses the same `Send now`, `Edit`, and `Delete` buttons.

### `/queue edit <id> <new text>`

Replaces the text for one queued prompt.

Syntax:

```text
/queue edit <id> <new text>
```

Example:

```text
/queue edit a1b2c3d4 focus only on the failing Linux startup path
```

Expected behavior:

1. Accepts a full queue item ID, an unambiguous prefix, or the current conversation list number.
2. Replaces queued text while preserving any queued attachments.
3. Rejects unknown, ambiguous, or already-drained items without changing the queue.

### `/queue delete <id>`

Deletes one queued prompt.

Syntax:

```text
/queue delete <id>
```

Expected behavior:

1. Accepts a full queue item ID, an unambiguous prefix, or the current conversation list number.
2. Removes only that queued prompt.
3. Deletes temporary attachment files owned by that queued prompt.
4. Leaves other queued prompts and session-level state untouched.

### `/queue send <id>`

Removes one queued prompt and sends it as steering input to the active turn.

Syntax:

```text
/queue send <id>
/queue now <id>
/queue steer <id>
```

Expected behavior:

1. Accepts a full queue item ID, an unambiguous prefix, or the current conversation list number.
2. Removes the queued prompt before attempting to steer the active turn.
3. Sends text and preserved attachments through the active-turn steering path.
4. Requeues the item if steering fails, including when no active turn is running.
5. Deletes temporary attachment files only after steering succeeds or after the target session is gone.

### `/sessions`

Lists active and Telegram-managed sessions.

Syntax:

```text
/sessions
```

Expected behavior:

1. Lists active and recently managed sessions.
2. Marks the active session with `*`.
3. Shows status and relative last activity.
4. Adds `Use` buttons for listed sessions.

### `/sessions all [count]`

Shows recent Codex history, including older idle sessions.

Syntax:

```text
/sessions all
/sessions all <count>
```

Examples:

```text
/sessions all
/sessions all 20
```

Expected behavior:

1. Includes older idle history.
2. Clamps the count to the supported range.
3. Adds `Use` buttons for listed sessions.

### `/use <sessionId>`

Selects an existing session for the current conversation.

Syntax:

```text
/use <sessionId>
```

Example:

```text
/use 019df8e5
```

Expected behavior:

1. Accepts a full session ID or unambiguous prefix.
2. Selects the session for this conversation.
3. Starts following live output for that session.
4. Rejects unknown or ambiguous IDs.

### `/status [sessionId]`

Shows session status.

Syntax:

```text
/status
/status <sessionId>
```

Expected behavior:

1. Defaults to the active session.
2. Shows status, working directory, model, thinking effort, created time, last activity, and use command.
3. Shows exit code or last error when present.
4. Includes a compact `Rate limits` line with five-hour and weekly block percentages and reset times when Codex account data is available.

### `/usage`

Shows Codex account usage reported by the local Codex app-server.

Syntax:

```text
/usage
```

Expected behavior:

1. Reads Codex account rate-limit data from the local Codex app-server.
2. Shows remaining percentage for the five-hour block.
3. Shows remaining percentage for the weekly block.
4. Shows reset timing and local reset time when Codex reports reset timestamps.
5. Fails with clear setup text if the local Codex executable is missing or the app-server does not expose account usage.

### `/tail [count]`

Shows recent output for the active session.

Syntax:

```text
/tail
/tail <count>
```

Examples:

```text
/tail
/tail 80
```

Expected behavior:

1. Defaults to the active session.
2. Defaults to 40 recent lines when no count is supplied.
3. Starts following live output for the session.
4. Adds a session button when applicable.

### `/tail <sessionId> [count]`

Shows recent output for a specific session.

Syntax:

```text
/tail <sessionId>
/tail <sessionId> <count>
```

Example:

```text
/tail 019df8e5 120
```

Expected behavior:

1. Resolves the full session ID or unambiguous prefix.
2. Shows recent output for that session.
3. Starts following live output for that session in the current conversation.

### `/model`

Shows model settings and model-selection buttons for the active session.

Syntax:

```text
/model
```

Expected behavior:

1. Shows current model and thinking effort.
2. Includes a compact `Rate limits` line when Codex account data is available quickly.
3. Shows available thinking efforts when known.
4. Shows up to eight model buttons when available.
5. Marks the selected model with `[x]`.

### `/model [model] [thinking <effort>]`

Changes model settings for the active session.

Syntax:

```text
/model <model>
/model <model> thinking <effort>
/model thinking <effort>
```

Examples:

```text
/model gpt-5.4
/model gpt-5.4 thinking high
/model thinking xhigh
```

Supported thinking values:

```text
minimal
low
medium
high
xhigh
```

Expected behavior:

1. Updates the selected session settings.
2. Leaves unspecified values unchanged.
3. Returns the updated model settings.
4. Includes a compact `Rate limits` line when Codex account data is available quickly.
5. Rejects invalid model or effort values reported by Codex.

### `/thinking`

Shows thinking-effort buttons for the active session.

Syntax:

```text
/thinking
```

Expected behavior:

1. Shows current model and thinking effort.
2. Shows available thinking-effort buttons when known.
3. Marks the selected effort with `[x]`.

### `/thinking <effort>`

Changes the thinking effort for the active session.

Syntax:

```text
/thinking <minimal|low|medium|high|xhigh>
```

Example:

```text
/thinking high
```

Expected behavior:

1. Updates only the thinking effort.
2. Leaves the model unchanged.
3. Returns the updated model settings.

### `/goal`

Shows the current Codex goal for the active session.

Syntax:

```text
/goal
```

Expected behavior:

1. Defaults to the active session.
2. Shows goal status, objective, token use when Codex reports it, elapsed goal time when Codex reports it, and last update age.
3. Explains how to set a goal when none is present.
4. Fails with clear setup text if the configured Codex backend or installed app-server does not expose thread goals.

### `/goal [objective]`

Sets a new active goal objective for the active session.

Syntax:

```text
/goal <objective>
/goal set <objective>
```

Examples:

```text
/goal finish the release checklist and stop at the first real blocker
/goal set get /goal working in the Telegram app
```

Expected behavior:

1. Sets the objective on the active Codex thread.
2. Marks the goal active.
3. Shows the updated goal.

### `/goal clear|pause|resume|complete`

Changes or clears the goal for the active session.

Syntax:

```text
/goal clear
/goal pause
/goal resume
/goal complete
```

Expected behavior:

1. `/goal clear` removes the current goal.
2. `/goal pause` marks the current goal paused.
3. `/goal resume` marks the current goal active.
4. `/goal complete` marks the current goal complete.

### Inline Model Control Phrase

Sets model/thinking and sends a prompt in one message.

Syntax:

```text
Codex settings model <model> thinking <effort>: <prompt>
```

Example:

```text
Codex settings model gpt-5.4 thinking high: inspect the release docs for gaps
```

Expected behavior:

1. Parses the model/thinking directive.
2. Updates the session settings.
3. Sends the remaining prompt text to Codex.

### `/outbound`

Shows outbound Telegram queue status.

Syntax:

```text
/outbound
/outbound status
```

Expected behavior:

1. Shows pending destinations, messages, chunks, and characters.
2. Shows global backoff when Telegram rate limits are active.
3. Shows pending output for the current chat.

Only `status` is implemented as an outbound subcommand.

### `/stop [sessionId]`

Gracefully stops a session.

Syntax:

```text
/stop
/stop <sessionId>
```

Expected behavior:

1. Defaults to the active session.
2. Requests a graceful stop.
3. Clears pending queued prompts for that session.

### `/kill <sessionId> confirm`

Hard-stops a session.

Syntax:

```text
/kill <sessionId> confirm
```

Expected behavior:

1. Requires explicit `confirm`.
2. Resolves the full session ID or unambiguous prefix.
3. Hard-stops the session.
4. Clears pending queued prompts for that session.

Use `/stop` first unless a session is stuck.

### `/rename <sessionId> <new name>`

Renames a session.

Syntax:

```text
/rename <sessionId> <new name>
```

Example:

```text
/rename 019df8e5 release demo
```

Expected behavior:

1. Resolves the session ID.
2. Updates the display name.
3. Does not change transcript logs.

### `/forget <sessionId>`

Hides a stopped or exited session from the managed list without deleting logs.

Syntax:

```text
/forget <sessionId>
```

Expected behavior:

1. Resolves the session ID.
2. Removes it from the Telegram-managed session list.
3. Does not delete transcript logs.

### `/restart confirm`

Explains restart behavior for the standalone process.

Syntax:

```text
/restart confirm
```

Expected behavior:

1. Does not restart the process from Telegram.
2. Explains that restart must be handled by the terminal, service manager, scheduled task, or container/runtime supervisor.

### `/topics`

Lists topic/session bindings for the current chat.

Aliases:

```text
/threads
/topic list
/topic ls
```

Expected behavior:

1. Lists main-chat and forum-topic bindings known for the chat.
2. Shows session summary, project name, and queued prompt count when present.
3. Marks the current topic or chat root with `*`.

### `/topic current`

Shows the current topic/session binding.

Syntax:

```text
/topic current
```

Expected behavior:

1. Shows topic thread ID.
2. Shows active session status.
3. Shows active project status when present.

### `/launchpad on|off|status`

Arms or disarms the root chat for repeated launch commands and plain-text or audio launch messages.

Syntax:

```text
/launchpad on
/launchpad off
/launchpad status
```

Expected behavior:

1. Works only from the root of a forum-enabled supergroup.
2. `/launchpad on` arms the root chat for 10 minutes of inactivity.
3. `/launchpad status` shows whether the root chat is armed, the remaining time, the active project, and the launch template session if one is selected.
4. `/launchpad off` clears the armed state immediately.
5. The bot auto-clears expired launchpad state and notifies the root chat when the timeout elapses.
6. While launchpad is armed, plain text or audio messages in the root chat create a new topic/session pair with a deterministic topic title based on the root chat name and a per-chat lane number, then seed the new session with that message text.
7. Launches provision detached git worktrees under the allowed workspace root so each lane has isolated on-disk work.

### `/launch <name> [| <path>]`

Creates a new forum topic and matching Codex session while launchpad is armed, using a detached git worktree.

Syntax:

```text
/launch <name>
/launch <name> | <absolute directory path>
```

Expected behavior:

1. Works only in the root of a forum-enabled supergroup while launchpad is armed.
2. Uses the supplied path when provided and allowed.
3. Provisions a detached git worktree under the allowed workspace root and seeds the new session with the launch name, with the forum topic title capped so long prompts do not become the topic name.
3. Otherwise uses the active project for the current conversation.
4. Creates a Telegram topic and Codex session, then binds them together.
5. Copies the current root session's model and thinking settings onto the launched session when a root session is selected; otherwise the launched session uses the normal Codex defaults.

### `/topic new <name> [| <path>]`

Creates a new Telegram forum topic and matching Codex session.

Syntax:

```text
/topic new <name>
/topic new <name> | <absolute directory path>
```

Examples:

```text
/topic new release readiness
/topic new docs polish | C:\src\my-repo
```

Expected behavior:

1. Works only in a forum-enabled supergroup.
2. Requires the bot to have the topic-management rights Telegram requires.
3. Uses the supplied path when provided and allowed.
4. Otherwise uses the active project for the current conversation.
5. Creates a Telegram topic and Codex session, then binds them together.

### `/topic attach [sessionId]`

Binds the current forum topic to an existing Codex session.

Syntax:

```text
/topic attach
/topic attach <sessionId>
```

Expected behavior:

1. Must be run inside the forum topic to bind.
2. With no session ID, tries the topic's current session first, then the user's private-chat active session.
3. With a session ID, resolves the full ID or unambiguous prefix.
4. Updates the topic's active session and project binding.

## Attachments

Images and documents can be attached to a routed message.

Expected behavior:

1. The file is downloaded to a temporary local path.
2. The attachment is forwarded to Codex with the prompt.
3. Temporary files are removed after processing when possible.

## Voice Notes

Voice notes are transcribed before being sent to Codex.

Requirements:

1. `OpenAI:ApiKey` or `OPENAI_API_KEY`.
2. A transcription-capable `OpenAI:Model`.
3. `ffmpeg` when transcoding is needed.
4. Audio duration inside configured limits.

Suggested first voice prompt:

```text
Please review the current project and tell me the three most important setup risks. Keep it concise and do not edit files.
```

## Queueing And Delivery

If a prompt arrives while a session has an active turn, it is queued for that session.

Expected behavior:

1. Queued prompts run in order.
2. Separate sessions can progress independently.
3. Telegram output is rate-limited and may be batched.
4. Batched output is concatenated with simple spacing and preserves multi-line content.
5. `/tail` is the best source when Telegram scrollback is not enough.
