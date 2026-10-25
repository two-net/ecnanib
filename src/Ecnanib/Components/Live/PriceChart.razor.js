// The price chart (FR-UI-03 c) over the vendored TradingView Lightweight Charts. One chart per element; colours come
// from the design tokens of the current theme and follow it live (FR-UI-02); markers are coloured by tone the same way.
const charts = new WeakMap();
const tones = { long: "--chart-up", short: "--chart-down", exit: "--chart-marker-exit", stop: "--chart-marker-stop" };

// Custom properties hold light-dark() expressions; a probe element resolves them to a colour the canvas can use.
function resolve(state, name) {
    state.probe.style.color = `var(${name})`;
    return getComputedStyle(state.probe).color;
}

function palette(state) {
    return {
        background: resolve(state, "--chart-bg"),
        text: resolve(state, "--chart-text"),
        grid: resolve(state, "--chart-grid"),
        border: resolve(state, "--border"),
        up: resolve(state, "--chart-up"),
        down: resolve(state, "--chart-down"),
    };
}

function chartOptions(p, intervalLabel) {
    return {
        autoSize: true,
        layout: { background: { type: "solid", color: p.background }, textColor: p.text, attributionLogo: true },
        grid: { vertLines: { color: p.grid }, horzLines: { color: p.grid } },
        rightPriceScale: { borderColor: p.border },
        timeScale: { borderColor: p.border, timeVisible: intervalLabel !== "1D" && intervalLabel !== "1W", secondsVisible: false },
    };
}

function seriesOptions(p) {
    return { upColor: p.up, downColor: p.down, wickUpColor: p.up, wickDownColor: p.down, borderVisible: false };
}

function applyTheme(state) {
    const p = palette(state);
    state.chart.applyOptions(chartOptions(p, state.intervalLabel));
    state.series.applyOptions(seriesOptions(p));
    if (state.lastMarkers) {
        state.markers.setMarkers(coloured(state, state.lastMarkers));
    }
}

function coloured(state, list) {
    return list.map(m => ({ time: m.time, position: m.position, shape: m.shape, color: resolve(state, tones[m.tone] || "--accent"), text: m.text, id: m.id }));
}

let library = null;

// The standalone build registers window.LightweightCharts and is loaded once, by a script element: the ESM build
// imports "fancy-canvas" by bare specifier, which a browser cannot resolve without an import map. The URL is the
// fingerprinted one the asset pipeline gives the page, resolved against the document base.
function loadLibrary(url) {
    if (window.LightweightCharts) {
        return Promise.resolve(window.LightweightCharts);
    }

    library ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = url;
        script.onload = () => resolve(window.LightweightCharts);
        script.onerror = () => {
            library = null;
            script.remove();
            reject(new Error(`The chart library could not be loaded from ${url}`));
        };
        document.head.appendChild(script);
    });
    return library;
}

export async function create(element, libraryUrl, intervalLabel) {
    if (charts.has(element)) {
        return;
    }

    const lib = await loadLibrary(new URL(libraryUrl, document.baseURI).href);
    const probe = document.createElement("span");
    probe.style.display = "none";
    element.appendChild(probe);
    const state = { lib, probe, intervalLabel, lastMarkers: null, hasData: false };
    const p = palette(state);
    state.chart = lib.createChart(element, chartOptions(p, intervalLabel));
    state.series = state.chart.addSeries(lib.CandlestickSeries, seriesOptions(p));
    state.markers = lib.createSeriesMarkers(state.series, []);
    state.observer = new MutationObserver(() => applyTheme(state));
    state.observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    state.media = window.matchMedia("(prefers-color-scheme: dark)");
    state.onMedia = () => applyTheme(state);
    state.media.addEventListener("change", state.onMedia);
    charts.set(element, state);
}

export function setData(element, bars) {
    const state = charts.get(element);
    if (!state) {
        return;
    }

    state.series.setData(bars);
    if (!state.hasData) {
        state.chart.timeScale().fitContent();
        state.hasData = true;
    } else {
        state.chart.timeScale().scrollToRealTime();
    }
}

export function update(element, bar) {
    const state = charts.get(element);
    if (state) {
        state.series.update(bar);
    }
}

export function setMarkers(element, list) {
    const state = charts.get(element);
    if (!state) {
        return;
    }

    state.lastMarkers = list;
    state.markers.setMarkers(coloured(state, list));
}

export function dispose(element) {
    const state = charts.get(element);
    if (!state) {
        return;
    }

    state.observer.disconnect();
    state.media.removeEventListener("change", state.onMedia);
    state.chart.remove();
    state.probe.remove();
    charts.delete(element);
}
