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
- Added `/doctor` in-chat diagnostics for authorization, routing, project/session state, workspace roots, outbound queue status, and the next best action.
- Added group-root plain-text guidance so allowed users are not left with a silent no-op when they accidentally send outside a topic.
- Changed stale/deleted topic send failures to fail closed instead of retrying Codex output in the group root.
- Reduced model/thinking update latency by avoiding a redundant model-list lookup after settings changes.
- Preserved full multi-line content in batched Telegram output instead of reducing each queued update to its first line.
- Reworked the README as a product-facing setup and download guide, with developer workflow details moved to a dedicated development guide.
- Added BotFather, command-reference, and menu/button documentation for first-time public users.
- Aligned model-setting examples on the canonical `/model <model> thinking <effort>` form.
- Removed `---` separators from batched Telegram output so queued multi-line content reads as one continuous update.
- Removed the batched-output `/tail 100` footer and redundant successful `Turn completed.` text before the `~~ fin ~~` marker.
- Removed the batched-output update-count and session-ID header so grouped Telegram sends start directly with Codex content.
- Changed the `~~ fin ~~` turn marker to send as one standalone Telegram message after terminal turn content.
- Recovered from empty or unreadable selected Codex thread transcripts by clearing the stale Telegram session binding, starting a fresh session, and retrying the prompt once.
- Made model and thinking button callbacks edit the tapped menu immediately while settings are loading or updating.
- Added XML documentation and named limit/default constants across the Telegram outbound queue, bot options, Codex option contracts, DTO contracts, and small Telegram routing/value-object surfaces.
- Enabled XML documentation generation for the app project and tightened non-configuration app seams to internal visibility.

Known boundaries:

- Private chat is the primary supported operating mode.
- Group and forum-topic support require explicit chat allowlisting and Telegram permissions.
- A process restart rehydrates stored conversation/session bindings, but does not resume a mid-turn Codex execution.
- The app does not bundle Codex, Telegram credentials, OpenAI credentials, or `ffmpeg`.
