import {
    incIconNames,
    normalizeIconName,
    replaceIconContents,
} from "../../icons/index.js";

const THEME_MODES = ["light", "dark", "system"];
const DEFAULT_THEME_STORAGE_KEY = "inc-theme-mode";
const BADGE_TONES = new Set(["primary", "secondary", "success", "danger", "warning", "info"]);
const SPINNER_VARIANTS = new Set(["border", "grow"]);
const ICON_NAME_SET = new Set(incIconNames);
const STATE_ICON_BY_VARIANT = new Map([
    ["empty", "empty"],
    ["results", "no-results"],
    ["loading", "loading"],
    ["error", "error"],
    ["danger", "error"],
    ["warning", "warning"],
    ["success", "success"],
    ["info", "info"],
]);
const STATE_ICON_BY_STATUS = new Map([
    ["+", "empty"],
    ["?", "no-results"],
    ["!", "error"],
    ["...", "loading"],
]);
const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;
const themeSubscribers = new Set();

let themeRuntimeInitialized = false;
let themeMode = "system";
let themeResolved = "light";
let themeStorageKey = DEFAULT_THEME_STORAGE_KEY;
let themeMediaQuery = null;
let themeStorageListenerBound = false;
let themeMediaListenerBound = false;

function isThemeMode(value) {
    return THEME_MODES.includes(value);
}

function toBooleanAttribute(value) {
    if (value === null || value === undefined) {
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
        size,
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

    return root.getAttribute("data-inc-theme-mode")
        || root.dataset.incThemeMode
        || root.getAttribute("data-bs-theme")
        || "system";
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
        // Ignore storage failures in restricted/private modes.
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
                resolved,
            },
        });
        root.dispatchEvent(event);
    }

    themeSubscribers.forEach((notify) => {
        try {
            notify({ mode: nextMode, resolved });
        } catch {
            // Isolate subscriber failures.
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
            storageKey: themeStorageKey,
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
                    storageKey: themeStorageKey,
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
                        storageKey: themeStorageKey,
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
        resolved: themeResolved,
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
    const seconds = totalSeconds % 60;
    return `${minutes}m ${seconds}s`;
}

export class IncStatePanel extends HostElement {
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

        const isOpen = this.getAttribute("open") === null
            ? true
            : toBooleanAttribute(this.getAttribute("open"));

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
}

export class IncBadgeElement extends HostElement {
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
        BADGE_TONES.forEach((tone) => this.classList.remove(`inc-badge--${tone}`));
        this.classList.remove("inc-badge--pill");

        const tone = normalizeToken(this.tone);
        if (BADGE_TONES.has(tone)) {
            this.classList.add(`inc-badge--${tone}`);
        }

        if (this.pill) {
            this.classList.add("inc-badge--pill");
        }
    }
}

export class IncLiveRegion extends HostElement {
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
        const isAtomic = this.getAttribute("atomic") === null
            ? true
            : toBooleanAttribute(this.getAttribute("atomic"));
        const isBusy = toBooleanAttribute(this.getAttribute("busy"));

        this.setAttribute("role", politeness === "assertive" ? "alert" : "status");
        this.setAttribute("aria-live", politeness);
        this.setAttribute("aria-atomic", isAtomic ? "true" : "false");
        this.setAttribute("aria-busy", isBusy ? "true" : "false");
    }
}

export class IncSpinnerElement extends HostElement {
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

        SPINNER_VARIANTS.forEach((variant) => {
            this.classList.remove(`inc-spinner--${variant}`);
            this.classList.remove(`inc-spinner--${variant}--sm`);
            BADGE_TONES.forEach((tone) => this.classList.remove(`inc-spinner--${variant}--${tone}`));
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
}

export class IncAutoRefresh extends HostElement {
    static observedAttributes = [
        "seconds",
        "label",
        "loading-label",
        "paused-label",
        "pause-action-label",
        "resume-action-label",
        "paused",
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
        this.#deadline = Date.now() + Math.max(this.#remainingMs, 1000);
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
            detail: this.#buildState(),
        });

        this.dispatchEvent(refreshEvent);
        if (!refreshEvent.defaultPrevented) {
            const deferToPaint = window.requestAnimationFrame
                ? window.requestAnimationFrame.bind(window)
                : (callback) => window.setTimeout(callback, 16);

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

                if ((this.#deadline - Date.now()) <= 0) {
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
            toggleText: this.querySelector(".inc-auto-refresh__toggle-text"),
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

        this.#deadline = Date.now() + (refreshSeconds * 1000);
        this.#remainingMs = refreshSeconds * 1000;

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

        const remainingSeconds = Math.ceil(remainingMs / 1000);
        this.#remainingMs = remainingMs;
        this.#renderCountdown(remainingSeconds);

        this.dispatchEvent(new CustomEvent("tick", {
            bubbles: true,
            composed: true,
            detail: this.#buildState(),
        }));

        const nextDelay = remainingMs % 1000 || 1000;
        this.#timeoutId = window.setTimeout(() => {
            this.#scheduleTick();
        }, nextDelay);
    }

    #render() {
        if (this.#isLoading) {
            this.#renderLoading();
            return;
        }

        const fallbackSeconds = Math.max(1, Math.ceil(this.#remainingMs / 1000));
        if (this.#isPaused) {
            this.#renderPaused(fallbackSeconds);
            return;
        }

        this.#renderCountdown(fallbackSeconds);
    }

    #renderCountdown(seconds) {
        const label = this.getAttribute("label") || "Refresh in";
        if (this.#parts.label) {
            this.#parts.label.textContent = label;
        }

        if (this.#parts.value) {
            this.#parts.value.textContent = formatRemaining(seconds);
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

    #renderPaused(seconds) {
        const label = this.getAttribute("paused-label") || "Paused at";
        if (this.#parts.label) {
            this.#parts.label.textContent = label;
        }

        if (this.#parts.value) {
            this.#parts.value.textContent = formatRemaining(seconds);
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
            remainingSeconds: Math.max(0, Math.ceil(this.#remainingMs / 1000)),
        };
    }

    #emitStateChange(status) {
        this.dispatchEvent(new CustomEvent("statechange", {
            bubbles: true,
            composed: true,
            detail: {
                status,
                ...this.#buildState(),
            },
        }));
    }
}

export class IncThemeSwitcher extends HostElement {
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
            storageKey: this.storageKey,
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
                detail.textContent = mode === "system"
                    ? "Match the device preference automatically."
                    : `Use the ${mode} application palette.`;

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
            const label = mode === "system"
                ? `System (${resolved.charAt(0).toUpperCase()}${resolved.slice(1)})`
                : `${mode.charAt(0).toUpperCase()}${mode.slice(1)}`;
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
}

export const feedbackDefinitions = [
    ["inc-badge", IncBadgeElement],
    ["inc-state-panel", IncStatePanel],
    ["inc-live-region", IncLiveRegion],
    ["inc-spinner", IncSpinnerElement],
    ["inc-auto-refresh", IncAutoRefresh],
    ["inc-theme-switcher", IncThemeSwitcher],
];

export function defineFeedbackComponents(definer = typeof customElements !== "undefined" ? customElements : null) {
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
    const namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
    namespace.feedback = Object.assign({}, namespace.feedback, {
        defineFeedbackComponents,
        feedbackDefinitions,
        components: {
            IncBadgeElement,
            IncSpinnerElement,
            IncStatePanel,
            IncLiveRegion,
            IncAutoRefresh,
            IncThemeSwitcher,
        },
    });
}
