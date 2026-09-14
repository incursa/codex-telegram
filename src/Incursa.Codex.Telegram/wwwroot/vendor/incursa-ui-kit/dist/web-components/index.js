// src/web-components/shared.js
var BOOLEAN_FALSE_TOKENS = /* @__PURE__ */ new Set(["false", "0", "off", "no"]);
function isCustomElementsAvailable() {
  return typeof globalThis !== "undefined" && "customElements" in globalThis;
}
function toKebabCase(value) {
  return String(value).replace(/([a-z0-9])([A-Z])/g, "$1-$2").replace(/[_\s]+/g, "-").toLowerCase();
}
function normalizeAttributeConfig(propertyName, config) {
  const normalized = typeof config === "string" ? { attribute: config } : { ...config };
  const type = normalized.type || "string";
  return {
    property: propertyName,
    attribute: normalized.attribute || toKebabCase(propertyName),
    type,
    reflect: normalized.reflect !== false,
    defaultValue: normalized.defaultValue,
    parse: normalized.parse,
    serialize: normalized.serialize
  };
}
function parseBoolean(value) {
  if (value == null) {
    return false;
  }
  return !BOOLEAN_FALSE_TOKENS.has(String(value).toLowerCase());
}
function parseNumber(value, fallback = null) {
  if (value == null || value === "") {
    return fallback;
  }
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}
function parseValueFromAttribute(rawValue, config) {
  if (typeof config.parse === "function") {
    return config.parse(rawValue);
  }
  if (config.type === "boolean") {
    return parseBoolean(rawValue);
  }
  if (config.type === "number") {
    return parseNumber(rawValue, config.defaultValue ?? null);
  }
  return rawValue ?? config.defaultValue ?? "";
}
function serializeValueForAttribute(value, config) {
  if (typeof config.serialize === "function") {
    return config.serialize(value);
  }
  if (config.type === "boolean") {
    return value ? "" : null;
  }
  if (value == null) {
    return null;
  }
  return String(value);
}
function reflectAttributeValue(host, attribute, serializedValue) {
  if (!host || typeof host.setAttribute !== "function") {
    return;
  }
  if (serializedValue == null) {
    host.removeAttribute(attribute);
    return;
  }
  host.setAttribute(attribute, serializedValue);
}
function readReflectedAttribute(host, config) {
  const rawValue = host.getAttribute(config.attribute);
  return parseValueFromAttribute(rawValue, config);
}
function getAssignedSlotElements(host, slotName = "") {
  if (!host || typeof host.querySelector !== "function") {
    return [];
  }
  const selector = slotName ? `slot[name="${slotName}"]` : "slot:not([name])";
  const slot = host.querySelector(selector);
  if (typeof HTMLSlotElement === "undefined" || !(slot instanceof HTMLSlotElement)) {
    return [];
  }
  return slot.assignedElements({ flatten: true });
}
function dispatchComponentEvent(host, type, detail = {}, options = {}) {
  if (!host || typeof host.dispatchEvent !== "function") {
    return false;
  }
  const event = new CustomEvent(type, {
    bubbles: options.bubbles !== false,
    composed: options.composed !== false,
    cancelable: options.cancelable === true,
    detail
  });
  return host.dispatchEvent(event);
}
function createUniqueId(prefix = "inc-wc") {
  const random = Math.random().toString(36).slice(2, 8);
  return `${prefix}-${random}`;
}
function getIncWebComponentsNamespace() {
  if (typeof globalThis === "undefined") {
    return null;
  }
  if (!globalThis.IncWebComponents || typeof globalThis.IncWebComponents !== "object") {
    globalThis.IncWebComponents = {};
  }
  return globalThis.IncWebComponents;
}
function defineCustomElement(name, constructor, registry = null) {
  if (!isCustomElementsAvailable()) {
    return { defined: false, reason: "custom-elements-unavailable", name };
  }
  const targetRegistry = registry || globalThis.customElements;
  const existing = targetRegistry.get(name);
  if (existing) {
    return {
      defined: existing === constructor,
      reason: existing === constructor ? "already-defined" : "name-conflict",
      name,
      constructor: existing
    };
  }
  targetRegistry.define(name, constructor);
  return { defined: true, reason: "defined", name, constructor };
}

// src/web-components/registry.js
var registryEntries = /* @__PURE__ */ new Map();
function registerComponent(name, constructor) {
  if (typeof name !== "string" || !name.startsWith("inc-")) {
    throw new TypeError(`Web Component name must use the "inc-" prefix. Received "${name}".`);
  }
  if (typeof constructor !== "function") {
    throw new TypeError(`Constructor for "${name}" must be a function.`);
  }
  registryEntries.set(name, constructor);
  return constructor;
}
function registerComponents(entries) {
  if (!entries || typeof entries !== "object") {
    return [];
  }
  const registered = [];
  Object.entries(entries).forEach(([name, constructor]) => {
    registerComponent(name, constructor);
    registered.push(name);
  });
  return registered;
}
function getRegisteredComponents() {
  return [...registryEntries.entries()].map(([name, constructor]) => ({ name, constructor }));
}
function defineAll(options = {}) {
  const results = [];
  const registry = options.registry || null;
  registryEntries.forEach((constructor, name) => {
    results.push(defineCustomElement(name, constructor, registry));
  });
  return results;
}
function hasRegisteredComponent(name) {
  return registryEntries.has(name);
}
function installRegistryNamespace() {
  const namespace2 = getIncWebComponentsNamespace();
  if (!namespace2) {
    return null;
  }
  namespace2.registry = {
    registerComponent,
    registerComponents,
    getRegisteredComponents,
    hasRegisteredComponent,
    defineAll
  };
  return namespace2.registry;
}

// src/web-components/components/layout.js
(function(root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
    return;
  }
  const exports = factory();
  root.IncWebComponents = root.IncWebComponents || {};
  root.IncWebComponents.layout = exports;
})(typeof globalThis !== "undefined" ? globalThis : window, function() {
  "use strict";
  const BOOLEAN_ATTRIBUTE_TYPES = /* @__PURE__ */ new Set(["boolean"]);
  const HTMLElementRef = typeof HTMLElement === "undefined" ? null : HTMLElement;
  const MutationObserverRef = typeof MutationObserver === "undefined" ? null : MutationObserver;
  const BaseElement = HTMLElementRef || class {
  };
  function toBooleanAttribute3(value) {
    return value === true || value === "" || value === "true";
  }
  function tokenList(value) {
    if (!value) {
      return [];
    }
    return String(value).split(/\s+/u).map((part) => part.trim()).filter(Boolean);
  }
  function dispatchSlotChange(host) {
    host.dispatchEvent(new Event("slotchange"));
  }
  class IncLayoutElement extends BaseElement {
    static get observedAttributes() {
      return this.layoutConfig ? Object.keys(this.layoutConfig.attributes || {}) : [];
    }
    constructor() {
      super();
      this._mutationObserver = null;
      this._syncQueued = false;
      this._appliedTokenClasses = /* @__PURE__ */ new Map();
      this._appliedBooleanClasses = /* @__PURE__ */ new Map();
      this._appliedIntegerClasses = /* @__PURE__ */ new Map();
    }
    connectedCallback() {
      this._applyHostClasses();
      this._syncChildren();
      if (!MutationObserverRef) {
        return;
      }
      if (!this._mutationObserver) {
        this._mutationObserver = new MutationObserverRef(() => this._queueSync());
      }
      this._mutationObserver.observe(this, {
        childList: true,
        attributes: true,
        subtree: false,
        attributeFilter: ["slot"]
      });
    }
    disconnectedCallback() {
      if (this._mutationObserver) {
        this._mutationObserver.disconnect();
      }
    }
    attributeChangedCallback() {
      this._applyHostClasses();
      this._queueSync();
    }
    _queueSync() {
      if (this._syncQueued) {
        return;
      }
      this._syncQueued = true;
      queueMicrotask(() => {
        this._syncQueued = false;
        this._syncChildren();
        dispatchSlotChange(this);
      });
    }
    _applyHostClasses() {
      const config = this.constructor.layoutConfig || {};
      const hostClasses = [config.baseClass, ...config.hostClasses || []].filter(Boolean);
      this.classList.add(...hostClasses);
      const parts = tokenList(config.parts);
      if (parts.length) {
        this.setAttribute("part", parts.join(" "));
      }
      const attributes = config.attributes || {};
      Object.entries(attributes).forEach(([name, meta]) => {
        const value = this.getAttribute(name);
        const baseClass = config.baseClass;
        const hostClassPrefix = meta.classPrefix || (baseClass ? `${baseClass}--` : "");
        if (meta.type === "token") {
          const values = tokenList(value);
          const appliedKey = `${name}:token`;
          const previousClasses = this._appliedTokenClasses.get(appliedKey) || [];
          previousClasses.forEach((className) => this.classList.remove(className));
          const nextClasses = values.map((token) => `${hostClassPrefix}${token}`);
          nextClasses.forEach((className) => this.classList.add(className));
          this._appliedTokenClasses.set(appliedKey, nextClasses);
          return;
        }
        if (BOOLEAN_ATTRIBUTE_TYPES.has(meta.type)) {
          const enabled = toBooleanAttribute3(value);
          const onClass = meta.trueClass || `${baseClass}--${name}`;
          const offClass = meta.falseClass;
          const appliedKey = `${name}:boolean`;
          const previousClasses = this._appliedBooleanClasses.get(appliedKey) || [];
          previousClasses.forEach((className) => this.classList.remove(className));
          const nextClasses = [];
          if (this.hasAttribute(name)) {
            if (enabled && onClass) {
              nextClasses.push(onClass);
            } else if (!enabled && offClass) {
              nextClasses.push(offClass);
            }
          }
          nextClasses.forEach((className) => this.classList.add(className));
          this._appliedBooleanClasses.set(appliedKey, nextClasses);
          return;
        }
        if (meta.type === "integer" && name === "columns") {
          const parsed = Number.parseInt(value || "", 10);
          const appliedKey = `${name}:integer`;
          const previousClasses = this._appliedIntegerClasses.get(appliedKey) || [];
          previousClasses.forEach((className) => this.classList.remove(className));
          if (Number.isInteger(parsed) && parsed > 0) {
            this.style.setProperty("--inc-summary-columns", String(parsed));
            const nextClasses = [`${baseClass}--${parsed}-col`];
            nextClasses.forEach((className) => this.classList.add(className));
            this._appliedIntegerClasses.set(appliedKey, nextClasses);
            return;
          }
          this.style.removeProperty("--inc-summary-columns");
          this._appliedIntegerClasses.set(appliedKey, []);
        }
      });
    }
    _syncChildren() {
      const config = this.constructor.layoutConfig || {};
      const slotClasses = config.slotClasses || {};
      const managedClasses = new Set(Object.values(slotClasses));
      Array.from(this.children).forEach((child) => {
        if (!HTMLElementRef || !(child instanceof HTMLElementRef)) {
          return;
        }
        managedClasses.forEach((className) => {
          child.classList.remove(className);
        });
        const slotName = child.getAttribute("slot");
        const slotClass = slotClasses[slotName] || null;
        if (slotClass) {
          child.classList.add(slotClass);
        }
      });
    }
  }
  function defineLayoutAccessors(ComponentClass) {
    const config = ComponentClass.layoutConfig || {};
    const attributes = config.attributes || {};
    Object.entries(attributes).forEach(([attributeName, meta]) => {
      if (Object.prototype.hasOwnProperty.call(ComponentClass.prototype, attributeName)) {
        return;
      }
      Object.defineProperty(ComponentClass.prototype, attributeName, {
        configurable: true,
        enumerable: true,
        get() {
          if (meta.type === "boolean") {
            return this.hasAttribute(attributeName);
          }
          if (meta.type === "integer") {
            const value = Number.parseInt(this.getAttribute(attributeName) || "", 10);
            return Number.isNaN(value) ? null : value;
          }
          return this.getAttribute(attributeName);
        },
        set(value) {
          if (meta.type === "boolean") {
            if (value) {
              this.setAttribute(attributeName, "");
            } else {
              this.removeAttribute(attributeName);
            }
            return;
          }
          if (value === null || value === void 0 || value === "") {
            this.removeAttribute(attributeName);
            return;
          }
          this.setAttribute(attributeName, String(value));
        }
      });
    });
  }
  class IncAppShellElement extends IncLayoutElement {
  }
  IncAppShellElement.layoutConfig = {
    baseClass: "inc-app-shell",
    parts: "shell header main footer",
    attributes: {
      variant: { type: "token" },
      dense: { type: "boolean" },
      collapsed: { type: "boolean" }
    },
    slotClasses: {
      header: "inc-app-shell__header",
      main: "inc-app-shell__main",
      footer: "inc-app-shell__footer"
    }
  };
  class IncPageElement extends IncLayoutElement {
  }
  IncPageElement.layoutConfig = {
    baseClass: "inc-page",
    parts: "page breadcrumbs body aside footer",
    attributes: {
      variant: { type: "token" },
      dense: { type: "boolean" },
      wide: { type: "boolean" }
    },
    slotClasses: {
      breadcrumbs: "inc-page__breadcrumbs",
      header: "inc-page__header",
      body: "inc-page__body",
      aside: "inc-page__aside",
      footer: "inc-page__footer"
    }
  };
  class IncPageHeaderElement extends IncLayoutElement {
  }
  IncPageHeaderElement.layoutConfig = {
    baseClass: "inc-page-header",
    parts: "header title body actions",
    attributes: {
      variant: { type: "token" },
      dense: { type: "boolean" }
    },
    slotClasses: {
      title: "inc-page-header__title",
      body: "inc-page-header__body",
      actions: "inc-page-header__actions"
    }
  };
  class IncSectionElement extends IncLayoutElement {
  }
  IncSectionElement.layoutConfig = {
    baseClass: "inc-section-container",
    hostClasses: ["inc-section"],
    parts: "section header body footer actions",
    attributes: {
      variant: { type: "token", classPrefix: "inc-section--" },
      dense: { type: "boolean", trueClass: "inc-section--dense" },
      tone: { type: "token", classPrefix: "inc-section--tone-" }
    },
    slotClasses: {
      header: "inc-section__header",
      body: "inc-section__body",
      footer: "inc-section__footer",
      actions: "inc-section__actions"
    }
  };
  class IncCardElement extends IncLayoutElement {
  }
  IncCardElement.layoutConfig = {
    baseClass: "inc-card",
    parts: "card header body footer",
    attributes: {
      variant: { type: "token" },
      tone: { type: "token", classPrefix: "inc-card--tone-" },
      elevated: { type: "boolean", trueClass: "inc-card--elevated" }
    },
    slotClasses: {
      header: "inc-card__header",
      body: "inc-card__body",
      footer: "inc-card__footer"
    }
  };
  class IncSummaryOverviewElement extends IncLayoutElement {
  }
  IncSummaryOverviewElement.layoutConfig = {
    baseClass: "inc-summary-overview",
    parts: "overview",
    attributes: {
      columns: { type: "integer" },
      dense: { type: "boolean" }
    },
    slotClasses: {}
  };
  class IncSummaryBlockElement extends IncLayoutElement {
  }
  IncSummaryBlockElement.layoutConfig = {
    baseClass: "inc-summary-block",
    parts: "block header body footer actions value status",
    attributes: {
      variant: { type: "token" },
      tone: { type: "token", classPrefix: "inc-summary-block--tone-" },
      dense: { type: "boolean" }
    },
    slotClasses: {
      header: "inc-summary-block__header",
      body: "inc-summary-block__body",
      footer: "inc-summary-block__footer",
      actions: "inc-summary-block__actions"
    }
  };
  class IncFooterBarElement extends IncLayoutElement {
  }
  IncFooterBarElement.layoutConfig = {
    baseClass: "inc-footer-bar",
    parts: "footer menu meta",
    attributes: {
      variant: { type: "token" },
      dense: { type: "boolean" }
    },
    slotClasses: {
      menu: "inc-footer-bar__menu",
      meta: "inc-footer-bar__meta"
    }
  };
  const layoutComponents = [
    ["inc-app-shell", IncAppShellElement],
    ["inc-page", IncPageElement],
    ["inc-page-header", IncPageHeaderElement],
    ["inc-section", IncSectionElement],
    ["inc-card", IncCardElement],
    ["inc-summary-overview", IncSummaryOverviewElement],
    ["inc-summary-block", IncSummaryBlockElement],
    ["inc-footer-bar", IncFooterBarElement]
  ];
  layoutComponents.forEach(([, ComponentClass]) => {
    defineLayoutAccessors(ComponentClass);
  });
  function defineLayoutComponents(registry) {
    const targetRegistry = registry || (typeof customElements !== "undefined" ? customElements : null);
    if (!targetRegistry) {
      return [];
    }
    const defined = [];
    layoutComponents.forEach(([tagName, ComponentClass]) => {
      if (targetRegistry.get(tagName)) {
        return;
      }
      targetRegistry.define(tagName, ComponentClass);
      defined.push(tagName);
    });
    return defined;
  }
  return {
    IncAppShellElement,
    IncPageElement,
    IncPageHeaderElement,
    IncSectionElement,
    IncCardElement,
    IncSummaryOverviewElement,
    IncSummaryBlockElement,
    IncFooterBarElement,
    layoutComponents,
    defineLayoutComponents
  };
});

// src/web-components/components/navigation.js
var NAVBAR_TAG = "inc-navbar";
var TABS_TAG = "inc-tabs";
var USER_MENU_TAG = "inc-user-menu";
var TAB_KEYS = /* @__PURE__ */ new Set(["ArrowRight", "ArrowLeft", "ArrowDown", "ArrowUp", "Home", "End", "Enter", " "]);
var MENU_KEYS = /* @__PURE__ */ new Set(["ArrowDown", "ArrowUp", "Home", "End", "Escape", "Enter", " "]);
var HostElement = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
var uidCounter = 0;
function nextId(prefix) {
  uidCounter += 1;
  return `${prefix}-${uidCounter}`;
}
function asBoolean(value) {
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "false" || normalized === "0" || normalized === "off" || normalized === "no") {
      return false;
    }
  }
  return Boolean(value);
}
function normalizeActivation(value) {
  return value === "manual" ? "manual" : "auto";
}
function normalizeOrientation(value) {
  return value === "vertical" ? "vertical" : "horizontal";
}
function emit(host, type, detail = {}) {
  host.dispatchEvent(new CustomEvent(type, {
    bubbles: true,
    composed: true,
    detail
  }));
}
function defineClassToken(element, token, on) {
  if (!element || !token) {
    return;
  }
  element.classList.toggle(token, Boolean(on));
}
function getFocusableItems(container) {
  if (!(container instanceof HTMLElement)) {
    return [];
  }
  return Array.from(container.querySelectorAll("a[href], button:not([disabled]), [role='menuitem'], [role='menuitemradio'], [role='menuitemcheckbox'], [tabindex]:not([tabindex='-1'])")).filter((candidate) => candidate instanceof HTMLElement && !candidate.hasAttribute("disabled") && !candidate.hasAttribute("aria-disabled"));
}
var IncNavbarElement = class extends HostElement {
  static get observedAttributes() {
    return ["expanded", "breakpoint", "app", "variant"];
  }
  constructor() {
    super();
    this._boundClick = (event) => this._onClick(event);
    this._boundKeydown = (event) => this._onKeydown(event);
    this._boundSlotChange = () => this._syncStructure();
  }
  connectedCallback() {
    this.classList.add("inc-navbar");
    this.setAttribute("role", this.getAttribute("role") || "navigation");
    this._syncStructure();
    this._syncClasses();
    this.addEventListener("click", this._boundClick);
    this.addEventListener("keydown", this._boundKeydown);
    this.addEventListener("slotchange", this._boundSlotChange);
  }
  disconnectedCallback() {
    this.removeEventListener("click", this._boundClick);
    this.removeEventListener("keydown", this._boundKeydown);
    this.removeEventListener("slotchange", this._boundSlotChange);
  }
  attributeChangedCallback(name) {
    if (name === "expanded") {
      emit(this, "toggle", { expanded: this.expanded });
    }
    this._syncClasses();
  }
  get expanded() {
    return this.hasAttribute("expanded");
  }
  set expanded(value) {
    if (asBoolean(value)) {
      this.setAttribute("expanded", "");
    } else {
      this.removeAttribute("expanded");
    }
  }
  expand() {
    if (!this.expanded) {
      this.expanded = true;
      emit(this, "open", { expanded: true });
    }
  }
  collapse() {
    if (this.expanded) {
      this.expanded = false;
      emit(this, "close", { expanded: false });
    }
  }
  toggle() {
    if (this.expanded) {
      this.collapse();
      return false;
    }
    this.expand();
    return true;
  }
  _syncClasses() {
    defineClassToken(this, "inc-navbar--app", this.hasAttribute("app"));
    const breakpoint = this.getAttribute("breakpoint");
    Array.from(this.classList).filter((token) => token.startsWith("inc-navbar--expand-")).forEach((token) => this.classList.remove(token));
    if (breakpoint) {
      this.classList.add(`inc-navbar--expand-${breakpoint}`);
    }
    const variant = this.getAttribute("variant");
    Array.from(this.classList).filter((token) => token.startsWith("inc-navbar--variant-")).forEach((token) => this.classList.remove(token));
    if (variant) {
      this.classList.add(`inc-navbar--variant-${variant}`);
    }
    this.setAttribute("aria-expanded", this.expanded ? "true" : "false");
  }
  _syncStructure() {
    this.querySelectorAll(":scope > [slot='brand']").forEach((node) => node.classList.add("inc-navbar__brand"));
    this.querySelectorAll(":scope > [slot='nav']").forEach((node) => node.classList.add("inc-navbar__nav"));
    this.querySelectorAll(":scope > [slot='utilities']").forEach((node) => node.classList.add("inc-navbar__utilities"));
    this.querySelectorAll(":scope > [slot='collapse']").forEach((node) => node.classList.add("inc-navbar__collapse"));
  }
  _onClick(event) {
    const toggle = event.target instanceof Element ? event.target.closest("[data-inc-navbar-toggle]") : null;
    if (!toggle || !this.contains(toggle)) {
      return;
    }
    event.preventDefault();
    this.toggle();
  }
  _onKeydown(event) {
    if (event.key === "Escape" && this.expanded) {
      this.collapse();
    }
  }
};
var IncTabsElement = class extends HostElement {
  static get observedAttributes() {
    return ["selected", "orientation", "activation", "variant", "fill", "justified"];
  }
  constructor() {
    super();
    this._boundClick = (event) => this._onClick(event);
    this._boundKeydown = (event) => this._onKeydown(event);
    this._boundSlotChange = () => this._initialize();
  }
  connectedCallback() {
    this.classList.add("inc-tabs-host");
    this.addEventListener("click", this._boundClick);
    this.addEventListener("keydown", this._boundKeydown);
    this.addEventListener("slotchange", this._boundSlotChange);
    this._initialize();
  }
  disconnectedCallback() {
    this.removeEventListener("click", this._boundClick);
    this.removeEventListener("keydown", this._boundKeydown);
    this.removeEventListener("slotchange", this._boundSlotChange);
  }
  attributeChangedCallback(name) {
    if (name === "selected") {
      this.select(this.getAttribute("selected"), { emitEvents: false, focus: false });
      return;
    }
    this._syncHostClasses();
    this._syncTabs();
  }
  get selected() {
    return this.getAttribute("selected");
  }
  set selected(value) {
    if (value === null || value === void 0 || value === "") {
      this.removeAttribute("selected");
      return;
    }
    this.setAttribute("selected", String(value));
  }
  select(value, options = {}) {
    const tabs = this._tabs();
    const panels = this._panels();
    const target = this._resolveTab(value, tabs);
    if (!target) {
      return false;
    }
    const previous = tabs.find((tab) => tab.getAttribute("aria-selected") === "true") || null;
    const previousId = previous?.id || null;
    const nextId2 = target.id || null;
    if (previous === target && options.force !== true) {
      if (options.focus) {
        target.focus();
      }
      return true;
    }
    tabs.forEach((tab, index) => {
      const isActive = tab === target;
      const panel = this._resolvePanel(tab, panels, index);
      tab.classList.toggle("active", isActive);
      tab.setAttribute("aria-selected", isActive ? "true" : "false");
      tab.tabIndex = isActive ? 0 : -1;
      if (panel) {
        panel.hidden = !isActive;
        panel.classList.toggle("active", isActive);
        panel.classList.toggle("show", isActive);
      }
    });
    if (nextId2) {
      this.setAttribute("selected", nextId2);
    }
    if (options.focus) {
      target.focus();
    }
    if (options.emitEvents !== false) {
      emit(this, "select", { previous: previousId, selected: nextId2, tab: target });
      emit(this, "change", { previous: previousId, selected: nextId2, tab: target });
    }
    return true;
  }
  next() {
    return this._stepSelection(1);
  }
  previous() {
    return this._stepSelection(-1);
  }
  _initialize() {
    this._syncHostClasses();
    this._syncTabs();
    const selected = this.getAttribute("selected");
    if (selected) {
      this.select(selected, { emitEvents: false, focus: false, force: true });
      return;
    }
    const tabs = this._tabs();
    const active = tabs.find((tab) => tab.classList.contains("active")) || tabs[0];
    if (active) {
      this.select(active.id, { emitEvents: false, focus: false, force: true });
    }
  }
  _syncHostClasses() {
    const orientation = normalizeOrientation(this.getAttribute("orientation"));
    this.setAttribute("data-inc-tabs-orientation", orientation);
    const activation = normalizeActivation(this.getAttribute("activation"));
    this.setAttribute("data-inc-tabs-activation", activation);
    const variant = this.getAttribute("variant");
    Array.from(this.classList).filter((token) => token.startsWith("inc-tabs-host--")).forEach((token) => this.classList.remove(token));
    if (variant) {
      this.classList.add(`inc-tabs-host--${variant}`);
    }
    defineClassToken(this, "inc-tabs-host--fill", this.hasAttribute("fill"));
    defineClassToken(this, "inc-tabs-host--justified", this.hasAttribute("justified"));
  }
  _syncTabs() {
    const tabs = this._tabs();
    const panels = this._panels();
    const orientation = normalizeOrientation(this.getAttribute("orientation"));
    const roleRoot = this.querySelector("[role='tablist'], .inc-tabs-nav");
    if (roleRoot instanceof HTMLElement) {
      roleRoot.setAttribute("role", "tablist");
      roleRoot.setAttribute("aria-orientation", orientation);
      if (!roleRoot.classList.contains("inc-tabs-nav")) {
        roleRoot.classList.add("inc-tabs-nav");
      }
    }
    tabs.forEach((tab, index) => {
      const panel = this._resolvePanel(tab, panels, index);
      if (!tab.id) {
        tab.id = nextId("inc-tab");
      }
      tab.setAttribute("role", "tab");
      if (!tab.hasAttribute("tabindex")) {
        tab.tabIndex = index === 0 ? 0 : -1;
      }
      if (panel && !panel.id) {
        panel.id = nextId("inc-tab-panel");
      }
      if (panel) {
        tab.setAttribute("aria-controls", panel.id);
        panel.setAttribute("role", "tabpanel");
        panel.setAttribute("aria-labelledby", tab.id);
      }
    });
  }
  _tabs() {
    const explicit = Array.from(this.querySelectorAll(":scope > .inc-tabs-nav > li > *"));
    const unique = [];
    explicit.forEach((candidate) => {
      if (!(candidate instanceof HTMLElement)) {
        return;
      }
      if (!unique.includes(candidate)) {
        unique.push(candidate);
      }
    });
    return unique;
  }
  _panels() {
    const explicit = Array.from(this.querySelectorAll(":scope > [slot='panel'], [data-inc-tab-panel], .inc-tab-pane, [role='tabpanel']"));
    const unique = [];
    explicit.forEach((candidate) => {
      if (!(candidate instanceof HTMLElement)) {
        return;
      }
      if (!unique.includes(candidate)) {
        unique.push(candidate);
      }
    });
    return unique;
  }
  _resolveTab(value, tabs) {
    if (!tabs.length) {
      return null;
    }
    if (value === null || value === void 0 || value === "") {
      return tabs[0];
    }
    if (typeof value === "number") {
      return tabs[value] || null;
    }
    const raw = String(value);
    const noHash = raw.startsWith("#") ? raw.slice(1) : raw;
    const byId = tabs.find((tab) => tab.id === noHash);
    if (byId) {
      return byId;
    }
    const asNumber = Number.parseInt(raw, 10);
    if (Number.isFinite(asNumber)) {
      return tabs[asNumber] || null;
    }
    return tabs.find((tab) => tab.getAttribute("aria-controls") === noHash) || null;
  }
  _resolvePanel(tab, panels, fallbackIndex) {
    if (!(tab instanceof HTMLElement)) {
      return null;
    }
    const ariaControls = tab.getAttribute("aria-controls");
    if (ariaControls) {
      const escapedId = typeof CSS !== "undefined" && typeof CSS.escape === "function" ? CSS.escape(ariaControls) : ariaControls.replace(/([^\w-])/g, "\\$1");
      const direct = this.querySelector(`#${escapedId}`);
      if (direct instanceof HTMLElement) {
        return direct;
      }
    }
    const href = tab.getAttribute("href");
    if (href && href.startsWith("#")) {
      const fromHref = this.querySelector(href);
      if (fromHref instanceof HTMLElement) {
        return fromHref;
      }
    }
    const target = tab.getAttribute("data-inc-target");
    if (target) {
      try {
        const fromTarget = this.querySelector(target);
        if (fromTarget instanceof HTMLElement) {
          return fromTarget;
        }
      } catch {
      }
    }
    return panels[fallbackIndex] || null;
  }
  _stepSelection(delta) {
    const tabs = this._tabs();
    if (!tabs.length) {
      return false;
    }
    const activeIndex = Math.max(0, tabs.findIndex((tab) => tab.getAttribute("aria-selected") === "true"));
    const nextIndex = (activeIndex + delta + tabs.length) % tabs.length;
    return this.select(tabs[nextIndex].id, { focus: true });
  }
  _onClick(event) {
    const tab = event.target instanceof Element ? event.target.closest("[slot='tab'], [data-inc-tab], [role='tab']") : null;
    if (!(tab instanceof HTMLElement) || !this.contains(tab)) {
      return;
    }
    if (tab.tagName === "A") {
      event.preventDefault();
    }
    this.select(tab.id || tab.getAttribute("aria-controls") || "", { focus: true });
  }
  _onKeydown(event) {
    if (!TAB_KEYS.has(event.key)) {
      return;
    }
    const tab = event.target instanceof Element ? event.target.closest("[slot='tab'], [data-inc-tab], [role='tab']") : null;
    if (!(tab instanceof HTMLElement) || !this.contains(tab)) {
      return;
    }
    const tabs = this._tabs();
    const currentIndex = tabs.indexOf(tab);
    if (currentIndex < 0) {
      return;
    }
    const orientation = normalizeOrientation(this.getAttribute("orientation"));
    const activation = normalizeActivation(this.getAttribute("activation"));
    let nextIndex = currentIndex;
    if (event.key === "Home") {
      nextIndex = 0;
    } else if (event.key === "End") {
      nextIndex = tabs.length - 1;
    } else if (event.key === "ArrowRight" && orientation === "horizontal" || event.key === "ArrowDown" && orientation === "vertical") {
      nextIndex = (currentIndex + 1) % tabs.length;
    } else if (event.key === "ArrowLeft" && orientation === "horizontal" || event.key === "ArrowUp" && orientation === "vertical") {
      nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      this.select(tab.id, { focus: true });
      return;
    } else {
      return;
    }
    event.preventDefault();
    const nextTab = tabs[nextIndex];
    nextTab.focus();
    if (activation === "auto") {
      this.select(nextTab.id, { focus: false });
    }
  }
};
var IncUserMenuElement = class extends HostElement {
  static get observedAttributes() {
    return ["open", "label", "placement"];
  }
  constructor() {
    super();
    this._boundClick = (event) => this._onClick(event);
    this._boundKeydown = (event) => this._onKeydown(event);
    this._boundPointerDown = (event) => this._onPointerDown(event);
    this._boundSlotChange = () => this._syncStructure();
  }
  connectedCallback() {
    this.classList.add("inc-native-menu", "inc-user-menu");
    this._syncStructure();
    this._syncState();
    this.addEventListener("click", this._boundClick);
    this.addEventListener("keydown", this._boundKeydown);
    this.addEventListener("slotchange", this._boundSlotChange);
    document.addEventListener("pointerdown", this._boundPointerDown, true);
  }
  disconnectedCallback() {
    this.removeEventListener("click", this._boundClick);
    this.removeEventListener("keydown", this._boundKeydown);
    this.removeEventListener("slotchange", this._boundSlotChange);
    document.removeEventListener("pointerdown", this._boundPointerDown, true);
  }
  attributeChangedCallback() {
    this._syncState();
  }
  open() {
    if (!this.hasAttribute("open")) {
      this.setAttribute("open", "");
      emit(this, "open", { open: true });
    }
  }
  close({ restoreFocus = false } = {}) {
    if (this.hasAttribute("open")) {
      this.removeAttribute("open");
      emit(this, "close", { open: false });
    }
    if (restoreFocus) {
      this._trigger()?.focus();
    }
  }
  toggle() {
    if (this.hasAttribute("open")) {
      this.close();
      return false;
    }
    this.open();
    return true;
  }
  _trigger() {
    return this.querySelector(":scope > [slot='trigger'], :scope > .inc-native-menu__summary");
  }
  _menu() {
    return this.querySelector(":scope > [slot='menu'], :scope > .inc-native-menu__panel");
  }
  _items() {
    const menu = this._menu();
    if (!(menu instanceof HTMLElement)) {
      return [];
    }
    return getFocusableItems(menu).filter((item) => menu.contains(item));
  }
  _syncStructure() {
    const trigger = this._trigger();
    const menu = this._menu();
    if (trigger instanceof HTMLElement) {
      trigger.classList.add("inc-native-menu__summary");
      if (!trigger.id) {
        trigger.id = nextId("inc-user-menu-trigger");
      }
      trigger.setAttribute("aria-haspopup", "menu");
    }
    if (menu instanceof HTMLElement) {
      menu.classList.add("inc-native-menu__panel");
      if (!menu.id) {
        menu.id = nextId("inc-user-menu-panel");
      }
      menu.setAttribute("role", "menu");
      menu.setAttribute("aria-label", this.getAttribute("label") || "User menu");
    }
    this.querySelectorAll(":scope > [slot='item']").forEach((item) => {
      item.classList.add("inc-native-menu__item");
      item.setAttribute("role", item.getAttribute("role") || "menuitem");
      if (!item.hasAttribute("tabindex")) {
        item.tabIndex = -1;
      }
    });
    if (trigger instanceof HTMLElement && menu instanceof HTMLElement) {
      trigger.setAttribute("aria-controls", menu.id);
    }
  }
  _syncState() {
    const trigger = this._trigger();
    const menu = this._menu();
    const isOpen = this.hasAttribute("open");
    defineClassToken(this, "is-open", isOpen);
    Array.from(this.classList).filter((token) => token.startsWith("inc-user-menu--")).forEach((token) => this.classList.remove(token));
    const placement = this.getAttribute("placement");
    if (placement) {
      this.classList.add(`inc-user-menu--${placement}`);
    }
    if (trigger instanceof HTMLElement) {
      trigger.setAttribute("aria-expanded", isOpen ? "true" : "false");
    }
    if (menu instanceof HTMLElement) {
      menu.classList.toggle("show", isOpen);
      menu.hidden = !isOpen;
    }
  }
  _focusItem(direction) {
    const items = this._items();
    if (!items.length) {
      return;
    }
    const active = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const currentIndex = active ? items.indexOf(active) : -1;
    let next = items[0];
    if (direction === "last") {
      next = items[items.length - 1];
    } else if (direction === "next" && currentIndex >= 0) {
      next = items[(currentIndex + 1) % items.length];
    } else if (direction === "previous" && currentIndex >= 0) {
      next = items[(currentIndex - 1 + items.length) % items.length];
    } else if (direction === "previous" && currentIndex < 0) {
      next = items[items.length - 1];
    }
    next.focus();
  }
  _onPointerDown(event) {
    if (!(event.target instanceof Node)) {
      return;
    }
    if (!this.contains(event.target)) {
      this.close();
    }
  }
  _onClick(event) {
    const trigger = event.target instanceof Element ? event.target.closest("[slot='trigger'], .inc-native-menu__summary") : null;
    if (trigger && this.contains(trigger)) {
      event.preventDefault();
      const openNow = this.toggle();
      if (openNow) {
        this._focusItem("first");
      }
      return;
    }
    const item = event.target instanceof Element ? event.target.closest("[slot='item'], .inc-native-menu__item, [role='menuitem']") : null;
    if (!item || !this.contains(item)) {
      return;
    }
    emit(this, "select", {
      item,
      value: item.getAttribute("value") || item.getAttribute("data-value") || item.textContent?.trim() || "",
      text: item.textContent?.trim() || ""
    });
    this.close({ restoreFocus: true });
  }
  _onKeydown(event) {
    if (!MENU_KEYS.has(event.key)) {
      return;
    }
    const trigger = event.target instanceof Element ? event.target.closest("[slot='trigger'], .inc-native-menu__summary") : null;
    const menu = event.target instanceof Element ? event.target.closest("[slot='menu'], .inc-native-menu__panel") : null;
    if (trigger && this.contains(trigger)) {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        this.open();
        this._focusItem(event.key === "ArrowDown" ? "first" : "last");
        return;
      }
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        const openNow = this.toggle();
        if (openNow) {
          this._focusItem("first");
        }
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        this.close({ restoreFocus: true });
      }
      return;
    }
    if (menu && this.contains(menu)) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        this._focusItem("next");
        return;
      }
      if (event.key === "ArrowUp") {
        event.preventDefault();
        this._focusItem("previous");
        return;
      }
      if (event.key === "Home") {
        event.preventDefault();
        this._focusItem("first");
        return;
      }
      if (event.key === "End") {
        event.preventDefault();
        this._focusItem("last");
        return;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        this.close({ restoreFocus: true });
      }
    }
  }
};
function defineNavigationComponents(registry = globalThis.customElements) {
  if (!registry) {
    return {
      navbarDefined: false,
      tabsDefined: false,
      userMenuDefined: false
    };
  }
  let navbarDefined = false;
  let tabsDefined = false;
  let userMenuDefined = false;
  if (!registry.get(NAVBAR_TAG)) {
    registry.define(NAVBAR_TAG, IncNavbarElement);
    navbarDefined = true;
  }
  if (!registry.get(TABS_TAG)) {
    registry.define(TABS_TAG, IncTabsElement);
    tabsDefined = true;
  }
  if (!registry.get(USER_MENU_TAG)) {
    registry.define(USER_MENU_TAG, IncUserMenuElement);
    userMenuDefined = true;
  }
  return { navbarDefined, tabsDefined, userMenuDefined };
}
var navigationApi = {
  NAVBAR_TAG,
  TABS_TAG,
  USER_MENU_TAG,
  IncNavbarElement,
  IncTabsElement,
  IncUserMenuElement,
  defineNavigationComponents
};
if (typeof module !== "undefined" && module.exports) {
  module.exports = navigationApi;
}
if (typeof window !== "undefined") {
  window.IncWebComponents = window.IncWebComponents || {};
  window.IncWebComponents.navigation = navigationApi;
}

