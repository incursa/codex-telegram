# Release Owner Actions

This file tracks work that needs the repository owner, a live Telegram account, private credentials, or public-release decisions. Agent-owned repo changes should not require any item in this file unless the item is explicitly handed back with results.

## Required Before Public Demo

1. Choose the demo mode.
   - Minimum credible demo: private chat only.
   - Extended demo: private chat plus one allowed group.
   - Advanced demo: private chat plus forum topics.

2. Provide or confirm the live Telegram bot setup.
   - BotFather bot username.
   - BotFather display name.
   - BotFather description/about text.
   - Whether the bot should allow group joins.
   - Whether privacy mode should stay enabled.
   - Whether forum-topic management should be shown publicly.

3. Run the private-chat section of `docs/manual-test-plan.md`.
   - Record the date.
   - Record the commit SHA or published asset.
   - Record the OS.
   - Record the Codex CLI version.
   - Record whether voice transcription was enabled.

4. Confirm the configured workspace boundary.
   - Workspace roots should be narrow enough for the demo.
   - Demo projects should not expose unrelated local paths.
   - Demo repositories should not contain secrets or private customer data.

5. Confirm the public support boundary.
   - Whether voice transcription is supported in the first public release.
   - Whether groups are supported in the first public release.
   - Whether forum topics are supported in the first public release.
   - Whether macOS and Linux artifacts are release claims or best-effort artifacts.

6. Confirm the public security contact.
   - Email, private disclosure process, or GitHub Security Advisory flow.
   - Whether `SECURITY.md` should continue to say "report privately to the repository owner."
   - Expected response posture for early public issues.

## Required Before Repository Visibility Change

1. Confirm GitHub repository metadata.
   - Repository description.
   - Repository topics.
   - Homepage URL, if any.
   - Whether issues should be enabled.
   - Whether discussions should be enabled.

2. Run a final tracked-file secret scan.
   - Confirm no bot token is tracked.
   - Confirm no OpenAI API key is tracked.
   - Confirm no personal `appsettings.Local.json` is tracked.
   - Confirm no demo transcript exposes private data.

3. Decide whether to include screenshots.
   - Bootstrap menu screenshot.
   - Private chat `/new` screenshot.
   - Private chat live response screenshot.
   - Optional `/model` or `/thinking` screenshot.
   - Optional group/forum screenshot only if those modes are public claims.

4. Decide public-facing wording for risk and safety.
   - This bot controls a local Codex installation.
   - Telegram allowlists are the access boundary.
   - Operators must keep workspace roots narrow.
   - Operators must review sandbox and approval settings.

5. Capture live GitHub workflow evidence.
   - Run or confirm the latest `CI` workflow on the release branch.
   - Run or confirm the latest `Publish` workflow on the release branch.
   - Record the workflow run URLs in the results log.
   - Do not rely only on local validation before changing visibility.

6. Decide first public versioning.
   - First tag name.
   - Whether the release should be a prerelease.
   - Whether release notes should be generated from GitHub or written manually.

## Optional Owner Tasks

1. Create a short demo script.
   - Start bot.
   - Show `/whoami`.
   - Show `/projects`.
   - Show `/doctor`.
   - Create a session with `/new`.
   - Send one practical prompt.
   - Show `/tail` or `/status`.

2. Prepare release notes context.
   - Why the project exists.
   - What is intentionally not included.
   - Known limitations.
   - Recommended first setup path.

3. Decide branding assets.
   - Bot avatar.
   - README screenshot style.
   - Release title.

## Results Log

Use this section to record owner-run evidence.

| Date | Owner | Commit or asset | Scope | Result | Notes |
| --- | --- | --- | --- | --- | --- |
| | | | | | |
