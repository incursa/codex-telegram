import { extent } from "d3-array";
import { scaleLinear, scaleTime } from "d3-scale";
import {
    area as d3Area,
    curveLinear,
    curveMonotoneX,
    curveStepAfter,
    line as d3Line,
} from "d3-shape";

import { createUniqueId } from "../shared.js";

const SVG_NS = "http://www.w3.org/2000/svg";
const DEFAULT_WIDTH = 120;
const DEFAULT_HEIGHT = 32;
const DEFAULT_PADDING = 3;
const VALID_VARIANTS = new Set(["line", "area", "bar"]);
const VALID_TONES = new Set(["default", "positive", "negative", "muted", "accent"]);
const VALID_CURVES = new Set(["linear", "monotone", "step"]);
const CURVES = {
    linear: curveLinear,
    monotone: curveMonotoneX,
    step: curveStepAfter,
};

function toFiniteNumber(value) {
    if (value === null || value === undefined || value === "") {
        return null;
    }

    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
}

function normalizeToken(value, allowedValues, fallback) {
    const normalized = String(value ?? "").trim().toLowerCase();
    return allowedValues.has(normalized) ? normalized : fallback;
}

function parseXValue(value, index) {
    if (value instanceof Date && Number.isFinite(value.getTime())) {
        return value;
    }

    if (typeof value === "number" && Number.isFinite(value)) {
        return value;
    }

    if (typeof value === "string" && value.trim()) {
        const numeric = Number(value);
        if (Number.isFinite(numeric)) {
            return numeric;
        }

        const date = new Date(value);
        if (Number.isFinite(date.getTime())) {
            return date;
        }
    }

    return index;
}

function normalizeInputPoint(item, index) {
    if (typeof item === "number") {
        return { x: index, y: Number.isFinite(item) ? item : null };
    }

    if (item instanceof Date) {
        return { x: index, y: null };
    }

    if (item && typeof item === "object") {
        return {
            x: item.x ?? index,
            y: toFiniteNumber(item.y),
        };
    }

    const numeric = toFiniteNumber(item);
    return { x: index, y: numeric };
}

function parseSparklinePoints(input) {
    if (Array.isArray(input)) {
        return input.map(normalizeInputPoint);
    }

    if (input === null || input === undefined) {
        return [];
    }

    if (typeof input === "string") {
        const text = input.trim();
        if (!text) {
            return [];
        }

        if (text.startsWith("[")) {
            try {
                const parsed = JSON.parse(text);
                return parseSparklinePoints(parsed);
            } catch {
                return [];
            }
        }

        return text
            .split(/[\s,;]+/u)
            .filter(Boolean)
            .map((part, index) => ({ x: index, y: toFiniteNumber(part) }));
    }

    return [];
}