// src/web-components/components/forms.js
var NATIVE_CONTROL_SELECTOR = [
  "input:not([type='hidden'])",
  "select",
  "textarea",
  "button"
].join(", ");
var HostElement2 = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
var generatedIdCounter = 0;
function nextGeneratedId(prefix) {
  generatedIdCounter += 1;
  return `inc-wc-${prefix}-${generatedIdCounter}`;
}
function parseBooleanAttribute(host, name) {
  return host.hasAttribute(name) && host.getAttribute(name) !== "false";
}
function reflectBooleanAttribute(host, name, value) {
  if (value) {
    host.setAttribute(name, "");
  } else {
    host.removeAttribute(name);
  }
}
function toggleClass(element, className, enabled) {
  if (!element) {
    return;
  }
  element.classList.toggle(className, Boolean(enabled));
}
function withPart(element, partName) {
  if (!element || !partName) {
    return element;
  }
  const existing = (element.getAttribute("part") || "").split(/\s+/).filter(Boolean);
  if (!existing.includes(partName)) {
    existing.push(partName);
    element.setAttribute("part", existing.join(" "));
  }
  return element;
}
function resolveAssignedElement(host, slotName, selector) {
  const explicit = Array.from(host.children).find((child) => child instanceof HTMLElement && child.getAttribute("slot") === slotName);
  if (explicit instanceof HTMLElement) {
    return explicit;
  }
  if (!selector) {
    return null;
  }
  return Array.from(host.children).find((child) => child instanceof HTMLElement && child.matches(selector)) || null;
}
function ensureGeneratedElement(host, key, selector, factory) {
  let element = host.querySelector(selector);
  if (element instanceof HTMLElement) {
    return element;
  }
  if (!host.__incGeneratedElements) {
    host.__incGeneratedElements = /* @__PURE__ */ new Map();
  }
  if (host.__incGeneratedElements.has(key)) {
    return host.__incGeneratedElements.get(key);
  }
  element = factory();
  element.setAttribute("data-inc-generated", key);
  host.append(element);
  host.__incGeneratedElements.set(key, element);
  return element;
}
function ensureControlId(control) {
  if (!control.id) {
    control.id = nextGeneratedId("control");
  }
  return control.id;
}
function setDescribedBy(control, ids) {
  const validIds = ids.filter(Boolean);
  if (!validIds.length) {
    control.removeAttribute("aria-describedby");
    return;
  }
  control.setAttribute("aria-describedby", Array.from(new Set(validIds)).join(" "));
}
var IncFormsElement = class extends HostElement2 {
  constructor() {
    super();
    this.__observer = null;
    this.__syncScheduled = false;
  }
  connectedCallback() {
    this.__installObserver();
    this.sync();
  }
  disconnectedCallback() {
    this.__observer?.disconnect();
    this.__observer = null;
  }
  attributeChangedCallback() {
    this.requestSync();
  }
  requestSync() {
    if (this.__syncScheduled) {
      return;
    }
    this.__syncScheduled = true;
    queueMicrotask(() => {
      this.__syncScheduled = false;
      this.sync();
    });
  }
  sync() {
  }
  notifySlotChange() {
    this.dispatchEvent(new CustomEvent("slotchange", {
      bubbles: true,
      composed: true
    }));
  }
  __installObserver() {
    if (this.__observer) {
      return;
    }
    this.__observer = new MutationObserver(() => {
      this.sync();
      this.notifySlotChange();
    });
    this.__observer.observe(this, {
      childList: true,
      subtree: false,
      attributes: true,
      attributeFilter: ["slot"]
    });
  }
};
var IncFieldElement = class extends IncFormsElement {
  static get observedAttributes() {
    return ["label", "hint", "error", "required", "invalid", "dense"];
  }
  get label() {
    return this.getAttribute("label") || "";
  }
  set label(value) {
    if (value == null || value === "") {
      this.removeAttribute("label");
    } else {
      this.setAttribute("label", String(value));
    }
  }
  get hint() {
    return this.getAttribute("hint") || "";
  }
  set hint(value) {
    if (value == null || value === "") {
      this.removeAttribute("hint");
    } else {
      this.setAttribute("hint", String(value));
    }
  }
  get error() {
    return this.getAttribute("error") || "";
  }
  set error(value) {
    if (value == null || value === "") {
      this.removeAttribute("error");
    } else {
      this.setAttribute("error", String(value));
    }
  }
  get required() {
    return parseBooleanAttribute(this, "required");
  }
  set required(value) {
    reflectBooleanAttribute(this, "required", value);
  }
  get invalid() {
    return parseBooleanAttribute(this, "invalid");
  }
  set invalid(value) {
    reflectBooleanAttribute(this, "invalid", value);
  }
  get dense() {
    return parseBooleanAttribute(this, "dense");
  }
  set dense(value) {
    reflectBooleanAttribute(this, "dense", value);
  }
  focus() {
    const control = this.__resolveControl();
    control?.focus();
  }
  sync() {
    this.classList.add("inc-form__field");
    toggleClass(this, "inc-form__field--compact", this.dense);
    withPart(this, "field");
    const control = this.__resolveControl();
    const label = this.__resolveLabel(control);
    const hint = this.__resolveHint();
    const error = this.__resolveError();
    if (label) {
      withPart(label, "label");
      label.classList.add("inc-form__label");
      toggleClass(label, "inc-form__label--required", this.required);
      if (control && label instanceof HTMLLabelElement) {
        label.htmlFor = ensureControlId(control);
      }
    }
    if (control) {
      withPart(control, "control");
      if (!control.classList.contains("inc-form__control") && !control.classList.contains("inc-input-group") && control.slot !== "control") {
        control.classList.add("inc-form__control");
      }
      control.required = this.required;
      const invalid = this.invalid || this.error.length > 0;
      if (invalid) {
        control.setAttribute("aria-invalid", "true");
        control.classList.add("is-invalid");
      } else if (control.getAttribute("aria-invalid") === "true") {
        control.removeAttribute("aria-invalid");
      }
      const describedBy = [];
      if (hint?.id) {
        describedBy.push(hint.id);
      }
      if (error?.id) {
        describedBy.push(error.id);
      }
      setDescribedBy(control, describedBy);
    }
    if (hint) {
      withPart(hint, "hint");
      hint.classList.add("inc-form__hint");
    }
    if (error) {
      withPart(error, "error");
      error.classList.add("inc-form__invalid-feedback");
      error.setAttribute("aria-live", "polite");
    }
  }
  __resolveControl() {
    const control = resolveAssignedElement(this, "control", NATIVE_CONTROL_SELECTOR);
    if (!(control instanceof HTMLElement)) {
      return null;
    }
    return control;
  }
  __resolveLabel(control) {
    const explicit = resolveAssignedElement(this, "label", "label, [data-inc-field-label]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    if (!this.label) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "label",
      '[data-inc-generated="label"]',
      () => document.createElement("label")
    );
    generated.textContent = this.label;
    if (control instanceof HTMLElement) {
      generated.setAttribute("for", ensureControlId(control));
    }
    return generated;
  }
  __resolveHint() {
    const explicit = resolveAssignedElement(this, "hint", ".inc-form__hint, [data-inc-field-hint]");
    if (explicit instanceof HTMLElement) {
      if (!explicit.id) {
        explicit.id = nextGeneratedId("hint");
      }
      return explicit;
    }
    if (!this.hint) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "hint",
      '[data-inc-generated="hint"]',
      () => document.createElement("p")
    );
    if (!generated.id) {
      generated.id = nextGeneratedId("hint");
    }
    generated.textContent = this.hint;
    return generated;
  }
  __resolveError() {
    const explicit = resolveAssignedElement(this, "error", ".inc-form__invalid-feedback, [data-inc-field-error]");
    if (explicit instanceof HTMLElement) {
      if (!explicit.id) {
        explicit.id = nextGeneratedId("error");
      }
      return explicit;
    }
    if (!this.error) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "error",
      '[data-inc-generated="error"]',
      () => document.createElement("p")
    );
    if (!generated.id) {
      generated.id = nextGeneratedId("error");
    }
    generated.textContent = this.error;
    return generated;
  }
};
var IncInputGroupElement = class extends IncFormsElement {
  static get observedAttributes() {
    return ["prefix", "suffix", "dense", "expand"];
  }
  get prefix() {
    return this.getAttribute("prefix") || "";
  }
  set prefix(value) {
    if (value == null || value === "") {
      this.removeAttribute("prefix");
    } else {
      this.setAttribute("prefix", String(value));
    }
  }
  get suffix() {
    return this.getAttribute("suffix") || "";
  }
  set suffix(value) {
    if (value == null || value === "") {
      this.removeAttribute("suffix");
    } else {
      this.setAttribute("suffix", String(value));
    }
  }
  get dense() {
    return parseBooleanAttribute(this, "dense");
  }
  set dense(value) {
    reflectBooleanAttribute(this, "dense", value);
  }
  get expand() {
    return parseBooleanAttribute(this, "expand");
  }
  set expand(value) {
    reflectBooleanAttribute(this, "expand", value);
  }
  focus() {
    const control = this.__resolveControl();
    control?.focus();
  }
  sync() {
    this.classList.add("inc-input-group");
    toggleClass(this, "inc-input-group--sm", this.dense);
    toggleClass(this, "inc-input-group--expand", this.expand);
    withPart(this, "group");
    const prefix = this.__resolvePrefix();
    const suffix = this.__resolveSuffix();
    const control = this.__resolveControl();
    if (prefix) {
      withPart(prefix, "prefix");
      prefix.classList.add("inc-input-group__text");
    }
    if (suffix) {
      withPart(suffix, "suffix");
      suffix.classList.add("inc-input-group__text");
    }
    if (control) {
      withPart(control, "control");
      if (!control.classList.contains("inc-form__control")) {
        control.classList.add("inc-form__control");
      }
    }
  }
  __resolvePrefix() {
    const explicit = resolveAssignedElement(this, "prefix", ".inc-input-group__text[data-inc-prefix]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    if (!this.prefix) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "prefix",
      '[data-inc-generated="prefix"]',
      () => document.createElement("span")
    );
    generated.setAttribute("data-inc-prefix", "true");
    generated.textContent = this.prefix;
    return generated;
  }
  __resolveSuffix() {
    const explicit = resolveAssignedElement(this, "suffix", ".inc-input-group__text[data-inc-suffix]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    if (!this.suffix) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "suffix",
      '[data-inc-generated="suffix"]',
      () => document.createElement("span")
    );
    generated.setAttribute("data-inc-suffix", "true");
    generated.textContent = this.suffix;
    return generated;
  }
  __resolveControl() {
    const control = resolveAssignedElement(this, "control", NATIVE_CONTROL_SELECTOR);
    return control instanceof HTMLElement ? control : null;
  }
};
var IncChoiceGroupElement = class extends IncFormsElement {
  static get observedAttributes() {
    return ["type", "legend", "orientation", "inline", "dense", "hint", "error"];
  }
  get legend() {
    return this.getAttribute("legend") || "";
  }
  set legend(value) {
    if (value == null || value === "") {
      this.removeAttribute("legend");
    } else {
      this.setAttribute("legend", String(value));
    }
  }
  get inline() {
    return parseBooleanAttribute(this, "inline");
  }
  set inline(value) {
    reflectBooleanAttribute(this, "inline", value);
  }
  focusFirst() {
    const firstFocusable = this.querySelector(NATIVE_CONTROL_SELECTOR);
    firstFocusable?.focus();
  }
  sync() {
    this.classList.add("inc-form__fieldset");
    withPart(this, "group");
    this.setAttribute("role", "group");
    const legend = this.__resolveLegend();
    const choices = this.__resolveChoices();
    const hint = this.__resolveHint();
    const error = this.__resolveError();
    if (legend) {
      withPart(legend, "legend");
      legend.classList.add("inc-form__legend");
      if (!legend.id) {
        legend.id = nextGeneratedId("legend");
      }
      this.setAttribute("aria-labelledby", legend.id);
    }
    if (choices) {
      withPart(choices, "control");
      choices.classList.add("inc-form__choices");
      toggleClass(choices, "inc-form__choices--inline", this.inline);
    }
    if (hint) {
      withPart(hint, "hint");
      hint.classList.add("inc-form__hint");
    }
    if (error) {
      withPart(error, "error");
      error.classList.add("inc-form__invalid-feedback");
      error.setAttribute("aria-live", "polite");
    }
  }
  __resolveLegend() {
    const explicit = resolveAssignedElement(this, "legend", "legend, [data-inc-choice-legend]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    if (!this.legend) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "legend",
      '[data-inc-generated="legend"]',
      () => document.createElement("legend")
    );
    generated.setAttribute("data-inc-choice-legend", "true");
    generated.textContent = this.legend;
    return generated;
  }
  __resolveChoices() {
    const existing = this.querySelector(".inc-form__choices, [data-inc-choice-items]");
    if (existing instanceof HTMLElement) {
      return existing;
    }
    const slotItems = Array.from(this.children).filter((child) => child instanceof HTMLElement && child.getAttribute("slot") === "item");
    if (!slotItems.length) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "items",
      '[data-inc-generated="items"]',
      () => document.createElement("div")
    );
    generated.setAttribute("data-inc-choice-items", "true");
    for (const node of slotItems) {
      if (!generated.contains(node)) {
        generated.append(node);
      }
    }
    return generated;
  }
  __resolveHint() {
    const explicit = resolveAssignedElement(this, "hint", ".inc-form__hint, [data-inc-choice-hint]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    const hintText = this.getAttribute("hint");
    if (!hintText) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "hint",
      '[data-inc-generated="hint"]',
      () => document.createElement("p")
    );
    generated.setAttribute("data-inc-choice-hint", "true");
    generated.textContent = hintText;
    return generated;
  }
  __resolveError() {
    const explicit = resolveAssignedElement(this, "error", ".inc-form__invalid-feedback, [data-inc-choice-error]");
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    const errorText = this.getAttribute("error");
    if (!errorText) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "error",
      '[data-inc-generated="error"]',
      () => document.createElement("p")
    );
    generated.setAttribute("data-inc-choice-error", "true");
    generated.textContent = errorText;
    return generated;
  }
};
var IncReadonlyFieldElement = class extends IncFormsElement {
  static get observedAttributes() {
    return ["label", "value", "dense"];
  }
  sync() {
    this.classList.add("inc-readonly-field");
    withPart(this, "field");
    const label = this.__resolveLabel();
    const value = this.__resolveValue();
    const meta = resolveAssignedElement(this, "meta", '[slot="meta"], [data-inc-readonly-meta]');
    if (label) {
      withPart(label, "label");
    }
    if (value) {
      withPart(value, "value");
    }
    if (meta) {
      withPart(meta, "meta");
    }
  }
  __resolveLabel() {
    const explicit = resolveAssignedElement(this, "label", '[slot="label"], [data-inc-readonly-label]');
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    const labelText = this.getAttribute("label");
    if (!labelText) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "label",
      '[data-inc-generated="label"]',
      () => document.createElement("span")
    );
    generated.setAttribute("data-inc-readonly-label", "true");
    generated.textContent = labelText;
    return generated;
  }
  __resolveValue() {
    const explicit = resolveAssignedElement(this, "value", '[slot="value"], [data-inc-readonly-value]');
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    const valueText = this.getAttribute("value");
    if (!valueText) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "value",
      '[data-inc-generated="value"]',
      () => document.createElement("span")
    );
    generated.setAttribute("data-inc-readonly-value", "true");
    generated.textContent = valueText;
    return generated;
  }
};
var IncValidationSummaryElement = class extends IncFormsElement {
  static get observedAttributes() {
    return ["title", "count", "live"];
  }
  get title() {
    return this.getAttribute("title") || "";
  }
  set title(value) {
    if (value == null || value === "") {
      this.removeAttribute("title");
    } else {
      this.setAttribute("title", String(value));
    }
  }
  announce(message) {
    const announcement = String(message || "").trim();
    if (!announcement) {
      return;
    }
    const node = ensureGeneratedElement(
      this,
      "announcement",
      '[data-inc-generated="announcement"]',
      () => document.createElement("span")
    );
    node.style.position = "absolute";
    node.style.width = "1px";
    node.style.height = "1px";
    node.style.overflow = "hidden";
    node.style.clip = "rect(0 0 0 0)";
    node.style.clipPath = "inset(50%)";
    node.style.whiteSpace = "nowrap";
    node.setAttribute("aria-live", this.getAttribute("live") || "polite");
    node.textContent = announcement;
  }
  sync() {
    this.classList.add("inc-form__error-summary");
    withPart(this, "summary");
    const title = this.__resolveTitle();
    const list = this.__resolveList();
    const liveMode = this.getAttribute("live");
    if (liveMode) {
      this.setAttribute("aria-live", liveMode);
    } else {
      this.removeAttribute("aria-live");
    }
    if (title) {
      withPart(title, "title");
      title.classList.add("inc-form__error-summary-title");
      const count = this.getAttribute("count");
      if (count && title.getAttribute("data-inc-generated") === "title") {
        const numeric = Number.parseInt(count, 10);
        if (Number.isFinite(numeric) && numeric >= 0) {
          title.textContent = numeric === 1 ? "There is 1 issue to fix" : `There are ${numeric} issues to fix`;
        }
      }
    }
    if (list) {
      withPart(list, "list");
      list.classList.add("inc-form__error-summary-list");
      for (const item of list.children) {
        withPart(item, "item");
      }
    }
  }
  __resolveTitle() {
    const explicit = resolveAssignedElement(
      this,
      "title",
      ".inc-form__error-summary-title, [data-inc-validation-title]"
    );
    if (explicit instanceof HTMLElement) {
      return explicit;
    }
    const titleText = this.title;
    if (!titleText && !this.hasAttribute("count")) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "title",
      '[data-inc-generated="title"]',
      () => document.createElement("h3")
    );
    generated.setAttribute("data-inc-validation-title", "true");
    if (titleText) {
      generated.textContent = titleText;
    }
    return generated;
  }
  __resolveList() {
    const existing = this.querySelector(".inc-form__error-summary-list, [data-inc-validation-list]");
    if (existing instanceof HTMLElement) {
      return existing;
    }
    const slotItems = Array.from(this.children).filter((child) => child instanceof HTMLElement && child.getAttribute("slot") === "item");
    if (!slotItems.length) {
      return null;
    }
    const generated = ensureGeneratedElement(
      this,
      "list",
      '[data-inc-generated="list"]',
      () => document.createElement("ul")
    );
    generated.setAttribute("data-inc-validation-list", "true");
    for (const item of slotItems) {
      if (generated.contains(item)) {
        continue;
      }
      if (item.tagName !== "LI") {
        const wrapped = document.createElement("li");
        wrapped.append(item);
        generated.append(wrapped);
        continue;
      }
      generated.append(item);
    }
    return generated;
  }
};
var FORM_COMPONENTS = [
  ["inc-field", IncFieldElement],
  ["inc-input-group", IncInputGroupElement],
  ["inc-choice-group", IncChoiceGroupElement],
  ["inc-readonly-field", IncReadonlyFieldElement],
  ["inc-validation-summary", IncValidationSummaryElement]
];
function registerFormsComponents(registry = globalThis.customElements) {
  if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
    return [];
  }
  const registered = [];
  for (const [name, ctor] of FORM_COMPONENTS) {
    if (!registry.get(name)) {
      registry.define(name, ctor);
      registered.push(name);
    }
  }
  return registered;
}
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    registerFormsComponents,
    IncFieldElement,
    IncInputGroupElement,
    IncChoiceGroupElement,
    IncReadonlyFieldElement,
    IncValidationSummaryElement
  };
}
if (typeof globalThis !== "undefined") {
  const namespace2 = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  namespace2.forms = Object.assign({}, namespace2.forms, {
    register: registerFormsComponents,
    components: {
      IncFieldElement,
      IncInputGroupElement,
      IncChoiceGroupElement,
      IncReadonlyFieldElement,
      IncValidationSummaryElement
    }
  });
}

// node_modules/lucide/dist/esm/defaultAttributes.mjs
var defaultAttributes = {
  xmlns: "http://www.w3.org/2000/svg",
  width: 24,
  height: 24,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  "stroke-width": 2,
  "stroke-linecap": "round",
  "stroke-linejoin": "round"
};

// node_modules/lucide/dist/esm/createElement.mjs
var createSVGElement = ([tag, attrs, children]) => {
  const element = document.createElementNS("http://www.w3.org/2000/svg", tag);
  Object.keys(attrs).forEach((name) => {
    element.setAttribute(name, String(attrs[name]));
  });
  if (children?.length) {
    children.forEach((child) => {
      const childElement = createSVGElement(child);
      element.appendChild(childElement);
    });
  }
  return element;
};
var createElement = (iconNode, customAttrs = {}) => {
  const tag = "svg";
  const attrs = {
    ...defaultAttributes,
    ...customAttrs
  };
  return createSVGElement([tag, attrs, iconNode]);
};

// node_modules/lucide/dist/esm/icons/circle-check.mjs
var CircleCheck = [
  ["circle", { cx: "12", cy: "12", r: "10" }],
  ["path", { d: "m9 12 2 2 4-4" }]
];

// node_modules/lucide/dist/esm/icons/circle-question-mark.mjs
var CircleQuestionMark = [
  ["circle", { cx: "12", cy: "12", r: "10" }],
  ["path", { d: "M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" }],
  ["path", { d: "M12 17h.01" }]
];

// node_modules/lucide/dist/esm/icons/circle-x.mjs
var CircleX = [
  ["circle", { cx: "12", cy: "12", r: "10" }],
  ["path", { d: "m15 9-6 6" }],
  ["path", { d: "m9 9 6 6" }]
];

// node_modules/lucide/dist/esm/icons/download.mjs
var Download = [
  ["path", { d: "M12 15V3" }],
  ["path", { d: "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" }],
  ["path", { d: "m7 10 5 5 5-5" }]
];

// node_modules/lucide/dist/esm/icons/external-link.mjs
var ExternalLink = [
  ["path", { d: "M15 3h6v6" }],
  ["path", { d: "M10 14 21 3" }],
  ["path", { d: "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" }]
];

// node_modules/lucide/dist/esm/icons/file-text.mjs
var FileText = [
  [
    "path",
    {
      d: "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z"
    }
  ],
  ["path", { d: "M14 2v5a1 1 0 0 0 1 1h5" }],
  ["path", { d: "M10 9H8" }],
  ["path", { d: "M16 13H8" }],
  ["path", { d: "M16 17H8" }]
];

// node_modules/lucide/dist/esm/icons/folder-plus.mjs
var FolderPlus = [
  ["path", { d: "M12 10v6" }],
  ["path", { d: "M9 13h6" }],
  [
    "path",
    {
      d: "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"
    }
  ]
];

// node_modules/lucide/dist/esm/icons/info.mjs
var Info = [
  ["circle", { cx: "12", cy: "12", r: "10" }],
  ["path", { d: "M12 16v-4" }],
  ["path", { d: "M12 8h.01" }]
];

// node_modules/lucide/dist/esm/icons/lock.mjs
var Lock = [
  ["rect", { width: "18", height: "11", x: "3", y: "11", rx: "2", ry: "2" }],
  ["path", { d: "M7 11V7a5 5 0 0 1 10 0v4" }]
];

// node_modules/lucide/dist/esm/icons/pause.mjs
var Pause = [
  ["rect", { x: "14", y: "3", width: "5", height: "18", rx: "1" }],
  ["rect", { x: "5", y: "3", width: "5", height: "18", rx: "1" }]
];

// node_modules/lucide/dist/esm/icons/play.mjs
var Play = [
  [
    "path",
    { d: "M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z" }
  ]
];

// node_modules/lucide/dist/esm/icons/refresh-cw.mjs
var RefreshCw = [
  ["path", { d: "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" }],
  ["path", { d: "M21 3v5h-5" }],
  ["path", { d: "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" }],
  ["path", { d: "M8 16H3v5" }]
];

// node_modules/lucide/dist/esm/icons/search-x.mjs
var SearchX = [
  ["path", { d: "m13.5 8.5-5 5" }],
  ["path", { d: "m8.5 8.5 5 5" }],
  ["circle", { cx: "11", cy: "11", r: "8" }],
  ["path", { d: "m21 21-4.3-4.3" }]
];

// node_modules/lucide/dist/esm/icons/settings.mjs
var Settings = [
  [
    "path",
    {
      d: "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915"
    }
  ],
  ["circle", { cx: "12", cy: "12", r: "3" }]
];

// node_modules/lucide/dist/esm/icons/shield-check.mjs
var ShieldCheck = [
  [
    "path",
    {
      d: "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"
    }
  ],
  ["path", { d: "m9 12 2 2 4-4" }]
];

// node_modules/lucide/dist/esm/icons/triangle-alert.mjs
var TriangleAlert = [
  ["path", { d: "m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3" }],
  ["path", { d: "M12 9v4" }],
  ["path", { d: "M12 17h.01" }]
];

// node_modules/lucide/dist/esm/icons/upload.mjs
var Upload = [
  ["path", { d: "M12 3v12" }],
  ["path", { d: "m17 8-5-5-5 5" }],
  ["path", { d: "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" }]
];

// src/icons/index.js
var ICON_NODES = Object.freeze({
  info: Info,
  help: CircleQuestionMark,
  success: CircleCheck,
  warning: TriangleAlert,
  error: CircleX,
  upload: Upload,
  document: FileText,
  download: Download,
  settings: Settings,
  "external-link": ExternalLink,
  empty: FolderPlus,
  "no-results": SearchX,
  loading: RefreshCw,
  lock: Lock,
  pause: Pause,
  play: Play,
  permission: ShieldCheck
});
var ICON_NAMES = Object.freeze(Object.keys(ICON_NODES));
var DEFAULT_SIZE = 16;
function getNamespace() {
  if (typeof globalThis === "undefined") {
    return null;
  }
  const root = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  const icons = root.icons || (root.icons = {});
  if (!icons.names) {
    icons.names = ICON_NAMES;
  }
  if (!icons.defaultRenderer) {
    icons.defaultRenderer = renderDefaultIcon;
  }
  if (!icons.render) {
    icons.render = renderIncIcon;
  }
  if (!icons.setRenderer) {
    icons.setRenderer = setIconRenderer;
  }
  return icons;
}
function normalizeIconName(name) {
  return String(name || "").trim().toLowerCase().replace(/[_\s]+/g, "-");
}
function normalizeSize(value) {
  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_SIZE;
}
function buildIconAttributes(name, options = {}) {
  const size = normalizeSize(options.size);
  const className = options.className || "inc-icon";
  const attrs = {
    width: size,
    height: size,
    "data-inc-icon": name,
    class: className,
    focusable: "false"
  };
  if (options.decorative !== false) {
    attrs["aria-hidden"] = "true";
  } else {
    attrs.role = "img";
    attrs["aria-label"] = options.label || name;
  }
  return attrs;
}
function renderDefaultIcon(name, options = {}) {
  const normalizedName = normalizeIconName(name);
  const iconNode = ICON_NODES[normalizedName] || ICON_NODES.info;
  if (typeof document === "undefined") {
    return "";
  }
  return createElement(iconNode, buildIconAttributes(normalizedName, options));
}
function coerceIconResult(result) {
  if (!result || typeof document === "undefined") {
    return null;
  }
  if (result instanceof Node) {
    return result;
  }
  if (typeof result === "string") {
    const template = document.createElement("template");
    template.innerHTML = result.trim();
    return template.content.firstElementChild || null;
  }
  return null;
}
function getIconRenderer() {
  const namespace2 = getNamespace();
  return typeof namespace2?.renderer === "function" ? namespace2.renderer : renderDefaultIcon;
}
function setIconRenderer(renderer) {
  const namespace2 = getNamespace();
  if (!namespace2) {
    return null;
  }
  if (renderer == null) {
    delete namespace2.renderer;
    return null;
  }
  if (typeof renderer !== "function") {
    throw new TypeError("Inc icon renderer must be a function.");
  }
  namespace2.renderer = renderer;
  return renderer;
}
function renderIncIcon(name, options = {}) {
  const normalizedName = normalizeIconName(name) || "info";
  const renderer = getIconRenderer();
  const rendered = renderer(normalizedName, options);
  const icon = coerceIconResult(rendered) || coerceIconResult(renderDefaultIcon(normalizedName, options));
  if (icon instanceof Element && options.decorative !== false) {
    icon.setAttribute("aria-hidden", "true");
    icon.removeAttribute("aria-label");
    icon.removeAttribute("role");
  }
  return icon;
}
function replaceIconContents(container, name, options = {}) {
  if (!(container instanceof Element)) {
    return null;
  }
  container.replaceChildren();
  const icon = renderIncIcon(name, options);
  if (icon) {
    icon.setAttribute("data-inc-generated-icon", "true");
    icon.setAttribute("data-inc-icon-upgraded", "true");
    container.append(icon);
  }
  return icon;
}
getNamespace();

