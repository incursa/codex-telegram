---
title: "Documentation Index"
---

# Documentation Index

This directory contains user, operator, and maintainer documentation for Incursa Codex Telegram.

This is the source-authored docs tree. The sync workflow mirrors it into `incursa-docs/src/content/docs/open-source/codex-telegram/` using `docs.site.json` and `.github/workflows/sync-docs.yml`. Edit the source files here; do not edit the mirrored copies in the central docs repo.

## User And Operator Guides

- [Getting started](getting-started.md): install, first-run setup, local settings, workspace roots, and first private-chat session.
- [Usage](usage.md): day-to-day Telegram behavior, commands, sessions, output delivery, groups, forum topics, voice, attachments, and local state.
- [Command reference](command-reference.md): full command syntax and expected behavior.
- [Menus and buttons](menus.md): inline button surfaces, session cards, project cards, output controls, queue controls, and live turn cards.
- [Operations](operations.md): start, stop, restart, local state, token rotation, group/forum operations, and health checks.
- [BotFather setup](botfather.md): bot creation, command registration, descriptions, and privacy-mode notes.
- [Manual test plan](manual-test-plan.md): live Telegram release validation checklist.

## Maintainer Guides

- [Maintainer readiness](maintainer-readiness.md): service boundaries, architecture, configuration, validation, release flow, deployment checks, troubleshooting, and known gaps.
- [Development](development.md): restore, build, test, publish, release-readiness gate, fuzzing, mutation testing, and documentation expectations.
- [Testing](testing.md): normal gate, fuzz corpus, mutation profiles, and quality notes.
- [Contributor agreement automation](contributor-agreement-automation.md): CLA workflow setup and maintenance.

## Root References

- [README](../README.md): public setup and download entry point.
- [Contributing](../CONTRIBUTING.md): contributor setup, validation, pull-request expectations, and security hygiene.
- [Security](../SECURITY.md): required controls and reporting instructions.
- [Changelog](../CHANGELOG.md): release history and current unreleased notes.
