import { IncElement } from "../base-element.js";
import {
    addClass,
    ensureNode,
    normalizeToken,
    removeMatchingClasses,
} from "./dom-helpers.js";

const FALSE_TOKENS = new Set(["false", "0", "off", "no"]);

function toBoolean(value, fallback = false) {
    if (value == null) {
        return fallback;
    }

    return !FALSE_TOKENS.has(String(value).toLowerCase());
}

export class IncListGroupElement extends IncElement {
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

        if (toBoolean(this.getAttribute("flush"))) {
            this.classList.add("inc-list-group--flush");
        }
        if (toBoolean(this.getAttribute("numbered"))) {
            this.classList.add("inc-list-group--numbered");
        }
        if (toBoolean(this.getAttribute("dense"))) {
            this.classList.add("inc-list-group--dense");
        }
        if (toBoolean(this.getAttribute("interactive"))) {
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
            if (toBoolean(this.getAttribute("interactive")) || node.matches("a[href], button:not([disabled])")) {
                node.classList.add("inc-list-group__item--action");
            }
            if (!node.hasAttribute("role") && !node.matches("a[href], button")) {
                node.setAttribute("role", "listitem");
            }
        });
    }
}

export class IncKeyValueGridElement extends IncElement {
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

        if (toBoolean(this.getAttribute("dense"))) {
            this.style.rowGap = "0.5rem";
            this.style.columnGap = "1rem";
        } else {
            this.style.removeProperty("row-gap");
            this.style.removeProperty("column-gap");
        }
    }
}

export class IncKeyValueElement extends IncElement {
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

        const variant = normalizeToken(this.getAttribute("variant"));
        const card = toBoolean(this.getAttribute("card")) || variant === "card";
        if (card) {
            this.classList.add("inc-key-value--card");
        }
        if (toBoolean(this.getAttribute("inline"))) {
            this.classList.add("inc-key-value--inline");
        }
        if (toBoolean(this.getAttribute("dense"))) {
            this.classList.add("inc-key-value--dense");
        }

        const definition = ensureNode(this, ".inc-key-value__definition", () => {
            const node = document.createElement("dl");
            node.className = "inc-key-value__definition";
            node.innerHTML = [
                '<dt class="inc-key-value__label" part="label"></dt>',
                '<dd class="inc-key-value__value" part="value"></dd>',
                '<div class="inc-key-value__meta" part="meta"></div>',
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
}

export const collectionDefinitions = [
    ["inc-list-group", IncListGroupElement],
    ["inc-key-value-grid", IncKeyValueGridElement],
    ["inc-key-value", IncKeyValueElement],
];

export const collectionComponents = {
    IncListGroupElement,
    IncKeyValueGridElement,
    IncKeyValueElement,
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
        IncKeyValueElement,
    };
}

if (typeof globalThis !== "undefined") {
    const namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
    namespace.collections = Object.assign({}, namespace.collections, {
        defineCollectionComponents,
        collectionDefinitions,
        components: collectionComponents,
    });
}
