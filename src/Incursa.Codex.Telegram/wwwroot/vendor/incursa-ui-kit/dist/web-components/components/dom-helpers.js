const ElementRef = typeof Element === "undefined" ? null : Element;

function normalizeToken(value) {
    return String(value ?? "").trim().toLowerCase();
}

function addClass(node, className) {
    if (ElementRef && node instanceof ElementRef && className) {
        node.classList.add(className);
    }
}

function removeMatchingClasses(node, predicate) {
    if (!ElementRef || !(node instanceof ElementRef)) {
        return;
    }

    Array.from(node.classList)
        .filter((token) => predicate(token))
        .forEach((token) => node.classList.remove(token));
}

function moveChildNodes(source, target, predicate = () => true) {
    Array.from(source.childNodes)
        .filter((node) => predicate(node))
        .forEach((node) => target.append(node));
}

function ensureNode(parent, selector, build) {
    const existing = parent.querySelector(`:scope > ${selector}`);
    if (existing) {
        return existing;
    }

    const node = build();
    parent.append(node);
    return node;
}

export {
    addClass,
    ensureNode,
    moveChildNodes,
    normalizeToken,
    removeMatchingClasses,
};
