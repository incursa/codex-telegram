---
title: "UX Assessment"
---

# UX Assessment

This is an initial, evidence-labeled assessment of the operator experience. It records what the repository can currently support and what still needs a live user check. It is not a claim that the Telegram UI has been validated with a production bot.

## Evidence Labels

- `automated`: repository build, unit/integration tests, format checks, fuzz checks, or static inspection.
- `synthetic`: a scripted Telegram/Codex test double or fixture. Useful for deterministic behavior, but not proof of a real external service.
- `local-live`: a real local process connected to a real Codex installation or bot account under controlled credentials.
- `Telegram-live`: a real Telegram chat, BotFather configuration, and observed messages in the intended private/group/forum scope.

## Initial Findings

| Finding | Evidence | User impact |
| --- | --- | --- |
| The first-run path can validate a BotFather token, capture an admin ID from a setup-code message, and fall back to manual ID entry. | `automated` source/test inspection; `Telegram-live` still required for the end-to-end path. | A failed capture should be recoverable without abandoning setup. |
| `GeneralPurpose` and `Repository` are distinct workspace contracts. | `automated` configuration/source inspection. | Operators need to choose scope before exposing a bot to a group or shared workflow. |
| Private chat is simpler than a group root; forum topics add trust, privacy, and permission requirements. | `automated` routing/authorization tests and docs inspection; live Telegram scope remains open. | Silent group behavior is easy to misread as a Codex failure. |
| Guided setup can apply the app-owned command list and menu button through the Bot API once, but there is no background synchronization; profile/privacy/group settings still need BotFather/manual handling. | `automated` source inspection. | The operator needs visible per-operation outcomes and a slash-command fallback. |
| Codex authentication belongs to the local Codex installation, not this app. | `automated` source/config boundary inspection. | Two instances must not accidentally share credentials or state. |
| `Balanced` presentation separates durable lifecycle/tool milestones from routine progress pulses, while `PlainText` remains the compatibility default and `SafeMarkdownV2` is opt-in. | `automated` source inspection and `synthetic` formatter/relay test coverage; no Telegram-live rendering. | Operators get a lower-noise middle mode and an explicit formatting choice without silently changing legacy text. |
| Build, test, fuzz, and mutation results are not equivalent to live Telegram proof. | `automated` test and release-script inspection. | Release notes must name the evidence type and any skipped live checks. |

## Finding Register

| Journey and reproducible friction | Evidence | Proposed improvement | Priority | Compatibility impact | Success condition | State |
| --- | --- | --- | --- | --- | --- | --- |
| First launch: operators had to assemble mode, paths, token, allowlist, and Telegram profile steps from separate guidance. Reproduce with no local settings file and an interactive terminal. | `automated` source review and bootstrap tests; no `Telegram-live` walkthrough. | Extend the existing wizard with explicit mode/storage validation, token identity, expiring pairing, readiness checks, and optional profile/menu setup. | P0 | Additive settings and existing `--run`/`--menu` behavior remain intact. | A new operator can save a verified token, admin ID, mode, and data root and see which checks remain unverified. | Implemented; live walkthrough follow-up. |
| Dedicated bot: project controls were still visible and a no-session prompt could depend on project selection. Reproduce in repository mode by opening `/home`, `/projects`, and sending ordinary text. | `automated` handler tests and source review. | Bind new/resumed sessions to the validated root, hide project controls, and auto-create a session for ordinary private text. | P0 | General-purpose project registration and selection are unchanged. | Repository mode creates at the configured root and rejects a session/callback from another root. | Implemented. |
| Input while work runs: queue edits, bundle cards, and callbacks could identify a user but not the originating conversation/topic. Reproduce with two topics for one user and reuse an old button. | `automated` state/input/plan tests. | Require conversation scope and expiry/replacement checks for queue and bundle mutations. | P0 | Existing persisted records retain their recorded conversation identity. | An old or cross-topic action is rejected without changing the other topic’s queue/bundle. | Implemented. |
| Telegram setup: command/profile changes had no per-operation ownership or repeatable repair path. Reproduce with a fake client that fails one Bot API operation. | `synthetic` profile-setup tests and source review; no remote API call. | Use one app-owned catalog, explicit opt-in synchronization, independent results, redaction, and BotFather fallbacks. | P1 | Custom settings are untouched unless the operator approves an app-managed operation. | Reapplying reports success/skip/failure per operation and never claims a failed change succeeded. | Implemented; live Bot API check follow-up. |
| Multiple instances: mutable state and polling conflicts were easy to create by copying the same build. Reproduce with two local processes and the same data root/token. | `automated` configuration/state tests and source review; no two-bot live run. | Add explicit `InstanceId`/data-root guidance and a local token receiver lock with actionable diagnostics. | P1 | Legacy defaults remain when no instance selector is supplied. | Distinct roots do not share state; a duplicate local poller fails clearly. | Implemented; two-process live check follow-up. |
| Daily mobile use: users could not easily tell whether a message was bundled, queued, active, or complete without verbose command output. Reproduce by sending text/voice/attachments and inspecting cards/status. | `automated` existing queue/bundle/output tests and source review; no phone walkthrough. | Keep compact presentation, add mode-aware home/repository summary, and preserve contextual controls with stale-action rejection. | P1 | Existing output modes and voice/attachment behavior remain supported. | A phone walkthrough can distinguish capture, queue, active turn, result, failure, approval, and recovery states. | Partly implemented; phone walkthrough and presentation polish remain follow-up. |

