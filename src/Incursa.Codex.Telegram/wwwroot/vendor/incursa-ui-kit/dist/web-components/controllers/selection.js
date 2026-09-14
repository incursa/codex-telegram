function clampIndex(value, length) {
    if (!length) {
        return -1;
    }

    if (!Number.isFinite(value)) {
        return 0;
    }

    if (value < 0) {
        return 0;
    }

    if (value >= length) {
        return length - 1;
    }

    return value;
}

function resolveNextIndex(currentIndex, key, length, orientation = "horizontal") {
    const horizontal = orientation === "horizontal";
    const vertical = orientation === "vertical";

    if (!length) {
        return -1;
    }

    if (key === "Home") {
        return 0;
    }

    if (key === "End") {
        return length - 1;
    }

    if ((horizontal && key === "ArrowRight") || (vertical && key === "ArrowDown")) {
        return (currentIndex + 1 + length) % length;
    }

    if ((horizontal && key === "ArrowLeft") || (vertical && key === "ArrowUp")) {
        return (currentIndex - 1 + length) % length;
    }

    return currentIndex;
}

function updateRovingTabIndex(items, activeIndex) {
    items.forEach((item, index) => {
        if (!(item instanceof HTMLElement)) {
            return;
        }

        item.tabIndex = index === activeIndex ? 0 : -1;
    });
}

function createRovingSelection(options) {
    const getItems = typeof options.getItems === "function"
        ? options.getItems
        : () => [];
    const onChange = typeof options.onChange === "function"
        ? options.onChange
        : () => {};
    const orientation = options.orientation || "horizontal";
    const activation = options.activation || "auto";
    let selectedIndex = Number.isFinite(options.initialIndex) ? options.initialIndex : 0;

    function getNormalizedItems() {
        return getItems().filter((item) => item instanceof HTMLElement);
    }

    function setSelectedIndex(nextIndex, origin = "api") {
        const items = getNormalizedItems();
        if (!items.length) {
            selectedIndex = -1;
            return -1;
        }

        selectedIndex = clampIndex(nextIndex, items.length);
        updateRovingTabIndex(items, selectedIndex);
        onChange({
            index: selectedIndex,
            item: items[selectedIndex],
            items,
            origin,
        });
        return selectedIndex;
    }

    function moveByKey(key, event = null) {
        const items = getNormalizedItems();
        if (!items.length) {
            return -1;
        }

        const normalizedCurrent = clampIndex(selectedIndex, items.length);
        const nextIndex = resolveNextIndex(normalizedCurrent, key, items.length, orientation);
        if (nextIndex === normalizedCurrent) {
            return normalizedCurrent;
        }

        if (event instanceof KeyboardEvent) {
            event.preventDefault();
        }

        setSelectedIndex(nextIndex, "keyboard");
        items[nextIndex]?.focus();

        if (activation === "manual") {
            return nextIndex;
        }

        return nextIndex;
    }

    function sync() {
        const items = getNormalizedItems();
        if (!items.length) {
            selectedIndex = -1;
            return -1;
        }

        return setSelectedIndex(clampIndex(selectedIndex, items.length), "sync");
    }

    return {
        get index() {
            return selectedIndex;
        },
        get items() {
            return getNormalizedItems();
        },
        setSelectedIndex,
        moveByKey,
        sync,
    };
}

export {
    clampIndex,
    createRovingSelection,
    resolveNextIndex,
    updateRovingTabIndex,
};