// src/web-components/components/feedback.js
var THEME_MODES = ["light", "dark", "system"];
var DEFAULT_THEME_STORAGE_KEY = "inc-theme-mode";
var BADGE_TONES = /* @__PURE__ */ new Set(["primary", "secondary", "success", "danger", "warning", "info"]);
var SPINNER_VARIANTS = /* @__PURE__ */ new Set(["border", "grow"]);
var ICON_NAME_SET = new Set(ICON_NAMES);
var STATE_ICON_BY_VARIANT = /* @__PURE__ */ new Map([
  ["empty", "empty"],
  ["results", "no-results"],
  ["loading", "loading"],
  ["error", "error"],
  ["danger", "error"],
  ["warning", "warning"],
  ["success", "success"],
  ["info", "info"]
]);
var STATE_ICON_BY_STATUS = /* @__PURE__ */ new Map([
  ["+", "empty"],
  ["?", "no-results"],
  ["!", "error"],
  ["...", "loading"]
]);
var HostElement3 = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
var themeSubscribers = /* @__PURE__ */ new Set();
var themeRuntimeInitialized = false;
var themeMode = "system";
var themeResolved = "light";
var themeStorageKey = DEFAULT_THEME_STORAGE_KEY;
var themeMediaQuery = null;
var themeStorageListenerBound = false;
var themeMediaListenerBound = false;
function isThemeMode(value) {
  return THEME_MODES.includes(value);
}
function toBooleanAttribute(value) {
  if (value === null || value === void 0) {
    return false;
  }
  if (value === "" || value === "true") {
    return true;
  }
  return value !== "false";
}
function toPositiveInt(value) {
  const parsed = Number.parseInt(value || "", 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}
function normalizeToken(value) {
  return String(value ?? "").trim().toLowerCase();
}
function resolveStateIconName(icon, status, variant) {
  const explicitIcon = normalizeIconName(icon);
  if (explicitIcon) {
    return explicitIcon;
  }
  const normalizedStatus = normalizeIconName(status);
  if (STATE_ICON_BY_STATUS.has(String(status || "").trim())) {
    return STATE_ICON_BY_STATUS.get(String(status || "").trim());
  }
  if (ICON_NAME_SET.has(normalizedStatus)) {
    return normalizedStatus;
  }
  return STATE_ICON_BY_VARIANT.get(normalizeToken(variant)) || "info";
}
function renderDecorativeIcon(container, name, size = 18) {
  replaceIconContents(container, name, {
    className: "inc-icon",
    decorative: true,
    size
  });
  container.hidden = false;
}
function getSystemTheme() {
  if (!window.matchMedia) {
    return "light";
  }
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}
function resolveTheme(mode) {
  return mode === "system" ? getSystemTheme() : mode;
}
function getRootThemeMode() {
  const root = document.documentElement;
  return root.getAttribute("data-inc-theme-mode") || root.dataset.incThemeMode || root.getAttribute("data-bs-theme") || "system";
}
function getStoredThemeMode(storageKey = DEFAULT_THEME_STORAGE_KEY) {
  try {
    const stored = window.localStorage.getItem(storageKey);
    return isThemeMode(stored) ? stored : null;
  } catch {
    return null;
  }
}
function persistThemeMode(mode, storageKey = DEFAULT_THEME_STORAGE_KEY) {
  try {
    if (mode === "system") {
      window.localStorage.removeItem(storageKey);
      return;
    }
    window.localStorage.setItem(storageKey, mode);
  } catch {
  }
}
function applyThemeMode(mode, options = {}) {
  const nextMode = isThemeMode(mode) ? mode : "system";
  const resolved = resolveTheme(nextMode);
  const root = document.documentElement;
  const storageKey = options.storageKey || themeStorageKey || DEFAULT_THEME_STORAGE_KEY;
  themeMode = nextMode;
  themeResolved = resolved;
  themeStorageKey = storageKey;
  root.setAttribute("data-inc-theme-mode", nextMode);
  root.setAttribute("data-bs-theme", resolved);
  root.style.colorScheme = resolved;
  root.dataset.incThemeModeState = nextMode;
  root.dataset.incThemeResolved = resolved;
  if (options.persist !== false) {
    persistThemeMode(nextMode, storageKey);
  }
  if (options.dispatch !== false) {
    const event = new CustomEvent("inc-theme-change", {
      bubbles: true,
      composed: true,
      detail: {
        mode: nextMode,
        resolved
      }
    });
    root.dispatchEvent(event);
  }
  themeSubscribers.forEach((notify) => {
    try {
      notify({ mode: nextMode, resolved });
    } catch {
    }
  });
  return { mode: nextMode, resolved };
}
function initializeThemeRuntime(storageKey = DEFAULT_THEME_STORAGE_KEY) {
  themeStorageKey = storageKey || DEFAULT_THEME_STORAGE_KEY;
  if (!themeRuntimeInitialized) {
    themeRuntimeInitialized = true;
    const initialMode = getStoredThemeMode(themeStorageKey) || getRootThemeMode();
    applyThemeMode(initialMode, {
      dispatch: false,
      persist: false,
      storageKey: themeStorageKey
    });
    if (!themeStorageListenerBound) {
      themeStorageListenerBound = true;
      window.addEventListener("storage", (event) => {
        if (event.key !== themeStorageKey) {
          return;
        }
        const storedMode = getStoredThemeMode(themeStorageKey) || getRootThemeMode();
        applyThemeMode(storedMode, {
          dispatch: false,
          persist: false,
          storageKey: themeStorageKey
        });
      });
    }
    if (!themeMediaListenerBound && window.matchMedia) {
      themeMediaListenerBound = true;
      themeMediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
      const onMediaChange = () => {
        if (themeMode === "system") {
          applyThemeMode("system", {
            dispatch: true,
            persist: false,
            storageKey: themeStorageKey
          });
        }
      };
      if (typeof themeMediaQuery.addEventListener === "function") {
        themeMediaQuery.addEventListener("change", onMediaChange);
      } else if (typeof themeMediaQuery.addListener === "function") {
        themeMediaQuery.addListener(onMediaChange);
      }
    }
  }
  return {
    mode: themeMode,
    resolved: themeResolved
  };
}
function subscribeThemeState(handler) {
  themeSubscribers.add(handler);
  handler({ mode: themeMode, resolved: themeResolved });
  return () => themeSubscribers.delete(handler);
}
function formatRemaining(totalSeconds) {
  if (totalSeconds < 60) {
    return `${totalSeconds}s`;
  }
  const minutes = Math.floor(totalSeconds / 60);
  const seconds2 = totalSeconds % 60;
  return `${minutes}m ${seconds2}s`;
}
var IncStatePanel = class extends HostElement3 {
  static observedAttributes = ["tone", "variant", "title", "body", "status", "icon", "open"];
  #fallback = null;
  #appliedVariantClass = "";
  connectedCallback() {
    this.classList.add("inc-state-panel");
    this.setAttribute("part", "panel");
    this.#ensureFallback();
    this.#syncFromAttributes();
    this.#dispatchSlotChange();
  }
  attributeChangedCallback() {
    if (!this.isConnected) {
      return;
    }
    this.#syncFromAttributes();
  }
  #ensureFallback() {
    if (this.childElementCount > 0) {
      this.#fallback = null;
      return;
    }
    const head = document.createElement("div");
    const icon = document.createElement("span");
    const title = document.createElement("h2");
    const body = document.createElement("p");
    const actions = document.createElement("div");
    head.className = "inc-state-panel__head";
    icon.className = "inc-state-panel__icon";
    title.className = "inc-state-panel__title";
    body.className = "inc-state-panel__body";
    actions.className = "inc-state-panel__actions";
    icon.setAttribute("part", "icon");
    icon.setAttribute("aria-hidden", "true");
    title.setAttribute("part", "title");
    body.setAttribute("part", "body");
    actions.setAttribute("part", "actions");
    head.append(icon, title);
    this.append(head, body, actions);
    this.#fallback = { icon, title, body, actions };
  }
  #syncFromAttributes() {
    const nextVariant = this.getAttribute("variant") || this.getAttribute("tone") || "";
    if (this.#appliedVariantClass) {
      this.classList.remove(this.#appliedVariantClass);
      this.#appliedVariantClass = "";
    }
    if (nextVariant) {
      this.#appliedVariantClass = `inc-state-panel--${nextVariant}`;
      this.classList.add(this.#appliedVariantClass);
    }
    const isOpen = this.getAttribute("open") === null ? true : toBooleanAttribute(this.getAttribute("open"));
    this.toggleAttribute("hidden", !isOpen);
    this.setAttribute("aria-hidden", isOpen ? "false" : "true");
    if (!this.#fallback) {
      return;
    }
    const title = this.getAttribute("title") || "";
    const body = this.getAttribute("body") || "";
    const status = this.getAttribute("status") || "";
    const iconName = resolveStateIconName(this.getAttribute("icon"), status, nextVariant);
    this.#fallback.title.textContent = title;
    this.#fallback.body.textContent = body;
    if (iconName === "none") {
      this.#fallback.icon.replaceChildren();
      this.#fallback.icon.hidden = true;
    } else if (ICON_NAME_SET.has(iconName)) {
      renderDecorativeIcon(this.#fallback.icon, iconName, 22);
    } else {
      this.#fallback.icon.textContent = status;
      this.#fallback.icon.hidden = !status;
    }
    this.#fallback.actions.hidden = true;
  }
  #dispatchSlotChange() {
    this.dispatchEvent(new Event("slotchange", { bubbles: true, composed: true }));
  }
};
var IncBadgeElement = class extends HostElement3 {
  static observedAttributes = ["tone", "variant", "pill"];
  connectedCallback() {
    this.classList.add("inc-badge");
    this.#sync();
  }
  attributeChangedCallback() {
    this.#sync();
  }
  get tone() {
    return this.getAttribute("tone") || this.getAttribute("variant") || "";
  }
  set tone(value) {
    if (value == null || value === "") {
      this.removeAttribute("tone");
      return;
    }
    this.setAttribute("tone", String(value));
  }
  get pill() {
    return this.hasAttribute("pill");
  }
  set pill(value) {
    if (value) {
      this.setAttribute("pill", "");
    } else {
      this.removeAttribute("pill");
    }
  }
  #sync() {
    this.classList.add("inc-badge");
    BADGE_TONES.forEach((tone2) => this.classList.remove(`inc-badge--${tone2}`));
    this.classList.remove("inc-badge--pill");
    const tone = normalizeToken(this.tone);
    if (BADGE_TONES.has(tone)) {
      this.classList.add(`inc-badge--${tone}`);
    }
    if (this.pill) {
      this.classList.add("inc-badge--pill");
    }
  }
};
var IncLiveRegion = class extends HostElement3 {
  static observedAttributes = ["politeness", "atomic", "busy"];
  #announceNode = null;
  connectedCallback() {
    this.classList.add("inc-live-region");
    this.setAttribute("part", "region");
    this.#ensureNode();
    this.#syncA11y();
  }
  attributeChangedCallback() {
    if (!this.isConnected) {
      return;
    }
    this.#syncA11y();
  }
  announce(message) {
    this.#ensureNode();
    const text = message == null ? "" : String(message);
    this.#announceNode.textContent = "";
    const apply = () => {
      this.#announceNode.textContent = text;
    };
    if (window.requestAnimationFrame) {
      window.requestAnimationFrame(apply);
      return;
    }
    window.setTimeout(apply, 0);
  }
  #ensureNode() {
    if (this.#announceNode) {
      return;
    }
    this.#announceNode = document.createElement("span");
    this.#announceNode.className = "inc-live-region__message";
    this.#announceNode.setAttribute("part", "region");
    if (!this.firstElementChild) {
      this.append(this.#announceNode);
      return;
    }
    const existing = this.querySelector(".inc-live-region__message");
    if (existing instanceof HTMLElement) {
      this.#announceNode = existing;
      return;
    }
    this.append(this.#announceNode);
  }
  #syncA11y() {
    const politeness = this.getAttribute("politeness") || "polite";
    const isAtomic = this.getAttribute("atomic") === null ? true : toBooleanAttribute(this.getAttribute("atomic"));
    const isBusy = toBooleanAttribute(this.getAttribute("busy"));
    this.setAttribute("role", politeness === "assertive" ? "alert" : "status");
    this.setAttribute("aria-live", politeness);
    this.setAttribute("aria-atomic", isAtomic ? "true" : "false");
    this.setAttribute("aria-busy", isBusy ? "true" : "false");
  }
};
var IncSpinnerElement = class extends HostElement3 {
  static observedAttributes = ["variant", "tone", "size", "label"];
  connectedCallback() {
    this.#sync();
  }
  attributeChangedCallback() {
    this.#sync();
  }
  get variant() {
    return this.getAttribute("variant") || "";
  }
  set variant(value) {
    if (value == null || value === "") {
      this.removeAttribute("variant");
      return;
    }
    this.setAttribute("variant", String(value));
  }
  get tone() {
    return this.getAttribute("tone") || "";
  }
  set tone(value) {
    if (value == null || value === "") {
      this.removeAttribute("tone");
      return;
    }
    this.setAttribute("tone", String(value));
  }
  get size() {
    return this.getAttribute("size") || "";
  }
  set size(value) {
    if (value == null || value === "") {
      this.removeAttribute("size");
      return;
    }
    this.setAttribute("size", String(value));
  }
  get label() {
    return this.getAttribute("label") || "";
  }
  set label(value) {
    if (value == null || value === "") {
      this.removeAttribute("label");
      return;
    }
    this.setAttribute("label", String(value));
  }
  #sync() {
    this.classList.add("inc-spinner");
    SPINNER_VARIANTS.forEach((variant2) => {
      this.classList.remove(`inc-spinner--${variant2}`);
      this.classList.remove(`inc-spinner--${variant2}--sm`);
      BADGE_TONES.forEach((tone2) => this.classList.remove(`inc-spinner--${variant2}--${tone2}`));
    });
    const variant = normalizeToken(this.variant) || "border";
    const resolvedVariant = SPINNER_VARIANTS.has(variant) ? variant : "border";
    this.classList.add(`inc-spinner--${resolvedVariant}`);
    if (normalizeToken(this.size) === "sm") {
      this.classList.add(`inc-spinner--${resolvedVariant}--sm`);
    }
    const tone = normalizeToken(this.tone);
    if (BADGE_TONES.has(tone)) {
      this.classList.add(`inc-spinner--${resolvedVariant}--${tone}`);
    }
    const label = this.label.trim();
    if (label) {
      this.removeAttribute("aria-hidden");
      this.setAttribute("role", "status");
      this.setAttribute("aria-live", "polite");
      this.setAttribute("aria-label", label);
      return;
    }
    this.setAttribute("aria-hidden", "true");
    this.removeAttribute("role");
    this.removeAttribute("aria-live");
    this.removeAttribute("aria-label");
  }
};
var IncAutoRefresh = class extends HostElement3 {
  static observedAttributes = [
    "seconds",
    "label",
    "loading-label",
    "paused-label",
    "pause-action-label",
    "resume-action-label",
    "paused"
  ];
  #parts = null;
  #timeoutId = 0;
  #visibilityHandler = null;
  #isPaused = false;
  #isLoading = false;
  #deadline = 0;
  #remainingMs = 0;
  connectedCallback() {
    this.classList.add("inc-auto-refresh");
    this.#ensureMarkup();
    this.#bindHandlers();
    this.#start();
  }
  disconnectedCallback() {
    this.#stop();
    if (this.#visibilityHandler) {
      document.removeEventListener("visibilitychange", this.#visibilityHandler);
      this.#visibilityHandler = null;
    }
  }
  attributeChangedCallback(name) {
    if (!this.isConnected || !this.#parts) {
      return;
    }
    if (name === "paused") {
      if (toBooleanAttribute(this.getAttribute("paused"))) {
        this.pause();
      } else {
        this.resume();
      }
      return;
    }
    if (name === "seconds") {
      this.#start();
      return;
    }
    this.#render();
  }
  pause() {
    if (this.#isLoading || this.#isPaused) {
      return;
    }
    this.#isPaused = true;
    this.#remainingMs = Math.max(this.#deadline - Date.now(), 0);
    this.#stop();
    this.setAttribute("paused", "");
    this.#render();
    this.dispatchEvent(new CustomEvent("pause", { bubbles: true, composed: true }));
    this.#emitStateChange("paused");
  }
  resume() {
    if (this.#isLoading || !this.#isPaused) {
      return;
    }
    this.#isPaused = false;
    this.removeAttribute("paused");
    this.#deadline = Date.now() + Math.max(this.#remainingMs, 1e3);
    this.#remainingMs = 0;
    this.#scheduleTick();
    this.dispatchEvent(new CustomEvent("resume", { bubbles: true, composed: true }));
    this.#emitStateChange("running");
  }
  toggle() {
    if (this.#isPaused) {
      this.resume();
      return;
    }
    this.pause();
  }
  refresh() {
    if (this.#isLoading) {
      return;
    }
    this.#isLoading = true;
    this.#stop();
    this.#render();
    this.#emitStateChange("loading");
    const refreshEvent = new CustomEvent("refresh", {
      bubbles: true,
      composed: true,
      cancelable: true,
      detail: this.#buildState()
    });
    this.dispatchEvent(refreshEvent);
    if (!refreshEvent.defaultPrevented) {
      const deferToPaint = window.requestAnimationFrame ? window.requestAnimationFrame.bind(window) : (callback) => window.setTimeout(callback, 16);
      deferToPaint(() => {
        window.setTimeout(() => {
          window.location.reload();
        }, 120);
      });
    }
  }
  #bindHandlers() {
    if (!this.#parts?.toggle) {
      return;
    }
    if (!this.#parts.toggle.dataset.incWcBound) {
      this.#parts.toggle.dataset.incWcBound = "true";
      this.#parts.toggle.addEventListener("click", (event) => {
        event.preventDefault();
        this.toggle();
      });
    }
    if (!this.#visibilityHandler) {
      this.#visibilityHandler = () => {
        if (document.hidden || this.#isPaused || this.#isLoading) {
          return;
        }
        if (this.#deadline - Date.now() <= 0) {
          this.refresh();
          return;
        }
        this.#scheduleTick();
      };
      document.addEventListener("visibilitychange", this.#visibilityHandler);
    }
  }
  #ensureMarkup() {
    if (this.querySelector(".inc-auto-refresh__countdown")) {
      this.#parts = this.#getParts();
      return;
    }
    this.innerHTML = `
<button type="button" class="inc-auto-refresh__toggle inc-btn inc-btn--outline-secondary inc-btn--micro" part="toggle">
  <span class="inc-auto-refresh__toggle-icon" aria-hidden="true"></span>
  <span class="inc-auto-refresh__toggle-text"></span>
</button>
<span class="inc-auto-refresh__countdown" part="countdown">
  <span class="inc-auto-refresh__label" part="label"></span>
  <span class="inc-auto-refresh__value" part="value"></span>
</span>
<span class="inc-auto-refresh__status" part="status" hidden>
  <span class="inc-auto-refresh__status-text"></span>
</span>
        `.trim();
    this.#parts = this.#getParts();
  }
  #getParts() {
    return {
      countdown: this.querySelector(".inc-auto-refresh__countdown"),
      label: this.querySelector(".inc-auto-refresh__label"),
      value: this.querySelector(".inc-auto-refresh__value"),
      status: this.querySelector(".inc-auto-refresh__status"),
      statusText: this.querySelector(".inc-auto-refresh__status-text"),
      toggle: this.querySelector(".inc-auto-refresh__toggle"),
      toggleIcon: this.querySelector(".inc-auto-refresh__toggle-icon"),
      toggleText: this.querySelector(".inc-auto-refresh__toggle-text")
    };
  }
  #start() {
    const refreshSeconds = toPositiveInt(this.getAttribute("seconds"));
    this.#stop();
    this.#isLoading = false;
    this.#isPaused = toBooleanAttribute(this.getAttribute("paused"));
    if (!refreshSeconds) {
      this.#render();
      return;
    }
    this.#deadline = Date.now() + refreshSeconds * 1e3;
    this.#remainingMs = refreshSeconds * 1e3;
    if (this.#isPaused) {
      this.#render();
      return;
    }
    this.#scheduleTick();
  }
  #scheduleTick() {
    if (this.#isPaused || this.#isLoading) {
      return;
    }
    this.#stop();
    const remainingMs = this.#deadline - Date.now();
    if (remainingMs <= 0) {
      this.refresh();
      return;
    }
    const remainingSeconds = Math.ceil(remainingMs / 1e3);
    this.#remainingMs = remainingMs;
    this.#renderCountdown(remainingSeconds);
    this.dispatchEvent(new CustomEvent("tick", {
      bubbles: true,
      composed: true,
      detail: this.#buildState()
    }));
    const nextDelay = remainingMs % 1e3 || 1e3;
    this.#timeoutId = window.setTimeout(() => {
      this.#scheduleTick();
    }, nextDelay);
  }
  #render() {
    if (this.#isLoading) {
      this.#renderLoading();
      return;
    }
    const fallbackSeconds = Math.max(1, Math.ceil(this.#remainingMs / 1e3));
    if (this.#isPaused) {
      this.#renderPaused(fallbackSeconds);
      return;
    }
    this.#renderCountdown(fallbackSeconds);
  }
  #renderCountdown(seconds2) {
    const label = this.getAttribute("label") || "Refresh in";
    if (this.#parts.label) {
      this.#parts.label.textContent = label;
    }
    if (this.#parts.value) {
      this.#parts.value.textContent = formatRemaining(seconds2);
    }
    this.classList.remove("is-paused");
    this.classList.remove("is-loading");
    this.setAttribute("aria-busy", "false");
    if (this.#parts.countdown) {
      this.#parts.countdown.hidden = false;
    }
    if (this.#parts.status) {
      this.#parts.status.hidden = true;
    }
    this.#updateToggle();
  }
  #renderPaused(seconds2) {
    const label = this.getAttribute("paused-label") || "Paused at";
    if (this.#parts.label) {
      this.#parts.label.textContent = label;
    }
    if (this.#parts.value) {
      this.#parts.value.textContent = formatRemaining(seconds2);
    }
    this.classList.add("is-paused");
    this.classList.remove("is-loading");
    this.setAttribute("aria-busy", "false");
    if (this.#parts.countdown) {
      this.#parts.countdown.hidden = false;
    }
    if (this.#parts.status) {
      this.#parts.status.hidden = true;
    }
    this.#updateToggle();
  }
  #renderLoading() {
    const loadingLabel = this.getAttribute("loading-label") || "Refreshing";
    this.classList.remove("is-paused");
    this.classList.add("is-loading");
    this.setAttribute("aria-busy", "true");
    if (this.#parts.countdown) {
      this.#parts.countdown.hidden = true;
    }
    if (this.#parts.statusText) {
      this.#parts.statusText.textContent = loadingLabel;
    }
    if (this.#parts.status) {
      this.#parts.status.hidden = false;
    }
    this.#updateToggle();
  }
  #updateToggle() {
    if (!(this.#parts.toggle instanceof HTMLElement)) {
      return;
    }
    const pauseLabel = this.getAttribute("pause-action-label") || "Pause";
    const resumeLabel = this.getAttribute("resume-action-label") || "Resume";
    const actionLabel = this.#isPaused ? resumeLabel : pauseLabel;
    this.#parts.toggle.disabled = this.#isLoading;
    this.#parts.toggle.setAttribute("aria-pressed", this.#isPaused ? "true" : "false");
    this.#parts.toggle.setAttribute("aria-label", actionLabel);
    if (this.#parts.toggleText) {
      this.#parts.toggleText.textContent = actionLabel;
    }
    if (this.#parts.toggleIcon instanceof HTMLElement) {
      renderDecorativeIcon(this.#parts.toggleIcon, this.#isPaused ? "play" : "pause", 16);
    }
  }
  #stop() {
    if (this.#timeoutId) {
      window.clearTimeout(this.#timeoutId);
      this.#timeoutId = 0;
    }
  }
  #buildState() {
    return {
      paused: this.#isPaused,
      loading: this.#isLoading,
      remainingSeconds: Math.max(0, Math.ceil(this.#remainingMs / 1e3))
    };
  }
  #emitStateChange(status) {
    this.dispatchEvent(new CustomEvent("statechange", {
      bubbles: true,
      composed: true,
      detail: {
        status,
        ...this.#buildState()
      }
    }));
  }
};
var IncThemeSwitcher = class extends HostElement3 {
  static observedAttributes = ["mode", "variant", "block", "label", "menu-label", "heading", "storage-key"];
  #details = null;
  #summary = null;
  #status = null;
  #panel = null;
  #bound = false;
  #unsubscribe = null;
  #ignoreModeReflection = false;
  connectedCallback() {
    initializeThemeRuntime(this.storageKey);
    this.#ensureMarkup();
    this.#applyVisualConfig();
    this.#bindHandlers();
    this.#subscribeTheme();
    this.#syncModeFromAttribute();
  }
  disconnectedCallback() {
    if (this.#unsubscribe) {
      this.#unsubscribe();
      this.#unsubscribe = null;
    }
  }
  attributeChangedCallback(name) {
    if (!this.isConnected) {
      return;
    }
    if (name === "storage-key") {
      initializeThemeRuntime(this.storageKey);
      return;
    }
    if (name === "mode" && !this.#ignoreModeReflection) {
      this.setMode(this.getAttribute("mode") || "system");
      return;
    }
    this.#applyVisualConfig();
  }
  get storageKey() {
    return this.getAttribute("storage-key") || DEFAULT_THEME_STORAGE_KEY;
  }
  getMode() {
    return themeMode;
  }
  getResolvedTheme() {
    return themeResolved;
  }
  setMode(mode) {
    initializeThemeRuntime(this.storageKey);
    const next = isThemeMode(mode) ? mode : "system";
    applyThemeMode(next, {
      dispatch: true,
      persist: true,
      storageKey: this.storageKey
    });
  }
  cycleMode() {
    const index = THEME_MODES.indexOf(themeMode);
    const nextMode = THEME_MODES[(index + 1) % THEME_MODES.length];
    this.setMode(nextMode);
  }
  #ensureMarkup() {
    this.classList.add("inc-theme-switcher-host");
    this.#details = this.querySelector("details.inc-theme-switcher");
    if (!(this.#details instanceof HTMLDetailsElement)) {
      this.innerHTML = `
<details class="inc-native-menu inc-theme-switcher">
  <summary class="inc-native-menu__summary inc-theme-switcher__summary" part="summary">
    <span class="inc-theme-switcher__meta">
      <span class="inc-theme-switcher__label" part="label"></span>
      <span class="inc-theme-switcher__status" part="status"></span>
    </span>
  </summary>
  <div class="inc-native-menu__panel inc-theme-switcher__panel" role="menu" part="panel">
    <div class="inc-native-menu__header"></div>
  </div>
</details>
            `.trim();
      this.#details = this.querySelector("details.inc-theme-switcher");
    }
    this.#summary = this.#details?.querySelector("summary");
    this.#status = this.#details?.querySelector(".inc-theme-switcher__status");
    this.#panel = this.#details?.querySelector(".inc-theme-switcher__panel");
    if (!this.#panel) {
      return;
    }
    const header = this.#panel.querySelector(".inc-native-menu__header") || document.createElement("div");
    header.classList.add("inc-native-menu__header");
    header.textContent = this.getAttribute("heading") || "Choose appearance";
    if (!header.parentElement) {
      this.#panel.append(header);
    }
    const existingOptions = this.#panel.querySelectorAll("[data-inc-theme-mode]");
    if (!existingOptions.length) {
      THEME_MODES.forEach((mode) => {
        const option = document.createElement("button");
        const body = document.createElement("span");
        const label = document.createElement("span");
        const detail = document.createElement("span");
        option.type = "button";
        option.className = "inc-theme-switcher__option";
        option.dataset.incThemeMode = mode;
        option.setAttribute("data-inc-theme-mode", mode);
        option.setAttribute("role", "menuitemradio");
        option.setAttribute("part", "option");
        body.className = "inc-theme-switcher__option-body";
        body.setAttribute("part", "option-body");
        label.className = "inc-theme-switcher__option-label";
        label.setAttribute("part", "option-label");
        detail.className = "inc-theme-switcher__option-detail";
        detail.setAttribute("part", "option-detail");
        label.textContent = mode.charAt(0).toUpperCase() + mode.slice(1);
        detail.textContent = mode === "system" ? "Match the device preference automatically." : `Use the ${mode} application palette.`;
        body.append(label, detail);
        option.append(body);
        this.#panel.append(option);
      });
    }
  }
  #applyVisualConfig() {
    if (!(this.#details instanceof HTMLDetailsElement)) {
      return;
    }
    const label = this.getAttribute("label") || "Theme";
    const menuLabel = this.getAttribute("menu-label") || "Theme";
    const heading = this.getAttribute("heading") || "Choose appearance";
    const isBlock = toBooleanAttribute(this.getAttribute("block"));
    const variant = this.getAttribute("variant");
    this.#details.classList.remove("inc-native-menu--navbar", "inc-native-menu--block");
    if (variant === "navbar") {
      this.#details.classList.add("inc-native-menu--navbar");
    }
    if (isBlock) {
      this.#details.classList.add("inc-native-menu--block");
    }
    const labelNode = this.#details.querySelector(".inc-theme-switcher__label");
    const headerNode = this.#details.querySelector(".inc-native-menu__header");
    if (labelNode) {
      labelNode.textContent = label;
    }
    if (this.#panel) {
      this.#panel.setAttribute("aria-label", menuLabel);
    }
    if (headerNode) {
      headerNode.textContent = heading;
    }
  }
  #bindHandlers() {
    if (this.#bound || !this.#details) {
      return;
    }
    this.#bound = true;
    this.#details.addEventListener("click", (event) => {
      const control = event.target.closest("[data-inc-theme-mode]");
      if (!control) {
        return;
      }
      event.preventDefault();
      const mode = control.getAttribute("data-inc-theme-mode");
      this.setMode(mode);
      this.#details.open = false;
      if (this.#summary) {
        this.#summary.focus();
      }
    });
    this.#summary?.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ") {
        return;
      }
      event.preventDefault();
      this.#details.open = !this.#details.open;
      if (!this.#details.open && this.#summary) {
        this.#summary.focus();
      }
    });
    this.#details.addEventListener("keydown", (event) => {
      const control = event.target.closest("[data-inc-theme-mode]");
      if (event.key === "Escape" && this.#details.open) {
        this.#details.open = false;
        if (this.#summary) {
          this.#summary.focus();
        }
        return;
      }
      if (!control || !this.#panel) {
        return;
      }
      const options = Array.from(this.#panel.querySelectorAll("[data-inc-theme-mode]"));
      if (!options.length) {
        return;
      }
      const index = options.indexOf(control);
      if (index < 0) {
        return;
      }
      if (event.key === "ArrowDown" || event.key === "ArrowRight") {
        event.preventDefault();
        options[(index + 1) % options.length]?.focus();
        return;
      }
      if (event.key === "ArrowUp" || event.key === "ArrowLeft") {
        event.preventDefault();
        options[(index - 1 + options.length) % options.length]?.focus();
        return;
      }
      if (event.key === "Home") {
        event.preventDefault();
        options[0]?.focus();
        return;
      }
      if (event.key === "End") {
        event.preventDefault();
        options[options.length - 1]?.focus();
      }
    });
  }
  #subscribeTheme() {
    if (this.#unsubscribe) {
      this.#unsubscribe();
    }
    this.#unsubscribe = subscribeThemeState((state) => {
      this.#syncUI(state.mode, state.resolved);
    });
  }
  #syncModeFromAttribute() {
    const declared = this.getAttribute("mode");
    if (!declared) {
      return;
    }
    this.setMode(declared);
  }
  #syncUI(mode, resolved) {
    if (this.#status) {
      const label = mode === "system" ? `System (${resolved.charAt(0).toUpperCase()}${resolved.slice(1)})` : `${mode.charAt(0).toUpperCase()}${mode.slice(1)}`;
      this.#status.textContent = label;
    }
    if (this.#panel) {
      const options = this.#panel.querySelectorAll("[data-inc-theme-mode]");
      options.forEach((option) => {
        const optionMode = option.getAttribute("data-inc-theme-mode");
        const isSelected = optionMode === mode;
        option.classList.toggle("is-selected", isSelected);
        option.setAttribute("aria-checked", isSelected ? "true" : "false");
        option.setAttribute("aria-pressed", isSelected ? "true" : "false");
      });
    }
    this.dataset.incThemeModeState = mode;
    this.dataset.incThemeResolved = resolved;
    this.#ignoreModeReflection = true;
    this.setAttribute("mode", mode);
    this.#ignoreModeReflection = false;
  }
};
var feedbackDefinitions = [
  ["inc-badge", IncBadgeElement],
  ["inc-state-panel", IncStatePanel],
  ["inc-live-region", IncLiveRegion],
  ["inc-spinner", IncSpinnerElement],
  ["inc-auto-refresh", IncAutoRefresh],
  ["inc-theme-switcher", IncThemeSwitcher]
];
function defineFeedbackComponents(definer = typeof customElements !== "undefined" ? customElements : null) {
  if (!definer || typeof definer.get !== "function" || typeof definer.define !== "function") {
    return;
  }
  feedbackDefinitions.forEach(([tagName, ctor]) => {
    if (!definer.get(tagName)) {
      definer.define(tagName, ctor);
    }
  });
}
if (typeof globalThis !== "undefined") {
  const namespace2 = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  namespace2.feedback = Object.assign({}, namespace2.feedback, {
    defineFeedbackComponents,
    feedbackDefinitions,
    components: {
      IncBadgeElement,
      IncSpinnerElement,
      IncStatePanel,
      IncLiveRegion,
      IncAutoRefresh,
      IncThemeSwitcher
    }
  });
}

