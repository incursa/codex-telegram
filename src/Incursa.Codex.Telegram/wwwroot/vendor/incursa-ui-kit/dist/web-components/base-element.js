import {
    dispatchComponentEvent,
    getAssignedSlotElements,
    normalizeAttributeConfig,
    parseValueFromAttribute,
    readReflectedAttribute,
    reflectAttributeValue,
    serializeValueForAttribute,
} from "./shared.js";

const HostElement = typeof HTMLElement === "undefined" ? class {} : HTMLElement;
const metadataByConstructor = new WeakMap();

function buildMetadata(constructor) {
    if (metadataByConstructor.has(constructor)) {
        return metadataByConstructor.get(constructor);
    }

    const reflectedConfig = constructor.reflectedAttributes || {};
    const propertyToConfig = new Map();
    const attributeToConfig = new Map();

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
            },
        });
    });

    const metadata = {
        propertyToConfig,
        attributeToConfig,
    };

    metadataByConstructor.set(constructor, metadata);
    return metadata;
}

class IncElement extends HostElement {
    static reflectedAttributes = {};

    static get observedAttributes() {
        const metadata = buildMetadata(this);
        return [...metadata.attributeToConfig.keys()];
    }

    constructor() {
        super();
        this._propertyValues = new Map();
        this._slotListeners = new Map();
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
            return () => {};
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
}

export {
    IncElement,
};
