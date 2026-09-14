# Web Components Runtime Notes

This folder ships the optional `./web-components` entrypoint for the UI kit.

## v1 Scope

The runtime defines the approved v1 host family set:

- layouts and shells: `inc-app-shell`, `inc-page`, `inc-page-header`, `inc-section`, `inc-card`, `inc-summary-overview`, `inc-summary-block`, `inc-footer-bar`
- navigation: `inc-navbar`, `inc-tabs`, `inc-user-menu`
- forms and inputs: `inc-field`, `inc-input-group`, `inc-choice-group`, `inc-readonly-field`, `inc-validation-summary`
- feedback and status: `inc-state-panel`, `inc-live-region`, `inc-auto-refresh`, `inc-theme-switcher`, `inc-badge`, `inc-spinner`
- actions and detail shells: `inc-button`, `inc-button-group`, `inc-button-toolbar`, `inc-close-button`, `inc-alert`, `inc-empty-state`
- collections: `inc-list-group`, `inc-key-value-grid`, `inc-key-value`
- data visualization: `inc-sparkline`
- overlays: `inc-disclosure`, `inc-dialog`, `inc-drawer`

## Contract shape

- CSS-first is still canonical. Components reuse existing `inc-*` class contracts.
- Badge and spinner hosts standardize the most common atomic status defaults while still reusing the same `inc-*` vocabulary, the action/detail hosts standardize repeated button, alert, and empty-state markup patterns, and the collection hosts standardize repeated list and key/value markup patterns.
- Package consumers should pair `@incursa/ui-kit/web-components` with `@incursa/ui-kit/web-components/style.css` when they want the default look out of the box.
- v1 stays light DOM first so current style selectors keep working.
- Native primitives are used for disclosure/menu/dialog behavior where practical.
- `index.js` is a thin bootstrap that registers family modules and the promoted action/detail and collection hosts:
  - `components/layout.js`
  - `components/navigation.js`
  - `components/forms.js`
  - `components/feedback.js`
  - `components/actions.js`
  - `components/collections.js`
  - `components/visualizations.js`
  - `components/overlays.js`
- Public registration API is idempotent:
  - `window.IncWebComponents.defineAll()`
  - `window.IncWebComponents.registerIncWebComponents()`
- The dedicated entrypoint auto-defines components on load.

## Explicitly deferred surfaces in v1

- tooltip and popover components
- permission-banner and toast runtime orchestration
- table/data wrappers and grid-like behavior
- filter/file/bulk workflow wrappers
- legacy helper-managed modal/offcanvas compatibility wrappers

Those surfaces remain CSS-first until a follow-up requirement pass promotes them.