function normalizeSparklineExtent(points, options = {}) {
    const normalizedPoints = parseSparklinePoints(points);
    const referenceValue = toFiniteNumber(options.referenceValue);
    const prepared = normalizedPoints.map((point, index) => ({
        x: point.x,
        xValue: parseXValue(point.x, index),
        y: toFiniteNumber(point.y),
        index,
    }));
    const validPoints = prepared.filter((point) => point.y !== null);
    const yValues = validPoints.map((point) => point.y);

    if (referenceValue !== null) {
        yValues.push(referenceValue);
    }

    if (!validPoints.length) {
        return {
            empty: true,
            points: prepared,
            validPoints,
            xDomain: [0, 1],
            yDomain: [0, 1],
            xMode: "index",
            referenceValue,
        };
    }

    let yDomain = extent(yValues);
    if (!Number.isFinite(yDomain[0]) || !Number.isFinite(yDomain[1])) {
        yDomain = [0, 1];
    }

    if (Object.is(yDomain[0], yDomain[1])) {
        const value = yDomain[0];
        const padding = value === 0 ? 1 : Math.max(Math.abs(value) * 0.08, 1);
        yDomain = [value - padding, value + padding];
    }

    const allDates = prepared.every((point) => point.xValue instanceof Date);
    const allNumbers = prepared.every((point) => typeof point.xValue === "number" && Number.isFinite(point.xValue));
    const xMode = allDates ? "time" : (allNumbers ? "number" : "index");
    const xValues = prepared.map((point) => xMode === "index" ? point.index : point.xValue);
    let xDomain = extent(xValues);

    if (xMode === "time") {
        const left = xDomain[0] instanceof Date ? xDomain[0].getTime() : NaN;
        const right = xDomain[1] instanceof Date ? xDomain[1].getTime() : NaN;
        if (!Number.isFinite(left) || !Number.isFinite(right)) {
            xDomain = [new Date(0), new Date(1)];
        } else if (left === right) {
            xDomain = [new Date(left - 1), new Date(right + 1)];
        }
    } else {
        if (!Number.isFinite(xDomain[0]) || !Number.isFinite(xDomain[1])) {
            xDomain = [0, Math.max(prepared.length - 1, 1)];
        } else if (Object.is(xDomain[0], xDomain[1])) {
            xDomain = [xDomain[0] - 1, xDomain[1] + 1];
        }
    }

    return {
        empty: false,
        points: prepared,
        validPoints,
        xDomain,
        yDomain,
        xMode,
        referenceValue,
    };
}

function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
}

function round(value) {
    return Number.isFinite(value) ? Number(value.toFixed(3)) : 0;
}

function createScales(extentInfo, width, height, padding) {
    const xRange = [padding, Math.max(width - padding, padding)];
    const yRange = [Math.max(height - padding, padding), padding];
    const xScale = extentInfo.xMode === "time"
        ? scaleTime(extentInfo.xDomain, xRange)
        : scaleLinear(extentInfo.xDomain, xRange);
    const yScale = scaleLinear(extentInfo.yDomain, yRange);

    return { xScale, yScale };
}

function getX(point, extentInfo) {
    return extentInfo.xMode === "index" ? point.index : point.xValue;
}

function singlePointPath(point, extentInfo, xScale, yScale, width) {
    const center = round(xScale(getX(point, extentInfo)));
    const y = round(yScale(point.y));
    const half = Math.min(4, Math.max(1, width / 20));
    const left = round(clamp(center - half, 0, width));
    const right = round(clamp(center + half, 0, width));

    return `M${left},${y}L${right},${y}`;
}