## Delivered Improvements

The current documentation slice delivers:

1. Explicit `CodexTelegram:Mode` guidance for `GeneralPurpose` and `Repository`, including `RepositoryRoot`, `RepositoryDisplayLabel`, and `InstanceId`.
2. Guided-setup outcome guidance for validated setup, manual fallback, save-after-warning, cancellation, and write failure.
3. Two-instance examples with separate BotFather tokens, executable folders, and local `DataRoot` values.
4. Configuration precedence and direct environment-variable fallback rules.
5. Clear ownership boundaries between app commands/`/help`, one-time Bot API command/menu setup, BotFather profile/privacy/group settings, and manual slash-command fallbacks.
6. An explicit Telegram repair action in the normal menu for reapplying mode-aware commands after an administrative mode change.
7. Private-chat, trusted-group-root, and forum-topic requirements with `/send` and topic commands as operational fallbacks.
8. Codex authentication isolation guidance that keeps credentials out of app state and repositories.
9. Honest verification language distinguishing automated, synthetic, local-live, and Telegram-live evidence.
10. `Balanced` output guidance for milestone durability, routine still-working pulses, aliases, and runtime reset behavior.
11. Independent `TelegramOutput:TextFormat` guidance for `PlainText`, constrained `SafeMarkdownV2`, escaping, unsupported transports, and unsafe chunk-boundary fallback.

## Synthetic Before/After Examples

### Example: setup outcome

Before (ambiguous):

```text
Setup complete. Start the bot.
```

After (evidence-aware):

```text
First-time setup is saved. The token was validated and the admin ID was captured from a private setup-code message.
Next: review the workspace mode and repository boundary, then run /doctor in the private chat.
```

The after text reports what the wizard observed. It does not imply that a normal Codex turn or a group workflow has been tested.

### Example: output presentation and formatting

Proposed before (default compatibility path):

```text
PresentationMode=Compact
TextFormat=PlainText
Every routine update is either durable or omitted from the operator's immediate view.
```

Proposed after (explicit middle mode):

```text
PresentationMode=Balanced
TextFormat=SafeMarkdownV2
Milestones and final output are durable; routine work uses sparse "Still working" pulses.
```

Observed synthetic before/after (formatter test double, not Telegram-live):

```text
Before input under PlainText: **bold** [docs](https://example.test)
After output under SafeMarkdownV2: *bold* [docs](https://example.test)
```

The observed result is local formatter behavior only. It does not establish that Telegram rendered or received the message. A malformed link, unsupported markup, unsupported transport, or unsafe formatted chunk boundary is expected to remain literal or fall back to plain-text chunks.

## Verification in this pass

Observed locally (`automated`): `dotnet build CodexTelegram.slnx -c Release -m:1 --no-restore` succeeded with zero warnings/errors; `dotnet test CodexTelegram.slnx -c Release --no-restore -m:1` passed 515 tests; the repository fuzz corpus passed 5 tests; `dotnet format --verify-no-changes` passed; the package vulnerability query found no vulnerable packages; and the tracked-file secret scan passed. A win-x64 publish was inspected and contained `Assets/codex-telegram-logo.png`. `Telegram-live`, phone, BotFather, and real Codex authentication checks were not run.

