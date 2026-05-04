# Telegram Fuzz Corpus

This directory contains a deterministic, checked-in corpus for Telegram-facing parsing and input mapping.

It is not a coverage-guided fuzzer. It is a fast corpus gate, run by `TelegramFuzzCorpusTests`, that feeds Telegram-like text and synthetic attachment descriptors through:

1. `TelegramCommandParser`.
2. `TelegramMessageChunker`.
3. `TelegramAttachmentInputBuilder`.

Run it with:

```powershell
pwsh -File scripts\Test-TelegramFuzzCorpus.ps1
```

The release-readiness script and GitHub workflows run this corpus so Telegram text and attachment edge cases stay part of normal validation.
