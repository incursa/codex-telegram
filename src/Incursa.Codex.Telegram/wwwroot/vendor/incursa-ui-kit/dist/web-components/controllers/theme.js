import { dispatchComponentEvent } from "../shared.js";

const DEFAULT_STORAGE_KEY = "inc-theme-mode";
const THEME_MODES = ["light", "dark", "system"];

function isThemeMode(value) {
    return THEME_MODES.includes(value);
}

function resolveSystemTheme() {
    if (typeof window === "undefined" || typeof window.matchMedia !== "function") {
        return "light";
    }

    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function resolveThemeMode(mode) {
    return mode === "system" ? resolveSystemTheme() : mode;
}

function getThemeRoot() {
    return typeof document !== "undefined" ? document.documentElement : null;
}

function getStoredThemeMode(storageKey = DEFAULT_STORAGE_KEY) {
    try {
        const value = window.localStorage.getItem(storageKey);
        return isThemeMode(value) ? value : null;
    } catch {
        return null;
    }
}

function persistThemeMode(mode, storageKey = DEFAULT_STORAGE_KEY) {
    try {
        if (mode === "system") {
            window.localStorage.removeItem(storageKey);
            return;
        }

        window.localStorage.setItem(storageKey, mode);
    } catch {
        // Ignore storage restrictions.
    }
}

function applyRootTheme(mode, options = {}) {
    const root = getThemeRoot();
    if (!(root instanceof HTMLElement)) {
        return { mode: "system", resolved: "light" };
    }

    const nextMode = isThemeMode(mode) ? mode : "system";
    const resolved = resolveThemeMode(nextMode);
    root.setAttribute("data-inc-theme-mode", nextMode);
    root.setAttribute("data-bs-theme", resolved);
    root.style.colorScheme = resolved;
    root.dataset.incThemeModeState = nextMode;
    root.dataset.incThemeResolved = resolved;

    if (options.persist !== false) {
        persistThemeMode(nextMode, options.storageKey);
    }

    if (options.dispatch !== false) {
        dispatchComponentEvent(root, "inc-theme-change", {
            mode: nextMode,
            resolved,
        });
    }

    return { mode: nextMode, resolved };
}

function getRootThemeState() {
    const root = getThemeRoot();
    if (!(root instanceof HTMLElement)) {
        return { mode: "system", resolved: "light" };
    }

    const configuredMode = root.getAttribute("data-inc-theme-mode")
        || root.dataset.incThemeMode
        || getStoredThemeMode()
        || "system";
    const mode = isThemeMode(configuredMode) ? configuredMode : "system";

    return {
        mode,
        resolved: resolveThemeMode(mode),
    };
}

function getLegacyThemeBridge() {
    if (typeof window === "undefined") {
        return null;
    }

    const legacy = window.IncTheme;
    if (!legacy || typeof legacy !== "object") {
        return null;
    }

    if (typeof legacy.setMode !== "function" || typeof legacy.cycleMode !== "function") {
        return null;
    }

    return legacy;
}

function createThemeController(options = {}) {
    const storageKey = options.storageKey || DEFAULT_STORAGE_KEY;
    const preferLegacyBridge = options.preferLegacyBridge !== false;
    let state = getRootThemeState();

    function apply(mode, applyOptions = {}) {
        const legacy = preferLegacyBridge ? getLegacyThemeBridge() : null;
        if (legacy && applyOptions.useLegacyBridge !== false) {
            legacy.setMode(mode);
            state = {
                mode: legacy.getMode(),
                resolved: legacy.getResolvedTheme(),
            };
            return state;
        }

        state = applyRootTheme(mode, {
            storageKey,
            persist: applyOptions.persist,
            dispatch: applyOptions.dispatch,
        });
        return state;
    }

    return {
        getMode() {
            return state.mode;
        },
        getResolvedTheme() {
            return state.resolved;
        },
        initialize() {
            const initialMode = getStoredThemeMode(storageKey)
                || getThemeRoot()?.getAttribute("data-inc-theme-mode")
                || "system";
            return apply(initialMode, { persist: false });
        },
        setMode(mode) {
            return apply(mode);
        },
        cycleMode() {
            const index = THEME_MODES.indexOf(state.mode);
            const nextMode = THEME_MODES[(index + 1) % THEME_MODES.length];
            return apply(nextMode);
        },
        syncFromRoot() {
            state = getRootThemeState();
            return state;
        },
    };
}

export {
    DEFAULT_STORAGE_KEY,
    THEME_MODES,
    applyRootTheme,
    createThemeController,
    getRootThemeState,
    getStoredThemeMode,
    isThemeMode,
    resolveSystemTheme,
    resolveThemeMode,
};
