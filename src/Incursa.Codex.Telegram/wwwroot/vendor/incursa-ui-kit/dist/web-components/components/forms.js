const NATIVE_CONTROL_SELECTOR = [
    "input:not([type='hidden'])",
    "select",
    "textarea",
    "button",
].join(", ");

const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;

let generatedIdCounter = 0;

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

    const existing = (element.getAttribute("part") || "")
        .split(/\s+/)
        .filter(Boolean);

    if (!existing.includes(partName)) {
        existing.push(partName);
        element.setAttribute("part", existing.join(" "));
    }

    return element;
}

function resolveAssignedElement(host, slotName, selector) {
    // Only treat direct light-DOM children as assigned content. Generated wrappers
    // live inside the host too, and reselecting them would make sync non-idempotent.
    const explicit = Array.from(host.children).find((child) => (
        child instanceof HTMLElement
        && child.getAttribute("slot") === slotName
    ));

    if (explicit instanceof HTMLElement) {
        return explicit;
    }

    if (!selector) {
        return null;
    }

    return Array.from(host.children).find((child) => (
        child instanceof HTMLElement
        && child.matches(selector)
    )) || null;
}

function ensureGeneratedElement(host, key, selector, factory) {
    let element = host.querySelector(selector);

    if (element instanceof HTMLElement) {
        return element;
    }

    if (!host.__incGeneratedElements) {
        host.__incGeneratedElements = new Map();
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

class IncFormsElement extends HostElement {
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
        // Overridden by concrete components.
    }

    notifySlotChange() {
        this.dispatchEvent(new CustomEvent("slotchange", {
            bubbles: true,
            composed: true,
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

        // Only watch direct child assignment changes. Managed subtrees are updated
        // by sync() itself and should not retrigger the observer loop.
        this.__observer.observe(this, {
            childList: true,
            subtree: false,
            attributes: true,
            attributeFilter: ["slot"],
        });
    }
}

class IncFieldElement extends IncFormsElement {
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

            if (!control.classList.contains("inc-form__control")
                && !control.classList.contains("inc-input-group")
                && control.slot !== "control") {
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
            () => document.createElement("label"),
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
            () => document.createElement("p"),
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
            () => document.createElement("p"),
        );

        if (!generated.id) {
            generated.id = nextGeneratedId("error");
        }

        generated.textContent = this.error;
        return generated;
    }
}

class IncInputGroupElement extends IncFormsElement {
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
            () => document.createElement("span"),
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
            () => document.createElement("span"),
        );

        generated.setAttribute("data-inc-suffix", "true");
        generated.textContent = this.suffix;
        return generated;
    }

    __resolveControl() {
        const control = resolveAssignedElement(this, "control", NATIVE_CONTROL_SELECTOR);
        return control instanceof HTMLElement ? control : null;
    }
}

class IncChoiceGroupElement extends IncFormsElement {
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
            () => document.createElement("legend"),
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

        const slotItems = Array.from(this.children).filter((child) => (
            child instanceof HTMLElement
            && child.getAttribute("slot") === "item"
        ));
        if (!slotItems.length) {
            return null;
        }

        const generated = ensureGeneratedElement(
            this,
            "items",
            '[data-inc-generated="items"]',
            () => document.createElement("div"),
        );

        generated.setAttribute("data-inc-choice-items", "true");

        for (const node of slotItems) {
            // Skip nodes that are already inside the generated wrapper to avoid
            // reordering the same items on every observer tick.
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
            () => document.createElement("p"),
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
            () => document.createElement("p"),
        );

        generated.setAttribute("data-inc-choice-error", "true");
        generated.textContent = errorText;
        return generated;
    }
}

class IncReadonlyFieldElement extends IncFormsElement {
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
            () => document.createElement("span"),
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
            () => document.createElement("span"),
        );

        generated.setAttribute("data-inc-readonly-value", "true");
        generated.textContent = valueText;
        return generated;
    }
}

class IncValidationSummaryElement extends IncFormsElement {
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
            () => document.createElement("span"),
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
                    title.textContent = numeric === 1
                        ? "There is 1 issue to fix"
                        : `There are ${numeric} issues to fix`;
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
            ".inc-form__error-summary-title, [data-inc-validation-title]",
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
            () => document.createElement("h3"),
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

        const slotItems = Array.from(this.children).filter((child) => (
            child instanceof HTMLElement
            && child.getAttribute("slot") === "item"
        ));
        if (!slotItems.length) {
            return null;
        }

        const generated = ensureGeneratedElement(
            this,
            "list",
            '[data-inc-generated="list"]',
            () => document.createElement("ul"),
        );

        generated.setAttribute("data-inc-validation-list", "true");

        for (const item of slotItems) {
            if (generated.contains(item)) {
                continue;
            }

            if (item.tagName !== "LI") {
                // Wrap non-list items once, then leave them in place on later syncs.
                const wrapped = document.createElement("li");
                wrapped.append(item);
                generated.append(wrapped);
                continue;
            }

            generated.append(item);
        }

        return generated;
    }
}

const FORM_COMPONENTS = [
    ["inc-field", IncFieldElement],
    ["inc-input-group", IncInputGroupElement],
    ["inc-choice-group", IncChoiceGroupElement],
    ["inc-readonly-field", IncReadonlyFieldElement],
    ["inc-validation-summary", IncValidationSummaryElement],
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
        IncValidationSummaryElement,
    };
}

if (typeof globalThis !== "undefined") {
    const namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
    namespace.forms = Object.assign({}, namespace.forms, {
        register: registerFormsComponents,
        components: {
            IncFieldElement,
            IncInputGroupElement,
            IncChoiceGroupElement,
            IncReadonlyFieldElement,
            IncValidationSummaryElement,
        },
    });
}
