import { createFocusRestorer, focusFirst, trapTabKey } from "./focus.js";

function setOpenState(host, isOpen) {
    host.toggleAttribute("open", Boolean(isOpen));
    host.setAttribute("aria-hidden", isOpen ? "false" : "true");
}

function createOverlayController(host, options = {}) {
    if (!(host instanceof HTMLElement)) {
        throw new TypeError("Overlay controller host must be an HTMLElement.");
    }

    const panel = typeof options.getPanel === "function"
        ? options.getPanel
        : () => host;
    const closeOnEscape = options.closeOnEscape !== false;
    const trapFocus = options.trapFocus !== false;
    let restoreFocus = null;
    let isOpen = host.hasAttribute("open");

    const onKeydown = (event) => {
        if (!(event instanceof KeyboardEvent) || !isOpen) {
            return;
        }

        if (closeOnEscape && event.key === "Escape") {
            event.preventDefault();
            api.close("escape");
            return;
        }

        if (trapFocus) {
            trapTabKey(event, panel());
        }
    };

    const onPointerDown = (event) => {
        if (!isOpen || options.closeOnOutsidePointerDown !== true) {
            return;
        }

        const currentPanel = panel();
        if (!(currentPanel instanceof HTMLElement)) {
            return;
        }

        if (!currentPanel.contains(event.target)) {
            api.close("outside-pointer");
        }
    };

    function bind() {
        document.addEventListener("keydown", onKeydown);
        document.addEventListener("pointerdown", onPointerDown);
    }

    function unbind() {
        document.removeEventListener("keydown", onKeydown);
        document.removeEventListener("pointerdown", onPointerDown);
    }

    const api = {
        get isOpen() {
            return isOpen;
        },
        open(origin = "api") {
            if (isOpen) {
                return false;
            }

            isOpen = true;
            restoreFocus = createFocusRestorer(options.fallbackFocus || null);
            setOpenState(host, true);
            bind();

            if (typeof options.onOpen === "function") {
                options.onOpen({ origin, host, panel: panel() });
            }

            if (options.focusFirst !== false) {
                focusFirst(panel());
            }

            return true;
        },
        close(reason = "api") {
            if (!isOpen) {
                return false;
            }

            isOpen = false;
            setOpenState(host, false);
            unbind();

            if (typeof options.onClose === "function") {
                options.onClose({ reason, host, panel: panel() });
            }

            if (options.restoreFocus !== false) {
                restoreFocus?.();
            }

            restoreFocus = null;
            return true;
        },
        toggle(origin = "api") {
            if (isOpen) {
                this.close(origin);
                return false;
            }

            this.open(origin);
            return true;
        },
        dispose() {
            unbind();
            restoreFocus = null;
        },
    };

    setOpenState(host, isOpen);
    return api;
}

export {
    createOverlayController,
    setOpenState,
};
