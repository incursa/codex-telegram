# Testing

Automated release readiness is intentionally split into a normal gate and a deeper mutation gate.

## Normal Gate

Run this before public demos, release tags, and pushes that affect runtime behavior:

```powershell
.\scripts\Test-ReleaseReadiness.ps1 -Runtime win-x64
```

That gate builds, tests, publishes, formats, audits packages, and runs the checked-in Telegram fuzz corpus.

## Telegram Fuzz Corpus

Run this directly when changing Telegram command parsing, message chunking, attachment mapping, or emoji/Unicode handling:

```powershell
.\scripts\Test-TelegramFuzzCorpus.ps1 -Configuration Release
```

The seed corpus lives under `fuzz/corpus` and is exercised by `TelegramFuzzCorpusTests`.

## Mutation Gate

Run scoped mutation testing when changing Telegram routing, parser, chunker, attachment, or sender behavior:

```powershell
.\scripts\Test-TelegramMutation.ps1 -Configuration Release
```

This uses the repo-local `dotnet-stryker` tool and `src/Incursa.Codex.Telegram/stryker-config.json`. It is not part of the normal release gate because it is slower and best used as focused quality evidence after meaningful Telegram behavior changes.
