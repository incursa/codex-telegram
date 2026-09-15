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
  - REQ-CTG-SUP-0011
related_artifacts:
  - SPEC-CTG-SUPERVISION
  - SPEC-CTG-TEST
---

# ARC-CTG-SUPERVISION-0001 - Codex Telegram Supervision Workspace Authority and Recovery

## Decision

Build the supervision workspace as a local control-plane projection around the existing Telegram state and Codex thread-manifest boundaries. Keep Telegram and Codex authoritative for their existing responsibilities. Introduce durable application-owned identity only through versioned, bounded records that reference Codex provenance; do not create a competing transcript or task database.

The first implementation slice persists Telegram `UpdateId` receipts in `telegram-state.json`. The next slice adds a separate, bounded supervision ledger for application-owned `TaskId`, `RunId`, and `CommandId` records. It protects the command boundary without treating a transport ID as the application-owned `CommandId`. The following R1 slice adds independent delivery, approval/input, claim, recovery-action, and restart-reconciliation records to that same bounded projection.

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

This provides at-most-once handling for completed or concurrently delivered update IDs within the retention/lease contract. It does not claim exactly-once behavior across an external Telegram or Codex side effect. The current one-command-per-update adapter uses a namespaced digest to keep retry identity stable, but `CommandId` remains a separate application field and future transformed/grouped commands must retain their own identity.

## Second slice: task/run/command projection

`CodexSupervisionLedger` stores a separate bounded `codex-supervision-state.json` in the same operator-owned data root. It is a supervision projection, not a transcript or replacement for Codex history. A task is scoped by the Codex thread, Telegram conversation, and authorized user so the same Codex thread cannot accidentally expose one user's command history in another chat. A run belongs to exactly one task and command.

The current Telegram prompt path creates a stable command identity in a distinct `command:telegram:` namespace. For a positive Telegram update the value is derived from a namespaced digest of the update ID, so a handler retry retains the same application command identity while remaining distinct from the raw transport ID. A replacement-session recovery deliberately creates a child command identity rather than replaying an uncertain side effect under the original command.

Run state is projected as `Accepted`, `Queued`, `Running`, `WaitingForInput`, `ReadyForReview`, `Completed`, `Failed`, `Interrupted`, or `Unknown`. `Unknown` is intentionally non-terminal: it records that an external Codex outcome could not be confirmed and requires explicit reconciliation. `CodexThreadId` and `CodexTurnId` remain provenance fields and are never replaced by `TaskId`, `RunId`, or `CommandId`.

The ledger records bounded labels, IDs, timestamps, and outcome codes only. It does not persist prompt bodies, response bodies, attachment paths, credentials, or authorization headers. Terminal Codex events update the matching run by `CodexTurnId`; Telegram delivery is represented by an independent delivery record and is not inferred from run state. A delivery record spans all chunks of one queued item and reaches `Delivered` only after the final chunk is accepted. Approval/input decisions remain records at the existing Telegram-controlled Codex approval seam. Claims are lease-bound and fail closed for another owner. Recovery actions record requested/applied/unknown outcomes and replacement dispatch uses a child command identity.

On startup, accepted or in-flight runs are reconciled atomically to non-terminal `Unknown` with a restart outcome code. This records the process boundary without claiming Codex completion or replaying an uncertain side effect. The same distinction applies to Telegram delivery when a timeout leaves acceptance unknown.

## Lifecycle and recovery invariants

- Telegram authorization is evaluated before normal message or callback handling.
- Replayed or concurrently processing update IDs do not download attachments, acknowledge messages, invoke command handlers, or invoke callback handlers again.
- Handler failure leaves the update retryable; process termination leaves it reclaimable after the lease.
- Completed Telegram delivery or failed Telegram delivery is never used as proof of Codex execution.
- An ambiguous Codex execution is visible as recovery-required evidence and is never silently resent.
- `Needs attention` remains a derived display category, not a persisted lifecycle.
- Mini App and browser endpoints remain GET-only and read-only.

## R2.1 review packet and handoff

Review packets are deterministic, bounded projections of Codex-reported file changes and safe artifact metadata. Each packet retains the application Task/Run identity plus the Codex thread and last-turn identifiers. Absolute local paths are reduced to a safe leaf representation; binary and unsupported evidence is labeled rather than rendered as an actionable diff. Packet identity is derived from canonical bounded fields so refreshes are stable without persisting a second review database.