function buildSparklinePath(points, options = {}) {
    const width = Math.max(toFiniteNumber(options.width) ?? DEFAULT_WIDTH, 1);
    const height = Math.max(toFiniteNumber(options.height) ?? DEFAULT_HEIGHT, 1);
    const padding = Math.max(toFiniteNumber(options.padding) ?? DEFAULT_PADDING, 0);
    const variant = normalizeToken(options.variant, VALID_VARIANTS, "line");
    const curve = normalizeToken(options.curve, VALID_CURVES, "linear");
    const extentInfo = normalizeSparklineExtent(points, options);

    if (extentInfo.empty) {
        return {
            empty: true,
            width,
            height,
            padding,
            variant,
            curve,
            linePath: "",
            areaPath: "",
            bars: [],
            marker: null,
            minPoint: null,
            maxPoint: null,
            referenceY: null,
            extent: extentInfo,
        };
    }

    const { xScale, yScale } = createScales(extentInfo, width, height, padding);
    const curveFactory = CURVES[curve] || curveLinear;
    const lineGenerator = d3Line()
        .defined((point) => point.y !== null)
        .x((point) => round(xScale(getX(point, extentInfo))))
        .y((point) => round(yScale(point.y)))
        .curve(curveFactory);
    const validPoints = extentInfo.validPoints;
    const linePath = validPoints.length === 1
        ? singlePointPath(validPoints[0], extentInfo, xScale, yScale, width)
        : (lineGenerator(extentInfo.points) || "");
    const baseline = clamp(0, extentInfo.yDomain[0], extentInfo.yDomain[1]);
    const areaGenerator = d3Area()
        .defined((point) => point.y !== null)
        .x((point) => round(xScale(getX(point, extentInfo))))
        .y0(round(yScale(baseline)))
        .y1((point) => round(yScale(point.y)))
        .curve(curveFactory);
    const areaPath = validPoints.length > 1 ? (areaGenerator(extentInfo.points) || "") : "";
    const barWidth = Math.max(1, Math.min(8, (width - (padding * 2)) / Math.max(extentInfo.points.length, 1) * 0.58));
    const baselineY = round(yScale(baseline));
    const bars = validPoints.map((point) => {
        const x = round(xScale(getX(point, extentInfo)) - (barWidth / 2));
        const y = round(yScale(point.y));
        return {
            x,
            y: Math.min(y, baselineY),
            width: round(barWidth),
            height: Math.max(1, round(Math.abs(baselineY - y))),
        };
    });
    const lastPoint = validPoints[validPoints.length - 1] || null;
    const marker = lastPoint ? {
        x: round(xScale(getX(lastPoint, extentInfo))),
        y: round(yScale(lastPoint.y)),
        value: lastPoint.y,
    } : null;
    const minPoint = validPoints.reduce((candidate, point) => point.y < candidate.y ? point : candidate, validPoints[0]);
    const maxPoint = validPoints.reduce((candidate, point) => point.y > candidate.y ? point : candidate, validPoints[0]);
    const mapMarker = (point) => point ? {
        x: round(xScale(getX(point, extentInfo))),
        y: round(yScale(point.y)),
        value: point.y,
    } : null;

    return {
        empty: false,
        width,
        height,
        padding,
        variant,
        curve,
        linePath,
        areaPath,
        bars,
        marker,
        minPoint: mapMarker(minPoint),
        maxPoint: mapMarker(maxPoint),
        referenceY: extentInfo.referenceValue === null ? null : round(yScale(extentInfo.referenceValue)),
        extent: extentInfo,
    };
}

function svgElement(name) {
    return document.createElementNS(SVG_NS, name);
}

function hasInvalidPathData(value) {
    return /(?:NaN|Infinity|-Infinity)/u.test(String(value ?? ""));
}

class IncSparklineElement extends HTMLElement {
    static observedAttributes = [
        "aria-label",
        "curve",
        "empty-label",
        "height",
        "points",
        "reference-value",
        "show-last-marker",
        "show-min-max",
        "tone",
        "values",
        "variant",
        "width",
    ];

    #hasPropertyPoints = false;
    #propertyPoints = [];
    #titleId = createUniqueId("inc-sparkline-title");
    #descId = createUniqueId("inc-sparkline-desc");

    connectedCallback() {
        this.#render();
    }

    attributeChangedCallback() {
        if (this.isConnected) {
            this.#render();
        }
    }

