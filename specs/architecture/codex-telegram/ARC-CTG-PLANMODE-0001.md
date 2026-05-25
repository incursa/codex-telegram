---
artifact_id: ARC-CTG-PLANMODE-0001
artifact_type: architecture
title: Telegram Plan Mode Bridge and SDK Integration
domain: codex-telegram
status: draft
owner: codex-telegram-maintainers
satisfies:
  - REQ-CTG-PLAN-0001
  - REQ-CTG-PLAN-0002
  - REQ-CTG-PLAN-0003
related_artifacts:
  - SPEC-CTG-PLAN
  - SPEC-CTG-TEST
---

# ARC-CTG-PLANMODE-0001 - Telegram Plan Mode Bridge and SDK Integration

## Purpose

Describe how the Telegram host should bridge the SDK's typed plan-mode configuration into runtime client creation while preserving the existing Telegram `/plan` prompt workflow and keeping goal mode separate.

## Requirements Satisfied

- REQ-CTG-PLAN-0001
- REQ-CTG-PLAN-0002
- REQ-CTG-PLAN-0003

## Scope

This design covers the Telegram process config binding path, the runtime-client cloning path, and the user-facing command model that already exists around plan prompts and goal controls.

It does not redesign the existing `/plan` command, and it does not invent a new Telegram goal-mode surface.

It also does not add a separate Telegram Plan client, Plan interface hierarchy, or mode controller. The SDK plan-mode object is a configuration concern, not a new runtime personality.

## Context

Telegram already has a plan-prompt path that wraps operator input in [`CodexPlanModePrompt`](../../../src/Incursa.Codex.Telegram/Services/CodexPlanModePrompt.cs) and sets `PlanMode = true` on the queued prompt path in [`CodexSessionManager`](../../../src/Incursa.Codex.Telegram/Services/CodexSessionManager.cs).

The host also already clones [`CodexClientOptions`](../../../src/Incursa.Codex.Telegram/Services/CodexSessionRuntimeRegistry.cs) when it creates runtime slots. That makes the runtime registry the right seam for carrying any nested SDK plan-mode defaults forward without widening the public Telegram surface.

At the settings layer, the bootstrap model persists the Codex default model, thinking effort, plan-mode thinking effort, sandbox, approval mode, and network access in [`LocalSettingsSnapshot`](../../../src/Incursa.Codex.Telegram/Configuration/LocalSettingsSnapshot.cs) and [`LocalSettingsStore`](../../../src/Incursa.Codex.Telegram/Configuration/LocalSettingsStore.cs). That keeps the local UI aligned with the SDK config shape without inventing a separate plan-mode client.

## Design Summary

The Telegram host should treat SDK plan mode as a nested client option that is bound from the existing `Codex` configuration section and copied into every runtime slot.

The important boundary is:

- the Telegram `/plan` command still means "wrap this prompt in planning instructions and queue a plan turn";
- the SDK `CodexClientOptions.PlanMode` object means "apply plan-mode defaults to the runtime client profile";
- goal mode remains thread state, not a sibling configuration tree.

### Configuration Binding

[`Program.cs`](../../../src/Incursa.Codex.Telegram/Program.cs) already binds `CodexClientOptions` from the `Codex` configuration section. Once the SDK package exposes the nested plan-mode property locally, the existing binding path should be enough for process-level defaults as long as the config shape matches the SDK's object model.

That means the host should avoid a Telegram-specific parser or a separate plan-mode settings block unless the SDK itself adds additional plan-only fields that need user-facing management.

### Runtime Client Cloning

[`CodexSessionRuntimeRegistry`](../../../src/Incursa.Codex.Telegram/Services/CodexSessionRuntimeRegistry.cs) is the critical copy seam. It already creates a fresh `CodexClientOptions` instance for each runtime slot so the slot can attach its own approval handler without mutating the shared binder result.

The plan-mode bridge must preserve the nested options object when that clone happens. A shallow copy that leaves the nested object behind would silently drop the configured plan-mode defaults for dedicated slots or thread-bound slots.

The intended shape is:

- copy the scalar and reference options already in use
- preserve the nested plan-mode options object
- continue to replace only the approval handler with the Telegram-specific wrapper

### User-Facing Surface

