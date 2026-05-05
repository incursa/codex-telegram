# Contributing

This repository is a standalone Telegram bot host for a local Codex installation. Contributions should keep that scope clear: a console app, local state, explicit Telegram allowlists, and operator-owned Codex credentials.

## Before You Start

1. Read [README.md](README.md) for the current support boundary.
2. Read [docs/getting-started.md](docs/getting-started.md) if you need to run the bot locally.
3. Read [docs/usage.md](docs/usage.md) before changing command behavior, output batching, queueing, attachments, voice, or group/forum flows.
4. Read [docs/development.md](docs/development.md) for source build, test, publish, and GitHub Actions workflow.
5. Check [docs/testing.md](docs/testing.md) for the relevant automated gate.

## Local Setup

Required tools:

1. .NET 10 SDK, matching [global.json](global.json).
2. A local `codex` executable if you want to run the bot end to end.
3. A Telegram bot token and numeric Telegram user ID for live manual checks.
4. `OPENAI_API_KEY` and `ffmpeg` only if you are testing voice transcription.

Normal source workflow:

```powershell
dotnet restore CodexTelegram.slnx
dotnet build CodexTelegram.slnx
dotnet test CodexTelegram.slnx
```

Run from source:

```powershell
dotnet run --project src\Incursa.Codex.Telegram
dotnet run --project src\Incursa.Codex.Telegram -- --run
```

Keep real local settings in `appsettings.Local.json`, user secrets, or environment variables. Do not commit bot tokens, OpenAI keys, personal workspace paths, private transcripts, or screenshots that expose private data.

## Validation

Run the smallest relevant validation while developing, then run the release gate before handing off a release candidate.

Fast local checks:

```powershell
dotnet build CodexTelegram.slnx
dotnet test CodexTelegram.slnx
dotnet format CodexTelegram.slnx --verify-no-changes --no-restore
.\scripts\Test-TrackedSecretScan.ps1
```

Release-readiness gate:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

Telegram fuzz corpus:

```powershell
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release
```

Mutation profiles are advisory but important for behavior-sensitive changes:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Profile core
.\scripts\Test-TelegramMutation.ps1 -Profile handler
.\scripts\Test-TelegramMutation.ps1 -Profile queue
```

Use the profile that matches the changed surface. Queueing, outbound Telegram delivery, live output relay, or scheduler changes should run the `queue` profile when time permits.

## Pull Request Expectations

Good pull requests include:

1. A focused change with a clear support-boundary impact.
2. Tests or explicit evidence for behavior changes.
3. Documentation updates when setup, commands, security posture, release workflow, or day-to-day usage changes.
4. Manual Telegram evidence when claiming real bot behavior that automated tests cannot prove.
5. No unrelated formatting churn.
6. A clean tracked-file secret scan before sharing public-release branches.

For public-facing behavior, avoid claims that are broader than the evidence. If a flow has only been validated in private chat, describe it as private-chat evidence until group, forum, voice, attachment, or platform checks have been run.

## Manual Telegram Evidence

Live Telegram checks require a human operator because they use private credentials and a real bot account. Use:

1. [docs/manual-test-plan.md](docs/manual-test-plan.md) for the checklist.
2. [docs/release-owner-actions.md](docs/release-owner-actions.md) for owner-run evidence and public-release decisions.
3. [docs/demo-readiness.md](docs/demo-readiness.md) for go/no-go status before a demo, tag, or visibility change.

Record the date, commit or asset, OS, Codex CLI version, BotFather privacy-mode setting, and any skipped checks.

## Security Hygiene

Treat these as sensitive:

1. Telegram bot tokens.
2. OpenAI API keys.
3. Codex local auth state.
4. Private repository paths and transcripts.
5. Screenshots from non-demo repositories.

If you find a security issue, follow [SECURITY.md](SECURITY.md). Do not open a public issue that includes secrets, exploit details, private transcripts, or local credential paths.
