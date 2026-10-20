# Strategies module

The Appendix A strategy catalog and its deterministic engines (SRS §1.3 "Strategy", §3.2 FR-STR-01..07, Appendix A;
C-03). Other modules depend only on `Ecnanib.Agents.Abstractions` (`IStrategyCatalog`, `IStrategyEngine`,
`StrategyDescriptor`, `StrategyOutput`, `MarketSnapshot`) and `AddStrategies()`, which registers both services with
`TryAdd` so a registration made earlier wins; no configuration section. Register it **before** `AddAgents`, which
requires the catalog. Everything in this folder is internal.

| Need | Use |
|---|---|
| The strategies a timeframe offers, each at its newest version (FR-AGT-03, FR-STR-06) | `IStrategyCatalog.ForTimeframe(timeframe)` / `All` / `Find(id)` → `StrategyDescriptor` (id, name, timeframe, version, summary, bounded parameters with defaults, the higher timeframes and reference series it needs) |
| The exact version an agent pinned | `IStrategyCatalog.Find(id, version)`; null when the module no longer ships it |
| Evaluate one closed candle (FR-STR-01/02) | `IStrategyEngine.Evaluate(StrategyContext)` → `StrategyOutput` (strategy id and version, bias, levels, indicators, the candidate setup or null, a note that is never null) |
| What the engine expects in the snapshot (FR-STR-03) | `MarketSnapshot.Candles` (the agent timeframe, ascending, ending with the evaluated candle), `HigherTimeframes[interval]` for every `StrategyDescriptor.HigherTimeframes` entry and `References[(symbol, interval)]` for every `References` entry, both closed at or before the evaluated candle's close; `MarkPrice` for futures when available |

The catalog (ids as the agents module stores them):

| Timeframe | Id | Higher timeframes | Reference |
|---|---|---|---|
| 15m | `intraday-trend-pullback` | 4H | — |
| 1H | `trend-pullback`, `range-breakout-retest` | 1D | — |
| 4H | `swing-pullback`, `consolidation-breakout` | 1D, 1W | BTCUSDT 1W |
| 1D | `position-breakout`, `ema-pullback` | 1W | BTCUSDT 1W |

Rules the module enforces:

- **Deterministic** (FR-STR-01): decimal arithmetic only, no clock and no randomness. Every time rule (session hours,
  no-entry windows, the weekend multiplier) is judged at the evaluated candle's close, `candle.OpenTime + timeframe`.
  Output prices and indicators are rounded to 8 decimals with the scale normalised, levels and filters keep a fixed
  order and indicators are sorted by key, so the same candles and parameters give byte-identical JSON.
- **Evaluation order**, the first reason that stops the chain being the note: enough history (own series first, then
  each higher timeframe; `insufficient history: need N 1d candles, have M`) → higher-timeframe bias → levels and
  indicators → a spot agent takes no short (FR-STR-07, FR-AGT-12) → setup and trigger → stop gates (the entry must be
  beyond the stop; at most `maxStopDistanceAtr` ATR away) → targets → a Paused agent gets bias, levels and indicators
  but no setup (FR-AGT-05) → filters → the setup with its rationale as the note.
- **Indicators**: EMA seeded with the simple average of the first `n` closes; Wilder ATR seeded with the mean of the
  first `n` true ranges; session VWAP of the typical price since 00:00 UTC; fractal swings confirmed `swingLookback`
  candles later (a high strictly above the lookback highs before it and at or above the ones after it, lows mirrored);
  structure from the last two confirmed highs and lows; a range is the envelope of the candles before a breakout, at
  most `rangeMaxHeightAtr` ATR tall.
- **Bias**: the latest own close against the higher timeframe's EMA with matching structure (15m → 4H EMA50; 1H → daily
  EMA20; 4H → weekly EMA20 and structure, confirmed by the daily EMA20 with a structure that is not the opposite); the
  1D regime is a daily EMA200 rising over `regimeRisingLookback` days with the close above it and weekly higher highs
  and lows. Shorts mirror every rule through a direction sign.
- **Pullback**: within the last `pullbackLookback + 1` candles the price came within `pullbackToleranceAtr` ATR of a
  level (EMA20, EMA50, session VWAP, the last broken swing in priority order) with no close more than that beyond it;
  the stop goes beyond the lowest low (highest high) of that window by `stopBufferAtr` ATR. **Breakout**: the latest
  close outside a range of at least `rangeMinCandles` candles, on `volumeMultiple` × the `volumeAveragePeriod` average
  where the rules ask for volume; a retest has touched the boundary within the tolerance, never closed more than the
  tolerance back inside, and the last candle holds it.
- **Entry and targets**: a limit at the trigger candle's close (FR-EXE-03); the partial target at `partialTakeProfitR`;
  the final target at the nearest prior swing when it offers at least `minRewardR` (a closer swing rejects the
  candidate), else `typicalRewardR` (1H) or `minRewardR` (15m), else none (4H); the 1D strategies carry only the
  partial and trail. The common parameters (ATR and EMA periods, stop buffer, partial fraction, trailing method, entry
  timeout, market fallback) are Appendix A's; risk per trade, open positions and the daily loss limit are the agent's
  risk limits.
- **Filters** (every one applied is listed on the setup, passed or not): the 15m session window
  (`sessionStartHour`–`sessionEndHour` UTC, end exclusive, wrapping past midnight when the end is earlier); the user's
  no-entry windows (FR-STR-05, start inclusive, end exclusive, recurring ones may wrap past Sunday); the weekend size
  multiplier on Saturday and Sunday (informational, carried as `SizeMultiplier` for execution); no 15m entry on the
  crowded side when `Sign × fundingRate ≥ fundingRateExtreme` (futures); the projected funding over
  `expectedHoldCandles` for the 4H Risk Manager (informational); no 1D entry more than `maxExtensionPercent` beyond
  EMA50; `minDailyQuoteVolume` for symbols other than BTC and ETH; the BTC weekly regime (close against EMA
  `btcRegimeEmaPeriod` and structure) for symbols other than BTC on 4H and 1D, which also blocks when the BTC series is
  unavailable (NFR-REL-03).
- **Versions** (FR-STR-06): every strategy is version 1; the engine is keyed by (id, version) so an older version can
  stay registered beside a newer one, and an agent pinned to a version without an engine gets a note telling it to
  upgrade. Adding a parameter or changing a rule is a new version; the agents module upgrades agents on request.

Deferred: the optional 1D entry in up to three tranches, tick-size rounding of entry, stop and targets, and the trailing
and breakeven management of open positions belong to execution (FR-EXE, #13); the engine is stateless, so a retest
that keeps holding is reported on each candle until execution has an order or position. Market data (#12) replaces the
agents module's loader, which today reads every series from the exchange on each evaluation.

Tests: `dotnet test` runs offline: hand-computed indicator values, the rule primitives at their boundaries, each
strategy's long setup with its mirrored short, spot ignoring shorts, the paused agent, insufficient history, window and
strategy-specific filters, determinism (the same input twice and reordered parameters give the same JSON), the catalog
and the engine dispatch.
