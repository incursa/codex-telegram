---
title: "BotFather Setup"
---

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

## Ownership And Manual Sync

The app owns command parsing, callback behavior, and the built-in `/help` response. Guided setup can apply the app-owned command list and Telegram menu button through the Bot API, one time; it does not run a background sync. BotFather is the manual owner for description, about text, group-join setting, privacy mode, and any command/profile operation that setup skips or cannot apply. Changes in `docs/` remain recommendations that an operator must review and apply where needed.

After an explicit workspace-mode change, open the interactive menu's **Telegram and admins** section and choose **Reapply command menu and optional profile setup**. This reuses the same explicit confirmation and reports each Bot API operation separately; it does not run during ordinary startup.

After changing the command list, verify it in Telegram. If the picker has not refreshed, use `/help` and the exact forms in [command-reference.md](command-reference.md); a stale picker is a discoverability issue, not evidence that a command is unsupported.

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
home - Show the bot home and active session
whoami - Show Telegram user, chat, and topic IDs
trust - Trust the current group or forum chat
doctor - Diagnose authorization, routing, project, session, and queue state
projects - List known local projects
project - Select, add, or show the current project
new - Create and select a Codex session
resume - Resume a Codex session
sessions - List active and managed sessions
use - Select an existing session
send - Send text to the active session
steer - Steer the active turn
queue - View, edit, send, or delete queued prompts
status - Show session status and compact usage
stop - Stop the active or selected session
topic - Manage forum-topic sessions
```

For a repository-mode bot, replace `projects` and `project` with:

```text
repo - Show repository status and guidance
```

Advanced commands such as `/model`, `/thinking`, `/goal`, `/tail`, `/usage`,
`/debug`, `/handoff`, `/outbound`, `/output`, `/task`, `/topics`, and `/restart` remain supported as slash
commands even when they are omitted from the compact picker.

`/output mode balanced` is the middle-ground presentation choice for concise
milestones and sparse still-working pulses. It can be used directly even when
the picker is stale; `TelegramOutput:TextFormat=SafeMarkdownV2` is an optional
local setting for constrained formatting and does not change BotFather profile
ownership.

Command behavior:

1. Keep the command descriptions short; Telegram rejects invalid command definitions.
2. BotFather may take a few minutes to reflect command-list changes in every client.
3. Telegram's command picker lists top-level commands only, so `/queue edit`, `/queue delete`, and `/queue send` stay under the `/queue` entry.
4. `/kill`, `/rename`, and `/forget` are supported but intentionally omitted from the public command picker to keep the common menu simple. They remain documented in [command-reference.md](command-reference.md).

## Group Join Setting

For private-chat-only use, disable group joins:

```text
/setjoingroups
```

Choose your bot, then choose the option that prevents adding it to groups.

If you want group or forum-topic support, enable group joins and read [menus.md](menus.md) and [command-reference.md](command-reference.md) before relying on it.

The group setting only controls whether the bot may be added. It does not authorize messages. Runtime authorization still requires an allowlisted user and, for groups/forums, a trusted or config-allowlisted chat. Forum topics additionally require a forum-enabled supergroup and the relevant bot permissions.

## Privacy Mode

For private-chat-only use, privacy mode can stay enabled:

```text
/setprivacy
```

Choose your bot, then keep privacy enabled.

If you want ordinary group text to route to Codex, privacy mode may need to be disabled. That is an advanced mode. With privacy enabled, Telegram generally limits group updates to commands, mentions, replies, inline messages, and service messages.

When privacy mode is enabled, test the intended group workflow with a command, mention, or reply before concluding that the bot is offline. `/send <text>` is the explicit manual dispatch fallback.

## Optional Profile Media

The guided setup can optionally apply an operator-approved profile photo through `setMyProfilePhoto`. Published builds include the project-owned `Assets/codex-telegram-logo.png`; the wizard offers it when present. You can instead provide a custom image or skip the step. If the installed Telegram client cannot apply the photo, use `/setuserpic` below and upload that file manually.

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
