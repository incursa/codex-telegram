# Web Components Runtime Notes

This folder contains the additive browser-native layer for `@incursa/ui-kit`.

The CSS-first [`inc-*`](../../reference.html) class surface remains the canonical API. The Web Component layer exists so the same design language can also be consumed from plain HTML, JavaScript, and browser-native primitives without creating a second design system.

## Public Entry Point

- Package entrypoint: `@incursa/ui-kit/web-components`
- Style entrypoint: `@incursa/ui-kit/web-components/style.css`
- Icon entrypoint: `@incursa/ui-kit/icons`
- Built output: `dist/web-components/`
- Package export: `./web-components` resolves to `dist/web-components/index.js`
- Package export: `./web-components/style.css` resolves to `dist/web-components/style.css`
- Package export: `./icons` resolves to `dist/icons/index.js`
- Module boundary: `src/web-components/package.json` sets this subtree to `type: module`

Load these entrypoints only when the consuming app wants the custom elements and their default look. CSS-only consumers should not pay for the runtime.

Recommended browser usage:

```html
<link rel="stylesheet" href="/node_modules/@incursa/ui-kit/web-components/style.css">
<script type="module">
    import "@incursa/ui-kit/web-components";
</script>
```

The runtime auto-defines the shipped elements on load. If a consumer needs explicit registry control, `registerIncWebComponents()` remains available.

## Runtime Rules

- Keep the layer additive.
- Reuse the current design tokens, CSS vocabulary, and helper behavior where they already exist.
- Prefer light DOM and slotted native content by default.
- Use open Shadow DOM only when a component needs a stable internal scaffold that materially improves correctness or maintainability.
- Keep public tag names in the [`inc-`](../../reference.html) namespace and align them with the CSS family names whenever possible.
- Expose public state through attributes and mirrored properties only when that state belongs in the contract.
- Dispatch DOM events instead of framework callbacks.
- Clean up observers, timers, and listeners on disconnect.

## Current Source Layout

- [`shared.js`](shared.js)
  Shared parsing, reflection, event, and namespace helpers.
- [`base-element.js`](base-element.js)
  The `IncElement` base class, including reflected attribute/property wiring and slot helpers.
- [`registry.js`](registry.js)
  Idempotent registration helpers and the `IncWebComponents.registry` namespace.
- [`../icons/index.js`](../icons/index.js)
  Semantic Incursa icon names, default Lucide-backed rendering, and the global renderer override used by component fallbacks.
- [`components/dom-helpers.js`](components/dom-helpers.js)
  Shared DOM helpers used by the action and collection modules.
- [`controllers/focus.js`](controllers/focus.js)
  Shared focus utilities for focus trapping, focus restoration, and focusable-element discovery.
- [`controllers/selection.js`](controllers/selection.js)
  Roving tabindex and keyboard navigation helpers for selection-based widgets.
- [`controllers/overlay.js`](controllers/overlay.js)
  Shared open/close, escape, backdrop, and focus-restoration behavior for overlays.
- [`controllers/theme.js`](controllers/theme.js)
  Shared root-theme helpers and the legacy bridge used by theme controls.
- [`components/layout.js`](components/layout.js)
  Layout and shell custom elements.
- [`components/navigation.js`](components/navigation.js)
  Navbar, tabs, and user-menu custom elements.
- [`components/forms.js`](components/forms.js)
  Field wrappers, input groups, choice groups, read-only fields, and validation summary custom elements.
- [`components/feedback.js`](components/feedback.js)
  State panel, live-region, auto-refresh, badge, spinner, and theme-switcher custom elements.
- [`components/actions.js`](components/actions.js)
  Button, button-group, button-toolbar, close-button, alert, and empty-state custom elements.
- [`components/collections.js`](components/collections.js)
  List-group, key-value-grid, and key-value custom elements.
- [`components/visualizations.js`](components/visualizations.js)
  D3-backed sparkline custom element and small path/data helpers.
- [`components/overlays.js`](components/overlays.js)
  Disclosure, dialog, and drawer custom elements.
- [`index.js`](index.js)
  Thin package bootstrap that imports the family modules, registers the shipped families, and exposes the public namespace.

As more families land, keep them in this same shape: small shared controllers plus family modules that register their public elements through the registry.

## Design Intent

The Web Component layer should mirror the current CSS kit, not reinterpret it.

- Layout primitives should stay composable and slot-driven.
- Form wrappers should keep native controls native.
- Navigation components should reflect keyboard and focus state in the DOM.
- Feedback and status shells should announce state accessibly, badge/spinner atoms should standardize the common tone and loading defaults, and action/detail plus collection hosts should keep buttons, alerts, list groups, and description-list pairs declarative without inventing a second styling vocabulary.
- Visualization components should stay compact, accessible, and evidence-supporting. Use D3 for geometry and scales, not DOM mutation.
- Overlays should prefer native `<details>` and `<dialog>` behavior when that satisfies the contract.
- Tables, data presentation, utility atoms, and the remaining presentation-only surfaces should remain CSS-first until the component contract is explicit and worth the runtime cost.

## Maintenance Notes

When you add or change a component:

1. Reuse `IncElement` or the shared controllers before inventing new behavior.
2. Keep the public contract declarative and observable in the DOM.
3. Update the package docs and the browser examples together.
4. Add or update Playwright coverage for keyboard, focus, dismissal, theming, and accessibility behavior.
5. Do not introduce a second token family or styling vocabulary.

The CSS class surface and the Web Component layer are meant to evolve together. If a change cannot be explained in terms of the existing [`inc-*`](../../reference.html) vocabulary, it is probably the wrong change.