The Mini App renders the packet read-only and states that decisions remain in Telegram. `/handoff [sessionId]` emits the same bounded task/run/command/review identity through the already authorized Telegram conversation. It does not transfer a workspace, replay a command, approve a Codex action, or create a new side-effecting route.

## R3.1 task workspace allocation

CodexTaskWorkspaceManager owns a bounded codex-task-workspaces.json projection in the operator data root. Given an already-authorized application TaskId, an existing repository root, and an optional explicit base ref, it creates a generated codex/task/... branch and Git worktree under the configured task-worktree root. Git arguments are passed without a shell, the source root is normalized, and the generated path is the only path later eligible for release.

The same record allocates the first currently non-listening development port in the configured range and a safe database namespace. Port reservations are held for provisioned records and released records remain as historical evidence. Creation is idempotent for an active TaskId. Clean release uses git worktree remove; forced discard is a separate explicit operation and is never used automatically after an error.

## R3.2 explicit task workspace flow

The Telegram `/task new [name] [| baseRef]` command resolves the already-authorized active project, provisions a workspace, creates a new Codex session whose working directory is exactly the returned worktree, and registers the TaskId against the requesting user and conversation in the supervision ledger. `/task status` and release/discard resolve the task through that same user-plus-conversation scope. A live Codex session blocks release until the operator stops it; a successful release clears the active session selection so later prompts cannot target a removed worktree. No existing session is moved and no prompt is replayed implicitly.

## R4.1 browser pairing

When `TelegramMiniApp:BrowserPairingEnabled` is enabled, an anonymous browser may request a one-time high-entropy pairing challenge. The challenge displays a short-lived code and is approved only by `/pair <code>` from an allowlisted private Telegram chat. The state file stores hashes of the code and token, never their plaintext values. After approval, the browser presents the high-entropy token as a session header; the session is time-limited and `/pair revoke` records revocation for all sessions belonging to that Telegram user. Browser-authenticated requests use the same read-only Mini App projections as Telegram-authenticated requests; no browser mutation or approval endpoint is introduced.

## R3.3 local worker boundary

The host owns one local worker identity persisted in `codex-worker-state.json`. The identity is stable across restarts, carries only operator-selected or generated display metadata, and is separate from Telegram and Codex identifiers. Task workspace allocations may claim a bounded lease on this worker. Lease acquisition is serialized and idempotent by TaskId, removes expired leases before capacity checks, and fails closed while the worker is draining. Drain and resume are explicit private-chat Telegram operations; draining does not interrupt active Codex sessions. Readiness is projected from the local Codex runtime and exposes bounded capabilities, lease capacity, heartbeat, and non-sensitive issues to the Mini App.

This slice is intentionally single-host. It does not accept arbitrary worker registration, expose a public coordinator endpoint, share credentials between containers, or route a task across processes. Authenticated outbound coordinator connections and cross-worker routing require a later contract with worker identity binding, endpoint authentication, lease ownership, and isolation enforcement.

## R3.4 authenticated coordinator worker registry

An operator may enable a coordinator control-plane endpoint and configure workers to send bounded heartbeat snapshots over HTTP(S). The request uses an operator-managed bearer token, optional exact `AllowedWorkerIds`, and no Codex credentials. The coordinator persists only redacted worker capability, readiness, lease, version, and heartbeat metadata, caps registered workers, and projects a worker as unavailable after a bounded missed-heartbeat window. The worker's own local registry remains the authority for its leases and Codex execution; the coordinator cannot claim a task or interrupt a session merely by receiving a heartbeat.

This is an admission and observation boundary, not yet a full task-dispatch protocol. Cross-worker assignment still requires a later lease-handoff contract that binds a task, workspace, worker, and coordinator-issued command without making the coordinator a second Codex authority.

## R5.1 inspectable task recipes

Recipes are configuration-owned definitions with a stable `RecipeId` and `RecipeVersion`, bounded objective and expected-output text, optional Codex session instructions, and required worker capabilities. `/recipe` is read-only. Selecting a recipe during `/task new` passes only its session-policy fields to the new Codex thread and records the ID, version, and display name in the application-owned task record. The selected snapshot is immutable for that task; later configuration reloads do not rewrite its provenance. Recipe text does not authorize a user, approve a Codex action, or create a browser mutation path.

## R5.2 staged worker updates