// src/web-components/components/dom-helpers.js
var ElementRef = typeof Element === "undefined" ? null : Element;
function normalizeToken2(value) {
  return String(value ?? "").trim().toLowerCase();
}
function addClass(node, className) {
  if (ElementRef && node instanceof ElementRef && className) {
    node.classList.add(className);
  }
}
function removeMatchingClasses(node, predicate) {
  if (!ElementRef || !(node instanceof ElementRef)) {
    return;
  }
  Array.from(node.classList).filter((token) => predicate(token)).forEach((token) => node.classList.remove(token));
}
function moveChildNodes(source, target, predicate = () => true) {
  Array.from(source.childNodes).filter((node) => predicate(node)).forEach((node) => target.append(node));
}
function ensureNode(parent, selector, build) {
  const existing = parent.querySelector(`:scope > ${selector}`);
  if (existing) {
    return existing;
  }
  const node = build();
  parent.append(node);
  return node;
}

// src/web-components/components/actions.js
var FALSE_TOKENS = /* @__PURE__ */ new Set(["false", "0", "off", "no"]);
var BADGE_TONES2 = /* @__PURE__ */ new Set(["primary", "secondary", "success", "danger", "warning", "info"]);
var BUTTON_VARIANTS = /* @__PURE__ */ new Set([
  "primary",
  "secondary",
  "success",
  "danger",
  "warning",
  "info",
  "link",
  "outline-primary",
  "outline-secondary",
  "outline-success",
  "outline-danger",
  "outline-warning",
  "outline-info"
]);
var BUTTON_SIZES = /* @__PURE__ */ new Set(["sm", "lg", "micro"]);
var ALERT_DEFAULT_ROLE_BY_TONE = /* @__PURE__ */ new Map([
  ["info", "status"],
  ["secondary", "status"]
]);
var ALERT_ICON_BY_TONE = /* @__PURE__ */ new Map([
  ["success", "success"],
  ["danger", "error"],
  ["warning", "warning"],
  ["info", "info"],
  ["secondary", "info"],
  ["primary", "info"]
]);
var HostElement4 = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
function toBoolean(value, fallback = false) {
  if (value == null) {
    return fallback;
  }
  return !FALSE_TOKENS.has(String(value).toLowerCase());
}
function toPositiveInt2(value) {
  const parsed = Number.parseInt(String(value ?? "").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}
function emit2(host, type, detail = {}, options = {}) {
  return host.dispatchEvent(new CustomEvent(type, {
    detail,
    bubbles: options.bubbles !== false,
    composed: options.composed !== false,
    cancelable: options.cancelable === true
  }));
}
function getDirectIconSlot(host) {
  return Array.from(host.children || []).find((node) => node instanceof HTMLElement && node.getAttribute("slot") === "icon") || null;
}
function hasConsumerIcon(container) {
  return Array.from(container.children || []).some((node) => node instanceof HTMLElement && !node.hasAttribute("data-inc-generated-icon"));
}
function renderDecorativeIcon2(container, name, options = {}) {
  replaceIconContents(container, name, {
    className: "inc-icon",
    decorative: true,
    size: options.size || 16
  });
  container.hidden = false;
}
var IncElement = class extends HostElement4 {
  emit(type, detail = {}, options = {}) {
    return emit2(this, type, detail, options);
  }
};
var IncButtonElement = class extends IncElement {
  static get observedAttributes() {
    return ["tone", "variant", "size", "loading", "href", "type", "disabled", "label", "target", "rel", "download", "icon"];
  }
  connectedCallback() {
    addClass(this, "inc-button");
    this.bindEvents();
    this.sync();
  }
  disconnectedCallback() {
    if (this._boundClick) {
      this.removeEventListener("click", this._boundClick);
    }
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  bindEvents() {
    if (this._boundClick) {
      return;
    }
    this._boundClick = (event) => {
      if (!this._control || !this.contains(this._control)) {
        return;
      }
      const isBlocked = this._control.hasAttribute("aria-disabled") || this._control.classList.contains("is-loading");
      if (!isBlocked || this._control.tagName !== "A") {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
    };
    this.addEventListener("click", this._boundClick);
  }
  sync() {
    addClass(this, "inc-button");
    this.setAttribute("part", "button");
    const control = this.ensureControl();
    const variant = normalizeToken2(this.getAttribute("variant") || this.getAttribute("tone")) || "secondary";
    const resolvedVariant = BUTTON_VARIANTS.has(variant) ? variant : "secondary";
    const size = normalizeToken2(this.getAttribute("size"));
    const loading = toBoolean(this.getAttribute("loading"));
    const disabled = toBoolean(this.getAttribute("disabled")) || loading;
    control.className = "inc-btn inc-button__control";
    control.setAttribute("part", "control");
    control.classList.add(`inc-btn--${resolvedVariant}`);
    if (BUTTON_SIZES.has(size)) {
      control.classList.add(`inc-btn--${size}`);
    }
    control.classList.toggle("is-loading", loading);
    if (control.tagName === "BUTTON") {
      control.type = this.getAttribute("type") || "button";
      control.disabled = disabled;
    } else {
      control.setAttribute("href", this.getAttribute("href") || "#");
      const target = this.getAttribute("target");
      if (target) {
        control.setAttribute("target", target);
      } else {
        control.removeAttribute("target");
      }
      const rel = this.getAttribute("rel");
      if (rel) {
        control.setAttribute("rel", rel);
      } else {
        control.removeAttribute("rel");
      }
      const download = this.getAttribute("download");
      if (download != null) {
        control.setAttribute("download", download);
      } else {
        control.removeAttribute("download");
      }
      if (disabled) {
        control.setAttribute("aria-disabled", "true");
        control.tabIndex = -1;
      } else {
        control.removeAttribute("aria-disabled");
        control.removeAttribute("tabindex");
      }
    }
    if (loading) {
      control.setAttribute("aria-busy", "true");
      this.ensureLoadingSpinner(control);
    } else {
      control.removeAttribute("aria-busy");
      this.removeLoadingSpinner(control);
    }
    this.syncIcon(control);
    const label = this.getAttribute("label");
    if (label) {
      control.setAttribute("aria-label", label);
    } else {
      control.removeAttribute("aria-label");
    }
  }
  ensureControl() {
    const desiredTag = this.hasAttribute("href") ? "A" : "BUTTON";
    const existing = this._control || this.querySelector(":scope > button.inc-button__control, :scope > a.inc-button__control");
    if (existing && existing.tagName === desiredTag) {
      this._control = existing;
      return existing;
    }
    const control = document.createElement(desiredTag.toLowerCase());
    if (desiredTag === "BUTTON") {
      control.type = this.getAttribute("type") || "button";
    }
    if (existing) {
      moveChildNodes(existing, control);
      existing.replaceWith(control);
    } else {
      moveChildNodes(this, control);
      this.append(control);
    }
    if (!control.childNodes.length) {
      control.textContent = this.textContent || "";
    }
    this._control = control;
    return control;
  }
  ensureLoadingSpinner(control) {
    this.removeLoadingSpinner(control);
    const spinner = document.createElement("span");
    spinner.dataset.incButtonSpinner = "true";
    spinner.className = "inc-spinner inc-spinner--border inc-spinner--border--sm";
    spinner.setAttribute("aria-hidden", "true");
    control.append(spinner);
  }
  removeLoadingSpinner(control) {
    if (!(control instanceof HTMLElement)) {
      return;
    }
    control.querySelectorAll(":scope > [data-inc-button-spinner]").forEach((node) => node.remove());
  }
  syncIcon(control) {
    if (!(control instanceof HTMLElement)) {
      return;
    }
    const explicitIcon = normalizeIconName(this.getAttribute("icon"));
    const inferredIcon = this.getAttribute("download") != null ? "download" : this.getAttribute("target") === "_blank" ? "external-link" : "";
    const iconName = explicitIcon || inferredIcon;
    let icon = control.querySelector(":scope > [data-inc-button-icon]");
    const slotted = getDirectIconSlot(control);
    if (!icon && (iconName || slotted)) {
      icon = document.createElement("span");
      icon.className = "inc-btn__icon";
      icon.setAttribute("data-inc-button-icon", "true");
      icon.setAttribute("aria-hidden", "true");
      control.prepend(icon);
    }
    if (!(icon instanceof HTMLElement)) {
      return;
    }
    if (slotted) {
      slotted.removeAttribute("slot");
      icon.replaceChildren(slotted);
      icon.hidden = false;
      return;
    }
    if (hasConsumerIcon(icon)) {
      icon.hidden = false;
      return;
    }
    if (iconName && iconName !== "none") {
      renderDecorativeIcon2(icon, iconName, { size: 16 });
      return;
    }
    icon.remove();
  }
};
var IncButtonGroupElement = class extends IncElement {
  static get observedAttributes() {
    return ["size", "label"];
  }
  connectedCallback() {
    addClass(this, "inc-button-group");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-button-group");
    this.setAttribute("part", "button-group");
    removeMatchingClasses(this, (token) => token.startsWith("inc-button-group--"));
    const size = normalizeToken2(this.getAttribute("size"));
    if (BUTTON_SIZES.has(size)) {
      this.classList.add(`inc-button-group--${size}`);
    }
    this.setAttribute("role", "group");
    const label = this.getAttribute("label") || this.getAttribute("aria-label") || "";
    if (label) {
      this.setAttribute("aria-label", label);
    } else {
      this.removeAttribute("aria-label");
    }
  }
};
var IncButtonToolbarElement = class extends IncElement {
  static get observedAttributes() {
    return ["label", "orientation"];
  }
  connectedCallback() {
    addClass(this, "inc-button-toolbar");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-button-toolbar");
    this.setAttribute("part", "button-toolbar");
    this.setAttribute("role", "toolbar");
    const orientation = normalizeToken2(this.getAttribute("orientation"));
    if (orientation === "vertical") {
      this.setAttribute("aria-orientation", "vertical");
    } else {
      this.removeAttribute("aria-orientation");
    }
    const label = this.getAttribute("label") || this.getAttribute("aria-label") || "";
    if (label) {
      this.setAttribute("aria-label", label);
    } else {
      this.removeAttribute("aria-label");
    }
  }
};
var IncCloseButtonElement = class extends IncElement {
  static get observedAttributes() {
    return ["label", "variant"];
  }
  connectedCallback() {
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    this.classList.remove("inc-close-button", "inc-close-button--white");
    this.setAttribute("part", "close-button");
    const control = this.ensureControl();
    const variant = normalizeToken2(this.getAttribute("variant"));
    control.className = "inc-close-button";
    control.setAttribute("part", "control");
    if (variant === "white") {
      control.classList.add("inc-close-button--white");
    }
    control.type = "button";
    control.setAttribute("aria-label", this.getAttribute("label") || "Close");
    control.textContent = "";
  }
  ensureControl() {
    const existing = this._control || this.querySelector(":scope > button.inc-close-button");
    if (existing) {
      this._control = existing;
      return existing;
    }
    const control = document.createElement("button");
    const previous = this.querySelector(":scope > a.inc-close-button");
    if (previous) {
      moveChildNodes(previous, control);
      previous.replaceWith(control);
    } else {
      moveChildNodes(this, control);
      this.append(control);
    }
    this._control = control;
    return control;
  }
};
var IncAlertElement = class extends IncElement {
  static get observedAttributes() {
    return ["tone", "variant", "dismissible", "dismiss-label", "timeout", "icon"];
  }
  connectedCallback() {
    addClass(this, "inc-alert");
    this.bindEvents();
    this.sync();
  }
  disconnectedCallback() {
    this.stopDismissTimer();
    if (this._boundClick) {
      this.removeEventListener("click", this._boundClick);
    }
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  bindEvents() {
    if (this._boundClick) {
      return;
    }
    this._boundClick = (event) => {
      const dismiss = event.target.closest("[data-inc-alert-dismiss]");
      if (!dismiss || !this.contains(dismiss)) {
        return;
      }
      event.preventDefault();
      this.dismiss("manual");
    };
    this.addEventListener("click", this._boundClick);
  }
  sync() {
    addClass(this, "inc-alert");
    this.setAttribute("part", "alert");
    removeMatchingClasses(this, (token) => token.startsWith("inc-alert--"));
    const tone = normalizeToken2(this.getAttribute("tone") || this.getAttribute("variant")) || "info";
    const resolvedTone = BADGE_TONES2.has(tone) ? tone : "info";
    this.classList.add(`inc-alert--${resolvedTone}`);
    this.syncIcon(resolvedTone);
    if (toBoolean(this.getAttribute("dismissible"))) {
      this.classList.add("inc-alert--dismissible");
      this.ensureDismissButton();
    } else {
      this.removeDismissButton();
    }
    if (!this.hasAttribute("role")) {
      this.setAttribute("role", ALERT_DEFAULT_ROLE_BY_TONE.get(resolvedTone) || "alert");
    }
    if (!this.hasAttribute("aria-live")) {
      this.setAttribute("aria-live", this.getAttribute("role") === "alert" ? "assertive" : "polite");
    }
    this.setAttribute("aria-atomic", "true");
    const timeoutMs = toPositiveInt2(this.getAttribute("timeout"));
    if (timeoutMs) {
      this.ensureProgressBar();
      if (!this.hidden && this.getAttribute("aria-hidden") !== "true") {
        this.startDismissTimer(timeoutMs);
      } else {
        this.stopDismissTimer();
      }
    } else {
      this.stopDismissTimer();
      this.removeProgressBar();
    }
  }
  ensureDismissButton() {
    let button = this.querySelector(":scope > [data-inc-alert-dismiss]");
    if (!button) {
      button = document.createElement("button");
      button.type = "button";
      button.dataset.incAlertDismiss = "true";
      this.append(button);
    }
    button.className = "inc-close-button";
    button.setAttribute("part", "dismiss");
    button.setAttribute("aria-label", this.getAttribute("dismiss-label") || "Dismiss alert");
    button.textContent = "";
    return button;
  }
  removeDismissButton() {
    this.querySelectorAll(":scope > [data-inc-alert-dismiss]").forEach((node) => node.remove());
  }
  ensureProgressBar() {
    let progress = this.querySelector(":scope > .inc-alert__progress");
    if (!progress) {
      progress = document.createElement("div");
      progress.className = "inc-alert__progress";
      progress.setAttribute("part", "progress");
      progress.setAttribute("aria-hidden", "true");
      this.append(progress);
    }
    return progress;
  }
  removeProgressBar() {
    this.querySelectorAll(":scope > .inc-alert__progress").forEach((node) => node.remove());
  }
  syncIcon(tone) {
    const explicitIcon = normalizeIconName(this.getAttribute("icon"));
    const iconName = explicitIcon || ALERT_ICON_BY_TONE.get(tone) || "info";
    let icon = this.querySelector(":scope > .inc-alert__icon");
    const slotted = getDirectIconSlot(this);
    if (!icon && (iconName !== "none" || slotted)) {
      icon = document.createElement("span");
      icon.className = "inc-alert__icon";
      icon.setAttribute("part", "icon");
      icon.setAttribute("aria-hidden", "true");
      this.prepend(icon);
    }
    if (!(icon instanceof HTMLElement)) {
      return;
    }
    if (slotted) {
      slotted.removeAttribute("slot");
      icon.replaceChildren(slotted);
      icon.hidden = false;
      return;
    }
    if (hasConsumerIcon(icon)) {
      icon.hidden = false;
      return;
    }
    if (iconName === "none") {
      icon.remove();
      return;
    }
    renderDecorativeIcon2(icon, iconName, { size: 18 });
  }
  startDismissTimer(timeoutMs) {
    const progress = this.ensureProgressBar();
    this.stopDismissTimer();
    this._dismissTimeoutMs = timeoutMs;
    this._dismissStartedAt = performance.now();
    const tick = (now) => {
      if (this.hidden || this.getAttribute("aria-hidden") === "true") {
        this.stopDismissTimer();
        return;
      }
      const elapsed = Math.max(0, now - this._dismissStartedAt);
      const remaining = Math.max(0, timeoutMs - elapsed);
      const ratio = timeoutMs > 0 ? remaining / timeoutMs : 0;
      progress.style.transform = `scaleX(${ratio})`;
      if (remaining <= 0) {
        this.dismiss("timeout");
        return;
      }
      this._dismissFrame = window.requestAnimationFrame(tick);
    };
    progress.style.transform = "scaleX(1)";
    this._dismissFrame = window.requestAnimationFrame(tick);
  }
  stopDismissTimer() {
    if (this._dismissFrame) {
      window.cancelAnimationFrame(this._dismissFrame);
      this._dismissFrame = 0;
    }
  }
  dismiss(reason = "manual") {
    this.hide(reason);
  }
  hide(reason = "manual") {
    this.stopDismissTimer();
    this.hidden = true;
    this.setAttribute("aria-hidden", "true");
    this.emit("dismiss", { hidden: true, reason });
  }
};
var IncEmptyStateElement = class extends IncElement {
  static get observedAttributes() {
    return ["icon"];
  }
  connectedCallback() {
    addClass(this, "inc-empty-state");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-empty-state");
    this.setAttribute("part", "empty-state content icon body actions");
    const content = ensureNode(this, ".inc-empty-state__content", () => {
      const node = document.createElement("div");
      node.className = "inc-empty-state__content";
      node.innerHTML = [
        '<div class="inc-empty-state__icon" part="icon"></div>',
        '<div class="inc-empty-state__body" part="body"></div>',
        '<div class="inc-empty-state__actions" part="actions"></div>'
      ].join("");
      return node;
    });
    const icon = ensureNode(content, ".inc-empty-state__icon", () => {
      const node = document.createElement("div");
      node.className = "inc-empty-state__icon";
      node.setAttribute("part", "icon");
      return node;
    });
    const body = ensureNode(content, ".inc-empty-state__body", () => {
      const node = document.createElement("div");
      node.className = "inc-empty-state__body";
      node.setAttribute("part", "body");
      return node;
    });
    const actions = ensureNode(content, ".inc-empty-state__actions", () => {
      const node = document.createElement("div");
      node.className = "inc-empty-state__actions";
      node.setAttribute("part", "actions");
      return node;
    });
    Array.from(this.childNodes).forEach((node) => {
      if (node === content) {
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE && node.getAttribute("slot") === "icon") {
        node.removeAttribute("slot");
        icon.append(node);
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE && node.getAttribute("slot") === "actions") {
        node.removeAttribute("slot");
        actions.append(node);
        return;
      }
      body.append(node);
    });
    if (hasConsumerIcon(icon)) {
      icon.hidden = false;
      return;
    }
    const iconName = normalizeIconName(this.getAttribute("icon")) || "empty";
    if (iconName === "none") {
      icon.replaceChildren();
      icon.hidden = true;
      return;
    }
    renderDecorativeIcon2(icon, iconName, { size: 34 });
  }
};
var actionDefinitions = [
  ["inc-button", IncButtonElement],
  ["inc-button-group", IncButtonGroupElement],
  ["inc-button-toolbar", IncButtonToolbarElement],
  ["inc-close-button", IncCloseButtonElement],
  ["inc-alert", IncAlertElement],
  ["inc-empty-state", IncEmptyStateElement]
];
var actionComponents = {
  IncButtonElement,
  IncButtonGroupElement,
  IncButtonToolbarElement,
  IncCloseButtonElement,
  IncAlertElement,
  IncEmptyStateElement
};
function defineActionComponents(registry = globalThis.customElements) {
  if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
    return [];
  }
  const defined = [];
  for (const [tagName, ctor] of actionDefinitions) {
    if (!registry.get(tagName)) {
      registry.define(tagName, ctor);
      defined.push(tagName);
    }
  }
  return defined;
}
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    defineActionComponents,
    actionDefinitions,
    actionComponents,
    IncButtonElement,
    IncButtonGroupElement,
    IncButtonToolbarElement,
    IncCloseButtonElement,
    IncAlertElement,
    IncEmptyStateElement
  };
}
if (typeof globalThis !== "undefined") {
  const namespace2 = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  namespace2.actions = Object.assign({}, namespace2.actions, {
    defineActionComponents,
    actionDefinitions,
    components: actionComponents
  });
}

// src/web-components/base-element.js
var HostElement5 = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
var metadataByConstructor = /* @__PURE__ */ new WeakMap();
function buildMetadata(constructor) {
  if (metadataByConstructor.has(constructor)) {
    return metadataByConstructor.get(constructor);
  }
  const reflectedConfig = constructor.reflectedAttributes || {};
  const propertyToConfig = /* @__PURE__ */ new Map();
  const attributeToConfig = /* @__PURE__ */ new Map();
  Object.keys(reflectedConfig).forEach((property) => {
    const normalized = normalizeAttributeConfig(property, reflectedConfig[property]);
    propertyToConfig.set(property, normalized);
    attributeToConfig.set(normalized.attribute, normalized);
    if (Object.prototype.hasOwnProperty.call(constructor.prototype, property)) {
      return;
    }
    Object.defineProperty(constructor.prototype, property, {
      configurable: true,
      enumerable: true,
      get() {
        return this._propertyValues.get(property);
      },
      set(value) {
        this._setReflectedPropertyValue(property, value, { reflect: true });
      }
    });
  });
  const metadata = {
    propertyToConfig,
    attributeToConfig
  };
  metadataByConstructor.set(constructor, metadata);
  return metadata;
}
var IncElement2 = class extends HostElement5 {
  static reflectedAttributes = {};
  static get observedAttributes() {
    const metadata = buildMetadata(this);
    return [...metadata.attributeToConfig.keys()];
  }
  constructor() {
    super();
    this._propertyValues = /* @__PURE__ */ new Map();
    this._slotListeners = /* @__PURE__ */ new Map();
    this._isReflectingAttribute = false;
    this._isConnected = false;
    const metadata = buildMetadata(this.constructor);
    metadata.propertyToConfig.forEach((config, property) => {
      if (this.hasAttribute(config.attribute)) {
        this._propertyValues.set(property, readReflectedAttribute(this, config));
        return;
      }
      this._propertyValues.set(property, config.defaultValue);
    });
  }
  connectedCallback() {
    this._isConnected = true;
    if (typeof this.onConnected === "function") {
      this.onConnected();
    }
  }
  disconnectedCallback() {
    this._isConnected = false;
    this._slotListeners.forEach((listener, slot) => {
      slot.removeEventListener("slotchange", listener);
    });
    this._slotListeners.clear();
    if (typeof this.onDisconnected === "function") {
      this.onDisconnected();
    }
  }
  attributeChangedCallback(name, oldValue, newValue) {
    if (oldValue === newValue) {
      return;
    }
    const metadata = buildMetadata(this.constructor);
    const config = metadata.attributeToConfig.get(name);
    if (config) {
      const parsed = parseValueFromAttribute(newValue, config);
      this._setReflectedPropertyValue(config.property, parsed, { reflect: false });
    }
    if (typeof this.onAttributeValueChanged === "function") {
      this.onAttributeValueChanged(name, oldValue, newValue);
    }
  }
  emit(type, detail = {}, options = {}) {
    return dispatchComponentEvent(this, type, detail, options);
  }
  getSlotElements(slotName = "") {
    return getAssignedSlotElements(this, slotName);
  }
  observeSlot(slotName = "", callback = null) {
    const selector = slotName ? `slot[name="${slotName}"]` : "slot:not([name])";
    const slot = this.querySelector(selector);
    if (typeof HTMLSlotElement === "undefined" || !(slot instanceof HTMLSlotElement)) {
      return () => {
      };
    }
    const listener = () => {
      if (typeof callback === "function") {
        callback(this.getSlotElements(slotName));
      }
    };
    slot.addEventListener("slotchange", listener);
    this._slotListeners.set(slot, listener);
    listener();
    return () => {
      slot.removeEventListener("slotchange", listener);
      this._slotListeners.delete(slot);
    };
  }
  reflectAllProperties() {
    const metadata = buildMetadata(this.constructor);
    metadata.propertyToConfig.forEach((config, property) => {
      const value = this._propertyValues.get(property);
      if (!config.reflect) {
        return;
      }
      const serialized = serializeValueForAttribute(value, config);
      reflectAttributeValue(this, config.attribute, serialized);
    });
  }
  _setReflectedPropertyValue(property, value, options = {}) {
    const metadata = buildMetadata(this.constructor);
    const config = metadata.propertyToConfig.get(property);
    if (!config) {
      this._propertyValues.set(property, value);
      return;
    }
    const previousValue = this._propertyValues.get(property);
    if (Object.is(previousValue, value)) {
      return;
    }
    this._propertyValues.set(property, value);
    if (options.reflect !== false && config.reflect && !this._isReflectingAttribute) {
      const serialized = serializeValueForAttribute(value, config);
      this._isReflectingAttribute = true;
      reflectAttributeValue(this, config.attribute, serialized);
      this._isReflectingAttribute = false;
    }
    if (typeof this.onPropertyValueChanged === "function") {
      this.onPropertyValueChanged(property, previousValue, value);
    }
  }
};

// src/web-components/components/collections.js
var FALSE_TOKENS2 = /* @__PURE__ */ new Set(["false", "0", "off", "no"]);
function toBoolean2(value, fallback = false) {
  if (value == null) {
    return fallback;
  }
  return !FALSE_TOKENS2.has(String(value).toLowerCase());
}
var IncListGroupElement = class extends IncElement2 {
  static get observedAttributes() {
    return ["flush", "numbered", "dense", "interactive", "label"];
  }
  connectedCallback() {
    addClass(this, "inc-list-group");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-list-group");
    this.setAttribute("part", "list-group");
    removeMatchingClasses(this, (token) => token.startsWith("inc-list-group--"));
    if (toBoolean2(this.getAttribute("flush"))) {
      this.classList.add("inc-list-group--flush");
    }
    if (toBoolean2(this.getAttribute("numbered"))) {
      this.classList.add("inc-list-group--numbered");
    }
    if (toBoolean2(this.getAttribute("dense"))) {
      this.classList.add("inc-list-group--dense");
    }
    if (toBoolean2(this.getAttribute("interactive"))) {
      this.classList.add("inc-list-group--interactive");
    }
    this.setAttribute("role", "list");
    const label = this.getAttribute("label") || this.getAttribute("aria-label") || "";
    if (label) {
      this.setAttribute("aria-label", label);
    } else {
      this.removeAttribute("aria-label");
    }
    Array.from(this.children).forEach((node) => {
      if (!(node instanceof Element)) {
        return;
      }
      if (node.getAttribute("slot") === "item") {
        node.removeAttribute("slot");
      }
      node.classList.add("inc-list-group__item");
      if (toBoolean2(this.getAttribute("interactive")) || node.matches("a[href], button:not([disabled])")) {
        node.classList.add("inc-list-group__item--action");
      }
      if (!node.hasAttribute("role") && !node.matches("a[href], button")) {
        node.setAttribute("role", "listitem");
      }
    });
  }
};
var IncKeyValueGridElement = class extends IncElement2 {
  static get observedAttributes() {
    return ["columns", "dense"];
  }
  connectedCallback() {
    addClass(this, "inc-key-value-grid");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-key-value-grid");
    this.setAttribute("part", "grid");
    const columns = Number.parseInt(this.getAttribute("columns") || "", 10);
    if (Number.isFinite(columns) && columns > 0) {
      this.style.gridTemplateColumns = `repeat(${columns}, minmax(0, 1fr))`;
    } else {
      this.style.removeProperty("grid-template-columns");
    }
    if (toBoolean2(this.getAttribute("dense"))) {
      this.style.rowGap = "0.5rem";
      this.style.columnGap = "1rem";
    } else {
      this.style.removeProperty("row-gap");
      this.style.removeProperty("column-gap");
    }
  }
};
var IncKeyValueElement = class extends IncElement2 {
  static get observedAttributes() {
    return ["label", "value", "meta", "inline", "dense", "card", "variant"];
  }
  connectedCallback() {
    addClass(this, "inc-key-value");
    this.sync();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.sync();
    }
  }
  sync() {
    addClass(this, "inc-key-value");
    this.setAttribute("part", "key-value");
    removeMatchingClasses(this, (token) => token.startsWith("inc-key-value--"));
    const variant = normalizeToken2(this.getAttribute("variant"));
    const card = toBoolean2(this.getAttribute("card")) || variant === "card";
    if (card) {
      this.classList.add("inc-key-value--card");
    }
    if (toBoolean2(this.getAttribute("inline"))) {
      this.classList.add("inc-key-value--inline");
    }
    if (toBoolean2(this.getAttribute("dense"))) {
      this.classList.add("inc-key-value--dense");
    }
    const definition = ensureNode(this, ".inc-key-value__definition", () => {
      const node = document.createElement("dl");
      node.className = "inc-key-value__definition";
      node.innerHTML = [
        '<dt class="inc-key-value__label" part="label"></dt>',
        '<dd class="inc-key-value__value" part="value"></dd>',
        '<div class="inc-key-value__meta" part="meta"></div>'
      ].join("");
      return node;
    });
    const label = ensureNode(definition, ".inc-key-value__label", () => {
      const node = document.createElement("dt");
      node.className = "inc-key-value__label";
      node.setAttribute("part", "label");
      return node;
    });
    const value = ensureNode(definition, ".inc-key-value__value", () => {
      const node = document.createElement("dd");
      node.className = "inc-key-value__value";
      node.setAttribute("part", "value");
      return node;
    });
    const meta = ensureNode(definition, ".inc-key-value__meta", () => {
      const node = document.createElement("div");
      node.className = "inc-key-value__meta";
      node.setAttribute("part", "meta");
      return node;
    });
    Array.from(this.childNodes).forEach((node) => {
      if (node === definition) {
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE && node.getAttribute("slot") === "label") {
        node.removeAttribute("slot");
        label.append(node);
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE && node.getAttribute("slot") === "value") {
        node.removeAttribute("slot");
        value.append(node);
        return;
      }
      if (node.nodeType === Node.ELEMENT_NODE && node.getAttribute("slot") === "meta") {
        node.removeAttribute("slot");
        meta.append(node);
        return;
      }
      if (value.childNodes.length === 0 || node.nodeType === Node.TEXT_NODE) {
        value.append(node);
        return;
      }
      meta.append(node);
    });
    const labelText = this.getAttribute("label") || "";
    const valueText = this.getAttribute("value") || "";
    const metaText = this.getAttribute("meta") || "";
    if (!label.childNodes.length) {
      label.textContent = labelText;
    }
    if (!value.childNodes.length) {
      value.textContent = valueText;
    }
    if (!meta.childNodes.length) {
      meta.textContent = metaText;
    }
  }
};
var collectionDefinitions = [
  ["inc-list-group", IncListGroupElement],
  ["inc-key-value-grid", IncKeyValueGridElement],
  ["inc-key-value", IncKeyValueElement]
];
var collectionComponents = {
  IncListGroupElement,
  IncKeyValueGridElement,
  IncKeyValueElement
};
function defineCollectionComponents(registry = globalThis.customElements) {
  if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
    return [];
  }
  const defined = [];
  for (const [tagName, ctor] of collectionDefinitions) {
    if (!registry.get(tagName)) {
      registry.define(tagName, ctor);
      defined.push(tagName);
    }
  }
  return defined;
}
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    defineCollectionComponents,
    collectionDefinitions,
    collectionComponents,
    IncListGroupElement,
    IncKeyValueGridElement,
    IncKeyValueElement
  };
}
if (typeof globalThis !== "undefined") {
  const namespace2 = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  namespace2.collections = Object.assign({}, namespace2.collections, {
    defineCollectionComponents,
    collectionDefinitions,
    components: collectionComponents
  });
}

// node_modules/d3-array/src/ascending.js
function ascending(a, b) {
  return a == null || b == null ? NaN : a < b ? -1 : a > b ? 1 : a >= b ? 0 : NaN;
}

