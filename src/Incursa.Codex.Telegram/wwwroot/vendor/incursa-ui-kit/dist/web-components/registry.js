import { defineCustomElement, getIncWebComponentsNamespace } from "./shared.js";

const registryEntries = new Map();

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
    const namespace = getIncWebComponentsNamespace();
    if (!namespace) {
        return null;
    }

    namespace.registry = {
        registerComponent,
        registerComponents,
        getRegisteredComponents,
        hasRegisteredComponent,
        defineAll,
    };

    return namespace.registry;
}

export {
    defineAll,
    getRegisteredComponents,
    hasRegisteredComponent,
    installRegistryNamespace,
    registerComponent,
    registerComponents,
};
