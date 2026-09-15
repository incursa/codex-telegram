---
title: "Codex supervision workspace roadmap"
---

# Codex supervision workspace roadmap

The long-term product is a private, self-hosted workspace for supervising Codex across repositories. Telegram remains the conversational control and approval channel; Codex remains the execution authority. The Mini App is an authenticated inspection and decision surface, not a second task database or an alternate authorization path.

## Delivery contract

The roadmap is delivered in independently verifiable releases:

| Release | Scope | Completion gate |
| --- | --- | --- |
| R0 | Authority, identity, lifecycle, approval, recovery, isolation, and ownership contracts | Requirements and verification points are recorded before durable writes are introduced. |
| R1 | Durable task/run identity, private-chat approval interlock, claim/recovery state, delivery acknowledgements, and duplicate protection | Unknown, stale, replayed, cross-user, or cross-chat actions fail closed; restart and crash fixtures are unambiguous. |
| R2 | Read-only changes/review, bounded artifact metadata, review evidence, acknowledgement, and explicit Telegram handoffs | Every review item links to a task/run and preserves Codex provenance; consequential actions still require Telegram approval. |
| R3 | Managed worktrees, per-task ports and database namespaces, worker registration, coordinator routing, leases, readiness, draining, and isolation enforcement | Concurrent workers cannot cross repository, workspace, port, database, or identity boundaries. |
| R4 | Needs attention, task detail, changes, artifacts/previews, workers, and revocable browser pairing | The Mini App is deterministic, signed, user-scoped, redacted, responsive in all Telegram display modes, and read-only. |
| R5 | Versioned task recipes, compatibility checks, staged worker updates, rollback, and release evidence | Recipes are inspectable and immutable per run; updates are drain-aware, operator-controlled, and reversible. |

## Current release

The current Mini App foundation is the first R2/R4-compatible read-only slice:

- `Needs attention` is derived from existing Codex thread and active-turn state. It reports operator input requests, failures, unavailable sessions, and runtime failures with deterministic ordering.
- `Recent activity` and task detail use stable Codex thread identity. Detail includes bounded turns, timeline entries, Codex-reported file diffs, and redacted artifact metadata.
- Live refresh failures preserve the last confirmed workspace snapshot as stale. Synthetic preview data is available only with the explicit `?preview=1` query string.
- Opening task detail does not create a missing local manifest or state directory.
- The surface has no Mini App mutation endpoints. Prompts, steering, approvals, retries, cancellation, file edits, uploads, commits, merges, and deployment remain in Telegram or their existing operator authority. Codex command and file-change approvals fail closed when no explicit Telegram decision exists.

The v1.0.28 R1.0 slice is the update-boundary foundation. Its contract is recorded in [`SPEC-CTG-SUPERVISION`](../specs/requirements/codex-telegram/SPEC-CTG-SUPERVISION.json) and [`ARC-CTG-SUPERVISION-0001`](../specs/architecture/codex-telegram/ARC-CTG-SUPERVISION-0001.md). Telegram `UpdateId` receipts provide bounded replay protection at the transport boundary; this is not yet the application-owned `CommandId` or full Task/Run lifecycle. Handler failures remain retryable, stale in-flight receipts are reclaimable, and completed receipts are retained only for the documented window.

The next R1 slice is application-owned Task/Run/Command identity and persisted approval, claim, recovery, and delivery state.

## Identity and state vocabulary

Until R1 introduces durable application records, a Mini App task is a Codex thread and its turns are the run history. The implementation must not imply stronger guarantees than the underlying thread state provides.

The durable target model is:

```text
Conversation ── routes to ── Task ── owns ── Run ── executes in ── Workspace
                                      │                         │
                                      └── assigned to ── Worker ─┘
```

Telegram identity and chat scope are authorization inputs. Codex thread and turn identifiers are execution provenance. They are related but not interchangeable. Telegram delivery state is separate from execution state, so a failed notification cannot turn a completed run into a failed run.

Initial display states are `queued`, `running`, `waiting`, `failed`, `completed`, `interrupted`, `unavailable`, and `archived`. `Needs attention` is a derived display category, not an independent persisted lifecycle.

## Later slices

1. Add durable task/run records, command IDs, persisted acknowledgements, claim ownership, safe-denial approval records, expiry, restart reconciliation, and recovery evidence.
2. Add deterministic Codex file-change projections, bounded diffs, binary/rename/unsupported handling, safe artifact inventory, review packets, and explicit Telegram handoff messages.
3. Add task-owned worktrees or branches, cleanup ownership, assigned development ports, explicit database settings, worker registration, outbound authenticated coordinator connections, and drain-aware scheduling.
4. Add the combined worker/attention view and short-lived browser pairing approved through Telegram. Browser access remains read-only until a separate authorization contract exists.
5. Add inspectable recipes and controlled worker updates with capability checks, staged rollout, drain, health/version verification, rollback, and provenance evidence.

Full IDE behavior, arbitrary terminal/file-manager access, autonomous merge/push/deploy, public multi-tenant hosting, and blind replay of side-effecting work are out of scope.
