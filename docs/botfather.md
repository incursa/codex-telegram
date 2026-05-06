# BotFather Setup

Use this guide when creating the Telegram bot account that Incursa Codex Telegram will run behind.

Telegram's official bot-management documentation is [Telegram Bot Features](https://core.telegram.org/bots/features). Telegram documents `/setdescription`, `/setabouttext`, `/setcommands`, `/setjoingroups`, and `/setprivacy` as BotFather management commands, and notes that privacy mode affects which group messages a bot receives.

## Recommended First-Release Posture

Use this posture unless you are intentionally demoing group or forum-topic behavior:

1. Private chats enabled.
2. Group joins disabled.
3. Privacy mode enabled.
4. Commands configured for discoverability.
5. No token, local path, or private project detail in public bot text.

This keeps the first setup path narrow: one allowed Telegram user talking to one local Codex installation.

## Create The Bot

Open a Telegram chat with `@BotFather` and send:

```text
/newbot
```

Suggested display name:

```text
Incursa Codex
```

Suggested username pattern:

```text
<your-name-or-org>_codex_bot
```

The username must be unique and must end in `bot`.

After BotFather returns the token, store it in the app setup menu, `appsettings.Local.json`, user secrets, or environment variables. Do not paste the token into public issues, screenshots, docs, commits, or demo videos.

## Set Description

The description appears when a user first opens the bot conversation.

Send this to BotFather:

```text
/setdescription
```

Choose your bot, then paste:

```text
Talk to a local Codex CLI session from Telegram. This bot runs on the operator's machine, uses an explicit allowlist, and only works after local setup.
```

## Set About Text

The about text is a shorter profile summary.

Send this to BotFather:

```text
/setabouttext
```

Choose your bot, then paste:

```text
Private Telegram control surface for a local Codex CLI session.
```

## Set Commands

The command list appears in Telegram's command picker when users type `/`.

Send this to BotFather:

```text
/setcommands
```

Choose your bot, then paste:

```text
help - Show supported commands
whoami - Show Telegram user, chat, and topic IDs
doctor - Diagnose authorization, routing, project, session, and queue state
projects - List known local projects
project - Select, add, or show the current project
new - Create and select a Codex session
sessions - List active and managed sessions
use - Select an existing session
send - Send text to the active session
steer - Steer the active turn
model - Show or change model settings
thinking - Show or change thinking effort
tail - Show recent session output
status - Show session status
usage - Show Codex usage and reset windows
outbound - Show outbound Telegram queue status
stop - Stop the active or selected session
topic - Manage forum-topic sessions
topics - List topic/session bindings
restart - Show restart guidance
```

Command behavior:

1. Keep the command descriptions short; Telegram rejects invalid command definitions.
2. BotFather may take a few minutes to reflect command-list changes in every client.
3. `/kill`, `/rename`, and `/forget` are supported but intentionally omitted from the public command picker to keep the common menu simple. They remain documented in [command-reference.md](command-reference.md).

## Group Join Setting

For private-chat-only use, disable group joins:

```text
/setjoingroups
```

Choose your bot, then choose the option that prevents adding it to groups.

If you want group or forum-topic support, enable group joins and read [menus.md](menus.md) and [command-reference.md](command-reference.md) before relying on it.

## Privacy Mode

For private-chat-only use, privacy mode can stay enabled:

```text
/setprivacy
```

Choose your bot, then keep privacy enabled.

If you want ordinary group text to route to Codex, privacy mode may need to be disabled. That is an advanced mode. With privacy enabled, Telegram generally limits group updates to commands, mentions, replies, inline messages, and service messages.

## Optional Profile Media

Use a neutral avatar if you are recording or sharing setup:

```text
/setuserpic
```

Avoid screenshots or profile images that contain local paths, tokens, private repositories, customer names, or private transcripts.

## Token Rotation

If the token is exposed, rotate it immediately:

```text
/revoke
```

After rotation:

1. Update the token in Incursa Codex Telegram configuration.
2. Restart the bot process.
3. Send `/doctor` in the private chat.
4. Confirm the old token is no longer present in any tracked file, screenshot, transcript, or release note.
