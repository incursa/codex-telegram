const FOCUSABLE_SELECTOR = [
    'a[href]:not([tabindex="-1"])',
    'button:not([disabled]):not([tabindex="-1"])',
    'input:not([disabled]):not([type="hidden"]):not([tabindex="-1"])',
    'select:not([disabled]):not([tabindex="-1"])',
    'textarea:not([disabled]):not([tabindex="-1"])',
    '[tabindex]:not([tabindex="-1"])',
    '[contenteditable="true"]',
].join(", ");

function getFocusableElements(root) {
    if (!(root instanceof Element || root instanceof Document || root instanceof ShadowRoot)) {
        return [];
    }

    return Array.from(root.querySelectorAll(FOCUSABLE_SELECTOR))
        .filter((element) => element instanceof HTMLElement)
        .filter((element) => !element.hasAttribute("inert"))
        .filter((element) => element.offsetParent !== null || element === document.activeElement);
}

function getFirstFocusable(root) {
    return getFocusableElements(root)[0] ?? null;
}

function getLastFocusable(root) {
    const items = getFocusableElements(root);
    return items.length ? items[items.length - 1] : null;
}

function focusFirst(root) {
    const first = getFirstFocusable(root);
    first?.focus();
    return first;
}

function focusLast(root) {
    const last = getLastFocusable(root);
    last?.focus();
    return last;
}

function trapTabKey(event, root) {
    if (!(event instanceof KeyboardEvent) || event.key !== "Tab") {
        return false;
    }

    const items = getFocusableElements(root);
    if (!items.length) {
        return false;
    }

    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;

    if (event.shiftKey && active === first) {
        event.preventDefault();
        last.focus();
        return true;
    }

    if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
        return true;
    }

    return false;
}

function createFocusRestorer(fallback = null) {
    const source = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : (fallback instanceof HTMLElement ? fallback : null);

    return () => {
        if (source && source.isConnected) {
            source.focus();
            return source;
        }

        if (fallback instanceof HTMLElement && fallback.isConnected) {
            fallback.focus();
            return fallback;
        }

        return null;
    };
}

export {
    FOCUSABLE_SELECTOR,
    createFocusRestorer,
    focusFirst,
    focusLast,
    getFirstFocusable,
    getFocusableElements,
    getLastFocusable,
    trapTabKey,
};
