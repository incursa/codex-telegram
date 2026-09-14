"use strict";

const NAVBAR_TAG = "inc-navbar";
const TABS_TAG = "inc-tabs";
const USER_MENU_TAG = "inc-user-menu";

const TAB_KEYS = new Set(["ArrowRight", "ArrowLeft", "ArrowDown", "ArrowUp", "Home", "End", "Enter", " "]);
const MENU_KEYS = new Set(["ArrowDown", "ArrowUp", "Home", "End", "Escape", "Enter", " "]);
const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;

let uidCounter = 0;

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
        detail,
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

    return Array.from(container.querySelectorAll("a[href], button:not([disabled]), [role='menuitem'], [role='menuitemradio'], [role='menuitemcheckbox'], [tabindex]:not([tabindex='-1'])"))
        .filter((candidate) => candidate instanceof HTMLElement && !candidate.hasAttribute("disabled") && !candidate.hasAttribute("aria-disabled"));
}

class IncNavbarElement extends HostElement {
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
        Array.from(this.classList)
            .filter((token) => token.startsWith("inc-navbar--expand-"))
            .forEach((token) => this.classList.remove(token));

        if (breakpoint) {
            this.classList.add(`inc-navbar--expand-${breakpoint}`);
        }

        const variant = this.getAttribute("variant");
        Array.from(this.classList)
            .filter((token) => token.startsWith("inc-navbar--variant-"))
            .forEach((token) => this.classList.remove(token));

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
}

class IncTabsElement extends HostElement {
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
        if (value === null || value === undefined || value === "") {
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
        const nextId = target.id || null;

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

        if (nextId) {
            this.setAttribute("selected", nextId);
        }

        if (options.focus) {
            target.focus();
        }

        if (options.emitEvents !== false) {
            emit(this, "select", { previous: previousId, selected: nextId, tab: target });
            emit(this, "change", { previous: previousId, selected: nextId, tab: target });
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
        Array.from(this.classList)
            .filter((token) => token.startsWith("inc-tabs-host--"))
            .forEach((token) => this.classList.remove(token));

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

        if (value === null || value === undefined || value === "") {
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
                const escapedId = typeof CSS !== "undefined" && typeof CSS.escape === "function"
                    ? CSS.escape(ariaControls)
                    : ariaControls.replace(/([^\w-])/g, "\\$1");
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
                // Invalid selector is ignored intentionally.
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
        } else if ((event.key === "ArrowRight" && orientation === "horizontal") || (event.key === "ArrowDown" && orientation === "vertical")) {
            nextIndex = (currentIndex + 1) % tabs.length;
        } else if ((event.key === "ArrowLeft" && orientation === "horizontal") || (event.key === "ArrowUp" && orientation === "vertical")) {
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
}

class IncUserMenuElement extends HostElement {
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

        Array.from(this.classList)
            .filter((token) => token.startsWith("inc-user-menu--"))
            .forEach((token) => this.classList.remove(token));

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
            text: item.textContent?.trim() || "",
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
}

function defineNavigationComponents(registry = globalThis.customElements) {
    if (!registry) {
        return {
            navbarDefined: false,
            tabsDefined: false,
            userMenuDefined: false,
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

const navigationApi = {
    NAVBAR_TAG,
    TABS_TAG,
    USER_MENU_TAG,
    IncNavbarElement,
    IncTabsElement,
    IncUserMenuElement,
    defineNavigationComponents,
};

if (typeof module !== "undefined" && module.exports) {
    module.exports = navigationApi;
}

if (typeof window !== "undefined") {
    window.IncWebComponents = window.IncWebComponents || {};
    window.IncWebComponents.navigation = navigationApi;
}
