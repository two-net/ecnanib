# TradingView Lightweight Charts (vendored)

The candlestick chart of the agent page (FR-UI-03 c) is drawn by TradingView Lightweight Charts, kept here so the
browser loads it from this application and nothing else (NFR-SEC-04: no CDN, no telemetry).

| Field | Value |
|---|---|
| Version | 5.2.1 |
| License | Apache License 2.0 (`LICENSE` beside this file; the banner at the top of the module is kept) |
| Source | https://github.com/tradingview/lightweight-charts |
| File | `lightweight-charts.production.mjs` from https://unpkg.com/lightweight-charts@5.2.1/dist/lightweight-charts.production.mjs |
| SHA-256 | `faf0924968b1cbb9ea5bb6baa43a27ed6a0802341c07238b72ae869ffebab90c` |
| Audit | the module makes no network call: `grep -cE 'fetch\(\|XMLHttpRequest\|sendBeacon\|new WebSocket\|importScripts'` is 0; the only URLs in it are the SVG namespace, the license and the attribution link the user may click |
| Attribution | the chart's `attributionLogo` stays on, as TradingView asks |

The module is imported by `Components/Live/PriceChart.razor.js` through the URL `App.razor`'s asset pipeline gives it,
so the fingerprinted copy is cached for good.

To update: download the new `dist/lightweight-charts.production.mjs` and `LICENSE` from unpkg into a scratch
directory, check the banner version and run the audit grep, record the new SHA-256 here, copy both files over, and
read the release notes for API changes (`createChart`, `addSeries(CandlestickSeries)`, `createSeriesMarkers`).
