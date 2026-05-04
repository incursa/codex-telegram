# Changelog

## Unreleased

- Added a standalone bootstrap/admin menu for local configuration.
- Added live Codex model discovery for setup, with curated fallback examples.
- Added durable local project, Telegram state, and thread-manifest storage under `CodexTelegram:Workspace:DataRoot`.
- Added startup rehydration for Telegram conversation-to-session follow state.
- Simplified Telegram inline buttons so single-session replies no longer show noisy numbered controls.
- Tightened group and forum authorization so messages require both an allowed user and an allowed chat.
- Added manual release validation docs for private chat, authorization, groups, forum topics, voice, attachments, queueing, and restart behavior.
- Added tests for button labels, authorization, outbound queue behavior, local settings, state persistence, and OpenAI transcription error boundaries.
- Added checked-in Telegram fuzz corpus coverage for command-like text, emoji intent, Unicode boundaries, formatting-like input, chunking, and attachment mapping.
- Added a scoped Telegram mutation-testing script and Stryker configuration for parser, chunker, attachment, sender, and topic-scope seams.
- Added group-root plain-text guidance so allowed users are not left with a silent no-op when they accidentally send outside a topic.
- Changed stale/deleted topic send failures to fail closed instead of retrying Codex output in the group root.
- Reduced model/thinking update latency by avoiding a redundant model-list lookup after settings changes.

Known boundaries:

- Private chat is the primary supported operating mode.
- Group and forum-topic support require explicit chat allowlisting and Telegram permissions.
- A process restart rehydrates stored conversation/session bindings, but does not resume a mid-turn Codex execution.
- The app does not bundle Codex, Telegram credentials, OpenAI credentials, or `ffmpeg`.