    get points() {
        if (this.#hasPropertyPoints) {
            return parseSparklinePoints(this.#propertyPoints);
        }

        if (this.hasAttribute("points")) {
            return parseSparklinePoints(this.getAttribute("points"));
        }

        return parseSparklinePoints(this.getAttribute("values"));
    }

    set points(value) {
        if (value === null || value === undefined) {
            this.#hasPropertyPoints = false;
            this.#propertyPoints = [];
        } else {
            this.#hasPropertyPoints = true;
            this.#propertyPoints = value;
        }

        if (this.isConnected) {
            this.#render();
        }
    }

    get values() {
        return this.getAttribute("values") || "";
    }

    set values(value) {
        if (value === null || value === undefined || value === "") {
            this.removeAttribute("values");
            return;
        }

        this.setAttribute("values", String(value));
    }

    get width() {
        return Math.max(toFiniteNumber(this.getAttribute("width")) ?? DEFAULT_WIDTH, 1);
    }

    set width(value) {
        if (value === null || value === undefined || value === "") {
            this.removeAttribute("width");
            return;
        }

        this.setAttribute("width", String(value));
    }

    get height() {
        return Math.max(toFiniteNumber(this.getAttribute("height")) ?? DEFAULT_HEIGHT, 1);
    }

    set height(value) {
        if (value === null || value === undefined || value === "") {
            this.removeAttribute("height");
            return;
        }

        this.setAttribute("height", String(value));
    }

    #render() {
        const width = this.width;
        const height = this.height;
        const variant = normalizeToken(this.getAttribute("variant"), VALID_VARIANTS, "line");
        const tone = normalizeToken(this.getAttribute("tone"), VALID_TONES, "default");
        const curve = normalizeToken(this.getAttribute("curve"), VALID_CURVES, "linear");
        const referenceValue = toFiniteNumber(this.getAttribute("reference-value"));
        const label = this.getAttribute("aria-label") || "Sparkline trend";
        const emptyLabel = this.getAttribute("empty-label") ?? "No data";
        const model = buildSparklinePath(this.points, {
            curve,
            height,
            referenceValue,
            variant,
            width,
        });

        this.classList.add("inc-sparkline");
        [...this.classList]
            .filter((token) => token.startsWith("inc-sparkline--"))
            .forEach((token) => this.classList.remove(token));
        this.classList.add(`inc-sparkline--${variant}`, `inc-sparkline--tone-${tone}`);
        this.style.setProperty("--inc-sparkline-width", `${width}px`);
        this.style.setProperty("--inc-sparkline-height", `${height}px`);

        const svg = svgElement("svg");
        svg.classList.add("inc-sparkline__svg");
        svg.setAttribute("part", "svg");
        svg.setAttribute("width", String(width));
        svg.setAttribute("height", String(height));
        svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
        svg.setAttribute("role", "img");
        svg.setAttribute("aria-labelledby", `${this.#titleId} ${this.#descId}`);
        svg.setAttribute("focusable", "false");

        const title = svgElement("title");
        title.id = this.#titleId;
        title.textContent = label;

        const desc = svgElement("desc");
        desc.id = this.#descId;
        desc.textContent = model.empty
            ? (emptyLabel || "No sparkline data available.")
            : this.#buildDescription(model);

        svg.append(title, desc);

        if (model.empty || hasInvalidPathData(model.linePath) || hasInvalidPathData(model.areaPath)) {
            this.#renderEmpty(svg, width, height, emptyLabel);
            this.replaceChildren(svg);
            return;
        }

        if (model.referenceY !== null) {
            const reference = svgElement("line");
            reference.classList.add("inc-sparkline__reference");
            reference.setAttribute("part", "reference");
            reference.setAttribute("x1", String(model.padding));
            reference.setAttribute("x2", String(width - model.padding));
            reference.setAttribute("y1", String(model.referenceY));
            reference.setAttribute("y2", String(model.referenceY));
            reference.setAttribute("vector-effect", "non-scaling-stroke");
            svg.append(reference);
        }

        if (variant === "bar") {
            model.bars.forEach((bar) => {
                const rect = svgElement("rect");
                rect.classList.add("inc-sparkline__bar");
                rect.setAttribute("part", "bar");
                rect.setAttribute("x", String(bar.x));
                rect.setAttribute("y", String(bar.y));
                rect.setAttribute("width", String(bar.width));
                rect.setAttribute("height", String(bar.height));
                svg.append(rect);
            });
        } else {
            if (variant === "area" && model.areaPath) {
                const area = svgElement("path");
                area.classList.add("inc-sparkline__area");
                area.setAttribute("part", "area");
                area.setAttribute("d", model.areaPath);
                area.setAttribute("vector-effect", "non-scaling-stroke");
                svg.append(area);
            }

            const line = svgElement("path");
            line.classList.add("inc-sparkline__line");
            line.setAttribute("part", "line");
            line.setAttribute("d", model.linePath);
            line.setAttribute("vector-effect", "non-scaling-stroke");
            svg.append(line);
        }

        if (this.hasAttribute("show-min-max")) {
            this.#appendMarker(svg, model.minPoint, "min");
            if (model.maxPoint?.x !== model.minPoint?.x || model.maxPoint?.y !== model.minPoint?.y) {
                this.#appendMarker(svg, model.maxPoint, "max");
            }
        }

        if (this.hasAttribute("show-last-marker")) {
            this.#appendMarker(svg, model.marker, "last");
        }

        this.replaceChildren(svg);
    }

