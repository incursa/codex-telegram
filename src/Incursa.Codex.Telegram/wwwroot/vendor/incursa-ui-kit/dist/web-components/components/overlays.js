const INTERNAL_NODE = Symbol("inc-internal-overlay-node");

const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;

const DETAILS_SELECTOR = "details.inc-disclosure";
const DIALOG_SELECTOR = "dialog.inc-native-dialog";

function toBooleanAttribute(value, fallback = false) {
    if (value === null || value === undefined) {
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

function emit(host, name, detail = {}) {
    host.dispatchEvent(new CustomEvent(name, {
        bubbles: true,
        composed: true,
        detail,
    }));
}

function appendProjectedChildren(host, destinationMap, fallbackDestination, ignoredNodes = new Set()) {
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
        "[tabindex]:not([tabindex='-1'])",
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

class IncDisclosureElement extends HostElement {
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

        const toggleable = toBooleanAttribute(this.getAttribute("toggleable"), true);
        this._details.dataset.incToggleable = toggleable ? "true" : "false";
    }

    _projectChildren() {
        if (!this._details || !this._summaryTitle || !this._content) {
            return;
        }

        this._isSyncing = true;

        this._summaryTitle.replaceChildren();
        this._content.replaceChildren();

        const destinations = new Map([
            ["summary", this._summaryTitle],
            ["content", this._content],
            ["default", this._content],
        ]);

        appendProjectedChildren(this, destinations, this._content, new Set([this._details]));

        this._isSyncing = false;
    }

    _onToggle() {
        const open = this._details?.open === true;
        setBooleanAttribute(this, "open", open);

        emit(this, "toggle", { open });
        emit(this, open ? "open" : "close", { open });
    }
}

class IncDialogBaseElement extends HostElement {
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
        return toBooleanAttribute(this.getAttribute("modal"), true);
    }

    set modal(value) {
        setBooleanAttribute(this, "modal", Boolean(value));
    }

    get dismissible() {
        return toBooleanAttribute(this.getAttribute("dismissible"), true);
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
        emit(this, "dismiss", { reason });
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

        const destinations = new Map([
            ["title", this._title],
            ["header", this._header],
            ["body", this._body],
            ["footer", this._footer],
            ["default", this._body],
        ]);

        appendProjectedChildren(this, destinations, this._body, new Set([this._dialog]));

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
        emit(this, "open", { modal: asModal });
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

        emit(this, "cancel", { reason: "escape" });
    }

    _onDialogClose() {
        setBooleanAttribute(this, "open", this._dialog?.open === true);
        emit(this, "close", { returnValue: this._dialog?.returnValue || "" });
        this._restoreFocus();
    }
}

class IncDialogElement extends IncDialogBaseElement {
    constructor() {
        super();
        this._tagType = "dialog";
    }
}

class IncDrawerElement extends IncDialogBaseElement {
    constructor() {
        super();
        this._tagType = "drawer";
    }

    show() {
        this._rememberTrigger();
        this.setAttribute("open", "");
        this._openDialog(this.modal);
    }
}

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

const overlayComponentsApi = {
    defineOverlayComponents,
    IncDisclosureElement,
    IncDialogElement,
    IncDrawerElement,
};

if (typeof module !== "undefined" && module.exports) {
    module.exports = overlayComponentsApi;
}

if (typeof globalThis !== "undefined") {
    globalThis.IncWebComponents = globalThis.IncWebComponents || {};
    globalThis.IncWebComponents.overlays = overlayComponentsApi;
}
