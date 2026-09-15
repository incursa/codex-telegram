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

The current Mini App foundation is the first R2/R4-compatible evidence and decision slice:

- `Needs attention` is derived from existing Codex thread and active-turn state. It reports operator input requests, failures, unavailable sessions, and runtime failures with deterministic ordering.
- `Recent activity` and task detail use stable Codex thread identity. Detail includes bounded turns, timeline entries, Codex-reported file diffs, and redacted artifact metadata.
- Live refresh failures preserve the last confirmed workspace snapshot as stale. Synthetic preview data is available only with the explicit `?preview=1` query string.
- Opening task detail does not create a missing local manifest or state directory.
- The surface has one bounded Mini App mutation capability: an authenticated user may acknowledge an exact task/run/review packet, and may prepare (but not execute) the existing Telegram `/handoff <thread>` command. Prompts, steering, approvals, retries, cancellation, file edits, uploads, commits, merges, and deployment remain in Telegram or their existing operator authority. Codex command and file-change approvals fail closed when no explicit Telegram decision exists.

The v1.0.28 R1.0 slice is the update-boundary foundation. Its contract is recorded in [`SPEC-CTG-SUPERVISION`](../specs/requirements/codex-telegram/SPEC-CTG-SUPERVISION.json) and [`ARC-CTG-SUPERVISION-0001`](../specs/architecture/codex-telegram/ARC-CTG-SUPERVISION-0001.md). Telegram `UpdateId` receipts provide bounded replay protection at the transport boundary; this is not yet the application-owned `CommandId` or full Task/Run lifecycle. Handler failures remain retryable, stale in-flight receipts are reclaimable, and completed receipts are retained only for the documented window.

The v1.0.29 R1.1 slice adds an application-owned Task/Run/Command projection for prompt dispatch. It stores only bounded, redacted lifecycle metadata in `codex-supervision-state.json`, scopes tasks by Codex thread plus Telegram user/conversation, retains the raw Codex thread/turn IDs as provenance, and exposes the projection to the read-only Mini App. Queueing, direct dispatch, terminal Codex events, shutdown interruption, and explicit replacement-session recovery update the same run identity. An `Unknown` result remains open for reconciliation and is never treated as success or silently replayed.

The v1.0.30 R1.2 slice adds bounded delivery, approval/input, claim, and recovery-action records to the same supervision projection. Delivery state is tracked independently through multi-chunk Telegram sends. Approval/input decisions are recorded at the existing Telegram-controlled Codex approval seam; claims are lease-bound and fail closed for another owner; unreadable-thread replacement records requested/applied/unknown transitions; and startup reconciliation marks in-flight runs unknown. These records are evidence and projection only: the Mini App remains read-only, Telegram remains the control/approval authority, and no ambiguous Codex side effect is replayed automatically.

The v1.0.31 R2.1 slice adds deterministic review packets and the explicit `/handoff [sessionId]` Telegram projection. Review packets are bounded to Codex-reported changes and safe artifact metadata, sanitize absolute paths, label binary or unsupported evidence, and retain thread/turn/task/run provenance. The Mini App renders them as read-only evidence; `/handoff` carries the same bounded context into the authorized Telegram conversation without transferring a workspace, replaying a command, or changing authorization semantics.

The v1.0.32 R3.1 slice adds a task workspace allocator. It creates an isolated Git branch/worktree from an explicit repository and base ref, persists the task-to-worktree mapping, allocates a non-listening development port from a configured range, and assigns a bounded database namespace. Repeated creation for the same active task is idempotent; release is recorded only after Git removes the exact recorded worktree.

The v1.0.33 R3.2 slice binds that allocator to an explicit Telegram `/task` flow. Creation resolves the already-authorized project, creates a new Codex session in the returned worktree, and registers the TaskId against the requesting user and conversation. Status and release require the same ownership scope; release refuses an in-use session, and discard remains an explicit confirmation. Coordinator/worker routing remains a later R3 slice.

The v1.0.34 R4.1 slice adds opt-in browser pairing. A browser receives a high-entropy challenge and short-lived code, an allowlisted private Telegram command approves it, and the resulting browser session is read-only, time-limited, hash-persisted, and revocable with `/pair revoke`. Telegram initialization remains the preferred identity path; browser pairing does not add control or approval endpoints.

The v1.0.35 R3.3 slice adds the local worker boundary. Each host has a persisted worker identity, bounded task leases, Codex-backed readiness, a private-chat drain/resume control, and a read-only Workers card in the Mini App. Draining prevents new task claims and task creation releases its lease on cleanup or explicit workspace release; it does not interrupt existing Codex sessions. The registry is local and does not pretend to provide coordinator routing, cross-container task execution, or fleet updates.

The v1.0.35 R5.1 slice adds inspectable task recipes alongside the local worker boundary. Built-in or operator-configured recipes carry a stable ID/version, objective, optional Codex session policy, expected outputs, and required capabilities. `/recipe` exposes bounded definitions; `/task new ... | recipeId` applies the selected session policy and snapshots the recipe identity in the supervision record. Existing tasks are not rewritten when configuration changes.