    #renderEmpty(svg, width, height, emptyLabel) {
        const line = svgElement("line");
        line.classList.add("inc-sparkline__empty-line");
        line.setAttribute("part", "line");
        line.setAttribute("x1", "3");
        line.setAttribute("x2", String(Math.max(width - 3, 3)));
        line.setAttribute("y1", String(round(height / 2)));
        line.setAttribute("y2", String(round(height / 2)));
        line.setAttribute("vector-effect", "non-scaling-stroke");
        svg.append(line);

        if (emptyLabel) {
            const text = svgElement("text");
            text.classList.add("inc-sparkline__empty");
            text.setAttribute("part", "empty");
            text.setAttribute("x", String(round(width / 2)));
            text.setAttribute("y", String(round((height / 2) + 3)));
            text.setAttribute("text-anchor", "middle");
            text.textContent = emptyLabel;
            svg.append(text);
        }
    }

    #appendMarker(svg, point, modifier) {
        if (!point || !Number.isFinite(point.x) || !Number.isFinite(point.y)) {
            return;
        }

        const marker = svgElement("circle");
        marker.classList.add("inc-sparkline__marker", `inc-sparkline__marker--${modifier}`);
        marker.setAttribute("part", "marker");
        marker.setAttribute("cx", String(point.x));
        marker.setAttribute("cy", String(point.y));
        marker.setAttribute("r", modifier === "last" ? "2.4" : "1.8");
        marker.setAttribute("vector-effect", "non-scaling-stroke");
        svg.append(marker);
    }

    #buildDescription(model) {
        const values = model.extent.validPoints.map((point) => point.y);
        const min = Math.min(...values);
        const max = Math.max(...values);
        const latest = model.marker?.value;
        const parts = [`${values.length} data ${values.length === 1 ? "point" : "points"}.`];

        if (Number.isFinite(latest)) {
            parts.push(`Latest value ${latest}.`);
        }

        if (Number.isFinite(min) && Number.isFinite(max)) {
            parts.push(`Range ${min} to ${max}.`);
        }

        if (model.extent.referenceValue !== null) {
            parts.push(`Reference value ${model.extent.referenceValue}.`);
        }

        return parts.join(" ");
    }
}

const visualizationDefinitions = [
    ["inc-sparkline", IncSparklineElement],
];

const visualizationComponents = {
    IncSparklineElement,
};

function defineVisualizationComponents(registry = globalThis.customElements) {
    if (!registry || typeof registry.define !== "function" || typeof registry.get !== "function") {
        return [];
    }

    const defined = [];
    for (const [tagName, ctor] of visualizationDefinitions) {
        if (!registry.get(tagName)) {
            registry.define(tagName, ctor);
            defined.push(tagName);
        }
    }

    return defined;
}

if (typeof globalThis !== "undefined") {
    const namespace = globalThis.IncWebComponents || (globalThis.IncWebComponents = {});
    namespace.visualizations = Object.assign({}, namespace.visualizations, {
        buildSparklinePath,
        defineVisualizationComponents,
        normalizeSparklineExtent,
        parseSparklinePoints,
        visualizationDefinitions,
        components: visualizationComponents,
    });
}

export {
    IncSparklineElement,
    buildSparklinePath,
    defineVisualizationComponents,
    normalizeSparklineExtent,
    parseSparklinePoints,
    visualizationComponents,
    visualizationDefinitions,
};