// node_modules/d3-array/src/descending.js
function descending(a, b) {
  return a == null || b == null ? NaN : b < a ? -1 : b > a ? 1 : b >= a ? 0 : NaN;
}

// node_modules/d3-array/src/bisector.js
function bisector(f) {
  let compare1, compare2, delta;
  if (f.length !== 2) {
    compare1 = ascending;
    compare2 = (d, x2) => ascending(f(d), x2);
    delta = (d, x2) => f(d) - x2;
  } else {
    compare1 = f === ascending || f === descending ? f : zero;
    compare2 = f;
    delta = f;
  }
  function left(a, x2, lo = 0, hi = a.length) {
    if (lo < hi) {
      if (compare1(x2, x2) !== 0) return hi;
      do {
        const mid = lo + hi >>> 1;
        if (compare2(a[mid], x2) < 0) lo = mid + 1;
        else hi = mid;
      } while (lo < hi);
    }
    return lo;
  }
  function right(a, x2, lo = 0, hi = a.length) {
    if (lo < hi) {
      if (compare1(x2, x2) !== 0) return hi;
      do {
        const mid = lo + hi >>> 1;
        if (compare2(a[mid], x2) <= 0) lo = mid + 1;
        else hi = mid;
      } while (lo < hi);
    }
    return lo;
  }
  function center(a, x2, lo = 0, hi = a.length) {
    const i = left(a, x2, lo, hi - 1);
    return i > lo && delta(a[i - 1], x2) > -delta(a[i], x2) ? i - 1 : i;
  }
  return { left, center, right };
}
function zero() {
  return 0;
}

// node_modules/d3-array/src/number.js
function number(x2) {
  return x2 === null ? NaN : +x2;
}

// node_modules/d3-array/src/bisect.js
var ascendingBisect = bisector(ascending);
var bisectRight = ascendingBisect.right;
var bisectLeft = ascendingBisect.left;
var bisectCenter = bisector(number).center;
var bisect_default = bisectRight;

// node_modules/d3-array/src/extent.js
function extent(values, valueof) {
  let min;
  let max;
  if (valueof === void 0) {
    for (const value of values) {
      if (value != null) {
        if (min === void 0) {
          if (value >= value) min = max = value;
        } else {
          if (min > value) min = value;
          if (max < value) max = value;
        }
      }
    }
  } else {
    let index = -1;
    for (let value of values) {
      if ((value = valueof(value, ++index, values)) != null) {
        if (min === void 0) {
          if (value >= value) min = max = value;
        } else {
          if (min > value) min = value;
          if (max < value) max = value;
        }
      }
    }
  }
  return [min, max];
}

// node_modules/d3-array/src/ticks.js
var e10 = Math.sqrt(50);
var e5 = Math.sqrt(10);
var e2 = Math.sqrt(2);
function tickSpec(start, stop, count) {
  const step = (stop - start) / Math.max(0, count), power = Math.floor(Math.log10(step)), error = step / Math.pow(10, power), factor = error >= e10 ? 10 : error >= e5 ? 5 : error >= e2 ? 2 : 1;
  let i1, i2, inc;
  if (power < 0) {
    inc = Math.pow(10, -power) / factor;
    i1 = Math.round(start * inc);
    i2 = Math.round(stop * inc);
    if (i1 / inc < start) ++i1;
    if (i2 / inc > stop) --i2;
    inc = -inc;
  } else {
    inc = Math.pow(10, power) * factor;
    i1 = Math.round(start / inc);
    i2 = Math.round(stop / inc);
    if (i1 * inc < start) ++i1;
    if (i2 * inc > stop) --i2;
  }
  if (i2 < i1 && 0.5 <= count && count < 2) return tickSpec(start, stop, count * 2);
  return [i1, i2, inc];
}
function ticks(start, stop, count) {
  stop = +stop, start = +start, count = +count;
  if (!(count > 0)) return [];
  if (start === stop) return [start];
  const reverse = stop < start, [i1, i2, inc] = reverse ? tickSpec(stop, start, count) : tickSpec(start, stop, count);
  if (!(i2 >= i1)) return [];
  const n = i2 - i1 + 1, ticks2 = new Array(n);
  if (reverse) {
    if (inc < 0) for (let i = 0; i < n; ++i) ticks2[i] = (i2 - i) / -inc;
    else for (let i = 0; i < n; ++i) ticks2[i] = (i2 - i) * inc;
  } else {
    if (inc < 0) for (let i = 0; i < n; ++i) ticks2[i] = (i1 + i) / -inc;
    else for (let i = 0; i < n; ++i) ticks2[i] = (i1 + i) * inc;
  }
  return ticks2;
}
function tickIncrement(start, stop, count) {
  stop = +stop, start = +start, count = +count;
  return tickSpec(start, stop, count)[2];
}
function tickStep(start, stop, count) {
  stop = +stop, start = +start, count = +count;
  const reverse = stop < start, inc = reverse ? tickIncrement(stop, start, count) : tickIncrement(start, stop, count);
  return (reverse ? -1 : 1) * (inc < 0 ? 1 / -inc : inc);
}

// node_modules/d3-scale/src/init.js
function initRange(domain, range) {
  switch (arguments.length) {
    case 0:
      break;
    case 1:
      this.range(domain);
      break;
    default:
      this.range(range).domain(domain);
      break;
  }
  return this;
}

// node_modules/d3-color/src/define.js
function define_default(constructor, factory, prototype) {
  constructor.prototype = factory.prototype = prototype;
  prototype.constructor = constructor;
}
function extend(parent, definition) {
  var prototype = Object.create(parent.prototype);
  for (var key in definition) prototype[key] = definition[key];
  return prototype;
}

// node_modules/d3-color/src/color.js
function Color() {
}
var darker = 0.7;
var brighter = 1 / darker;
var reI = "\\s*([+-]?\\d+)\\s*";
var reN = "\\s*([+-]?(?:\\d*\\.)?\\d+(?:[eE][+-]?\\d+)?)\\s*";
var reP = "\\s*([+-]?(?:\\d*\\.)?\\d+(?:[eE][+-]?\\d+)?)%\\s*";
var reHex = /^#([0-9a-f]{3,8})$/;
var reRgbInteger = new RegExp(`^rgb\\(${reI},${reI},${reI}\\)$`);
var reRgbPercent = new RegExp(`^rgb\\(${reP},${reP},${reP}\\)$`);
var reRgbaInteger = new RegExp(`^rgba\\(${reI},${reI},${reI},${reN}\\)$`);
var reRgbaPercent = new RegExp(`^rgba\\(${reP},${reP},${reP},${reN}\\)$`);
var reHslPercent = new RegExp(`^hsl\\(${reN},${reP},${reP}\\)$`);
var reHslaPercent = new RegExp(`^hsla\\(${reN},${reP},${reP},${reN}\\)$`);
var named = {
  aliceblue: 15792383,
  antiquewhite: 16444375,
  aqua: 65535,
  aquamarine: 8388564,
  azure: 15794175,
  beige: 16119260,
  bisque: 16770244,
  black: 0,
  blanchedalmond: 16772045,
  blue: 255,
  blueviolet: 9055202,
  brown: 10824234,
  burlywood: 14596231,
  cadetblue: 6266528,
  chartreuse: 8388352,
  chocolate: 13789470,
  coral: 16744272,
  cornflowerblue: 6591981,
  cornsilk: 16775388,
  crimson: 14423100,
  cyan: 65535,
  darkblue: 139,
  darkcyan: 35723,
  darkgoldenrod: 12092939,
  darkgray: 11119017,
  darkgreen: 25600,
  darkgrey: 11119017,
  darkkhaki: 12433259,
  darkmagenta: 9109643,
  darkolivegreen: 5597999,
  darkorange: 16747520,
  darkorchid: 10040012,
  darkred: 9109504,
  darksalmon: 15308410,
  darkseagreen: 9419919,
  darkslateblue: 4734347,
  darkslategray: 3100495,
  darkslategrey: 3100495,
  darkturquoise: 52945,
  darkviolet: 9699539,
  deeppink: 16716947,
  deepskyblue: 49151,
  dimgray: 6908265,
  dimgrey: 6908265,
  dodgerblue: 2003199,
  firebrick: 11674146,
  floralwhite: 16775920,
  forestgreen: 2263842,
  fuchsia: 16711935,
  gainsboro: 14474460,
  ghostwhite: 16316671,
  gold: 16766720,
  goldenrod: 14329120,
  gray: 8421504,
  green: 32768,
  greenyellow: 11403055,
  grey: 8421504,
  honeydew: 15794160,
  hotpink: 16738740,
  indianred: 13458524,
  indigo: 4915330,
  ivory: 16777200,
  khaki: 15787660,
  lavender: 15132410,
  lavenderblush: 16773365,
  lawngreen: 8190976,
  lemonchiffon: 16775885,
  lightblue: 11393254,
  lightcoral: 15761536,
  lightcyan: 14745599,
  lightgoldenrodyellow: 16448210,
  lightgray: 13882323,
  lightgreen: 9498256,
  lightgrey: 13882323,
  lightpink: 16758465,
  lightsalmon: 16752762,
  lightseagreen: 2142890,
  lightskyblue: 8900346,
  lightslategray: 7833753,
  lightslategrey: 7833753,
  lightsteelblue: 11584734,
  lightyellow: 16777184,
  lime: 65280,
  limegreen: 3329330,
  linen: 16445670,
  magenta: 16711935,
  maroon: 8388608,
  mediumaquamarine: 6737322,
  mediumblue: 205,
  mediumorchid: 12211667,
  mediumpurple: 9662683,
  mediumseagreen: 3978097,
  mediumslateblue: 8087790,
  mediumspringgreen: 64154,
  mediumturquoise: 4772300,
  mediumvioletred: 13047173,
  midnightblue: 1644912,
  mintcream: 16121850,
  mistyrose: 16770273,
  moccasin: 16770229,
  navajowhite: 16768685,
  navy: 128,
  oldlace: 16643558,
  olive: 8421376,
  olivedrab: 7048739,
  orange: 16753920,
  orangered: 16729344,
  orchid: 14315734,
  palegoldenrod: 15657130,
  palegreen: 10025880,
  paleturquoise: 11529966,
  palevioletred: 14381203,
  papayawhip: 16773077,
  peachpuff: 16767673,
  peru: 13468991,
  pink: 16761035,
  plum: 14524637,
  powderblue: 11591910,
  purple: 8388736,
  rebeccapurple: 6697881,
  red: 16711680,
  rosybrown: 12357519,
  royalblue: 4286945,
  saddlebrown: 9127187,
  salmon: 16416882,
  sandybrown: 16032864,
  seagreen: 3050327,
  seashell: 16774638,
  sienna: 10506797,
  silver: 12632256,
  skyblue: 8900331,
  slateblue: 6970061,
  slategray: 7372944,
  slategrey: 7372944,
  snow: 16775930,
  springgreen: 65407,
  steelblue: 4620980,
  tan: 13808780,
  teal: 32896,
  thistle: 14204888,
  tomato: 16737095,
  turquoise: 4251856,
  violet: 15631086,
  wheat: 16113331,
  white: 16777215,
  whitesmoke: 16119285,
  yellow: 16776960,
  yellowgreen: 10145074
};
define_default(Color, color, {
  copy(channels) {
    return Object.assign(new this.constructor(), this, channels);
  },
  displayable() {
    return this.rgb().displayable();
  },
  hex: color_formatHex,
  // Deprecated! Use color.formatHex.
  formatHex: color_formatHex,
  formatHex8: color_formatHex8,
  formatHsl: color_formatHsl,
  formatRgb: color_formatRgb,
  toString: color_formatRgb
});
function color_formatHex() {
  return this.rgb().formatHex();
}
function color_formatHex8() {
  return this.rgb().formatHex8();
}
function color_formatHsl() {
  return hslConvert(this).formatHsl();
}
function color_formatRgb() {
  return this.rgb().formatRgb();
}
function color(format2) {
  var m, l;
  format2 = (format2 + "").trim().toLowerCase();
  return (m = reHex.exec(format2)) ? (l = m[1].length, m = parseInt(m[1], 16), l === 6 ? rgbn(m) : l === 3 ? new Rgb(m >> 8 & 15 | m >> 4 & 240, m >> 4 & 15 | m & 240, (m & 15) << 4 | m & 15, 1) : l === 8 ? rgba(m >> 24 & 255, m >> 16 & 255, m >> 8 & 255, (m & 255) / 255) : l === 4 ? rgba(m >> 12 & 15 | m >> 8 & 240, m >> 8 & 15 | m >> 4 & 240, m >> 4 & 15 | m & 240, ((m & 15) << 4 | m & 15) / 255) : null) : (m = reRgbInteger.exec(format2)) ? new Rgb(m[1], m[2], m[3], 1) : (m = reRgbPercent.exec(format2)) ? new Rgb(m[1] * 255 / 100, m[2] * 255 / 100, m[3] * 255 / 100, 1) : (m = reRgbaInteger.exec(format2)) ? rgba(m[1], m[2], m[3], m[4]) : (m = reRgbaPercent.exec(format2)) ? rgba(m[1] * 255 / 100, m[2] * 255 / 100, m[3] * 255 / 100, m[4]) : (m = reHslPercent.exec(format2)) ? hsla(m[1], m[2] / 100, m[3] / 100, 1) : (m = reHslaPercent.exec(format2)) ? hsla(m[1], m[2] / 100, m[3] / 100, m[4]) : named.hasOwnProperty(format2) ? rgbn(named[format2]) : format2 === "transparent" ? new Rgb(NaN, NaN, NaN, 0) : null;
}
function rgbn(n) {
  return new Rgb(n >> 16 & 255, n >> 8 & 255, n & 255, 1);
}
function rgba(r, g, b, a) {
  if (a <= 0) r = g = b = NaN;
  return new Rgb(r, g, b, a);
}
function rgbConvert(o) {
  if (!(o instanceof Color)) o = color(o);
  if (!o) return new Rgb();
  o = o.rgb();
  return new Rgb(o.r, o.g, o.b, o.opacity);
}
function rgb(r, g, b, opacity) {
  return arguments.length === 1 ? rgbConvert(r) : new Rgb(r, g, b, opacity == null ? 1 : opacity);
}
function Rgb(r, g, b, opacity) {
  this.r = +r;
  this.g = +g;
  this.b = +b;
  this.opacity = +opacity;
}
define_default(Rgb, rgb, extend(Color, {
  brighter(k) {
    k = k == null ? brighter : Math.pow(brighter, k);
    return new Rgb(this.r * k, this.g * k, this.b * k, this.opacity);
  },
  darker(k) {
    k = k == null ? darker : Math.pow(darker, k);
    return new Rgb(this.r * k, this.g * k, this.b * k, this.opacity);
  },
  rgb() {
    return this;
  },
  clamp() {
    return new Rgb(clampi(this.r), clampi(this.g), clampi(this.b), clampa(this.opacity));
  },
  displayable() {
    return -0.5 <= this.r && this.r < 255.5 && (-0.5 <= this.g && this.g < 255.5) && (-0.5 <= this.b && this.b < 255.5) && (0 <= this.opacity && this.opacity <= 1);
  },
  hex: rgb_formatHex,
  // Deprecated! Use color.formatHex.
  formatHex: rgb_formatHex,
  formatHex8: rgb_formatHex8,
  formatRgb: rgb_formatRgb,
  toString: rgb_formatRgb
}));
function rgb_formatHex() {
  return `#${hex(this.r)}${hex(this.g)}${hex(this.b)}`;
}
function rgb_formatHex8() {
  return `#${hex(this.r)}${hex(this.g)}${hex(this.b)}${hex((isNaN(this.opacity) ? 1 : this.opacity) * 255)}`;
}
function rgb_formatRgb() {
  const a = clampa(this.opacity);
  return `${a === 1 ? "rgb(" : "rgba("}${clampi(this.r)}, ${clampi(this.g)}, ${clampi(this.b)}${a === 1 ? ")" : `, ${a})`}`;
}
function clampa(opacity) {
  return isNaN(opacity) ? 1 : Math.max(0, Math.min(1, opacity));
}
function clampi(value) {
  return Math.max(0, Math.min(255, Math.round(value) || 0));
}
function hex(value) {
  value = clampi(value);
  return (value < 16 ? "0" : "") + value.toString(16);
}
function hsla(h, s, l, a) {
  if (a <= 0) h = s = l = NaN;
  else if (l <= 0 || l >= 1) h = s = NaN;
  else if (s <= 0) h = NaN;
  return new Hsl(h, s, l, a);
}
function hslConvert(o) {
  if (o instanceof Hsl) return new Hsl(o.h, o.s, o.l, o.opacity);
  if (!(o instanceof Color)) o = color(o);
  if (!o) return new Hsl();
  if (o instanceof Hsl) return o;
  o = o.rgb();
  var r = o.r / 255, g = o.g / 255, b = o.b / 255, min = Math.min(r, g, b), max = Math.max(r, g, b), h = NaN, s = max - min, l = (max + min) / 2;
  if (s) {
    if (r === max) h = (g - b) / s + (g < b) * 6;
    else if (g === max) h = (b - r) / s + 2;
    else h = (r - g) / s + 4;
    s /= l < 0.5 ? max + min : 2 - max - min;
    h *= 60;
  } else {
    s = l > 0 && l < 1 ? 0 : h;
  }
  return new Hsl(h, s, l, o.opacity);
}
function hsl(h, s, l, opacity) {
  return arguments.length === 1 ? hslConvert(h) : new Hsl(h, s, l, opacity == null ? 1 : opacity);
}
function Hsl(h, s, l, opacity) {
  this.h = +h;
  this.s = +s;
  this.l = +l;
  this.opacity = +opacity;
}
define_default(Hsl, hsl, extend(Color, {
  brighter(k) {
    k = k == null ? brighter : Math.pow(brighter, k);
    return new Hsl(this.h, this.s, this.l * k, this.opacity);
  },
  darker(k) {
    k = k == null ? darker : Math.pow(darker, k);
    return new Hsl(this.h, this.s, this.l * k, this.opacity);
  },
  rgb() {
    var h = this.h % 360 + (this.h < 0) * 360, s = isNaN(h) || isNaN(this.s) ? 0 : this.s, l = this.l, m2 = l + (l < 0.5 ? l : 1 - l) * s, m1 = 2 * l - m2;
    return new Rgb(
      hsl2rgb(h >= 240 ? h - 240 : h + 120, m1, m2),
      hsl2rgb(h, m1, m2),
      hsl2rgb(h < 120 ? h + 240 : h - 120, m1, m2),
      this.opacity
    );
  },
  clamp() {
    return new Hsl(clamph(this.h), clampt(this.s), clampt(this.l), clampa(this.opacity));
  },
  displayable() {
    return (0 <= this.s && this.s <= 1 || isNaN(this.s)) && (0 <= this.l && this.l <= 1) && (0 <= this.opacity && this.opacity <= 1);
  },
  formatHsl() {
    const a = clampa(this.opacity);
    return `${a === 1 ? "hsl(" : "hsla("}${clamph(this.h)}, ${clampt(this.s) * 100}%, ${clampt(this.l) * 100}%${a === 1 ? ")" : `, ${a})`}`;
  }
}));
function clamph(value) {
  value = (value || 0) % 360;
  return value < 0 ? value + 360 : value;
}
function clampt(value) {
  return Math.max(0, Math.min(1, value || 0));
}
function hsl2rgb(h, m1, m2) {
  return (h < 60 ? m1 + (m2 - m1) * h / 60 : h < 180 ? m2 : h < 240 ? m1 + (m2 - m1) * (240 - h) / 60 : m1) * 255;
}

// node_modules/d3-interpolate/src/basis.js
function basis(t12, v0, v1, v2, v3) {
  var t2 = t12 * t12, t3 = t2 * t12;
  return ((1 - 3 * t12 + 3 * t2 - t3) * v0 + (4 - 6 * t2 + 3 * t3) * v1 + (1 + 3 * t12 + 3 * t2 - 3 * t3) * v2 + t3 * v3) / 6;
}
function basis_default(values) {
  var n = values.length - 1;
  return function(t) {
    var i = t <= 0 ? t = 0 : t >= 1 ? (t = 1, n - 1) : Math.floor(t * n), v1 = values[i], v2 = values[i + 1], v0 = i > 0 ? values[i - 1] : 2 * v1 - v2, v3 = i < n - 1 ? values[i + 2] : 2 * v2 - v1;
    return basis((t - i / n) * n, v0, v1, v2, v3);
  };
}

// node_modules/d3-interpolate/src/basisClosed.js
function basisClosed_default(values) {
  var n = values.length;
  return function(t) {
    var i = Math.floor(((t %= 1) < 0 ? ++t : t) * n), v0 = values[(i + n - 1) % n], v1 = values[i % n], v2 = values[(i + 1) % n], v3 = values[(i + 2) % n];
    return basis((t - i / n) * n, v0, v1, v2, v3);
  };
}

// node_modules/d3-interpolate/src/constant.js
var constant_default = (x2) => () => x2;

// node_modules/d3-interpolate/src/color.js
function linear(a, d) {
  return function(t) {
    return a + t * d;
  };
}
function exponential(a, b, y2) {
  return a = Math.pow(a, y2), b = Math.pow(b, y2) - a, y2 = 1 / y2, function(t) {
    return Math.pow(a + t * b, y2);
  };
}
function gamma(y2) {
  return (y2 = +y2) === 1 ? nogamma : function(a, b) {
    return b - a ? exponential(a, b, y2) : constant_default(isNaN(a) ? b : a);
  };
}
function nogamma(a, b) {
  var d = b - a;
  return d ? linear(a, d) : constant_default(isNaN(a) ? b : a);
}

// node_modules/d3-interpolate/src/rgb.js
var rgb_default = (function rgbGamma(y2) {
  var color2 = gamma(y2);
  function rgb2(start, end) {
    var r = color2((start = rgb(start)).r, (end = rgb(end)).r), g = color2(start.g, end.g), b = color2(start.b, end.b), opacity = nogamma(start.opacity, end.opacity);
    return function(t) {
      start.r = r(t);
      start.g = g(t);
      start.b = b(t);
      start.opacity = opacity(t);
      return start + "";
    };
  }
  rgb2.gamma = rgbGamma;
  return rgb2;
})(1);
function rgbSpline(spline) {
  return function(colors) {
    var n = colors.length, r = new Array(n), g = new Array(n), b = new Array(n), i, color2;
    for (i = 0; i < n; ++i) {
      color2 = rgb(colors[i]);
      r[i] = color2.r || 0;
      g[i] = color2.g || 0;
      b[i] = color2.b || 0;
    }
    r = spline(r);
    g = spline(g);
    b = spline(b);
    color2.opacity = 1;
    return function(t) {
      color2.r = r(t);
      color2.g = g(t);
      color2.b = b(t);
      return color2 + "";
    };
  };
}
var rgbBasis = rgbSpline(basis_default);
var rgbBasisClosed = rgbSpline(basisClosed_default);

// node_modules/d3-interpolate/src/numberArray.js
function numberArray_default(a, b) {
  if (!b) b = [];
  var n = a ? Math.min(b.length, a.length) : 0, c = b.slice(), i;
  return function(t) {
    for (i = 0; i < n; ++i) c[i] = a[i] * (1 - t) + b[i] * t;
    return c;
  };
}
function isNumberArray(x2) {
  return ArrayBuffer.isView(x2) && !(x2 instanceof DataView);
}

// node_modules/d3-interpolate/src/array.js
function genericArray(a, b) {
  var nb = b ? b.length : 0, na = a ? Math.min(nb, a.length) : 0, x2 = new Array(na), c = new Array(nb), i;
  for (i = 0; i < na; ++i) x2[i] = value_default(a[i], b[i]);
  for (; i < nb; ++i) c[i] = b[i];
  return function(t) {
    for (i = 0; i < na; ++i) c[i] = x2[i](t);
    return c;
  };
}

// node_modules/d3-interpolate/src/date.js
function date_default(a, b) {
  var d = /* @__PURE__ */ new Date();
  return a = +a, b = +b, function(t) {
    return d.setTime(a * (1 - t) + b * t), d;
  };
}

// node_modules/d3-interpolate/src/number.js
function number_default(a, b) {
  return a = +a, b = +b, function(t) {
    return a * (1 - t) + b * t;
  };
}

// node_modules/d3-interpolate/src/object.js
function object_default(a, b) {
  var i = {}, c = {}, k;
  if (a === null || typeof a !== "object") a = {};
  if (b === null || typeof b !== "object") b = {};
  for (k in b) {
    if (k in a) {
      i[k] = value_default(a[k], b[k]);
    } else {
      c[k] = b[k];
    }
  }
  return function(t) {
    for (k in i) c[k] = i[k](t);
    return c;
  };
}

// node_modules/d3-interpolate/src/string.js
var reA = /[-+]?(?:\d+\.?\d*|\.?\d+)(?:[eE][-+]?\d+)?/g;
var reB = new RegExp(reA.source, "g");
function zero2(b) {
  return function() {
    return b;
  };
}
function one(b) {
  return function(t) {
    return b(t) + "";
  };
}
function string_default(a, b) {
  var bi = reA.lastIndex = reB.lastIndex = 0, am, bm, bs, i = -1, s = [], q = [];
  a = a + "", b = b + "";
  while ((am = reA.exec(a)) && (bm = reB.exec(b))) {
    if ((bs = bm.index) > bi) {
      bs = b.slice(bi, bs);
      if (s[i]) s[i] += bs;
      else s[++i] = bs;
    }
    if ((am = am[0]) === (bm = bm[0])) {
      if (s[i]) s[i] += bm;
      else s[++i] = bm;
    } else {
      s[++i] = null;
      q.push({ i, x: number_default(am, bm) });
    }
    bi = reB.lastIndex;
  }
  if (bi < b.length) {
    bs = b.slice(bi);
    if (s[i]) s[i] += bs;
    else s[++i] = bs;
  }
  return s.length < 2 ? q[0] ? one(q[0].x) : zero2(b) : (b = q.length, function(t) {
    for (var i2 = 0, o; i2 < b; ++i2) s[(o = q[i2]).i] = o.x(t);
    return s.join("");
  });
}

// node_modules/d3-interpolate/src/value.js
function value_default(a, b) {
  var t = typeof b, c;
  return b == null || t === "boolean" ? constant_default(b) : (t === "number" ? number_default : t === "string" ? (c = color(b)) ? (b = c, rgb_default) : string_default : b instanceof color ? rgb_default : b instanceof Date ? date_default : isNumberArray(b) ? numberArray_default : Array.isArray(b) ? genericArray : typeof b.valueOf !== "function" && typeof b.toString !== "function" || isNaN(b) ? object_default : number_default)(a, b);
}

// node_modules/d3-interpolate/src/round.js
function round_default(a, b) {
  return a = +a, b = +b, function(t) {
    return Math.round(a * (1 - t) + b * t);
  };
}

// node_modules/d3-scale/src/constant.js
function constants(x2) {
  return function() {
    return x2;
  };
}

// node_modules/d3-scale/src/number.js
function number2(x2) {
  return +x2;
}

// node_modules/d3-scale/src/continuous.js
var unit = [0, 1];
function identity(x2) {
  return x2;
}
function normalize(a, b) {
  return (b -= a = +a) ? function(x2) {
    return (x2 - a) / b;
  } : constants(isNaN(b) ? NaN : 0.5);
}
function clamper(a, b) {
  var t;
  if (a > b) t = a, a = b, b = t;
  return function(x2) {
    return Math.max(a, Math.min(b, x2));
  };
}
function bimap(domain, range, interpolate) {
  var d0 = domain[0], d1 = domain[1], r0 = range[0], r1 = range[1];
  if (d1 < d0) d0 = normalize(d1, d0), r0 = interpolate(r1, r0);
  else d0 = normalize(d0, d1), r0 = interpolate(r0, r1);
  return function(x2) {
    return r0(d0(x2));
  };
}
function polymap(domain, range, interpolate) {
  var j = Math.min(domain.length, range.length) - 1, d = new Array(j), r = new Array(j), i = -1;
  if (domain[j] < domain[0]) {
    domain = domain.slice().reverse();
    range = range.slice().reverse();
  }
  while (++i < j) {
    d[i] = normalize(domain[i], domain[i + 1]);
    r[i] = interpolate(range[i], range[i + 1]);
  }
  return function(x2) {
    var i2 = bisect_default(domain, x2, 1, j) - 1;
    return r[i2](d[i2](x2));
  };
}
function copy(source, target) {
  return target.domain(source.domain()).range(source.range()).interpolate(source.interpolate()).clamp(source.clamp()).unknown(source.unknown());
}
function transformer() {
  var domain = unit, range = unit, interpolate = value_default, transform, untransform, unknown, clamp2 = identity, piecewise, output, input;
  function rescale() {
    var n = Math.min(domain.length, range.length);
    if (clamp2 !== identity) clamp2 = clamper(domain[0], domain[n - 1]);
    piecewise = n > 2 ? polymap : bimap;
    output = input = null;
    return scale;
  }
  function scale(x2) {
    return x2 == null || isNaN(x2 = +x2) ? unknown : (output || (output = piecewise(domain.map(transform), range, interpolate)))(transform(clamp2(x2)));
  }
  scale.invert = function(y2) {
    return clamp2(untransform((input || (input = piecewise(range, domain.map(transform), number_default)))(y2)));
  };
  scale.domain = function(_) {
    return arguments.length ? (domain = Array.from(_, number2), rescale()) : domain.slice();
  };
  scale.range = function(_) {
    return arguments.length ? (range = Array.from(_), rescale()) : range.slice();
  };
  scale.rangeRound = function(_) {
    return range = Array.from(_), interpolate = round_default, rescale();
  };
  scale.clamp = function(_) {
    return arguments.length ? (clamp2 = _ ? true : identity, rescale()) : clamp2 !== identity;
  };
  scale.interpolate = function(_) {
    return arguments.length ? (interpolate = _, rescale()) : interpolate;
  };
  scale.unknown = function(_) {
    return arguments.length ? (unknown = _, scale) : unknown;
  };
  return function(t, u) {
    transform = t, untransform = u;
    return rescale();
  };
}
function continuous() {
  return transformer()(identity, identity);
}

// node_modules/d3-format/src/formatDecimal.js
function formatDecimal_default(x2) {
  return Math.abs(x2 = Math.round(x2)) >= 1e21 ? x2.toLocaleString("en").replace(/,/g, "") : x2.toString(10);
}
function formatDecimalParts(x2, p) {
  if (!isFinite(x2) || x2 === 0) return null;
  var i = (x2 = p ? x2.toExponential(p - 1) : x2.toExponential()).indexOf("e"), coefficient = x2.slice(0, i);
  return [
    coefficient.length > 1 ? coefficient[0] + coefficient.slice(2) : coefficient,
    +x2.slice(i + 1)
  ];
}

// node_modules/d3-format/src/exponent.js
function exponent_default(x2) {
  return x2 = formatDecimalParts(Math.abs(x2)), x2 ? x2[1] : NaN;
}

// node_modules/d3-format/src/formatGroup.js
function formatGroup_default(grouping, thousands) {
  return function(value, width) {
    var i = value.length, t = [], j = 0, g = grouping[0], length = 0;
    while (i > 0 && g > 0) {
      if (length + g + 1 > width) g = Math.max(1, width - length);
      t.push(value.substring(i -= g, i + g));
      if ((length += g + 1) > width) break;
      g = grouping[j = (j + 1) % grouping.length];
    }
    return t.reverse().join(thousands);
  };
}

// node_modules/d3-format/src/formatNumerals.js
function formatNumerals_default(numerals) {
  return function(value) {
    return value.replace(/[0-9]/g, function(i) {
      return numerals[+i];
    });
  };
}

