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

The app now syncs its command list, description, short description, and conservative group-admin defaults automatically on startup, so the manual BotFather commands below are only needed if you want to override the defaults yourself.

## Set Description

The app now writes the description automatically on startup. Use this BotFather command only if you want to override the default text manually.

Send this to BotFather:

```text
/setdescription
```

Choose your bot, then paste:

```text
Talk to a local Codex installation from Telegram and route prompts to sessions.
```

## Set About Text

The app now writes the short profile summary automatically on startup. Use this BotFather command only if you want to override the default text manually.

Send this to BotFather:

```text
/setabouttext
```

Choose your bot, then paste:

```text
Control local Codex sessions from Telegram.
```

## Set Commands

The app now writes the command picker list automatically on startup. Use this BotFather command only if you want to override the default list manually.

Send this to BotFather:

```text
/setcommands
```

Choose your bot, then paste:

```text
help - show this help
whoami - show Telegram user, chat, and topic thread IDs
version - show the running Codex Telegram app version
trust - trust the current group or forum chat for allowlisted users
projects - list known project directories
project - add or select a project
topics - list Telegram topics/sessions in this chat
topic - manage forum-topic sessions
launchpad - arm or disarm root-chat launch mode for plain-text and audio launches
launch - create a detached git worktree-backed forum topic and session while launchpad is armed
sessions - show active and Telegram-managed sessions
new - create and select a Codex session in the active project
use - select the active session for this conversation
send - send text to the active session
steer - steer the active turn in the selected session
queue - view, edit, delete, or send queued prompts now
model - show or change the selected session model
thinking - change the selected session thinking effort
goal - show or change the selected session goal
tail - show recent output and keep following the session live
status - show session status
usage - show Codex account usage remaining and reset times
doctor - explain authorization, routing, active project/session, workspace roots, and queue state
outbound - show outbound Telegram queue status
stop - gracefully stop a session
restart - show restart guidance
```

Command behavior:

1. Keep the command descriptions short; Telegram rejects invalid command definitions.
2. BotFather may take a few minutes to reflect command-list changes in every client.
3. Telegram's command picker lists top-level commands only, so `/queue edit`, `/queue delete`, and `/queue send` stay under the `/queue` entry.
4. `/launchpad` and `/launch` are supported so the bot can spawn repeated forum-topic lanes from the group root without keeping launch mode on forever, and launchpad mode can also turn plain text or audio into a seeded launch while it is armed. Each launch provisions a detached git worktree-backed session lane.
5. `/kill`, `/rename`, and `/forget` are supported but intentionally omitted from the public command picker to keep the common menu simple. They remain documented in [command-reference.md](command-reference.md).

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
