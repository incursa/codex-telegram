# Agent Instructions

This repository is a standalone Telegram bot host for a local Codex installation. Treat it as an operator-facing service, not as a library-only project.

## Start Here

1. Check `git status --short --branch` before reading or editing. Preserve unrelated dirty work.
2. Read `README.md`, `docs/getting-started.md`, `docs/usage.md`, `docs/operations.md`, and `docs/development.md` before changing runtime behavior or operator docs.
3. Read `docs/testing.md` and `scripts/Test-ReleaseReadiness.ps1` before claiming release readiness.
4. Read `SECURITY.md` before changing configuration, trace capture, attachment handling, transcription, or Telegram authorization.

## Runtime Boundaries

- The app runs as a local console process and uses Telegram long polling when `TelegramBot:Enabled` is true.
- The app controls a local Codex runtime. It does not bundle Codex or own Codex authentication.
- Telegram authorization is a primary access boundary. Keep user and chat allowlists narrow.
- Local state, traces, input bundles, and thread manifests are operator-owned data. Do not commit local state or captured private transcripts.

## Validation

Use local validation as proof. Do not use GitHub Actions as the only evidence for a local change.

Normal validation:

```powershell
dotnet restore CodexTelegram.slnx
dotnet build CodexTelegram.slnx -c Release -m:1 --no-restore
dotnet test tests\Incursa.Codex.Telegram.Tests\Incursa.Codex.Telegram.Tests.csproj -c Release --no-build --no-restore -m:1
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release -NoRestore -NoBuild
.\scripts\Test-TrackedSecretScan.ps1
dotnet format CodexTelegram.slnx --verify-no-changes --no-restore --include src\Incursa.Codex.Telegram tests\Incursa.Codex.Telegram.Tests
git diff --check
```

Release-readiness validation:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

Use `-SkipPublish` only when the publish output is intentionally out of scope, and report that limitation.

## Change Guidance

- Update docs when changing commands, menus, output modes, configuration defaults, authorization behavior, tracing, attachment handling, queueing, publishing, or release validation.
- Keep Telegram behavior tests close to the changed surface.
- Run mutation profiles from `docs/testing.md` when time permits for routing, parser, queue, sender, or output-relay changes.
- Do not expose bot tokens, OpenAI keys, local Codex auth state, private repository paths, debug traces, private transcripts, or screenshots from non-demo repositories.