### Example: group troubleshooting

Before (misleading):

```text
The bot is offline because the group message was ignored.
```

After (actionable):

```text
No group update was observed. Check the allowlisted user, trusted chat, and BotFather privacy mode. Try a command, mention, reply, or /send <text>; then inspect /doctor and /outbound.
```

### Example: verification report

Before (overclaiming):

```text
All tests pass, so Telegram and Codex authentication are ready.
```

After (bounded):

```text
automated: build, tests, fuzz, and format checks passed.
synthetic: scripted Codex routing checks passed.
Telegram-live: not run; real bot, BotFather settings, and Codex account authentication remain unverified.
```

## Follow-Up User Stories

### Title: Validate the guided setup path with a real private bot

Description: As an operator, I want to complete first-run setup against a disposable BotFather bot so that token validation, setup-code capture, manual fallback, and saved settings are observed end to end.

Acceptance criteria:

- A real private chat captures the setup code and saves the expected numeric user ID.
- An expired or skipped capture reaches the manual numeric-ID fallback.
- Invalid-token and save-after-warning outcomes are visible and do not claim validation.
- The resulting local settings file contains no plaintext value in the assessment artifact or published logs.

### Title: Validate general-purpose and repository mode boundaries

Description: As an operator, I want to exercise both workspace modes with disposable repositories so that project browsing and repository pinning are understandable and fail closed when configuration is invalid.

Acceptance criteria:

- `GeneralPurpose` can list/select a project under an allowlisted workspace root.
- `Repository` requires a valid `RepositoryRoot` and does not select an unrelated path.
- The configured repository label appears where the operator needs to distinguish instances.
- Invalid or missing repository configuration reports an actionable error.

### Title: Validate two-instance state and credential isolation

Description: As an operator, I want two bot processes to run concurrently without cross-talk so that each instance has independent Telegram updates, local state, and Codex authentication context.

Acceptance criteria:

- Each process uses a distinct BotFather token and receives only its own test messages.
- Each process uses a distinct `DataRoot`; project/session bindings never cross instance boundaries.
- Codex authentication is verified under the intended OS account/context without copying auth files into the repository.
- Restarting one instance does not alter the other instance's state or delivery queue.

### Title: Validate command/profile ownership and stale-picker fallback

Description: As an operator, I want to understand which command/profile operations guided setup applies through the Bot API and which remain manual in BotFather, so that I can continue using slash commands when the picker is stale.

Acceptance criteria:

- `/help` and the command reference describe the actual runtime command behavior.
- Guided setup reports independent success, skip, and failure outcomes for its app-owned command/menu operations.
- BotFather's description and privacy/group settings are updated through the documented manual steps.
- A stale or unavailable picker does not block `/doctor`, `/send`, `/status`, and the other documented slash commands.

### Title: Complete private, group-root, and forum-topic live smoke coverage

Description: As a maintainer, I want evidence for each Telegram scope so that release readiness does not rely on private-chat behavior alone.

Acceptance criteria:

- Private-chat prompt, output, queue, and restart checks pass with a real bot.
- Trusted group-root checks pass for an allowlisted user and chat under the intended privacy mode.
- Forum-topic creation/attachment checks pass only in a forum-enabled supergroup with required permissions.
- The release record labels each result `Telegram-live` and names skipped scopes explicitly.

### Title: Validate Balanced presentation and safe text formatting

Description: As a mobile operator, I want a concise milestone view and predictable text formatting so that I can follow important work without losing final output or relying on Telegram-specific markup quirks.

Acceptance criteria:

- A synthetic relay check proves routine events produce sparse pulses while lifecycle/tool milestones, approvals, errors, artifacts, and final output remain durable.
- A synthetic formatter check proves `PlainText` preserves legacy literal text and `SafeMarkdownV2` handles only the documented headings, emphasis, links, lists, and code subset.
- Malformed/unsupported markup, unsafe links, unsupported formatted transports, and unsafe chunk boundaries are observed as literal text or plain-text fallback rather than a delivery error.
- A private-chat run is performed and recorded separately as `Telegram-live`; no local automated or synthetic result is labeled as live evidence.
