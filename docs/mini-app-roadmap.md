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

The v1.0.29 R1.1 slice adds an application-owned Task/Run/Command projection for prompt dispatch. It stores only bounded, redacted lifecycle metadata in `codex-supervision-state.json`, scopes tasks by Codex thread plus Telegram user/conversation, retains the raw Codex thread/turn IDs as provenance, and exposes the projection to the read-only Mini App. Queueing, direct dispatch, terminal Codex events, shutdown interruption, and explicit replacement-session recovery update the same run identity. An `Unknown` result remains open for reconciliation and is never treated as success or silently replayed.

The v1.0.30 R1.2 slice adds bounded delivery, approval/input, claim, and recovery-action records to the same supervision projection. Delivery state is tracked independently through multi-chunk Telegram sends. Approval/input decisions are recorded at the existing Telegram-controlled Codex approval seam; claims are lease-bound and fail closed for another owner; unreadable-thread replacement records requested/applied/unknown transitions; and startup reconciliation marks in-flight runs unknown. These records are evidence and projection only: the Mini App remains read-only, Telegram remains the control/approval authority, and no ambiguous Codex side effect is replayed automatically.

The v1.0.31 R2.1 slice adds deterministic review packets and the explicit `/handoff [sessionId]` Telegram projection. Review packets are bounded to Codex-reported changes and safe artifact metadata, sanitize absolute paths, label binary or unsupported evidence, and retain thread/turn/task/run provenance. The Mini App renders them as read-only evidence; `/handoff` carries the same bounded context into the authorized Telegram conversation without transferring a workspace, replaying a command, or changing authorization semantics.

The v1.0.32 R3.1 slice adds a task workspace allocator. It creates an isolated Git branch/worktree from an explicit repository and base ref, persists the task-to-worktree mapping, allocates a non-listening development port from a configured range, and assigns a bounded database namespace. Repeated creation for the same active task is idempotent; release is recorded only after Git removes the exact recorded worktree.

The v1.0.33 R3.2 slice binds that allocator to an explicit Telegram `/task` flow. Creation resolves the already-authorized project, creates a new Codex session in the returned worktree, and registers the TaskId against the requesting user and conversation. Status and release require the same ownership scope; release refuses an in-use session, and discard remains an explicit confirmation. Coordinator/worker routing remains a later R3 slice.

The v1.0.34 R4.1 slice adds opt-in browser pairing. A browser receives a high-entropy challenge and short-lived code, an allowlisted private Telegram command approves it, and the resulting browser session is read-only, time-limited, hash-persisted, and revocable with `/pair revoke`. Telegram initialization remains the preferred identity path; browser pairing does not add control or approval endpoints.

The v1.0.35 R3.3 slice adds the local worker boundary. Each host has a persisted worker identity, bounded task leases, Codex-backed readiness, a private-chat drain/resume control, and a read-only Workers card in the Mini App. Draining prevents new claims but does not interrupt existing Codex sessions. The registry is deliberately local and does not pretend to provide coordinator routing, cross-container authentication, or fleet updates.

The v1.0.35 R5.1 slice adds inspectable task recipes alongside the local worker boundary. Built-in or operator-configured recipes carry a stable ID/version, objective, optional Codex session policy, expected outputs, and required capabilities. `/recipe` exposes bounded definitions; `/task new ... | recipeId` applies the selected session policy and snapshots the recipe identity in the supervision record. Existing tasks are not rewritten when configuration changes.

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

1. Add outbound authenticated coordinator connections, multi-worker routing, lease handoff, and isolation enforcement around the task workspace boundary.
2. Expand the combined worker/attention view with cross-worker task ownership and recovery state. Browser access remains read-only until a separate authorization contract exists.
3. Add inspectable recipes and controlled worker updates with capability checks, staged rollout, drain, health/version verification, rollback, and provenance evidence.

Full IDE behavior, arbitrary terminal/file-manager access, autonomous merge/push/deploy, public multi-tenant hosting, and blind replay of side-effecting work are out of scope.
