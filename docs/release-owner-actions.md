# Release Owner Actions

This file tracks work that needs the repository owner, a live Telegram account, private credentials, or public-release decisions. Agent-owned repo changes should not require any item in this file unless the item is explicitly handed back with results.

Use `docs/demo-readiness.md` as the go/no-go scorecard that ties these owner actions to automated and manual evidence.

## Current Repository Prep Status

Completed repo-side setup:

1. GitHub Issues are enabled; wiki and discussions are disabled for the first public release.
2. Repository description, homepage, and topics are populated.
3. Secret scanning and push protection are enabled.
4. Dependabot security updates are enabled.
5. Dependabot is configured for GitHub Actions and NuGet.
6. CodeQL is configured to run after the repository is public.
7. `main` is protected by a ruleset that requires pull requests, Code Owner review, current required status checks, review-thread resolution, and squash merge.
8. All tags are protected by a ruleset; only repository admins can intentionally bypass.
9. `Contributor Agreement` is a required status check on `main`.
10. Contributor agreement signatures are stored in the private `incursa/contributor-agreements` repository through the org secret `INCURSA_CONTRIBUTOR_AGREEMENTS_TOKEN`.
11. Release `v1.0.8` is the current public release target; the publish workflow produces Windows, Linux, and macOS arm64 binaries plus checksums and license assets.
12. Publish workflow is configured to emit GitHub artifact attestations for release binaries after the repository is public.

Known repo-side limitation:

1. GitHub private vulnerability reporting could not be enabled while the repository is private; retry after switching the repository to public visibility.

Remaining owner-owned work is the live Telegram evidence, sanitized demo assets, and the final visibility decision.

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
   - General OSS contact: `oss@incursa.com`.
   - Security contact: `security@incursa.com`.
   - Whether GitHub private vulnerability reporting is enabled before public visibility.
   - Expected response posture for early public issues.

## Required Before Repository Visibility Change

1. Confirm GitHub repository metadata.
   - Repository description: populated.
   - Repository topics: populated.
   - Homepage URL: latest release page.
   - Issues: enabled.
   - Discussions: disabled for the first public release.
   - Wiki: disabled.

2. Run a final tracked-file secret scan.
   - Run `scripts\Test-TrackedSecretScan.ps1`.
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
   - Confirm release binary artifact attestations are visible after the next tagged publish.
   - Do not rely only on local validation before changing visibility.

6. Confirm first public versioning.
   - Current public release target: `v1.0.8`.
   - Release type: full release, not prerelease.
   - Release notes: generated from GitHub for the first public release.

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