The v1.0.36 R3.4 control-plane slice adds authenticated outbound worker heartbeats and a bounded coordinator worker registry. The coordinator admits only token-authenticated, optionally allowlisted worker IDs, persists redacted status, and projects stale heartbeats as unavailable. It is an observation and admission layer; workers retain their own Codex execution, state, credentials, and task isolation. A coordinator-issued task lease handoff is deliberately still separate.

The v1.0.37 R5.2 slice adds drain-aware worker update staging. An authorized private Telegram command verifies a package's exact digest, release version, and required capabilities while the local worker is drained and idle, then stages the package and a last-known-good executable under an operator-owned writable root. The protected installation is never overwritten by the application; an external service installer owns activation and post-install health. Rollback staging is explicit and persisted as bounded evidence.

The v1.0.38 R3.5 slice adds the coordinator lease-handoff protocol. Workers advertise a private control endpoint, the coordinator selects only fresh ready workers with matching capabilities and spare capacity, persists an idempotent five-minute grant, and authenticates the worker-side acceptance. The worker binds the grant to its own identity and local lease registry; no Codex credentials, prompts, or execution authority move to the coordinator.

The v1.0.39 R5.3 slice adds authenticated external-installer completion evidence. A staged update becomes active only after the restarted worker reports matching version/digest, ready worker state, and a positive health result. Failed verification is persisted as `HealthFailed`, and rollback remains an explicit drain-aware operation.

The v1.0.40 R3.6 slice uses the coordinator lease handoff for explicit remote task provisioning. `/task remote` chooses one registered worker, and the worker re-checks its identity, admitted repository root, recipe version, readiness, and local lease before creating its own worktree and Codex session. The coordinator retains only bounded task ownership/resource metadata; it does not receive private paths, credentials, prompts, or transcripts.

The v1.0.41 R3.7 slice adds authenticated Telegram text and Plan mode relay for a selected remote task. The coordinator sends a bounded command with the durable CommandId and an exact callback URL; the worker re-checks task ownership, local readiness, callback configuration, and command idempotency before invoking its local Codex session. Worker timeline events are forwarded to the coordinator over the authenticated callback and projected to Telegram, while terminal events reconcile the coordinator's supervision run. Attachments, remote steering/stopping, model/goal controls, and file/review actions remain separate capabilities.

The v1.0.42 R3.8 slice adds authenticated remote steering and stop/kill controls. These operations use a separate worker control endpoint, task ownership checks, durable CommandIds, and bounded completion/unknown/interrupted outcomes. A ready worker may accept stop/kill while draining, but draining still prevents new task claims. Attachments, model/goal controls, remote workspace lifecycle, and review/file actions remain separate capabilities.

The v1.0.43 R3.9 slice completes the currently supported remote task lifecycle. Remote status and confirmed release/discard now execute on the owning worker and return only bounded workspace evidence; local worktree paths never cross the coordinator boundary. The coordinator clears its local selection only after the worker reports a released workspace. Attachments, remote model/goal controls, and review/file actions remain separate capabilities.

The v1.0.44 R3.10 slice makes worker-owned task detail available through the Mini App. The worker reads the authoritative Codex thread and builds the existing bounded detail, review, and artifact projection locally; the coordinator relays only that redacted view after exact user, task, worker, lease, and thread checks. Remote detail does not expose worker paths or create coordinator-side manifests.

The v1.0.45 R3.11 slice completes the next remote execution boundary. Model/reasoning controls and goal lifecycle operations relay through an owner- and lease-bound worker contract. Normal remote prompts can carry bounded attachment content; the coordinator never sends a local path, and the worker persists the content before constructing Codex input items. Queued remote prompts use the same relay after the task becomes available. Plan mode with attachments remains explicitly rejected because it has a separate Codex execution contract.

The v1.0.46 R2.2 slice adds the first authenticated Mini App task actions. A user-scoped acknowledgement store records only the exact TaskId, RunId, PacketId, and timestamp; stale runs and cross-user tasks fail closed, and repeated acknowledgement is idempotent. The Mini App can also prepare a bounded `/handoff <thread>` command for the existing Telegram handoff flow. These actions do not execute Codex, grant approval, or bypass Telegram authorization. Bootstrap and detail projections show acknowledgement state, and a newer run makes attention visible again.

The v1.0.47 R3.12 slice adds explicitly confirmed worker drain/resume actions to the Mini App. Local workers apply the change through their existing serialized registry; admitted remote workers receive an authenticated worker-identity-bound control request through the coordinator. Stale or unavailable workers fail closed, and draining changes admission only—existing Codex sessions and leases are not interrupted. The UI returns to the Telegram path for prompts, approvals, and other execution controls.

The v1.0.48 R2.3 slice extends Mini App handoff preparation with bounded evidence from the same redacted review packet used by task detail. The response and UI include review status, changed-file metadata, artifact metadata, counts, and Codex turn provenance. If the runtime is unavailable, the command remains available but the response says explicitly that evidence could not be retrieved. No raw worker paths, file payloads, Telegram delivery, Codex execution, or approval semantics are added.

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

1. Add fleet-wide staged rollout coordination, compatibility gates, and safe rollback finalization.

Full IDE behavior, arbitrary terminal/file-manager access, autonomous merge/push/deploy, public multi-tenant hosting, and blind replay of side-effecting work are out of scope.