[`TelegramSessionCardBehavior`](../../../src/Incursa.Codex.Telegram/Telegram/TelegramSessionCardBehavior.cs) already surfaces buttons for model, thinking, and goal-related control. This design does not require a new plan-mode button because the existing `/plan` workflow is already a Telegram prompt path, not a runtime mode selector.

[`InteractiveBootstrapMenu`](../../../src/Incursa.Codex.Telegram/Configuration/InteractiveBootstrapMenu.cs) now exposes a separate plan-mode thinking default in the Codex runtime menu so operators can configure the SDK bridge without editing JSON by hand.

## Data and State Considerations

Plan-mode configuration should stay profile-level, not per-turn.

That means:

- binding should happen once at startup
- runtime slots should inherit the same configured defaults
- queued plan prompts should continue to set `PlanMode = true` on the Telegram prompt path independently of SDK plan-mode configuration
- goal mode should remain thread-goal state reachable through existing `/goal` behavior, not a second mode bag

If the SDK later adds more plan-only keys, they should extend the nested plan-mode object rather than spawning a parallel Telegram-side mode hierarchy.

## Edge Cases and Constraints

- The Telegram package fallback is pinned to `Incursa.OpenAI.Codex` 2.3.0, which exposes the SDK plan-mode configuration surface described here.
- A shallow `CodexClientOptions` clone must not drop nested plan-mode defaults.
- The existing prompt-based `/plan` workflow must remain available even if the SDK adds more plan configuration keys later.
- Goal support should not be re-expressed as a plan-mode sibling, because that would conflate configuration with conversation state.

## Alternatives Considered

- Separate `CodexPlanClient` or `CodexPlanThread` classes.
  - Rejected because they would fragment a single runtime profile into multiple public entry points without adding any real polymorphism.
- A Telegram-specific plan-mode interface hierarchy.
  - Rejected because plan mode is passive configuration, not a behavior contract.
- Moving plan-mode into `CodexThreadOptions`.
  - Rejected because thread options are execution-scoped and would blur the distinction between runtime profile defaults and a specific turn.
- Replacing the existing `/plan` prompt path with SDK plan-mode configuration only.
  - Rejected because Telegram already uses the prompt wrapper as the operator-facing plan workflow and that should remain intact.

## Implementation Slice

When the SDK package exposes the nested plan-mode property locally, the implementation slice should stay narrow:

- [`Program.cs`](../../../src/Incursa.Codex.Telegram/Program.cs) for configuration binding and post-configuration defaults
- [`CodexSessionRuntimeRegistry.cs`](../../../src/Incursa.Codex.Telegram/Services/CodexSessionRuntimeRegistry.cs) for preserving the nested plan-mode object when cloning runtime options
- [`LocalSettingsSnapshot.cs`](../../../src/Incursa.Codex.Telegram/Configuration/LocalSettingsSnapshot.cs) and [`LocalSettingsStore.cs`](../../../src/Incursa.Codex.Telegram/Configuration/LocalSettingsStore.cs) only if the bootstrap/menu flow needs a persisted user-facing plan-mode default
- [`InteractiveBootstrapMenu.cs`](../../../src/Incursa.Codex.Telegram/Configuration/InteractiveBootstrapMenu.cs) only if plan-mode defaults are intentionally exposed to operators
- [`TelegramSessionCardBehavior.cs`](../../../src/Incursa.Codex.Telegram/Telegram/TelegramSessionCardBehavior.cs) only if a new visible control becomes necessary, which this design does not currently require

## Risks

- The SDK and Telegram repos can drift if the SDK adds more plan-only keys but Telegram keeps only the reasoning-effort override in its local config story.
- The current local settings UX may make plan-mode defaults feel "missing" until the SDK package update lands and the repo chooses whether to surface them in the bootstrap menu.
- The existing `/plan` workflow and the new SDK plan-mode config can be confused in docs unless the distinction stays explicit.

## Open Questions

- Should Telegram expose plan-mode defaults in the bootstrap menu once the SDK package version is bumped, or should they remain process-config-only?
- If the SDK later exposes `plan_mode_model`, should Telegram surface that in the local settings menu or keep it hidden behind config files and environment variables?
- If a future goal-mode UI is added, should it reuse the existing `/goal` commands only, or should it mirror the button-style navigation used for model and thinking?
