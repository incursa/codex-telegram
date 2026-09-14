const BOOLEAN_FALSE_TOKENS = new Set(["false", "0", "off", "no"]);

function isClientEnvironment() {
    return typeof window !== "undefined" && typeof document !== "undefined";
}

function isCustomElementsAvailable() {
    return typeof globalThis !== "undefined" && "customElements" in globalThis;
}

function toKebabCase(value) {
    return String(value)
        .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
        .replace(/[_\s]+/g, "-")
        .toLowerCase();
}

function normalizeAttributeConfig(propertyName, config) {
    const normalized = typeof config === "string"
        ? { attribute: config }
        : { ...config };
    const type = normalized.type || "string";

    return {
        property: propertyName,
        attribute: normalized.attribute || toKebabCase(propertyName),
        type,
        reflect: normalized.reflect !== false,
        defaultValue: normalized.defaultValue,
        parse: normalized.parse,
        serialize: normalized.serialize,
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
        detail,
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
            constructor: existing,
        };
    }

    targetRegistry.define(name, constructor);
    return { defined: true, reason: "defined", name, constructor };
}

export {
    createUniqueId,
    defineCustomElement,
    dispatchComponentEvent,
    getAssignedSlotElements,
    getIncWebComponentsNamespace,
    isClientEnvironment,
    isCustomElementsAvailable,
    normalizeAttributeConfig,
    parseBoolean,
    parseNumber,
    parseValueFromAttribute,
    readReflectedAttribute,
    reflectAttributeValue,
    serializeValueForAttribute,
    toKebabCase,
};
