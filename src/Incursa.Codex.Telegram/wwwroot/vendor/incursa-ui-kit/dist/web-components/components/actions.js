import {
    normalizeIconName,
    replaceIconContents,
} from "../../icons/index.js";
import {
    addClass,
    ensureNode,
    moveChildNodes,
    normalizeToken,
    removeMatchingClasses,
} from "./dom-helpers.js";

const FALSE_TOKENS = new Set(["false", "0", "off", "no"]);
const BADGE_TONES = new Set(["primary", "secondary", "success", "danger", "warning", "info"]);
const BUTTON_VARIANTS = new Set([
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
    "outline-info",
]);
const BUTTON_SIZES = new Set(["sm", "lg", "micro"]);
const ALERT_DEFAULT_ROLE_BY_TONE = new Map([
    ["info", "status"],
    ["secondary", "status"],
]);
const ALERT_ICON_BY_TONE = new Map([
    ["success", "success"],
    ["danger", "error"],
    ["warning", "warning"],
    ["info", "info"],
    ["secondary", "info"],
    ["primary", "info"],
]);

const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;

function toBoolean(value, fallback = false) {
    if (value == null) {
        return fallback;
    }

    return !FALSE_TOKENS.has(String(value).toLowerCase());
}

function toPositiveInt(value) {
    const parsed = Number.parseInt(String(value ?? "").trim(), 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function emit(host, type, detail = {}, options = {}) {
    return host.dispatchEvent(new CustomEvent(type, {
        detail,
        bubbles: options.bubbles !== false,
        composed: options.composed !== false,
        cancelable: options.cancelable === true,
    }));
}

function getDirectIconSlot(host) {
    return Array.from(host.children || []).find((node) => (
        node instanceof HTMLElement
        && node.getAttribute("slot") === "icon"
    )) || null;
}

function hasConsumerIcon(container) {
    return Array.from(container.children || []).some((node) => (
        node instanceof HTMLElement
        && !node.hasAttribute("data-inc-generated-icon")
    ));
}

function renderDecorativeIcon(container, name, options = {}) {
    replaceIconContents(container, name, {
        className: "inc-icon",
        decorative: true,
        size: options.size || 16,
    });
    container.hidden = false;
}

class IncElement extends HostElement {
    emit(type, detail = {}, options = {}) {
        return emit(this, type, detail, options);
    }
}

export class IncButtonElement extends IncElement {
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
        const variant = normalizeToken(this.getAttribute("variant") || this.getAttribute("tone")) || "secondary";
        const resolvedVariant = BUTTON_VARIANTS.has(variant) ? variant : "secondary";
        const size = normalizeToken(this.getAttribute("size"));
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
        const inferredIcon = this.getAttribute("download") != null
            ? "download"
            : this.getAttribute("target") === "_blank"
                ? "external-link"
                : "";
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
            renderDecorativeIcon(icon, iconName, { size: 16 });
            return;
        }

        icon.remove();
    }
}

export class IncButtonGroupElement extends IncElement {
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

        const size = normalizeToken(this.getAttribute("size"));
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
}

export class IncButtonToolbarElement extends IncElement {
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

        const orientation = normalizeToken(this.getAttribute("orientation"));
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
}

export class IncCloseButtonElement extends IncElement {
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
        const variant = normalizeToken(this.getAttribute("variant"));

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
}

export class IncAlertElement extends IncElement {
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

        const tone = normalizeToken(this.getAttribute("tone") || this.getAttribute("variant")) || "info";
        const resolvedTone = BADGE_TONES.has(tone) ? tone : "info";
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

        const timeoutMs = toPositiveInt(this.getAttribute("timeout"));
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

        renderDecorativeIcon(icon, iconName, { size: 18 });
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
}

export class IncEmptyStateElement extends IncElement {
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
                '<div class="inc-empty-state__actions" part="actions"></div>',
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

        renderDecorativeIcon(icon, iconName, { size: 34 });
    }
}

const actionDefinitions = [
    ["inc-button", IncButtonElement],
    ["inc-button-group", IncButtonGroupElement],
    ["inc-button-toolbar", IncButtonToolbarElement],
    ["inc-close-button", IncCloseButtonElement],
    ["inc-alert", IncAlertElement],
    ["inc-empty-state", IncEmptyStateElement],
];

const actionComponents = {
    IncButtonElement,
    IncButtonGroupElement,
    IncButtonToolbarElement,
    IncCloseButtonElement,
    IncAlertElement,
    IncEmptyStateElement,
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
        IncEmptyStateElement,
    };
}

if (typeof globalThis !== "undefined") {
    const namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
    namespace.actions = Object.assign({}, namespace.actions, {
        defineActionComponents,
        actionDefinitions,
        components: actionComponents,
    });
}