When enabled, `CodexWorkerUpdateManager` accepts an operator-configured package path only after the local worker is explicitly draining and has zero active leases. It verifies the exact SHA-256 digest, the package assembly major/minor/build version, and configured worker capabilities before copying the package into an operator-owned staging root. It also captures the current executable as a last-known-good rollback artifact. The manager persists only bounded package names, target version, digest, timestamps, and outcome codes in `codex-worker-update-state.json`; it does not persist source paths, credentials, or transcripts.

Staging is not activation. The protected installation is never overwritten by the application, and the running process is not stopped from Telegram. An external service or fleet installer consumes the staged package, performs the stop/start and post-install version/health checks, and decides whether to complete the update or apply the explicitly staged rollback artifact. Until that acknowledgement exists, the application reports the package as staged rather than healthy or active. This preserves rollback and release provenance without weakening `ProtectSystem` or making the coordinator a second execution authority.

## R3.5 coordinator lease handoff

Workers may advertise an operator-configured private `ControlPlaneUrl` in their authenticated heartbeat. A coordinator request identifies a bounded `TaskId`, optional `WorkspaceId`, required capabilities, and optional exact worker. The handoff service chooses only a fresh remote worker that is online, ready, below lease capacity, has the requested capabilities, and exposes a control endpoint. It persists a five-minute `Issued` grant before sending it, and repeated requests for the same active task return the existing grant rather than creating another lease.

The worker-side endpoint requires the same exact bearer token, checks the grant lifetime and worker identity, re-reads local readiness/draining/capability state, and acquires the lease through `ICodexWorkerRegistry`. The coordinator records the worker's accepted lease ID or a bounded rejection outcome. A successful handoff is admission evidence only: workspace provisioning, Codex session creation, prompts, approvals, and execution remain separate worker-owned operations. The coordinator cannot stop or replay a Codex turn.

## R5.3 installer completion evidence

The external installer reports completion only through the authenticated `/api/worker/v1/update/complete` endpoint using a token separate from coordinator worker registration. The request contains the running release version, binary SHA-256, and a health flag. The manager compares those fields with the currently staged target or rollback artifact and re-reads local worker readiness. A positive result records `Active` or `RollbackActive`; a mismatch, unhealthy flag, or non-ready worker records `HealthFailed` without claiming activation. This endpoint is an acknowledgement after an actual restart, not a Telegram-controlled stop/start path.

## R3.6 remote task provisioning

`/task remote` is an explicit coordinator operation. The coordinator first obtains a bounded lease handoff for the requested worker and then sends authenticated provisioning metadata to `/api/worker/v1/tasks/provision`. The worker re-checks the grant's WorkerId and LeaseId, its local ready state, its configured Repository mode and exact repository root, and the recipe version before creating the task worktree and Codex session. The worker registers the task locally and the coordinator records only the returned WorkerId, LeaseId, WorkspaceId, Codex thread ID, branch, port, and database namespace. No private filesystem path, credential, prompt body, or transcript is transferred. The endpoint is idempotent by owner and TaskId.

## R3.7 remote prompt and event relay

For a selected remote task, `/send` and Plan mode use an authenticated coordinator-to-worker request at `/api/worker/v1/sessions/send`. The request contains only the bounded task/thread ownership identifiers, Telegram conversation scope, durable CommandId, text, Plan mode, and an exact coordinator callback URL. The worker validates its registered identity, ready state, task ownership, callback configuration, and supervision command receipt before invoking the local Codex session manager. Repeated CommandId values return the recorded execution result and do not start a second turn.

The worker's existing realtime event seam forwards bounded timeline entries for the registered thread to `/api/coordinator/v1/worker-events` using the same exact bearer-token boundary. The coordinator accepts events only from a registered remote worker, republishes them through the normal Telegram output relay, and updates the matching supervision run on terminal events. Callback registrations are intentionally in-memory and must be re-established by a new accepted send after a worker restart. Attachments, steering, cancellation, model/goal controls, and review/file actions are not implied by this slice.

## Later dependency sequence

1. Complete remote session/control relay as separately authorized operations, including steering, stop, attachments, model/goal controls, and task status/release where worker-owned.
2. Add combined Mini App task detail, attention, review-packet, artifact, and worker views backed by the coordinator's bounded projections.
3. Add fleet-wide staged rollout coordination, compatibility gates, and safe rollback finalization.

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