// node_modules/d3-format/src/formatSpecifier.js
var re = /^(?:(.)?([<>=^]))?([+\-( ])?([$#])?(0)?(\d+)?(,)?(\.\d+)?(~)?([a-z%])?$/i;
function formatSpecifier(specifier) {
  if (!(match = re.exec(specifier))) throw new Error("invalid format: " + specifier);
  var match;
  return new FormatSpecifier({
    fill: match[1],
    align: match[2],
    sign: match[3],
    symbol: match[4],
    zero: match[5],
    width: match[6],
    comma: match[7],
    precision: match[8] && match[8].slice(1),
    trim: match[9],
    type: match[10]
  });
}
formatSpecifier.prototype = FormatSpecifier.prototype;
function FormatSpecifier(specifier) {
  this.fill = specifier.fill === void 0 ? " " : specifier.fill + "";
  this.align = specifier.align === void 0 ? ">" : specifier.align + "";
  this.sign = specifier.sign === void 0 ? "-" : specifier.sign + "";
  this.symbol = specifier.symbol === void 0 ? "" : specifier.symbol + "";
  this.zero = !!specifier.zero;
  this.width = specifier.width === void 0 ? void 0 : +specifier.width;
  this.comma = !!specifier.comma;
  this.precision = specifier.precision === void 0 ? void 0 : +specifier.precision;
  this.trim = !!specifier.trim;
  this.type = specifier.type === void 0 ? "" : specifier.type + "";
}
FormatSpecifier.prototype.toString = function() {
  return this.fill + this.align + this.sign + this.symbol + (this.zero ? "0" : "") + (this.width === void 0 ? "" : Math.max(1, this.width | 0)) + (this.comma ? "," : "") + (this.precision === void 0 ? "" : "." + Math.max(0, this.precision | 0)) + (this.trim ? "~" : "") + this.type;
};

// node_modules/d3-format/src/formatTrim.js
function formatTrim_default(s) {
  out: for (var n = s.length, i = 1, i0 = -1, i1; i < n; ++i) {
    switch (s[i]) {
      case ".":
        i0 = i1 = i;
        break;
      case "0":
        if (i0 === 0) i0 = i;
        i1 = i;
        break;
      default:
        if (!+s[i]) break out;
        if (i0 > 0) i0 = 0;
        break;
    }
  }
  return i0 > 0 ? s.slice(0, i0) + s.slice(i1 + 1) : s;
}

// node_modules/d3-format/src/formatPrefixAuto.js
var prefixExponent;
function formatPrefixAuto_default(x2, p) {
  var d = formatDecimalParts(x2, p);
  if (!d) return prefixExponent = void 0, x2.toPrecision(p);
  var coefficient = d[0], exponent = d[1], i = exponent - (prefixExponent = Math.max(-8, Math.min(8, Math.floor(exponent / 3))) * 3) + 1, n = coefficient.length;
  return i === n ? coefficient : i > n ? coefficient + new Array(i - n + 1).join("0") : i > 0 ? coefficient.slice(0, i) + "." + coefficient.slice(i) : "0." + new Array(1 - i).join("0") + formatDecimalParts(x2, Math.max(0, p + i - 1))[0];
}

// node_modules/d3-format/src/formatRounded.js
function formatRounded_default(x2, p) {
  var d = formatDecimalParts(x2, p);
  if (!d) return x2 + "";
  var coefficient = d[0], exponent = d[1];
  return exponent < 0 ? "0." + new Array(-exponent).join("0") + coefficient : coefficient.length > exponent + 1 ? coefficient.slice(0, exponent + 1) + "." + coefficient.slice(exponent + 1) : coefficient + new Array(exponent - coefficient.length + 2).join("0");
}

// node_modules/d3-format/src/formatTypes.js
var formatTypes_default = {
  "%": (x2, p) => (x2 * 100).toFixed(p),
  "b": (x2) => Math.round(x2).toString(2),
  "c": (x2) => x2 + "",
  "d": formatDecimal_default,
  "e": (x2, p) => x2.toExponential(p),
  "f": (x2, p) => x2.toFixed(p),
  "g": (x2, p) => x2.toPrecision(p),
  "o": (x2) => Math.round(x2).toString(8),
  "p": (x2, p) => formatRounded_default(x2 * 100, p),
  "r": formatRounded_default,
  "s": formatPrefixAuto_default,
  "X": (x2) => Math.round(x2).toString(16).toUpperCase(),
  "x": (x2) => Math.round(x2).toString(16)
};

// node_modules/d3-format/src/identity.js
function identity_default(x2) {
  return x2;
}

// node_modules/d3-format/src/locale.js
var map = Array.prototype.map;
var prefixes = ["y", "z", "a", "f", "p", "n", "\xB5", "m", "", "k", "M", "G", "T", "P", "E", "Z", "Y"];
function locale_default(locale3) {
  var group = locale3.grouping === void 0 || locale3.thousands === void 0 ? identity_default : formatGroup_default(map.call(locale3.grouping, Number), locale3.thousands + ""), currencyPrefix = locale3.currency === void 0 ? "" : locale3.currency[0] + "", currencySuffix = locale3.currency === void 0 ? "" : locale3.currency[1] + "", decimal = locale3.decimal === void 0 ? "." : locale3.decimal + "", numerals = locale3.numerals === void 0 ? identity_default : formatNumerals_default(map.call(locale3.numerals, String)), percent = locale3.percent === void 0 ? "%" : locale3.percent + "", minus = locale3.minus === void 0 ? "\u2212" : locale3.minus + "", nan = locale3.nan === void 0 ? "NaN" : locale3.nan + "";
  function newFormat(specifier, options) {
    specifier = formatSpecifier(specifier);
    var fill = specifier.fill, align = specifier.align, sign2 = specifier.sign, symbol = specifier.symbol, zero3 = specifier.zero, width = specifier.width, comma = specifier.comma, precision = specifier.precision, trim = specifier.trim, type = specifier.type;
    if (type === "n") comma = true, type = "g";
    else if (!formatTypes_default[type]) precision === void 0 && (precision = 12), trim = true, type = "g";
    if (zero3 || fill === "0" && align === "=") zero3 = true, fill = "0", align = "=";
    var prefix = (options && options.prefix !== void 0 ? options.prefix : "") + (symbol === "$" ? currencyPrefix : symbol === "#" && /[boxX]/.test(type) ? "0" + type.toLowerCase() : ""), suffix = (symbol === "$" ? currencySuffix : /[%p]/.test(type) ? percent : "") + (options && options.suffix !== void 0 ? options.suffix : "");
    var formatType = formatTypes_default[type], maybeSuffix = /[defgprs%]/.test(type);
    precision = precision === void 0 ? 6 : /[gprs]/.test(type) ? Math.max(1, Math.min(21, precision)) : Math.max(0, Math.min(20, precision));
    function format2(value) {
      var valuePrefix = prefix, valueSuffix = suffix, i, n, c;
      if (type === "c") {
        valueSuffix = formatType(value) + valueSuffix;
        value = "";
      } else {
        value = +value;
        var valueNegative = value < 0 || 1 / value < 0;
        value = isNaN(value) ? nan : formatType(Math.abs(value), precision);
        if (trim) value = formatTrim_default(value);
        if (valueNegative && +value === 0 && sign2 !== "+") valueNegative = false;
        valuePrefix = (valueNegative ? sign2 === "(" ? sign2 : minus : sign2 === "-" || sign2 === "(" ? "" : sign2) + valuePrefix;
        valueSuffix = (type === "s" && !isNaN(value) && prefixExponent !== void 0 ? prefixes[8 + prefixExponent / 3] : "") + valueSuffix + (valueNegative && sign2 === "(" ? ")" : "");
        if (maybeSuffix) {
          i = -1, n = value.length;
          while (++i < n) {
            if (c = value.charCodeAt(i), 48 > c || c > 57) {
              valueSuffix = (c === 46 ? decimal + value.slice(i + 1) : value.slice(i)) + valueSuffix;
              value = value.slice(0, i);
              break;
            }
          }
        }
      }
      if (comma && !zero3) value = group(value, Infinity);
      var length = valuePrefix.length + value.length + valueSuffix.length, padding = length < width ? new Array(width - length + 1).join(fill) : "";
      if (comma && zero3) value = group(padding + value, padding.length ? width - valueSuffix.length : Infinity), padding = "";
      switch (align) {
        case "<":
          value = valuePrefix + value + valueSuffix + padding;
          break;
        case "=":
          value = valuePrefix + padding + value + valueSuffix;
          break;
        case "^":
          value = padding.slice(0, length = padding.length >> 1) + valuePrefix + value + valueSuffix + padding.slice(length);
          break;
        default:
          value = padding + valuePrefix + value + valueSuffix;
          break;
      }
      return numerals(value);
    }
    format2.toString = function() {
      return specifier + "";
    };
    return format2;
  }
  function formatPrefix2(specifier, value) {
    var e = Math.max(-8, Math.min(8, Math.floor(exponent_default(value) / 3))) * 3, k = Math.pow(10, -e), f = newFormat((specifier = formatSpecifier(specifier), specifier.type = "f", specifier), { suffix: prefixes[8 + e / 3] });
    return function(value2) {
      return f(k * value2);
    };
  }
  return {
    format: newFormat,
    formatPrefix: formatPrefix2
  };
}

// node_modules/d3-format/src/defaultLocale.js
var locale;
var format;
var formatPrefix;
defaultLocale({
  thousands: ",",
  grouping: [3],
  currency: ["$", ""]
});
function defaultLocale(definition) {
  locale = locale_default(definition);
  format = locale.format;
  formatPrefix = locale.formatPrefix;
  return locale;
}

// node_modules/d3-format/src/precisionFixed.js
function precisionFixed_default(step) {
  return Math.max(0, -exponent_default(Math.abs(step)));
}

// node_modules/d3-format/src/precisionPrefix.js
function precisionPrefix_default(step, value) {
  return Math.max(0, Math.max(-8, Math.min(8, Math.floor(exponent_default(value) / 3))) * 3 - exponent_default(Math.abs(step)));
}

// node_modules/d3-format/src/precisionRound.js
function precisionRound_default(step, max) {
  step = Math.abs(step), max = Math.abs(max) - step;
  return Math.max(0, exponent_default(max) - exponent_default(step)) + 1;
}

// node_modules/d3-scale/src/tickFormat.js
function tickFormat(start, stop, count, specifier) {
  var step = tickStep(start, stop, count), precision;
  specifier = formatSpecifier(specifier == null ? ",f" : specifier);
  switch (specifier.type) {
    case "s": {
      var value = Math.max(Math.abs(start), Math.abs(stop));
      if (specifier.precision == null && !isNaN(precision = precisionPrefix_default(step, value))) specifier.precision = precision;
      return formatPrefix(specifier, value);
    }
    case "":
    case "e":
    case "g":
    case "p":
    case "r": {
      if (specifier.precision == null && !isNaN(precision = precisionRound_default(step, Math.max(Math.abs(start), Math.abs(stop))))) specifier.precision = precision - (specifier.type === "e");
      break;
    }
    case "f":
    case "%": {
      if (specifier.precision == null && !isNaN(precision = precisionFixed_default(step))) specifier.precision = precision - (specifier.type === "%") * 2;
      break;
    }
  }
  return format(specifier);
}

// node_modules/d3-scale/src/linear.js
function linearish(scale) {
  var domain = scale.domain;
  scale.ticks = function(count) {
    var d = domain();
    return ticks(d[0], d[d.length - 1], count == null ? 10 : count);
  };
  scale.tickFormat = function(count, specifier) {
    var d = domain();
    return tickFormat(d[0], d[d.length - 1], count == null ? 10 : count, specifier);
  };
  scale.nice = function(count) {
    if (count == null) count = 10;
    var d = domain();
    var i0 = 0;
    var i1 = d.length - 1;
    var start = d[i0];
    var stop = d[i1];
    var prestep;
    var step;
    var maxIter = 10;
    if (stop < start) {
      step = start, start = stop, stop = step;
      step = i0, i0 = i1, i1 = step;
    }
    while (maxIter-- > 0) {
      step = tickIncrement(start, stop, count);
      if (step === prestep) {
        d[i0] = start;
        d[i1] = stop;
        return domain(d);
      } else if (step > 0) {
        start = Math.floor(start / step) * step;
        stop = Math.ceil(stop / step) * step;
      } else if (step < 0) {
        start = Math.ceil(start * step) / step;
        stop = Math.floor(stop * step) / step;
      } else {
        break;
      }
      prestep = step;
    }
    return scale;
  };
  return scale;
}
function linear2() {
  var scale = continuous();
  scale.copy = function() {
    return copy(scale, linear2());
  };
  initRange.apply(scale, arguments);
  return linearish(scale);
}

// node_modules/d3-scale/src/nice.js
function nice(domain, interval) {
  domain = domain.slice();
  var i0 = 0, i1 = domain.length - 1, x0 = domain[i0], x1 = domain[i1], t;
  if (x1 < x0) {
    t = i0, i0 = i1, i1 = t;
    t = x0, x0 = x1, x1 = t;
  }
  domain[i0] = interval.floor(x0);
  domain[i1] = interval.ceil(x1);
  return domain;
}

// node_modules/d3-time/src/interval.js
var t0 = /* @__PURE__ */ new Date();
var t1 = /* @__PURE__ */ new Date();
function timeInterval(floori, offseti, count, field) {
  function interval(date2) {
    return floori(date2 = arguments.length === 0 ? /* @__PURE__ */ new Date() : /* @__PURE__ */ new Date(+date2)), date2;
  }
  interval.floor = (date2) => {
    return floori(date2 = /* @__PURE__ */ new Date(+date2)), date2;
  };
  interval.ceil = (date2) => {
    return floori(date2 = new Date(date2 - 1)), offseti(date2, 1), floori(date2), date2;
  };
  interval.round = (date2) => {
    const d0 = interval(date2), d1 = interval.ceil(date2);
    return date2 - d0 < d1 - date2 ? d0 : d1;
  };
  interval.offset = (date2, step) => {
    return offseti(date2 = /* @__PURE__ */ new Date(+date2), step == null ? 1 : Math.floor(step)), date2;
  };
  interval.range = (start, stop, step) => {
    const range = [];
    start = interval.ceil(start);
    step = step == null ? 1 : Math.floor(step);
    if (!(start < stop) || !(step > 0)) return range;
    let previous;
    do
      range.push(previous = /* @__PURE__ */ new Date(+start)), offseti(start, step), floori(start);
    while (previous < start && start < stop);
    return range;
  };
  interval.filter = (test) => {
    return timeInterval((date2) => {
      if (date2 >= date2) while (floori(date2), !test(date2)) date2.setTime(date2 - 1);
    }, (date2, step) => {
      if (date2 >= date2) {
        if (step < 0) while (++step <= 0) {
          while (offseti(date2, -1), !test(date2)) {
          }
        }
        else while (--step >= 0) {
          while (offseti(date2, 1), !test(date2)) {
          }
        }
      }
    });
  };
  if (count) {
    interval.count = (start, end) => {
      t0.setTime(+start), t1.setTime(+end);
      floori(t0), floori(t1);
      return Math.floor(count(t0, t1));
    };
    interval.every = (step) => {
      step = Math.floor(step);
      return !isFinite(step) || !(step > 0) ? null : !(step > 1) ? interval : interval.filter(field ? (d) => field(d) % step === 0 : (d) => interval.count(0, d) % step === 0);
    };
  }
  return interval;
}

// node_modules/d3-time/src/millisecond.js
var millisecond = timeInterval(() => {
}, (date2, step) => {
  date2.setTime(+date2 + step);
}, (start, end) => {
  return end - start;
});
millisecond.every = (k) => {
  k = Math.floor(k);
  if (!isFinite(k) || !(k > 0)) return null;
  if (!(k > 1)) return millisecond;
  return timeInterval((date2) => {
    date2.setTime(Math.floor(date2 / k) * k);
  }, (date2, step) => {
    date2.setTime(+date2 + step * k);
  }, (start, end) => {
    return (end - start) / k;
  });
};
var milliseconds = millisecond.range;

// node_modules/d3-time/src/duration.js
var durationSecond = 1e3;
var durationMinute = durationSecond * 60;
var durationHour = durationMinute * 60;
var durationDay = durationHour * 24;
var durationWeek = durationDay * 7;
var durationMonth = durationDay * 30;
var durationYear = durationDay * 365;

// node_modules/d3-time/src/second.js
var second = timeInterval((date2) => {
  date2.setTime(date2 - date2.getMilliseconds());
}, (date2, step) => {
  date2.setTime(+date2 + step * durationSecond);
}, (start, end) => {
  return (end - start) / durationSecond;
}, (date2) => {
  return date2.getUTCSeconds();
});
var seconds = second.range;

// node_modules/d3-time/src/minute.js
var timeMinute = timeInterval((date2) => {
  date2.setTime(date2 - date2.getMilliseconds() - date2.getSeconds() * durationSecond);
}, (date2, step) => {
  date2.setTime(+date2 + step * durationMinute);
}, (start, end) => {
  return (end - start) / durationMinute;
}, (date2) => {
  return date2.getMinutes();
});
var timeMinutes = timeMinute.range;
var utcMinute = timeInterval((date2) => {
  date2.setUTCSeconds(0, 0);
}, (date2, step) => {
  date2.setTime(+date2 + step * durationMinute);
}, (start, end) => {
  return (end - start) / durationMinute;
}, (date2) => {
  return date2.getUTCMinutes();
});
var utcMinutes = utcMinute.range;

// node_modules/d3-time/src/hour.js
var timeHour = timeInterval((date2) => {
  date2.setTime(date2 - date2.getMilliseconds() - date2.getSeconds() * durationSecond - date2.getMinutes() * durationMinute);
}, (date2, step) => {
  date2.setTime(+date2 + step * durationHour);
}, (start, end) => {
  return (end - start) / durationHour;
}, (date2) => {
  return date2.getHours();
});
var timeHours = timeHour.range;
var utcHour = timeInterval((date2) => {
  date2.setUTCMinutes(0, 0, 0);
}, (date2, step) => {
  date2.setTime(+date2 + step * durationHour);
}, (start, end) => {
  return (end - start) / durationHour;
}, (date2) => {
  return date2.getUTCHours();
});
var utcHours = utcHour.range;

// node_modules/d3-time/src/day.js
var timeDay = timeInterval(
  (date2) => date2.setHours(0, 0, 0, 0),
  (date2, step) => date2.setDate(date2.getDate() + step),
  (start, end) => (end - start - (end.getTimezoneOffset() - start.getTimezoneOffset()) * durationMinute) / durationDay,
  (date2) => date2.getDate() - 1
);
var timeDays = timeDay.range;
var utcDay = timeInterval((date2) => {
  date2.setUTCHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setUTCDate(date2.getUTCDate() + step);
}, (start, end) => {
  return (end - start) / durationDay;
}, (date2) => {
  return date2.getUTCDate() - 1;
});
var utcDays = utcDay.range;
var unixDay = timeInterval((date2) => {
  date2.setUTCHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setUTCDate(date2.getUTCDate() + step);
}, (start, end) => {
  return (end - start) / durationDay;
}, (date2) => {
  return Math.floor(date2 / durationDay);
});
var unixDays = unixDay.range;

// node_modules/d3-time/src/week.js
function timeWeekday(i) {
  return timeInterval((date2) => {
    date2.setDate(date2.getDate() - (date2.getDay() + 7 - i) % 7);
    date2.setHours(0, 0, 0, 0);
  }, (date2, step) => {
    date2.setDate(date2.getDate() + step * 7);
  }, (start, end) => {
    return (end - start - (end.getTimezoneOffset() - start.getTimezoneOffset()) * durationMinute) / durationWeek;
  });
}
var timeSunday = timeWeekday(0);
var timeMonday = timeWeekday(1);
var timeTuesday = timeWeekday(2);
var timeWednesday = timeWeekday(3);
var timeThursday = timeWeekday(4);
var timeFriday = timeWeekday(5);
var timeSaturday = timeWeekday(6);
var timeSundays = timeSunday.range;
var timeMondays = timeMonday.range;
var timeTuesdays = timeTuesday.range;
var timeWednesdays = timeWednesday.range;
var timeThursdays = timeThursday.range;
var timeFridays = timeFriday.range;
var timeSaturdays = timeSaturday.range;
function utcWeekday(i) {
  return timeInterval((date2) => {
    date2.setUTCDate(date2.getUTCDate() - (date2.getUTCDay() + 7 - i) % 7);
    date2.setUTCHours(0, 0, 0, 0);
  }, (date2, step) => {
    date2.setUTCDate(date2.getUTCDate() + step * 7);
  }, (start, end) => {
    return (end - start) / durationWeek;
  });
}
var utcSunday = utcWeekday(0);
var utcMonday = utcWeekday(1);
var utcTuesday = utcWeekday(2);
var utcWednesday = utcWeekday(3);
var utcThursday = utcWeekday(4);
var utcFriday = utcWeekday(5);
var utcSaturday = utcWeekday(6);
var utcSundays = utcSunday.range;
var utcMondays = utcMonday.range;
var utcTuesdays = utcTuesday.range;
var utcWednesdays = utcWednesday.range;
var utcThursdays = utcThursday.range;
var utcFridays = utcFriday.range;
var utcSaturdays = utcSaturday.range;

// node_modules/d3-time/src/month.js
var timeMonth = timeInterval((date2) => {
  date2.setDate(1);
  date2.setHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setMonth(date2.getMonth() + step);
}, (start, end) => {
  return end.getMonth() - start.getMonth() + (end.getFullYear() - start.getFullYear()) * 12;
}, (date2) => {
  return date2.getMonth();
});
var timeMonths = timeMonth.range;
var utcMonth = timeInterval((date2) => {
  date2.setUTCDate(1);
  date2.setUTCHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setUTCMonth(date2.getUTCMonth() + step);
}, (start, end) => {
  return end.getUTCMonth() - start.getUTCMonth() + (end.getUTCFullYear() - start.getUTCFullYear()) * 12;
}, (date2) => {
  return date2.getUTCMonth();
});
var utcMonths = utcMonth.range;

// node_modules/d3-time/src/year.js
var timeYear = timeInterval((date2) => {
  date2.setMonth(0, 1);
  date2.setHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setFullYear(date2.getFullYear() + step);
}, (start, end) => {
  return end.getFullYear() - start.getFullYear();
}, (date2) => {
  return date2.getFullYear();
});
timeYear.every = (k) => {
  return !isFinite(k = Math.floor(k)) || !(k > 0) ? null : timeInterval((date2) => {
    date2.setFullYear(Math.floor(date2.getFullYear() / k) * k);
    date2.setMonth(0, 1);
    date2.setHours(0, 0, 0, 0);
  }, (date2, step) => {
    date2.setFullYear(date2.getFullYear() + step * k);
  });
};
var timeYears = timeYear.range;
var utcYear = timeInterval((date2) => {
  date2.setUTCMonth(0, 1);
  date2.setUTCHours(0, 0, 0, 0);
}, (date2, step) => {
  date2.setUTCFullYear(date2.getUTCFullYear() + step);
}, (start, end) => {
  return end.getUTCFullYear() - start.getUTCFullYear();
}, (date2) => {
  return date2.getUTCFullYear();
});
utcYear.every = (k) => {
  return !isFinite(k = Math.floor(k)) || !(k > 0) ? null : timeInterval((date2) => {
    date2.setUTCFullYear(Math.floor(date2.getUTCFullYear() / k) * k);
    date2.setUTCMonth(0, 1);
    date2.setUTCHours(0, 0, 0, 0);
  }, (date2, step) => {
    date2.setUTCFullYear(date2.getUTCFullYear() + step * k);
  });
};
var utcYears = utcYear.range;

// node_modules/d3-time/src/ticks.js
function ticker(year, month, week, day, hour, minute) {
  const tickIntervals = [
    [second, 1, durationSecond],
    [second, 5, 5 * durationSecond],
    [second, 15, 15 * durationSecond],
    [second, 30, 30 * durationSecond],
    [minute, 1, durationMinute],
    [minute, 5, 5 * durationMinute],
    [minute, 15, 15 * durationMinute],
    [minute, 30, 30 * durationMinute],
    [hour, 1, durationHour],
    [hour, 3, 3 * durationHour],
    [hour, 6, 6 * durationHour],
    [hour, 12, 12 * durationHour],
    [day, 1, durationDay],
    [day, 2, 2 * durationDay],
    [week, 1, durationWeek],
    [month, 1, durationMonth],
    [month, 3, 3 * durationMonth],
    [year, 1, durationYear]
  ];
  function ticks2(start, stop, count) {
    const reverse = stop < start;
    if (reverse) [start, stop] = [stop, start];
    const interval = count && typeof count.range === "function" ? count : tickInterval(start, stop, count);
    const ticks3 = interval ? interval.range(start, +stop + 1) : [];
    return reverse ? ticks3.reverse() : ticks3;
  }
  function tickInterval(start, stop, count) {
    const target = Math.abs(stop - start) / count;
    const i = bisector(([, , step2]) => step2).right(tickIntervals, target);
    if (i === tickIntervals.length) return year.every(tickStep(start / durationYear, stop / durationYear, count));
    if (i === 0) return millisecond.every(Math.max(tickStep(start, stop, count), 1));
    const [t, step] = tickIntervals[target / tickIntervals[i - 1][2] < tickIntervals[i][2] / target ? i - 1 : i];
    return t.every(step);
  }
  return [ticks2, tickInterval];
}
var [utcTicks, utcTickInterval] = ticker(utcYear, utcMonth, utcSunday, unixDay, utcHour, utcMinute);
var [timeTicks, timeTickInterval] = ticker(timeYear, timeMonth, timeSunday, timeDay, timeHour, timeMinute);

// node_modules/d3-time-format/src/locale.js
function localDate(d) {
  if (0 <= d.y && d.y < 100) {
    var date2 = new Date(-1, d.m, d.d, d.H, d.M, d.S, d.L);
    date2.setFullYear(d.y);
    return date2;
  }
  return new Date(d.y, d.m, d.d, d.H, d.M, d.S, d.L);
}
function utcDate(d) {
  if (0 <= d.y && d.y < 100) {
    var date2 = new Date(Date.UTC(-1, d.m, d.d, d.H, d.M, d.S, d.L));
    date2.setUTCFullYear(d.y);
    return date2;
  }
  return new Date(Date.UTC(d.y, d.m, d.d, d.H, d.M, d.S, d.L));
}
function newDate(y2, m, d) {
  return { y: y2, m, d, H: 0, M: 0, S: 0, L: 0 };
}
function formatLocale(locale3) {
  var locale_dateTime = locale3.dateTime, locale_date = locale3.date, locale_time = locale3.time, locale_periods = locale3.periods, locale_weekdays = locale3.days, locale_shortWeekdays = locale3.shortDays, locale_months = locale3.months, locale_shortMonths = locale3.shortMonths;
  var periodRe = formatRe(locale_periods), periodLookup = formatLookup(locale_periods), weekdayRe = formatRe(locale_weekdays), weekdayLookup = formatLookup(locale_weekdays), shortWeekdayRe = formatRe(locale_shortWeekdays), shortWeekdayLookup = formatLookup(locale_shortWeekdays), monthRe = formatRe(locale_months), monthLookup = formatLookup(locale_months), shortMonthRe = formatRe(locale_shortMonths), shortMonthLookup = formatLookup(locale_shortMonths);
  var formats = {
    "a": formatShortWeekday,
    "A": formatWeekday,
    "b": formatShortMonth,
    "B": formatMonth,
    "c": null,
    "d": formatDayOfMonth,
    "e": formatDayOfMonth,
    "f": formatMicroseconds,
    "g": formatYearISO,
    "G": formatFullYearISO,
    "H": formatHour24,
    "I": formatHour12,
    "j": formatDayOfYear,
    "L": formatMilliseconds,
    "m": formatMonthNumber,
    "M": formatMinutes,
    "p": formatPeriod,
    "q": formatQuarter,
    "Q": formatUnixTimestamp,
    "s": formatUnixTimestampSeconds,
    "S": formatSeconds,
    "u": formatWeekdayNumberMonday,
    "U": formatWeekNumberSunday,
    "V": formatWeekNumberISO,
    "w": formatWeekdayNumberSunday,
    "W": formatWeekNumberMonday,
    "x": null,
    "X": null,
    "y": formatYear,
    "Y": formatFullYear,
    "Z": formatZone,
    "%": formatLiteralPercent
  };
  var utcFormats = {
    "a": formatUTCShortWeekday,
    "A": formatUTCWeekday,
    "b": formatUTCShortMonth,
    "B": formatUTCMonth,
    "c": null,
    "d": formatUTCDayOfMonth,
    "e": formatUTCDayOfMonth,
    "f": formatUTCMicroseconds,
    "g": formatUTCYearISO,
    "G": formatUTCFullYearISO,
    "H": formatUTCHour24,
    "I": formatUTCHour12,
    "j": formatUTCDayOfYear,
    "L": formatUTCMilliseconds,
    "m": formatUTCMonthNumber,
    "M": formatUTCMinutes,
    "p": formatUTCPeriod,
    "q": formatUTCQuarter,
    "Q": formatUnixTimestamp,
    "s": formatUnixTimestampSeconds,
    "S": formatUTCSeconds,
    "u": formatUTCWeekdayNumberMonday,
    "U": formatUTCWeekNumberSunday,
    "V": formatUTCWeekNumberISO,
    "w": formatUTCWeekdayNumberSunday,
    "W": formatUTCWeekNumberMonday,
    "x": null,
    "X": null,
    "y": formatUTCYear,
    "Y": formatUTCFullYear,
    "Z": formatUTCZone,
    "%": formatLiteralPercent
  };
  var parses = {
    "a": parseShortWeekday,
    "A": parseWeekday,
    "b": parseShortMonth,
    "B": parseMonth,
    "c": parseLocaleDateTime,
    "d": parseDayOfMonth,
    "e": parseDayOfMonth,
    "f": parseMicroseconds,
    "g": parseYear,
    "G": parseFullYear,
    "H": parseHour24,
    "I": parseHour24,
    "j": parseDayOfYear,
    "L": parseMilliseconds,
    "m": parseMonthNumber,
    "M": parseMinutes,
    "p": parsePeriod,
    "q": parseQuarter,
    "Q": parseUnixTimestamp,
    "s": parseUnixTimestampSeconds,
    "S": parseSeconds,
    "u": parseWeekdayNumberMonday,
    "U": parseWeekNumberSunday,
    "V": parseWeekNumberISO,
    "w": parseWeekdayNumberSunday,
    "W": parseWeekNumberMonday,
    "x": parseLocaleDate,
    "X": parseLocaleTime,
    "y": parseYear,
    "Y": parseFullYear,
    "Z": parseZone,
    "%": parseLiteralPercent
  };
  formats.x = newFormat(locale_date, formats);
  formats.X = newFormat(locale_time, formats);
  formats.c = newFormat(locale_dateTime, formats);
  utcFormats.x = newFormat(locale_date, utcFormats);
  utcFormats.X = newFormat(locale_time, utcFormats);
  utcFormats.c = newFormat(locale_dateTime, utcFormats);
  function newFormat(specifier, formats2) {
    return function(date2) {
      var string = [], i = -1, j = 0, n = specifier.length, c, pad2, format2;
      if (!(date2 instanceof Date)) date2 = /* @__PURE__ */ new Date(+date2);
      while (++i < n) {
        if (specifier.charCodeAt(i) === 37) {
          string.push(specifier.slice(j, i));
          if ((pad2 = pads[c = specifier.charAt(++i)]) != null) c = specifier.charAt(++i);
          else pad2 = c === "e" ? " " : "0";
          if (format2 = formats2[c]) c = format2(date2, pad2);
          string.push(c);
          j = i + 1;
        }
      }
      string.push(specifier.slice(j, i));
      return string.join("");
    };
  }
  function newParse(specifier, Z) {
    return function(string) {
      var d = newDate(1900, void 0, 1), i = parseSpecifier(d, specifier, string += "", 0), week, day;
      if (i != string.length) return null;
      if ("Q" in d) return new Date(d.Q);
      if ("s" in d) return new Date(d.s * 1e3 + ("L" in d ? d.L : 0));
      if (Z && !("Z" in d)) d.Z = 0;
      if ("p" in d) d.H = d.H % 12 + d.p * 12;
      if (d.m === void 0) d.m = "q" in d ? d.q : 0;
      if ("V" in d) {
        if (d.V < 1 || d.V > 53) return null;
        if (!("w" in d)) d.w = 1;
        if ("Z" in d) {
          week = utcDate(newDate(d.y, 0, 1)), day = week.getUTCDay();
          week = day > 4 || day === 0 ? utcMonday.ceil(week) : utcMonday(week);
          week = utcDay.offset(week, (d.V - 1) * 7);
          d.y = week.getUTCFullYear();
          d.m = week.getUTCMonth();
          d.d = week.getUTCDate() + (d.w + 6) % 7;
        } else {
          week = localDate(newDate(d.y, 0, 1)), day = week.getDay();
          week = day > 4 || day === 0 ? timeMonday.ceil(week) : timeMonday(week);
          week = timeDay.offset(week, (d.V - 1) * 7);
          d.y = week.getFullYear();
          d.m = week.getMonth();
          d.d = week.getDate() + (d.w + 6) % 7;
        }
      } else if ("W" in d || "U" in d) {
        if (!("w" in d)) d.w = "u" in d ? d.u % 7 : "W" in d ? 1 : 0;
        day = "Z" in d ? utcDate(newDate(d.y, 0, 1)).getUTCDay() : localDate(newDate(d.y, 0, 1)).getDay();
        d.m = 0;
        d.d = "W" in d ? (d.w + 6) % 7 + d.W * 7 - (day + 5) % 7 : d.w + d.U * 7 - (day + 6) % 7;
      }
      if ("Z" in d) {
        d.H += d.Z / 100 | 0;
        d.M += d.Z % 100;
        return utcDate(d);
      }
      return localDate(d);
    };
  }
  function parseSpecifier(d, specifier, string, j) {
    var i = 0, n = specifier.length, m = string.length, c, parse;
    while (i < n) {
      if (j >= m) return -1;
      c = specifier.charCodeAt(i++);
      if (c === 37) {
        c = specifier.charAt(i++);
        parse = parses[c in pads ? specifier.charAt(i++) : c];
        if (!parse || (j = parse(d, string, j)) < 0) return -1;
      } else if (c != string.charCodeAt(j++)) {
        return -1;
      }
    }
    return j;
  }
  function parsePeriod(d, string, i) {
    var n = periodRe.exec(string.slice(i));
    return n ? (d.p = periodLookup.get(n[0].toLowerCase()), i + n[0].length) : -1;
  }
  function parseShortWeekday(d, string, i) {
    var n = shortWeekdayRe.exec(string.slice(i));
    return n ? (d.w = shortWeekdayLookup.get(n[0].toLowerCase()), i + n[0].length) : -1;
  }
  function parseWeekday(d, string, i) {
    var n = weekdayRe.exec(string.slice(i));
    return n ? (d.w = weekdayLookup.get(n[0].toLowerCase()), i + n[0].length) : -1;
  }
  function parseShortMonth(d, string, i) {
    var n = shortMonthRe.exec(string.slice(i));
    return n ? (d.m = shortMonthLookup.get(n[0].toLowerCase()), i + n[0].length) : -1;
  }
  function parseMonth(d, string, i) {
    var n = monthRe.exec(string.slice(i));
    return n ? (d.m = monthLookup.get(n[0].toLowerCase()), i + n[0].length) : -1;
  }
  function parseLocaleDateTime(d, string, i) {
    return parseSpecifier(d, locale_dateTime, string, i);
  }
  function parseLocaleDate(d, string, i) {
    return parseSpecifier(d, locale_date, string, i);
  }
  function parseLocaleTime(d, string, i) {
    return parseSpecifier(d, locale_time, string, i);
  }
  function formatShortWeekday(d) {
    return locale_shortWeekdays[d.getDay()];
  }
  function formatWeekday(d) {
    return locale_weekdays[d.getDay()];
  }
  function formatShortMonth(d) {
    return locale_shortMonths[d.getMonth()];
  }
  function formatMonth(d) {
    return locale_months[d.getMonth()];
  }
  function formatPeriod(d) {
    return locale_periods[+(d.getHours() >= 12)];
  }
  function formatQuarter(d) {
    return 1 + ~~(d.getMonth() / 3);
  }
  function formatUTCShortWeekday(d) {
    return locale_shortWeekdays[d.getUTCDay()];
  }
  function formatUTCWeekday(d) {
    return locale_weekdays[d.getUTCDay()];
  }
  function formatUTCShortMonth(d) {
    return locale_shortMonths[d.getUTCMonth()];
  }
  function formatUTCMonth(d) {
    return locale_months[d.getUTCMonth()];
  }
  function formatUTCPeriod(d) {
    return locale_periods[+(d.getUTCHours() >= 12)];
  }
  function formatUTCQuarter(d) {
    return 1 + ~~(d.getUTCMonth() / 3);
  }
  return {
    format: function(specifier) {
      var f = newFormat(specifier += "", formats);
      f.toString = function() {
        return specifier;
      };
      return f;
    },
    parse: function(specifier) {
      var p = newParse(specifier += "", false);
      p.toString = function() {
        return specifier;
      };
      return p;
    },
    utcFormat: function(specifier) {
      var f = newFormat(specifier += "", utcFormats);
      f.toString = function() {
        return specifier;
      };
      return f;
    },
    utcParse: function(specifier) {
      var p = newParse(specifier += "", true);
      p.toString = function() {
        return specifier;
      };
      return p;
    }
  };
}
var pads = { "-": "", "_": " ", "0": "0" };
var numberRe = /^\s*\d+/;
var percentRe = /^%/;
var requoteRe = /[\\^$*+?|[\]().{}]/g;
function pad(value, fill, width) {
  var sign2 = value < 0 ? "-" : "", string = (sign2 ? -value : value) + "", length = string.length;
  return sign2 + (length < width ? new Array(width - length + 1).join(fill) + string : string);
}
function requote(s) {
  return s.replace(requoteRe, "\\$&");
}
function formatRe(names) {
  return new RegExp("^(?:" + names.map(requote).join("|") + ")", "i");
}
function formatLookup(names) {
  return new Map(names.map((name, i) => [name.toLowerCase(), i]));
}
function parseWeekdayNumberSunday(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 1));
  return n ? (d.w = +n[0], i + n[0].length) : -1;
}
function parseWeekdayNumberMonday(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 1));
  return n ? (d.u = +n[0], i + n[0].length) : -1;
}
function parseWeekNumberSunday(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.U = +n[0], i + n[0].length) : -1;
}
function parseWeekNumberISO(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.V = +n[0], i + n[0].length) : -1;
}
function parseWeekNumberMonday(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.W = +n[0], i + n[0].length) : -1;
}
function parseFullYear(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 4));
  return n ? (d.y = +n[0], i + n[0].length) : -1;
}
function parseYear(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.y = +n[0] + (+n[0] > 68 ? 1900 : 2e3), i + n[0].length) : -1;
}
function parseZone(d, string, i) {
  var n = /^(Z)|([+-]\d\d)(?::?(\d\d))?/.exec(string.slice(i, i + 6));
  return n ? (d.Z = n[1] ? 0 : -(n[2] + (n[3] || "00")), i + n[0].length) : -1;
}
function parseQuarter(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 1));
  return n ? (d.q = n[0] * 3 - 3, i + n[0].length) : -1;
}
function parseMonthNumber(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.m = n[0] - 1, i + n[0].length) : -1;
}
function parseDayOfMonth(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.d = +n[0], i + n[0].length) : -1;
}
function parseDayOfYear(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 3));
  return n ? (d.m = 0, d.d = +n[0], i + n[0].length) : -1;
}
function parseHour24(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.H = +n[0], i + n[0].length) : -1;
}
function parseMinutes(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.M = +n[0], i + n[0].length) : -1;
}
function parseSeconds(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 2));
  return n ? (d.S = +n[0], i + n[0].length) : -1;
}
function parseMilliseconds(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 3));
  return n ? (d.L = +n[0], i + n[0].length) : -1;
}
function parseMicroseconds(d, string, i) {
  var n = numberRe.exec(string.slice(i, i + 6));
  return n ? (d.L = Math.floor(n[0] / 1e3), i + n[0].length) : -1;
}
function parseLiteralPercent(d, string, i) {
  var n = percentRe.exec(string.slice(i, i + 1));
  return n ? i + n[0].length : -1;
}
function parseUnixTimestamp(d, string, i) {
  var n = numberRe.exec(string.slice(i));
  return n ? (d.Q = +n[0], i + n[0].length) : -1;
}
function parseUnixTimestampSeconds(d, string, i) {
  var n = numberRe.exec(string.slice(i));
  return n ? (d.s = +n[0], i + n[0].length) : -1;
}
function formatDayOfMonth(d, p) {
  return pad(d.getDate(), p, 2);
}
function formatHour24(d, p) {
  return pad(d.getHours(), p, 2);
}
function formatHour12(d, p) {
  return pad(d.getHours() % 12 || 12, p, 2);
}
function formatDayOfYear(d, p) {
  return pad(1 + timeDay.count(timeYear(d), d), p, 3);
}
function formatMilliseconds(d, p) {
  return pad(d.getMilliseconds(), p, 3);
}
function formatMicroseconds(d, p) {
  return formatMilliseconds(d, p) + "000";
}
function formatMonthNumber(d, p) {
  return pad(d.getMonth() + 1, p, 2);
}
function formatMinutes(d, p) {
  return pad(d.getMinutes(), p, 2);
}
function formatSeconds(d, p) {
  return pad(d.getSeconds(), p, 2);
}
function formatWeekdayNumberMonday(d) {
  var day = d.getDay();
  return day === 0 ? 7 : day;
}
function formatWeekNumberSunday(d, p) {
  return pad(timeSunday.count(timeYear(d) - 1, d), p, 2);
}
function dISO(d) {
  var day = d.getDay();
  return day >= 4 || day === 0 ? timeThursday(d) : timeThursday.ceil(d);
}
function formatWeekNumberISO(d, p) {
  d = dISO(d);
  return pad(timeThursday.count(timeYear(d), d) + (timeYear(d).getDay() === 4), p, 2);
}
function formatWeekdayNumberSunday(d) {
  return d.getDay();
}
function formatWeekNumberMonday(d, p) {
  return pad(timeMonday.count(timeYear(d) - 1, d), p, 2);
}
function formatYear(d, p) {
  return pad(d.getFullYear() % 100, p, 2);
}
function formatYearISO(d, p) {
  d = dISO(d);
  return pad(d.getFullYear() % 100, p, 2);
}
function formatFullYear(d, p) {
  return pad(d.getFullYear() % 1e4, p, 4);
}
function formatFullYearISO(d, p) {
  var day = d.getDay();
  d = day >= 4 || day === 0 ? timeThursday(d) : timeThursday.ceil(d);
  return pad(d.getFullYear() % 1e4, p, 4);
}
function formatZone(d) {
  var z = d.getTimezoneOffset();
  return (z > 0 ? "-" : (z *= -1, "+")) + pad(z / 60 | 0, "0", 2) + pad(z % 60, "0", 2);
}
function formatUTCDayOfMonth(d, p) {
  return pad(d.getUTCDate(), p, 2);
}
function formatUTCHour24(d, p) {
  return pad(d.getUTCHours(), p, 2);
}
function formatUTCHour12(d, p) {
  return pad(d.getUTCHours() % 12 || 12, p, 2);
}
function formatUTCDayOfYear(d, p) {
  return pad(1 + utcDay.count(utcYear(d), d), p, 3);
}
function formatUTCMilliseconds(d, p) {
  return pad(d.getUTCMilliseconds(), p, 3);
}
function formatUTCMicroseconds(d, p) {
  return formatUTCMilliseconds(d, p) + "000";
}
function formatUTCMonthNumber(d, p) {
  return pad(d.getUTCMonth() + 1, p, 2);
}
function formatUTCMinutes(d, p) {
  return pad(d.getUTCMinutes(), p, 2);
}
function formatUTCSeconds(d, p) {
  return pad(d.getUTCSeconds(), p, 2);
}
function formatUTCWeekdayNumberMonday(d) {
  var dow = d.getUTCDay();
  return dow === 0 ? 7 : dow;
}
function formatUTCWeekNumberSunday(d, p) {
  return pad(utcSunday.count(utcYear(d) - 1, d), p, 2);
}
function UTCdISO(d) {
  var day = d.getUTCDay();
  return day >= 4 || day === 0 ? utcThursday(d) : utcThursday.ceil(d);
}
function formatUTCWeekNumberISO(d, p) {
  d = UTCdISO(d);
  return pad(utcThursday.count(utcYear(d), d) + (utcYear(d).getUTCDay() === 4), p, 2);
}
function formatUTCWeekdayNumberSunday(d) {
  return d.getUTCDay();
}
function formatUTCWeekNumberMonday(d, p) {
  return pad(utcMonday.count(utcYear(d) - 1, d), p, 2);
}
function formatUTCYear(d, p) {
  return pad(d.getUTCFullYear() % 100, p, 2);
}
function formatUTCYearISO(d, p) {
  d = UTCdISO(d);
  return pad(d.getUTCFullYear() % 100, p, 2);
}
function formatUTCFullYear(d, p) {
  return pad(d.getUTCFullYear() % 1e4, p, 4);
}
function formatUTCFullYearISO(d, p) {
  var day = d.getUTCDay();
  d = day >= 4 || day === 0 ? utcThursday(d) : utcThursday.ceil(d);
  return pad(d.getUTCFullYear() % 1e4, p, 4);
}
function formatUTCZone() {
  return "+0000";
}
function formatLiteralPercent() {
  return "%";
}
function formatUnixTimestamp(d) {
  return +d;
}
function formatUnixTimestampSeconds(d) {
  return Math.floor(+d / 1e3);
}

