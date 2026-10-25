# TradingView Lightweight Charts (vendored)

The candlestick chart of the agent page (FR-UI-03 c) is drawn by TradingView Lightweight Charts, kept here so the
browser loads it from this application and nothing else (NFR-SEC-04: no CDN, no telemetry).

| Field | Value |
|---|---|
| Version | 5.2.1 |
| License | Apache License 2.0 (`LICENSE` beside this file; the banner at the top of the script is kept). The build bundles TradingView's `fancy-canvas` (MIT) |
| Source | https://github.com/tradingview/lightweight-charts |
| File | `lightweight-charts.standalone.production.js` from https://unpkg.com/lightweight-charts@5.2.1/dist/lightweight-charts.standalone.production.js |
| SHA-256 | `e21cc5caa0226ef30bd8549c50b9ef926615f2a4ee6b4e486353477a55f598cf` |
| Audit | the script makes no network call: `grep -cE 'fetch\(\|XMLHttpRequest\|sendBeacon\|new WebSocket\|importScripts'` is 0; the only URLs in it are the SVG namespace, the license and the attribution link the user may click |
| Attribution | the chart's `attributionLogo` stays on, as TradingView asks |

The standalone build is used because the ESM build (`lightweight-charts.production.mjs`) imports `fancy-canvas` by
bare specifier, which a browser cannot resolve without an import map. `Components/Live/PriceChart.razor.js` loads it
once, by a script element, at the fingerprinted URL the asset pipeline gives the page, so the copy is cached for good;
the script registers `window.LightweightCharts`.

To update: download the new `dist/lightweight-charts.standalone.production.js` and `LICENSE` from unpkg into a scratch
directory, check the banner version and run the audit grep, record the new SHA-256 here, copy both files over, and
read the release notes for API changes (`createChart`, `addSeries(CandlestickSeries)`, `createSeriesMarkers`).
