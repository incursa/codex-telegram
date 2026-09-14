/* eslint-disable max-classes-per-file */
(function (root, factory) {
    if (typeof module === "object" && module.exports) {
        module.exports = factory();
        return;
    }

    const exports = factory();
    root.IncWebComponents = root.IncWebComponents || {};
    root.IncWebComponents.layout = exports;
}(typeof globalThis !== "undefined" ? globalThis : window, function () {
    "use strict";

    const BOOLEAN_ATTRIBUTE_TYPES = new Set(["boolean"]);
    const HTMLElementRef = typeof HTMLElement === "undefined" ? null : HTMLElement;
    const MutationObserverRef = typeof MutationObserver === "undefined" ? null : MutationObserver;
    const BaseElement = HTMLElementRef || class {};

    function toBooleanAttribute(value) {
        return value === true || value === "" || value === "true";
    }

    function tokenList(value) {
        if (!value) {
            return [];
        }

        return String(value)
            .split(/\s+/u)
            .map((part) => part.trim())
            .filter(Boolean);
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
            this._appliedTokenClasses = new Map();
            this._appliedBooleanClasses = new Map();
            this._appliedIntegerClasses = new Map();
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
                attributeFilter: ["slot"],
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
            const hostClasses = [config.baseClass, ...(config.hostClasses || [])].filter(Boolean);

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
                    const enabled = toBooleanAttribute(value);
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

                    if (value === null || value === undefined || value === "") {
                        this.removeAttribute(attributeName);
                        return;
                    }

                    this.setAttribute(attributeName, String(value));
                },
            });
        });
    }

    class IncAppShellElement extends IncLayoutElement {}
    IncAppShellElement.layoutConfig = {
        baseClass: "inc-app-shell",
        parts: "shell header main footer",
        attributes: {
            variant: { type: "token" },
            dense: { type: "boolean" },
            collapsed: { type: "boolean" },
        },
        slotClasses: {
            header: "inc-app-shell__header",
            main: "inc-app-shell__main",
            footer: "inc-app-shell__footer",
        },
    };

    class IncPageElement extends IncLayoutElement {}
    IncPageElement.layoutConfig = {
        baseClass: "inc-page",
        parts: "page breadcrumbs body aside footer",
        attributes: {
            variant: { type: "token" },
            dense: { type: "boolean" },
            wide: { type: "boolean" },
        },
        slotClasses: {
            breadcrumbs: "inc-page__breadcrumbs",
            header: "inc-page__header",
            body: "inc-page__body",
            aside: "inc-page__aside",
            footer: "inc-page__footer",
        },
    };

    class IncPageHeaderElement extends IncLayoutElement {}
    IncPageHeaderElement.layoutConfig = {
        baseClass: "inc-page-header",
        parts: "header title body actions",
        attributes: {
            variant: { type: "token" },
            dense: { type: "boolean" },
        },
        slotClasses: {
            title: "inc-page-header__title",
            body: "inc-page-header__body",
            actions: "inc-page-header__actions",
        },
    };

    class IncSectionElement extends IncLayoutElement {}
    IncSectionElement.layoutConfig = {
        baseClass: "inc-section-container",
        hostClasses: ["inc-section"],
        parts: "section header body footer actions",
        attributes: {
            variant: { type: "token", classPrefix: "inc-section--" },
            dense: { type: "boolean", trueClass: "inc-section--dense" },
            tone: { type: "token", classPrefix: "inc-section--tone-" },
        },
        slotClasses: {
            header: "inc-section__header",
            body: "inc-section__body",
            footer: "inc-section__footer",
            actions: "inc-section__actions",
        },
    };

    class IncCardElement extends IncLayoutElement {}
    IncCardElement.layoutConfig = {
        baseClass: "inc-card",
        parts: "card header body footer",
        attributes: {
            variant: { type: "token" },
            tone: { type: "token", classPrefix: "inc-card--tone-" },
            elevated: { type: "boolean", trueClass: "inc-card--elevated" },
        },
        slotClasses: {
            header: "inc-card__header",
            body: "inc-card__body",
            footer: "inc-card__footer",
        },
    };

    class IncSummaryOverviewElement extends IncLayoutElement {}
    IncSummaryOverviewElement.layoutConfig = {
        baseClass: "inc-summary-overview",
        parts: "overview",
        attributes: {
            columns: { type: "integer" },
            dense: { type: "boolean" },
        },
        slotClasses: {},
    };

    class IncSummaryBlockElement extends IncLayoutElement {}
    IncSummaryBlockElement.layoutConfig = {
        baseClass: "inc-summary-block",
        parts: "block header body footer actions value status",
        attributes: {
            variant: { type: "token" },
            tone: { type: "token", classPrefix: "inc-summary-block--tone-" },
            dense: { type: "boolean" },
        },
        slotClasses: {
            header: "inc-summary-block__header",
            body: "inc-summary-block__body",
            footer: "inc-summary-block__footer",
            actions: "inc-summary-block__actions",
        },
    };

    class IncFooterBarElement extends IncLayoutElement {}
    IncFooterBarElement.layoutConfig = {
        baseClass: "inc-footer-bar",
        parts: "footer menu meta",
        attributes: {
            variant: { type: "token" },
            dense: { type: "boolean" },
        },
        slotClasses: {
            menu: "inc-footer-bar__menu",
            meta: "inc-footer-bar__meta",
        },
    };

    const layoutComponents = [
        ["inc-app-shell", IncAppShellElement],
        ["inc-page", IncPageElement],
        ["inc-page-header", IncPageHeaderElement],
        ["inc-section", IncSectionElement],
        ["inc-card", IncCardElement],
        ["inc-summary-overview", IncSummaryOverviewElement],
        ["inc-summary-block", IncSummaryBlockElement],
        ["inc-footer-bar", IncFooterBarElement],
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
        defineLayoutComponents,
    };
}));