// node_modules/d3-time-format/src/defaultLocale.js
var locale2;
var timeFormat;
var timeParse;
var utcFormat;
var utcParse;
defaultLocale2({
  dateTime: "%x, %X",
  date: "%-m/%-d/%Y",
  time: "%-I:%M:%S %p",
  periods: ["AM", "PM"],
  days: ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
  shortDays: ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
  months: ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"],
  shortMonths: ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
});
function defaultLocale2(definition) {
  locale2 = formatLocale(definition);
  timeFormat = locale2.format;
  timeParse = locale2.parse;
  utcFormat = locale2.utcFormat;
  utcParse = locale2.utcParse;
  return locale2;
}

// node_modules/d3-scale/src/time.js
function date(t) {
  return new Date(t);
}
function number3(t) {
  return t instanceof Date ? +t : +/* @__PURE__ */ new Date(+t);
}
function calendar(ticks2, tickInterval, year, month, week, day, hour, minute, second2, format2) {
  var scale = continuous(), invert = scale.invert, domain = scale.domain;
  var formatMillisecond = format2(".%L"), formatSecond = format2(":%S"), formatMinute = format2("%I:%M"), formatHour = format2("%I %p"), formatDay = format2("%a %d"), formatWeek = format2("%b %d"), formatMonth = format2("%B"), formatYear2 = format2("%Y");
  function tickFormat2(date2) {
    return (second2(date2) < date2 ? formatMillisecond : minute(date2) < date2 ? formatSecond : hour(date2) < date2 ? formatMinute : day(date2) < date2 ? formatHour : month(date2) < date2 ? week(date2) < date2 ? formatDay : formatWeek : year(date2) < date2 ? formatMonth : formatYear2)(date2);
  }
  scale.invert = function(y2) {
    return new Date(invert(y2));
  };
  scale.domain = function(_) {
    return arguments.length ? domain(Array.from(_, number3)) : domain().map(date);
  };
  scale.ticks = function(interval) {
    var d = domain();
    return ticks2(d[0], d[d.length - 1], interval == null ? 10 : interval);
  };
  scale.tickFormat = function(count, specifier) {
    return specifier == null ? tickFormat2 : format2(specifier);
  };
  scale.nice = function(interval) {
    var d = domain();
    if (!interval || typeof interval.range !== "function") interval = tickInterval(d[0], d[d.length - 1], interval == null ? 10 : interval);
    return interval ? domain(nice(d, interval)) : scale;
  };
  scale.copy = function() {
    return copy(scale, calendar(ticks2, tickInterval, year, month, week, day, hour, minute, second2, format2));
  };
  return scale;
}
function time() {
  return initRange.apply(calendar(timeTicks, timeTickInterval, timeYear, timeMonth, timeSunday, timeDay, timeHour, timeMinute, second, timeFormat).domain([new Date(2e3, 0, 1), new Date(2e3, 0, 2)]), arguments);
}

// node_modules/d3-shape/src/constant.js
function constant_default2(x2) {
  return function constant() {
    return x2;
  };
}

// node_modules/d3-path/src/path.js
var pi = Math.PI;
var tau = 2 * pi;
var epsilon = 1e-6;
var tauEpsilon = tau - epsilon;
function append(strings) {
  this._ += strings[0];
  for (let i = 1, n = strings.length; i < n; ++i) {
    this._ += arguments[i] + strings[i];
  }
}
function appendRound(digits) {
  let d = Math.floor(digits);
  if (!(d >= 0)) throw new Error(`invalid digits: ${digits}`);
  if (d > 15) return append;
  const k = 10 ** d;
  return function(strings) {
    this._ += strings[0];
    for (let i = 1, n = strings.length; i < n; ++i) {
      this._ += Math.round(arguments[i] * k) / k + strings[i];
    }
  };
}
var Path = class {
  constructor(digits) {
    this._x0 = this._y0 = // start of current subpath
    this._x1 = this._y1 = null;
    this._ = "";
    this._append = digits == null ? append : appendRound(digits);
  }
  moveTo(x2, y2) {
    this._append`M${this._x0 = this._x1 = +x2},${this._y0 = this._y1 = +y2}`;
  }
  closePath() {
    if (this._x1 !== null) {
      this._x1 = this._x0, this._y1 = this._y0;
      this._append`Z`;
    }
  }
  lineTo(x2, y2) {
    this._append`L${this._x1 = +x2},${this._y1 = +y2}`;
  }
  quadraticCurveTo(x1, y1, x2, y2) {
    this._append`Q${+x1},${+y1},${this._x1 = +x2},${this._y1 = +y2}`;
  }
  bezierCurveTo(x1, y1, x2, y2, x3, y3) {
    this._append`C${+x1},${+y1},${+x2},${+y2},${this._x1 = +x3},${this._y1 = +y3}`;
  }
  arcTo(x1, y1, x2, y2, r) {
    x1 = +x1, y1 = +y1, x2 = +x2, y2 = +y2, r = +r;
    if (r < 0) throw new Error(`negative radius: ${r}`);
    let x0 = this._x1, y0 = this._y1, x21 = x2 - x1, y21 = y2 - y1, x01 = x0 - x1, y01 = y0 - y1, l01_2 = x01 * x01 + y01 * y01;
    if (this._x1 === null) {
      this._append`M${this._x1 = x1},${this._y1 = y1}`;
    } else if (!(l01_2 > epsilon)) ;
    else if (!(Math.abs(y01 * x21 - y21 * x01) > epsilon) || !r) {
      this._append`L${this._x1 = x1},${this._y1 = y1}`;
    } else {
      let x20 = x2 - x0, y20 = y2 - y0, l21_2 = x21 * x21 + y21 * y21, l20_2 = x20 * x20 + y20 * y20, l21 = Math.sqrt(l21_2), l01 = Math.sqrt(l01_2), l = r * Math.tan((pi - Math.acos((l21_2 + l01_2 - l20_2) / (2 * l21 * l01))) / 2), t01 = l / l01, t21 = l / l21;
      if (Math.abs(t01 - 1) > epsilon) {
        this._append`L${x1 + t01 * x01},${y1 + t01 * y01}`;
      }
      this._append`A${r},${r},0,0,${+(y01 * x20 > x01 * y20)},${this._x1 = x1 + t21 * x21},${this._y1 = y1 + t21 * y21}`;
    }
  }
  arc(x2, y2, r, a0, a1, ccw) {
    x2 = +x2, y2 = +y2, r = +r, ccw = !!ccw;
    if (r < 0) throw new Error(`negative radius: ${r}`);
    let dx = r * Math.cos(a0), dy = r * Math.sin(a0), x0 = x2 + dx, y0 = y2 + dy, cw = 1 ^ ccw, da = ccw ? a0 - a1 : a1 - a0;
    if (this._x1 === null) {
      this._append`M${x0},${y0}`;
    } else if (Math.abs(this._x1 - x0) > epsilon || Math.abs(this._y1 - y0) > epsilon) {
      this._append`L${x0},${y0}`;
    }
    if (!r) return;
    if (da < 0) da = da % tau + tau;
    if (da > tauEpsilon) {
      this._append`A${r},${r},0,1,${cw},${x2 - dx},${y2 - dy}A${r},${r},0,1,${cw},${this._x1 = x0},${this._y1 = y0}`;
    } else if (da > epsilon) {
      this._append`A${r},${r},0,${+(da >= pi)},${cw},${this._x1 = x2 + r * Math.cos(a1)},${this._y1 = y2 + r * Math.sin(a1)}`;
    }
  }
  rect(x2, y2, w, h) {
    this._append`M${this._x0 = this._x1 = +x2},${this._y0 = this._y1 = +y2}h${w = +w}v${+h}h${-w}Z`;
  }
  toString() {
    return this._;
  }
};
function path() {
  return new Path();
}
path.prototype = Path.prototype;

// node_modules/d3-shape/src/path.js
function withPath(shape) {
  let digits = 3;
  shape.digits = function(_) {
    if (!arguments.length) return digits;
    if (_ == null) {
      digits = null;
    } else {
      const d = Math.floor(_);
      if (!(d >= 0)) throw new RangeError(`invalid digits: ${_}`);
      digits = d;
    }
    return shape;
  };
  return () => new Path(digits);
}

// node_modules/d3-shape/src/array.js
var slice = Array.prototype.slice;
function array_default(x2) {
  return typeof x2 === "object" && "length" in x2 ? x2 : Array.from(x2);
}

// node_modules/d3-shape/src/curve/linear.js
function Linear(context) {
  this._context = context;
}
Linear.prototype = {
  areaStart: function() {
    this._line = 0;
  },
  areaEnd: function() {
    this._line = NaN;
  },
  lineStart: function() {
    this._point = 0;
  },
  lineEnd: function() {
    if (this._line || this._line !== 0 && this._point === 1) this._context.closePath();
    this._line = 1 - this._line;
  },
  point: function(x2, y2) {
    x2 = +x2, y2 = +y2;
    switch (this._point) {
      case 0:
        this._point = 1;
        this._line ? this._context.lineTo(x2, y2) : this._context.moveTo(x2, y2);
        break;
      case 1:
        this._point = 2;
      // falls through
      default:
        this._context.lineTo(x2, y2);
        break;
    }
  }
};
function linear_default(context) {
  return new Linear(context);
}

// node_modules/d3-shape/src/point.js
function x(p) {
  return p[0];
}
function y(p) {
  return p[1];
}

// node_modules/d3-shape/src/line.js
function line_default(x2, y2) {
  var defined = constant_default2(true), context = null, curve = linear_default, output = null, path2 = withPath(line);
  x2 = typeof x2 === "function" ? x2 : x2 === void 0 ? x : constant_default2(x2);
  y2 = typeof y2 === "function" ? y2 : y2 === void 0 ? y : constant_default2(y2);
  function line(data) {
    var i, n = (data = array_default(data)).length, d, defined0 = false, buffer;
    if (context == null) output = curve(buffer = path2());
    for (i = 0; i <= n; ++i) {
      if (!(i < n && defined(d = data[i], i, data)) === defined0) {
        if (defined0 = !defined0) output.lineStart();
        else output.lineEnd();
      }
      if (defined0) output.point(+x2(d, i, data), +y2(d, i, data));
    }
    if (buffer) return output = null, buffer + "" || null;
  }
  line.x = function(_) {
    return arguments.length ? (x2 = typeof _ === "function" ? _ : constant_default2(+_), line) : x2;
  };
  line.y = function(_) {
    return arguments.length ? (y2 = typeof _ === "function" ? _ : constant_default2(+_), line) : y2;
  };
  line.defined = function(_) {
    return arguments.length ? (defined = typeof _ === "function" ? _ : constant_default2(!!_), line) : defined;
  };
  line.curve = function(_) {
    return arguments.length ? (curve = _, context != null && (output = curve(context)), line) : curve;
  };
  line.context = function(_) {
    return arguments.length ? (_ == null ? context = output = null : output = curve(context = _), line) : context;
  };
  return line;
}

// node_modules/d3-shape/src/area.js
function area_default(x0, y0, y1) {
  var x1 = null, defined = constant_default2(true), context = null, curve = linear_default, output = null, path2 = withPath(area);
  x0 = typeof x0 === "function" ? x0 : x0 === void 0 ? x : constant_default2(+x0);
  y0 = typeof y0 === "function" ? y0 : y0 === void 0 ? constant_default2(0) : constant_default2(+y0);
  y1 = typeof y1 === "function" ? y1 : y1 === void 0 ? y : constant_default2(+y1);
  function area(data) {
    var i, j, k, n = (data = array_default(data)).length, d, defined0 = false, buffer, x0z = new Array(n), y0z = new Array(n);
    if (context == null) output = curve(buffer = path2());
    for (i = 0; i <= n; ++i) {
      if (!(i < n && defined(d = data[i], i, data)) === defined0) {
        if (defined0 = !defined0) {
          j = i;
          output.areaStart();
          output.lineStart();
        } else {
          output.lineEnd();
          output.lineStart();
          for (k = i - 1; k >= j; --k) {
            output.point(x0z[k], y0z[k]);
          }
          output.lineEnd();
          output.areaEnd();
        }
      }
      if (defined0) {
        x0z[i] = +x0(d, i, data), y0z[i] = +y0(d, i, data);
        output.point(x1 ? +x1(d, i, data) : x0z[i], y1 ? +y1(d, i, data) : y0z[i]);
      }
    }
    if (buffer) return output = null, buffer + "" || null;
  }
  function arealine() {
    return line_default().defined(defined).curve(curve).context(context);
  }
  area.x = function(_) {
    return arguments.length ? (x0 = typeof _ === "function" ? _ : constant_default2(+_), x1 = null, area) : x0;
  };
  area.x0 = function(_) {
    return arguments.length ? (x0 = typeof _ === "function" ? _ : constant_default2(+_), area) : x0;
  };
  area.x1 = function(_) {
    return arguments.length ? (x1 = _ == null ? null : typeof _ === "function" ? _ : constant_default2(+_), area) : x1;
  };
  area.y = function(_) {
    return arguments.length ? (y0 = typeof _ === "function" ? _ : constant_default2(+_), y1 = null, area) : y0;
  };
  area.y0 = function(_) {
    return arguments.length ? (y0 = typeof _ === "function" ? _ : constant_default2(+_), area) : y0;
  };
  area.y1 = function(_) {
    return arguments.length ? (y1 = _ == null ? null : typeof _ === "function" ? _ : constant_default2(+_), area) : y1;
  };
  area.lineX0 = area.lineY0 = function() {
    return arealine().x(x0).y(y0);
  };
  area.lineY1 = function() {
    return arealine().x(x0).y(y1);
  };
  area.lineX1 = function() {
    return arealine().x(x1).y(y0);
  };
  area.defined = function(_) {
    return arguments.length ? (defined = typeof _ === "function" ? _ : constant_default2(!!_), area) : defined;
  };
  area.curve = function(_) {
    return arguments.length ? (curve = _, context != null && (output = curve(context)), area) : curve;
  };
  area.context = function(_) {
    return arguments.length ? (_ == null ? context = output = null : output = curve(context = _), area) : context;
  };
  return area;
}

// node_modules/d3-shape/src/curve/monotone.js
function sign(x2) {
  return x2 < 0 ? -1 : 1;
}
function slope3(that, x2, y2) {
  var h0 = that._x1 - that._x0, h1 = x2 - that._x1, s0 = (that._y1 - that._y0) / (h0 || h1 < 0 && -0), s1 = (y2 - that._y1) / (h1 || h0 < 0 && -0), p = (s0 * h1 + s1 * h0) / (h0 + h1);
  return (sign(s0) + sign(s1)) * Math.min(Math.abs(s0), Math.abs(s1), 0.5 * Math.abs(p)) || 0;
}
function slope2(that, t) {
  var h = that._x1 - that._x0;
  return h ? (3 * (that._y1 - that._y0) / h - t) / 2 : t;
}
function point(that, t02, t12) {
  var x0 = that._x0, y0 = that._y0, x1 = that._x1, y1 = that._y1, dx = (x1 - x0) / 3;
  that._context.bezierCurveTo(x0 + dx, y0 + dx * t02, x1 - dx, y1 - dx * t12, x1, y1);
}
function MonotoneX(context) {
  this._context = context;
}
MonotoneX.prototype = {
  areaStart: function() {
    this._line = 0;
  },
  areaEnd: function() {
    this._line = NaN;
  },
  lineStart: function() {
    this._x0 = this._x1 = this._y0 = this._y1 = this._t0 = NaN;
    this._point = 0;
  },
  lineEnd: function() {
    switch (this._point) {
      case 2:
        this._context.lineTo(this._x1, this._y1);
        break;
      case 3:
        point(this, this._t0, slope2(this, this._t0));
        break;
    }
    if (this._line || this._line !== 0 && this._point === 1) this._context.closePath();
    this._line = 1 - this._line;
  },
  point: function(x2, y2) {
    var t12 = NaN;
    x2 = +x2, y2 = +y2;
    if (x2 === this._x1 && y2 === this._y1) return;
    switch (this._point) {
      case 0:
        this._point = 1;
        this._line ? this._context.lineTo(x2, y2) : this._context.moveTo(x2, y2);
        break;
      case 1:
        this._point = 2;
        break;
      case 2:
        this._point = 3;
        point(this, slope2(this, t12 = slope3(this, x2, y2)), t12);
        break;
      default:
        point(this, this._t0, t12 = slope3(this, x2, y2));
        break;
    }
    this._x0 = this._x1, this._x1 = x2;
    this._y0 = this._y1, this._y1 = y2;
    this._t0 = t12;
  }
};
function MonotoneY(context) {
  this._context = new ReflectContext(context);
}
(MonotoneY.prototype = Object.create(MonotoneX.prototype)).point = function(x2, y2) {
  MonotoneX.prototype.point.call(this, y2, x2);
};
function ReflectContext(context) {
  this._context = context;
}
ReflectContext.prototype = {
  moveTo: function(x2, y2) {
    this._context.moveTo(y2, x2);
  },
  closePath: function() {
    this._context.closePath();
  },
  lineTo: function(x2, y2) {
    this._context.lineTo(y2, x2);
  },
  bezierCurveTo: function(x1, y1, x2, y2, x3, y3) {
    this._context.bezierCurveTo(y1, x1, y2, x2, y3, x3);
  }
};
function monotoneX(context) {
  return new MonotoneX(context);
}

// node_modules/d3-shape/src/curve/step.js
function Step(context, t) {
  this._context = context;
  this._t = t;
}
Step.prototype = {
  areaStart: function() {
    this._line = 0;
  },
  areaEnd: function() {
    this._line = NaN;
  },
  lineStart: function() {
    this._x = this._y = NaN;
    this._point = 0;
  },
  lineEnd: function() {
    if (0 < this._t && this._t < 1 && this._point === 2) this._context.lineTo(this._x, this._y);
    if (this._line || this._line !== 0 && this._point === 1) this._context.closePath();
    if (this._line >= 0) this._t = 1 - this._t, this._line = 1 - this._line;
  },
  point: function(x2, y2) {
    x2 = +x2, y2 = +y2;
    switch (this._point) {
      case 0:
        this._point = 1;
        this._line ? this._context.lineTo(x2, y2) : this._context.moveTo(x2, y2);
        break;
      case 1:
        this._point = 2;
      // falls through
      default: {
        if (this._t <= 0) {
          this._context.lineTo(this._x, y2);
          this._context.lineTo(x2, y2);
        } else {
          var x1 = this._x * (1 - this._t) + x2 * this._t;
          this._context.lineTo(x1, this._y);
          this._context.lineTo(x1, y2);
        }
        break;
      }
    }
    this._x = x2, this._y = y2;
  }
};
function stepAfter(context) {
  return new Step(context, 1);
}

