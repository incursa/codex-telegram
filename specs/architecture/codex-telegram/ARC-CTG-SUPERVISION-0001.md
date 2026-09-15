---
artifact_id: ARC-CTG-SUPERVISION-0001
artifact_type: architecture
title: Codex Telegram Supervision Workspace Authority and Recovery
domain: codex-telegram
status: draft
owner: codex-telegram-maintainers
satisfies:
  - REQ-CTG-SUP-0001
  - REQ-CTG-SUP-0002
  - REQ-CTG-SUP-0003
  - REQ-CTG-SUP-0004
  - REQ-CTG-SUP-0005
  - REQ-CTG-SUP-0006
related_artifacts:
  - SPEC-CTG-SUPERVISION
  - SPEC-CTG-TEST
---

# ARC-CTG-SUPERVISION-0001 - Codex Telegram Supervision Workspace Authority and Recovery

## Decision

Build the supervision workspace as a local control-plane projection around the existing Telegram state and Codex thread-manifest boundaries. Keep Telegram and Codex authoritative for their existing responsibilities. Introduce durable application-owned identity only through versioned, bounded records that reference Codex provenance; do not create a competing transcript or task database.

The first implementation slice persists Telegram `UpdateId` receipts in `telegram-state.json`. It protects the update boundary without treating a transport ID as the future application-owned `CommandId`.

## Boundaries

```text
Telegram update
  -> authenticated update boundary
  -> durable update receipt
  -> existing Telegram command/callback handler
  -> existing Codex session manager and gateway
  -> Codex thread/turn execution
```

The update receipt is a delivery and replay guard. The command handler remains responsible for authorization and routing. The gateway and Codex runtime remain responsible for creating and executing threads and turns. The Mini App and browser are projections only.

The target supervision identity is:

```text
Conversation -> Task -> Run -> Codex Thread/Turn
                         |       |
                         |       +-> Workspace -> Worker
                         +-> Command -> Approval -> Delivery
```

`TaskId`, `RunId`, `CommandId`, `ApprovalId`, `WorkspaceId`, `WorkerId`, and `LeaseId` are application identifiers. `CodexThreadId` and `CodexTurnId` are retained as execution provenance. The mapping rules for future Task and Run creation remain versioned contract work, not an inference from a thread list.

## First slice: durable update receipts

`TelegramBotStateStore` owns a bounded `UpdateReceipts` collection in its existing JSON state file. Each receipt contains a positive Telegram update ID, first-attempt timestamp, optional completion timestamp, and attempt count.

`TryBeginUpdateAsync` takes the store gate, loads the current state, prunes completed receipts older than seven days and abandoned in-flight leases older than fifteen minutes, then atomically inserts a new processing receipt. A completed receipt or a non-expired processing receipt rejects the replay. An expired processing receipt is reclaimed with an incremented attempt count.

`CompleteUpdateAsync` marks a claimed receipt complete only after the existing update handler finishes successfully. A handler exception calls `AbandonUpdateAsync`, removing the processing receipt so the update can be retried. If the process terminates before either operation, the lease expiry permits restart recovery.

The receipt list is capped at 2,000 entries. The state schema version is advanced to 2, while existing state files with no `SchemaVersion` or `UpdateReceipts` property deserialize with compatible defaults and continue to work. A malformed state file remains a startup/read failure; the host does not guess or overwrite operator state.

This provides at-most-once handling for completed or concurrently delivered update IDs within the retention/lease contract. It does not claim exactly-once behavior across an external Telegram or Codex side effect. Future `CommandId` records must add idempotency at the command boundary and must not rely solely on `UpdateId`.

## Lifecycle and recovery invariants

- Telegram authorization is evaluated before normal message or callback handling.
- Replayed or concurrently processing update IDs do not download attachments, acknowledge messages, invoke command handlers, or invoke callback handlers again.
- Handler failure leaves the update retryable; process termination leaves it reclaimable after the lease.
- Completed Telegram delivery or failed Telegram delivery is never used as proof of Codex execution.
- An ambiguous Codex execution is visible as recovery-required evidence and is never silently resent.
- `Needs attention` remains a derived display category, not a persisted lifecycle.
- Mini App and browser endpoints remain GET-only and read-only.

## Later dependency sequence

1. Define and trace Task/Run/Command/Approval/Claim/Recovery/Delivery contracts and migration tests.
2. Add application-owned Task and Run records beside existing state/manifests, with atomic transitions and explicit Codex provenance.
3. Add deterministic review packets and Telegram handoffs using those records.
4. Add task-owned worktrees, ports, database namespaces, worker registration, authenticated routing, leases, readiness, draining, and cleanup.
5. Add the combined Mini App worker/attention projection and revocable Telegram-approved browser pairing as read-only surfaces.
6. Add immutable task recipes and staged, drain-aware, health-verified worker updates with rollback evidence.

Each step requires focused automated tests plus the repository release floor. Browser and Telegram-live checks remain separate evidence categories.

## Alternatives rejected

### Mark an update completed before invoking the handler

Rejected because a process crash could lose an update permanently and because it would make a receipt look like execution proof.

### Use only an in-memory concurrent dictionary

Rejected because process restart and multi-instance behavior would re-open duplicate delivery races.

### Treat Telegram UpdateId as CommandId

Rejected because a transport delivery may contain an update that is filtered, grouped, transformed, or later mapped to more than one application command. Application idempotency needs its own identifier and state transition.

### Add a new database immediately

Rejected because the current host is a local single-process service with an established atomic JSON state boundary. A second database would create a competing task/state authority before its ownership and migration contract is defined.

## Verification obligations

The first slice must prove concurrent claim exclusion, completed replay suppression across reload, stale lease reclamation, retention pruning, legacy-state compatibility, retry after handler failure, and propagation of the update ID into the inbound command context. Full roadmap releases must additionally prove authorization negatives, restart/crash reconciliation, review provenance/redaction, worker isolation, browser pairing revocation, and update rollback.