// src/web-components/components/visualizations.js
var SVG_NS = "http://www.w3.org/2000/svg";
var DEFAULT_WIDTH = 120;
var DEFAULT_HEIGHT = 32;
var DEFAULT_PADDING = 3;
var VALID_VARIANTS = /* @__PURE__ */ new Set(["line", "area", "bar"]);
var VALID_TONES = /* @__PURE__ */ new Set(["default", "positive", "negative", "muted", "accent"]);
var VALID_CURVES = /* @__PURE__ */ new Set(["linear", "monotone", "step"]);
var CURVES = {
  linear: linear_default,
  monotone: monotoneX,
  step: stepAfter
};
function toFiniteNumber(value) {
  if (value === null || value === void 0 || value === "") {
    return null;
  }
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}
function normalizeToken3(value, allowedValues, fallback) {
  const normalized = String(value ?? "").trim().toLowerCase();
  return allowedValues.has(normalized) ? normalized : fallback;
}
function parseXValue(value, index) {
  if (value instanceof Date && Number.isFinite(value.getTime())) {
    return value;
  }
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === "string" && value.trim()) {
    const numeric = Number(value);
    if (Number.isFinite(numeric)) {
      return numeric;
    }
    const date2 = new Date(value);
    if (Number.isFinite(date2.getTime())) {
      return date2;
    }
  }
  return index;
}
function normalizeInputPoint(item, index) {
  if (typeof item === "number") {
    return { x: index, y: Number.isFinite(item) ? item : null };
  }
  if (item instanceof Date) {
    return { x: index, y: null };
  }
  if (item && typeof item === "object") {
    return {
      x: item.x ?? index,
      y: toFiniteNumber(item.y)
    };
  }
  const numeric = toFiniteNumber(item);
  return { x: index, y: numeric };
}
function parseSparklinePoints(input) {
  if (Array.isArray(input)) {
    return input.map(normalizeInputPoint);
  }
  if (input === null || input === void 0) {
    return [];
  }
  if (typeof input === "string") {
    const text = input.trim();
    if (!text) {
      return [];
    }
    if (text.startsWith("[")) {
      try {
        const parsed = JSON.parse(text);
        return parseSparklinePoints(parsed);
      } catch {
        return [];
      }
    }
    return text.split(/[\s,;]+/u).filter(Boolean).map((part, index) => ({ x: index, y: toFiniteNumber(part) }));
  }
  return [];
}
function normalizeSparklineExtent(points, options = {}) {
  const normalizedPoints = parseSparklinePoints(points);
  const referenceValue = toFiniteNumber(options.referenceValue);
  const prepared = normalizedPoints.map((point2, index) => ({
    x: point2.x,
    xValue: parseXValue(point2.x, index),
    y: toFiniteNumber(point2.y),
    index
  }));
  const validPoints = prepared.filter((point2) => point2.y !== null);
  const yValues = validPoints.map((point2) => point2.y);
  if (referenceValue !== null) {
    yValues.push(referenceValue);
  }
  if (!validPoints.length) {
    return {
      empty: true,
      points: prepared,
      validPoints,
      xDomain: [0, 1],
      yDomain: [0, 1],
      xMode: "index",
      referenceValue
    };
  }
  let yDomain = extent(yValues);
  if (!Number.isFinite(yDomain[0]) || !Number.isFinite(yDomain[1])) {
    yDomain = [0, 1];
  }
  if (Object.is(yDomain[0], yDomain[1])) {
    const value = yDomain[0];
    const padding = value === 0 ? 1 : Math.max(Math.abs(value) * 0.08, 1);
    yDomain = [value - padding, value + padding];
  }
  const allDates = prepared.every((point2) => point2.xValue instanceof Date);
  const allNumbers = prepared.every((point2) => typeof point2.xValue === "number" && Number.isFinite(point2.xValue));
  const xMode = allDates ? "time" : allNumbers ? "number" : "index";
  const xValues = prepared.map((point2) => xMode === "index" ? point2.index : point2.xValue);
  let xDomain = extent(xValues);
  if (xMode === "time") {
    const left = xDomain[0] instanceof Date ? xDomain[0].getTime() : NaN;
    const right = xDomain[1] instanceof Date ? xDomain[1].getTime() : NaN;
    if (!Number.isFinite(left) || !Number.isFinite(right)) {
      xDomain = [/* @__PURE__ */ new Date(0), /* @__PURE__ */ new Date(1)];
    } else if (left === right) {
      xDomain = [new Date(left - 1), new Date(right + 1)];
    }
  } else {
    if (!Number.isFinite(xDomain[0]) || !Number.isFinite(xDomain[1])) {
      xDomain = [0, Math.max(prepared.length - 1, 1)];
    } else if (Object.is(xDomain[0], xDomain[1])) {
      xDomain = [xDomain[0] - 1, xDomain[1] + 1];
    }
  }
  return {
    empty: false,
    points: prepared,
    validPoints,
    xDomain,
    yDomain,
    xMode,
    referenceValue
  };
}
function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}
function round(value) {
  return Number.isFinite(value) ? Number(value.toFixed(3)) : 0;
}
function createScales(extentInfo, width, height, padding) {
  const xRange = [padding, Math.max(width - padding, padding)];
  const yRange = [Math.max(height - padding, padding), padding];
  const xScale = extentInfo.xMode === "time" ? time(extentInfo.xDomain, xRange) : linear2(extentInfo.xDomain, xRange);
  const yScale = linear2(extentInfo.yDomain, yRange);
  return { xScale, yScale };
}
function getX(point2, extentInfo) {
  return extentInfo.xMode === "index" ? point2.index : point2.xValue;
}
function singlePointPath(point2, extentInfo, xScale, yScale, width) {
  const center = round(xScale(getX(point2, extentInfo)));
  const y2 = round(yScale(point2.y));
  const half = Math.min(4, Math.max(1, width / 20));
  const left = round(clamp(center - half, 0, width));
  const right = round(clamp(center + half, 0, width));
  return `M${left},${y2}L${right},${y2}`;
}
function buildSparklinePath(points, options = {}) {
  const width = Math.max(toFiniteNumber(options.width) ?? DEFAULT_WIDTH, 1);
  const height = Math.max(toFiniteNumber(options.height) ?? DEFAULT_HEIGHT, 1);
  const padding = Math.max(toFiniteNumber(options.padding) ?? DEFAULT_PADDING, 0);
  const variant = normalizeToken3(options.variant, VALID_VARIANTS, "line");
  const curve = normalizeToken3(options.curve, VALID_CURVES, "linear");
  const extentInfo = normalizeSparklineExtent(points, options);
  if (extentInfo.empty) {
    return {
      empty: true,
      width,
      height,
      padding,
      variant,
      curve,
      linePath: "",
      areaPath: "",
      bars: [],
      marker: null,
      minPoint: null,
      maxPoint: null,
      referenceY: null,
      extent: extentInfo
    };
  }
  const { xScale, yScale } = createScales(extentInfo, width, height, padding);
  const curveFactory = CURVES[curve] || linear_default;
  const lineGenerator = line_default().defined((point2) => point2.y !== null).x((point2) => round(xScale(getX(point2, extentInfo)))).y((point2) => round(yScale(point2.y))).curve(curveFactory);
  const validPoints = extentInfo.validPoints;
  const linePath = validPoints.length === 1 ? singlePointPath(validPoints[0], extentInfo, xScale, yScale, width) : lineGenerator(extentInfo.points) || "";
  const baseline = clamp(0, extentInfo.yDomain[0], extentInfo.yDomain[1]);
  const areaGenerator = area_default().defined((point2) => point2.y !== null).x((point2) => round(xScale(getX(point2, extentInfo)))).y0(round(yScale(baseline))).y1((point2) => round(yScale(point2.y))).curve(curveFactory);
  const areaPath = validPoints.length > 1 ? areaGenerator(extentInfo.points) || "" : "";
  const barWidth = Math.max(1, Math.min(8, (width - padding * 2) / Math.max(extentInfo.points.length, 1) * 0.58));
  const baselineY = round(yScale(baseline));
  const bars = validPoints.map((point2) => {
    const x2 = round(xScale(getX(point2, extentInfo)) - barWidth / 2);
    const y2 = round(yScale(point2.y));
    return {
      x: x2,
      y: Math.min(y2, baselineY),
      width: round(barWidth),
      height: Math.max(1, round(Math.abs(baselineY - y2)))
    };
  });
  const lastPoint = validPoints[validPoints.length - 1] || null;
  const marker = lastPoint ? {
    x: round(xScale(getX(lastPoint, extentInfo))),
    y: round(yScale(lastPoint.y)),
    value: lastPoint.y
  } : null;
  const minPoint = validPoints.reduce((candidate, point2) => point2.y < candidate.y ? point2 : candidate, validPoints[0]);
  const maxPoint = validPoints.reduce((candidate, point2) => point2.y > candidate.y ? point2 : candidate, validPoints[0]);
  const mapMarker = (point2) => point2 ? {
    x: round(xScale(getX(point2, extentInfo))),
    y: round(yScale(point2.y)),
    value: point2.y
  } : null;
  return {
    empty: false,
    width,
    height,
    padding,
    variant,
    curve,
    linePath,
    areaPath,
    bars,
    marker,
    minPoint: mapMarker(minPoint),
    maxPoint: mapMarker(maxPoint),
    referenceY: extentInfo.referenceValue === null ? null : round(yScale(extentInfo.referenceValue)),
    extent: extentInfo
  };
}
function svgElement(name) {
  return document.createElementNS(SVG_NS, name);
}
function hasInvalidPathData(value) {
  return /(?:NaN|Infinity|-Infinity)/u.test(String(value ?? ""));
}
var IncSparklineElement = class extends HTMLElement {
  static observedAttributes = [
    "aria-label",
    "curve",
    "empty-label",
    "height",
    "points",
    "reference-value",
    "show-last-marker",
    "show-min-max",
    "tone",
    "values",
    "variant",
    "width"
  ];
  #hasPropertyPoints = false;
  #propertyPoints = [];
  #titleId = createUniqueId("inc-sparkline-title");
  #descId = createUniqueId("inc-sparkline-desc");
  connectedCallback() {
    this.#render();
  }
  attributeChangedCallback() {
    if (this.isConnected) {
      this.#render();
    }
  }
  get points() {
    if (this.#hasPropertyPoints) {
      return parseSparklinePoints(this.#propertyPoints);
    }
    if (this.hasAttribute("points")) {
      return parseSparklinePoints(this.getAttribute("points"));
    }
    return parseSparklinePoints(this.getAttribute("values"));
  }
  set points(value) {
    if (value === null || value === void 0) {
      this.#hasPropertyPoints = false;
      this.#propertyPoints = [];
    } else {
      this.#hasPropertyPoints = true;
      this.#propertyPoints = value;
    }
    if (this.isConnected) {
      this.#render();
    }
  }
  get values() {
    return this.getAttribute("values") || "";
  }
  set values(value) {
    if (value === null || value === void 0 || value === "") {
      this.removeAttribute("values");
      return;
    }
    this.setAttribute("values", String(value));
  }
  get width() {
    return Math.max(toFiniteNumber(this.getAttribute("width")) ?? DEFAULT_WIDTH, 1);
  }
  set width(value) {
    if (value === null || value === void 0 || value === "") {
      this.removeAttribute("width");
      return;
    }
    this.setAttribute("width", String(value));
  }
  get height() {
    return Math.max(toFiniteNumber(this.getAttribute("height")) ?? DEFAULT_HEIGHT, 1);
  }
  set height(value) {
    if (value === null || value === void 0 || value === "") {
      this.removeAttribute("height");
      return;
    }
    this.setAttribute("height", String(value));
  }
  #render() {
    const width = this.width;
    const height = this.height;
    const variant = normalizeToken3(this.getAttribute("variant"), VALID_VARIANTS, "line");
    const tone = normalizeToken3(this.getAttribute("tone"), VALID_TONES, "default");
    const curve = normalizeToken3(this.getAttribute("curve"), VALID_CURVES, "linear");
    const referenceValue = toFiniteNumber(this.getAttribute("reference-value"));
    const label = this.getAttribute("aria-label") || "Sparkline trend";
    const emptyLabel = this.getAttribute("empty-label") ?? "No data";
    const model = buildSparklinePath(this.points, {
      curve,
      height,
      referenceValue,
      variant,
      width
    });
    this.classList.add("inc-sparkline");
    [...this.classList].filter((token) => token.startsWith("inc-sparkline--")).forEach((token) => this.classList.remove(token));
    this.classList.add(`inc-sparkline--${variant}`, `inc-sparkline--tone-${tone}`);
    this.style.setProperty("--inc-sparkline-width", `${width}px`);
    this.style.setProperty("--inc-sparkline-height", `${height}px`);
    const svg = svgElement("svg");
    svg.classList.add("inc-sparkline__svg");
    svg.setAttribute("part", "svg");
    svg.setAttribute("width", String(width));
    svg.setAttribute("height", String(height));
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("role", "img");
    svg.setAttribute("aria-labelledby", `${this.#titleId} ${this.#descId}`);
    svg.setAttribute("focusable", "false");
    const title = svgElement("title");
    title.id = this.#titleId;
    title.textContent = label;
    const desc = svgElement("desc");
    desc.id = this.#descId;
    desc.textContent = model.empty ? emptyLabel || "No sparkline data available." : this.#buildDescription(model);
    svg.append(title, desc);
    if (model.empty || hasInvalidPathData(model.linePath) || hasInvalidPathData(model.areaPath)) {
      this.#renderEmpty(svg, width, height, emptyLabel);
      this.replaceChildren(svg);
      return;
    }
    if (model.referenceY !== null) {
      const reference = svgElement("line");
      reference.classList.add("inc-sparkline__reference");
      reference.setAttribute("part", "reference");
      reference.setAttribute("x1", String(model.padding));
      reference.setAttribute("x2", String(width - model.padding));
      reference.setAttribute("y1", String(model.referenceY));
      reference.setAttribute("y2", String(model.referenceY));
      reference.setAttribute("vector-effect", "non-scaling-stroke");
      svg.append(reference);
    }
    if (variant === "bar") {
      model.bars.forEach((bar) => {
        const rect = svgElement("rect");
        rect.classList.add("inc-sparkline__bar");
        rect.setAttribute("part", "bar");
        rect.setAttribute("x", String(bar.x));
        rect.setAttribute("y", String(bar.y));
        rect.setAttribute("width", String(bar.width));
        rect.setAttribute("height", String(bar.height));
        svg.append(rect);
      });
    } else {
      if (variant === "area" && model.areaPath) {
        const area = svgElement("path");
        area.classList.add("inc-sparkline__area");
        area.setAttribute("part", "area");
        area.setAttribute("d", model.areaPath);
        area.setAttribute("vector-effect", "non-scaling-stroke");
        svg.append(area);
      }
      const line = svgElement("path");
      line.classList.add("inc-sparkline__line");
      line.setAttribute("part", "line");
      line.setAttribute("d", model.linePath);
      line.setAttribute("vector-effect", "non-scaling-stroke");
      svg.append(line);
    }
    if (this.hasAttribute("show-min-max")) {
      this.#appendMarker(svg, model.minPoint, "min");
      if (model.maxPoint?.x !== model.minPoint?.x || model.maxPoint?.y !== model.minPoint?.y) {
        this.#appendMarker(svg, model.maxPoint, "max");
      }
    }
    if (this.hasAttribute("show-last-marker")) {
      this.#appendMarker(svg, model.marker, "last");
    }
    this.replaceChildren(svg);
  }
  #renderEmpty(svg, width, height, emptyLabel) {
    const line = svgElement("line");
    line.classList.add("inc-sparkline__empty-line");
    line.setAttribute("part", "line");
    line.setAttribute("x1", "3");
    line.setAttribute("x2", String(Math.max(width - 3, 3)));
    line.setAttribute("y1", String(round(height / 2)));
    line.setAttribute("y2", String(round(height / 2)));
    line.setAttribute("vector-effect", "non-scaling-stroke");
    svg.append(line);
    if (emptyLabel) {
      const text = svgElement("text");
      text.classList.add("inc-sparkline__empty");
      text.setAttribute("part", "empty");
      text.setAttribute("x", String(round(width / 2)));
      text.setAttribute("y", String(round(height / 2 + 3)));
      text.setAttribute("text-anchor", "middle");
      text.textContent = emptyLabel;
      svg.append(text);
    }
  }
  #appendMarker(svg, point2, modifier) {
    if (!point2 || !Number.isFinite(point2.x) || !Number.isFinite(point2.y)) {
      return;
    }
    const marker = svgElement("circle");
    marker.classList.add("inc-sparkline__marker", `inc-sparkline__marker--${modifier}`);
    marker.setAttribute("part", "marker");
    marker.setAttribute("cx", String(point2.x));
    marker.setAttribute("cy", String(point2.y));
    marker.setAttribute("r", modifier === "last" ? "2.4" : "1.8");
    marker.setAttribute("vector-effect", "non-scaling-stroke");
    svg.append(marker);
  }
  #buildDescription(model) {
    const values = model.extent.validPoints.map((point2) => point2.y);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const latest = model.marker?.value;
    const parts = [`${values.length} data ${values.length === 1 ? "point" : "points"}.`];
    if (Number.isFinite(latest)) {
      parts.push(`Latest value ${latest}.`);
    }
    if (Number.isFinite(min) && Number.isFinite(max)) {
      parts.push(`Range ${min} to ${max}.`);
    }
    if (model.extent.referenceValue !== null) {
      parts.push(`Reference value ${model.extent.referenceValue}.`);
    }
    return parts.join(" ");
  }
};
var visualizationDefinitions = [
  ["inc-sparkline", IncSparklineElement]
];
var visualizationComponents = {
  IncSparklineElement
};
function defineVisualizationComponents(registry = globalThis.customElements) {
  if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
    return [];
  }
  const defined = [];
  for (const [tagName, ctor] of visualizationDefinitions) {
    if (!registry.get(tagName)) {
      registry.define(tagName, ctor);
      defined.push(tagName);
    }
  }
  return defined;
}
if (typeof globalThis !== "undefined") {
  const namespace2 = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
  namespace2.visualizations = Object.assign({}, namespace2.visualizations, {
    buildSparklinePath,
    defineVisualizationComponents,
    normalizeSparklineExtent,
    parseSparklinePoints,
    visualizationDefinitions,
    components: visualizationComponents
  });
}

// src/web-components/components/overlays.js
var INTERNAL_NODE = /* @__PURE__ */ Symbol("inc-internal-overlay-node");
var HostElement6 = typeof HTMLElement === "undefined" ? class {
} : HTMLElement;
function toBooleanAttribute2(value, fallback = false) {
  if (value === null || value === void 0) {
    return fallback;
  }
  if (value === "" || value === true) {
    return true;
  }
  if (value === false) {
    return false;
  }
  const normalized = String(value).trim().toLowerCase();
  return !(normalized === "false" || normalized === "0" || normalized === "no" || normalized === "off");
}
function setBooleanAttribute(target, name, value) {
  if (value) {
    target.setAttribute(name, "");
    return;
  }
  target.removeAttribute(name);
}
function emit3(host, name, detail = {}) {
  host.dispatchEvent(new CustomEvent(name, {
    bubbles: true,
    composed: true,
    detail
  }));
}
function appendProjectedChildren(host, destinationMap, fallbackDestination, ignoredNodes = /* @__PURE__ */ new Set()) {
  const nodes = Array.from(host.childNodes);
  for (const node of nodes) {
    if (ignoredNodes.has(node)) {
      continue;
    }
    if (node.nodeType === Node.TEXT_NODE && !node.textContent?.trim()) {
      continue;
    }
    if (!(node instanceof HTMLElement)) {
      fallbackDestination.append(node);
      continue;
    }
    const slotName = (node.getAttribute("slot") || "").trim();
    const destination = destinationMap.get(slotName) || fallbackDestination;
    destination.append(node);
  }
}
function getFocusableElements(container) {
  const selector = [
    "a[href]",
    "button:not([disabled])",
    "input:not([disabled]):not([type='hidden'])",
    "select:not([disabled])",
    "textarea:not([disabled])",
    "[tabindex]:not([tabindex='-1'])"
  ].join(",");
  return Array.from(container.querySelectorAll(selector)).filter((element) => {
    if (!(element instanceof HTMLElement)) {
      return false;
    }
    if (element.hasAttribute("hidden") || element.getAttribute("aria-hidden") === "true") {
      return false;
    }
    return element.offsetParent !== null || element === document.activeElement;
  });
}
var IncDisclosureElement = class extends HostElement6 {
  static get observedAttributes() {
    return ["open", "summary", "toggleable"];
  }
  constructor() {
    super();
    this._isSyncing = false;
    this._observer = null;
    this._details = null;
    this._summaryTitle = null;
    this._content = null;
    this._onToggle = this._onToggle.bind(this);
  }
  connectedCallback() {
    this._ensureStructure();
    this._syncFromAttributes();
    this._projectChildren();
    this._observer = new MutationObserver((mutations) => {
      if (this._isSyncing) {
        return;
      }
      const shouldProject = mutations.some((mutation) => {
        if (mutation.type !== "childList") {
          return false;
        }
        const changedNodes = [...mutation.addedNodes, ...mutation.removedNodes];
        return changedNodes.some((node) => node !== this._details);
      });
      if (shouldProject) {
        this._projectChildren();
      }
    });
    this._observer.observe(this, { childList: true });
  }
  disconnectedCallback() {
    this._observer?.disconnect();
    this._observer = null;
    this._details?.removeEventListener("toggle", this._onToggle);
  }
  attributeChangedCallback() {
    this._syncFromAttributes();
  }
  open() {
    this.setAttribute("open", "");
  }
  close() {
    this.removeAttribute("open");
  }
  toggle(force) {
    if (typeof force === "boolean") {
      setBooleanAttribute(this, "open", force);
      return;
    }
    setBooleanAttribute(this, "open", !this.hasAttribute("open"));
  }
  _ensureStructure() {
    if (this._details?.isConnected) {
      return;
    }
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    const summaryTitle = document.createElement("span");
    const content = document.createElement("div");
    details.className = "inc-disclosure";
    details.setAttribute("part", "surface");
    details[INTERNAL_NODE] = true;
    summary.className = "inc-disclosure__summary";
    summary.setAttribute("part", "summary");
    summaryTitle.className = "inc-disclosure__title";
    summaryTitle.setAttribute("part", "title");
    summary.append(summaryTitle);
    content.className = "inc-disclosure__content";
    content.setAttribute("part", "content");
    details.append(summary, content);
    this.append(details);
    this._details = details;
    this._summaryTitle = summaryTitle;
    this._content = content;
    details.addEventListener("toggle", this._onToggle);
  }
  _syncFromAttributes() {
    if (!this._details) {
      return;
    }
    const summaryText = this.getAttribute("summary");
    if (summaryText) {
      this._summaryTitle.textContent = summaryText;
    } else if (!this._summaryTitle.querySelector(":scope > *")) {
      this._summaryTitle.textContent = "";
    }
    const open = this.hasAttribute("open");
    this._details.open = open;
    const toggleable = toBooleanAttribute2(this.getAttribute("toggleable"), true);
    this._details.dataset.incToggleable = toggleable ? "true" : "false";
  }
  _projectChildren() {
    if (!this._details || !this._summaryTitle || !this._content) {
      return;
    }
    this._isSyncing = true;
    this._summaryTitle.replaceChildren();
    this._content.replaceChildren();
    const destinations = /* @__PURE__ */ new Map([
      ["summary", this._summaryTitle],
      ["content", this._content],
      ["default", this._content]
    ]);
    appendProjectedChildren(this, destinations, this._content, /* @__PURE__ */ new Set([this._details]));
    this._isSyncing = false;
  }
  _onToggle() {
    const open = this._details?.open === true;
    setBooleanAttribute(this, "open", open);
    emit3(this, "toggle", { open });
    emit3(this, open ? "open" : "close", { open });
  }
};
var IncDialogBaseElement = class extends HostElement6 {
  static get observedAttributes() {
    return ["open", "modal", "dismissible", "size", "label", "placement"];
  }
  constructor() {
    super();
    this._dialog = null;
    this._surface = null;
    this._header = null;
    this._title = null;
    this._body = null;
    this._footer = null;
    this._closeButton = null;
    this._observer = null;
    this._syncing = false;
    this._lastTrigger = null;
    this._tagType = "dialog";
    this._onDialogClose = this._onDialogClose.bind(this);
    this._onDialogCancel = this._onDialogCancel.bind(this);
    this._onDialogPointerDown = this._onDialogPointerDown.bind(this);
  }
  connectedCallback() {
    this._ensureStructure();
    this._syncFromAttributes();
    this._projectChildren();
    this._observer = new MutationObserver((mutations) => {
      if (this._syncing) {
        return;
      }
      const shouldProject = mutations.some((mutation) => {
        const changedNodes = [...mutation.addedNodes, ...mutation.removedNodes];
        return changedNodes.some((node) => node !== this._dialog);
      });
      if (shouldProject) {
        this._projectChildren();
      }
    });
    this._observer.observe(this, { childList: true });
  }
  disconnectedCallback() {
    this._observer?.disconnect();
    this._observer = null;
    if (this._dialog) {
      this._dialog.removeEventListener("close", this._onDialogClose);
      this._dialog.removeEventListener("cancel", this._onDialogCancel);
      this._dialog.removeEventListener("click", this._onDialogPointerDown);
    }
  }
  attributeChangedCallback() {
    this._syncFromAttributes();
  }
  get open() {
    return this.hasAttribute("open");
  }
  set open(value) {
    setBooleanAttribute(this, "open", Boolean(value));
  }
  get modal() {
    return toBooleanAttribute2(this.getAttribute("modal"), true);
  }
  set modal(value) {
    setBooleanAttribute(this, "modal", Boolean(value));
  }
  get dismissible() {
    return toBooleanAttribute2(this.getAttribute("dismissible"), true);
  }
  set dismissible(value) {
    setBooleanAttribute(this, "dismissible", Boolean(value));
  }
  show() {
    this._rememberTrigger();
    this.setAttribute("open", "");
    this._openDialog(false);
  }
  showModal() {
    this._rememberTrigger();
    this.setAttribute("open", "");
    this._openDialog(true);
  }
  close(returnValue = "") {
    if (!this._dialog) {
      return;
    }
    if (this._dialog.open && typeof this._dialog.close === "function") {
      this._dialog.close(returnValue);
    } else {
      this.removeAttribute("open");
    }
  }
  dismiss(reason = "dismiss") {
    if (!this.dismissible) {
      return;
    }
    this.close(reason);
    emit3(this, "dismiss", { reason });
  }
  _ensureStructure() {
    if (this._dialog?.isConnected) {
      return;
    }
    const dialog = document.createElement("dialog");
    const surface = document.createElement("div");
    const header = document.createElement("div");
    const title = document.createElement("div");
    const body = document.createElement("div");
    const footer = document.createElement("div");
    const closeButton = document.createElement("button");
    dialog.className = "inc-native-dialog";
    dialog.setAttribute("part", "backdrop");
    dialog[INTERNAL_NODE] = true;
    surface.className = "inc-native-dialog__surface";
    surface.setAttribute("part", "surface");
    header.className = "inc-native-dialog__header";
    header.setAttribute("part", "header");
    title.className = "inc-native-dialog__title";
    title.setAttribute("part", "title");
    closeButton.type = "button";
    closeButton.className = "inc-native-dialog__close";
    closeButton.setAttribute("part", "close");
    closeButton.setAttribute("aria-label", "Close");
    closeButton.textContent = "x";
    closeButton.addEventListener("click", () => this.dismiss("close-button"));
    header.append(title, closeButton);
    body.className = "inc-native-dialog__body";
    body.setAttribute("part", "body");
    footer.className = "inc-native-dialog__footer";
    footer.setAttribute("part", "footer");
    surface.append(header, body, footer);
    dialog.append(surface);
    this.append(dialog);
    this._dialog = dialog;
    this._surface = surface;
    this._header = header;
    this._title = title;
    this._body = body;
    this._footer = footer;
    this._closeButton = closeButton;
    dialog.addEventListener("close", this._onDialogClose);
    dialog.addEventListener("cancel", this._onDialogCancel);
    dialog.addEventListener("click", this._onDialogPointerDown);
  }
  _projectChildren() {
    if (!this._title || !this._body || !this._footer || !this._header) {
      return;
    }
    this._syncing = true;
    this._title.replaceChildren();
    this._body.replaceChildren();
    this._footer.replaceChildren();
    const destinations = /* @__PURE__ */ new Map([
      ["title", this._title],
      ["header", this._header],
      ["body", this._body],
      ["footer", this._footer],
      ["default", this._body]
    ]);
    appendProjectedChildren(this, destinations, this._body, /* @__PURE__ */ new Set([this._dialog]));
    if (!this._header.contains(this._closeButton)) {
      this._header.append(this._closeButton);
    }
    if (!this._title.textContent?.trim()) {
      const label = this.getAttribute("label");
      if (label) {
        this._title.textContent = label;
      }
    }
    this._syncing = false;
  }
  _syncFromAttributes() {
    if (!this._dialog) {
      return;
    }
    const open = this.hasAttribute("open");
    const dismissible = this.dismissible;
    this._dialog.dataset.incDismissible = dismissible ? "true" : "false";
    this._closeButton.hidden = !dismissible;
    const size = this.getAttribute("size");
    if (size) {
      this._dialog.dataset.incSize = size;
    } else {
      delete this._dialog.dataset.incSize;
    }
    const label = this.getAttribute("label");
    if (label) {
      this._dialog.setAttribute("aria-label", label);
    } else {
      this._dialog.removeAttribute("aria-label");
    }
    const placement = this.getAttribute("placement");
    if (placement) {
      this._dialog.dataset.incPlacement = placement;
    } else {
      delete this._dialog.dataset.incPlacement;
    }
    if (this._tagType === "drawer") {
      this._dialog.classList.add("inc-native-dialog--drawer");
    } else {
      this._dialog.classList.remove("inc-native-dialog--drawer");
    }
    if (open && !this._dialog.open) {
      this._openDialog(this.modal);
    }
    if (!open && this._dialog.open) {
      this._dialog.close();
    }
  }
  _openDialog(asModal) {
    if (!this._dialog || this._dialog.open) {
      return;
    }
    try {
      if (asModal && typeof this._dialog.showModal === "function") {
        this._dialog.showModal();
      } else if (typeof this._dialog.show === "function") {
        this._dialog.show();
      } else {
        this._dialog.setAttribute("open", "");
      }
    } catch {
      this._dialog.setAttribute("open", "");
    }
    const focusInitial = () => this._focusInitial();
    if (typeof window.requestAnimationFrame === "function") {
      window.requestAnimationFrame(focusInitial);
    } else {
      window.setTimeout(focusInitial, 0);
    }
    emit3(this, "open", { modal: asModal });
  }
  _focusInitial() {
    if (!this._dialog) {
      return;
    }
    const explicit = this._dialog.querySelector("[data-inc-initial-focus]");
    if (explicit instanceof HTMLElement) {
      explicit.focus({ preventScroll: true });
      return;
    }
    const focusables = getFocusableElements(this._dialog);
    if (focusables[0]) {
      focusables[0].focus({ preventScroll: true });
    }
  }
  _rememberTrigger() {
    const active = document.activeElement;
    this._lastTrigger = active instanceof HTMLElement ? active : null;
  }
  _restoreFocus() {
    if (!this._lastTrigger || !this._lastTrigger.isConnected) {
      return;
    }
    this._lastTrigger.focus({ preventScroll: true });
  }
  _onDialogPointerDown(event) {
    if (!this.dismissible || !this._dialog) {
      return;
    }
    if (event.target === this._dialog) {
      this.dismiss("backdrop");
    }
  }
  _onDialogCancel(event) {
    if (!this.dismissible) {
      event.preventDefault();
      return;
    }
    emit3(this, "cancel", { reason: "escape" });
  }
  _onDialogClose() {
    setBooleanAttribute(this, "open", this._dialog?.open === true);
    emit3(this, "close", { returnValue: this._dialog?.returnValue || "" });
    this._restoreFocus();
  }
};
var IncDialogElement = class extends IncDialogBaseElement {
  constructor() {
    super();
    this._tagType = "dialog";
  }
};
var IncDrawerElement = class extends IncDialogBaseElement {
  constructor() {
    super();
    this._tagType = "drawer";
  }
  show() {
    this._rememberTrigger();
    this.setAttribute("open", "");
    this._openDialog(this.modal);
  }
};
function defineOverlayComponents(registry = globalThis.customElements) {
  if (!registry) {
    return;
  }
  if (!registry.get("inc-disclosure")) {
    registry.define("inc-disclosure", IncDisclosureElement);
  }
  if (!registry.get("inc-dialog")) {
    registry.define("inc-dialog", IncDialogElement);
  }
  if (!registry.get("inc-drawer")) {
    registry.define("inc-drawer", IncDrawerElement);
  }
}
var overlayComponentsApi = {
  defineOverlayComponents,
  IncDisclosureElement,
  IncDialogElement,
  IncDrawerElement
};
if (typeof module !== "undefined" && module.exports) {
  module.exports = overlayComponentsApi;
}
if (typeof globalThis !== "undefined") {
  globalThis.IncWebComponents = globalThis.IncWebComponents || {};
  globalThis.IncWebComponents.overlays = overlayComponentsApi;
}

// src/web-components/index.js
var namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
installRegistryNamespace();
function addEntry(entryMap, tagName, constructor) {
  if (typeof tagName !== "string" || !tagName || typeof constructor !== "function") {
    return;
  }
  if (!entryMap.has(tagName)) {
    entryMap.set(tagName, constructor);
  }
}
function addEntries(entryMap, entries) {
  if (!Array.isArray(entries)) {
    return;
  }
  entries.forEach(([tagName, constructor]) => {
    addEntry(entryMap, tagName, constructor);
  });
}
function getComponentEntries() {
  const entryMap = /* @__PURE__ */ new Map();
  addEntries(entryMap, namespace.layout?.layoutComponents);
  addEntry(entryMap, "inc-navbar", namespace.navigation?.IncNavbarElement);
  addEntry(entryMap, "inc-tabs", namespace.navigation?.IncTabsElement);
  addEntry(entryMap, "inc-user-menu", namespace.navigation?.IncUserMenuElement);
  addEntry(entryMap, "inc-field", namespace.forms?.components?.IncFieldElement);
  addEntry(entryMap, "inc-input-group", namespace.forms?.components?.IncInputGroupElement);
  addEntry(entryMap, "inc-choice-group", namespace.forms?.components?.IncChoiceGroupElement);
  addEntry(entryMap, "inc-readonly-field", namespace.forms?.components?.IncReadonlyFieldElement);
  addEntry(entryMap, "inc-validation-summary", namespace.forms?.components?.IncValidationSummaryElement);
  addEntries(entryMap, namespace.feedback?.feedbackDefinitions);
  addEntries(entryMap, namespace.actions?.actionDefinitions);
  addEntries(entryMap, namespace.collections?.collectionDefinitions);
  addEntries(entryMap, namespace.visualizations?.visualizationDefinitions);
  addEntry(entryMap, "inc-disclosure", namespace.overlays?.IncDisclosureElement);
  addEntry(entryMap, "inc-dialog", namespace.overlays?.IncDialogElement);
  addEntry(entryMap, "inc-drawer", namespace.overlays?.IncDrawerElement);
  return [...entryMap.entries()];
}
function syncNamespace() {
  namespace.components = new Map(getComponentEntries());
  return namespace.components;
}
function defineAll2(options = {}) {
  const registry = options.registry || globalThis.customElements;
  if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
    return [];
  }
  const entries = getComponentEntries();
  const results = entries.map(([name, ctor]) => defineCustomElement(name, ctor, registry));
  syncNamespace();
  return results;
}
function registerIncWebComponents(options = {}) {
  return defineAll2(options);
}
namespace.defineAll = defineAll2;
namespace.registerIncWebComponents = registerIncWebComponents;
syncNamespace();
defineAll2();
export {
  buildSparklinePath,
  defineAll2 as defineAll,
  normalizeSparklineExtent,
  parseSparklinePoints,
  registerIncWebComponents
};
/*! Bundled license information:

lucide/dist/esm/defaultAttributes.mjs:
lucide/dist/esm/createElement.mjs:
lucide/dist/esm/icons/circle-check.mjs:
lucide/dist/esm/icons/circle-question-mark.mjs:
lucide/dist/esm/icons/circle-x.mjs:
lucide/dist/esm/icons/download.mjs:
lucide/dist/esm/icons/external-link.mjs:
lucide/dist/esm/icons/file-text.mjs:
lucide/dist/esm/icons/folder-plus.mjs:
lucide/dist/esm/icons/info.mjs:
lucide/dist/esm/icons/lock.mjs:
lucide/dist/esm/icons/pause.mjs:
lucide/dist/esm/icons/play.mjs:
lucide/dist/esm/icons/refresh-cw.mjs:
lucide/dist/esm/icons/search-x.mjs:
lucide/dist/esm/icons/settings.mjs:
lucide/dist/esm/icons/shield-check.mjs:
lucide/dist/esm/icons/triangle-alert.mjs:
lucide/dist/esm/icons/upload.mjs:
  (**
   * @license lucide v1.17.0 - ISC
   *
   * This source code is licensed under the ISC license.
   * See the LICENSE file in the root directory of this source tree.
   *)
*/
