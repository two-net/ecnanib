# Market data module

The series the evaluations see and the symbols the agents trade (SRS §3.2 FR-DATA-01..05; EX-03, EX-06; FR-STR-03;
NFR-REL-03; NFR-DATA-03). Every series a tracked agent needs is backfilled from the exchange when the agent is tracked,
then followed on the shared candle streams and repaired when a stream left a hole; the closed candles are written
through for audit; futures symbols have their mark price and funding followed; the symbol listing with its filters is
kept per environment and market and refreshed on a schedule. Other modules depend on the seams in
`Ecnanib.Agents.Abstractions` (`IMarketDataLoader`, `ISymbolCatalog`), on `Ecnanib.MarketData.Abstractions`
(`IFuturesMetrics`) and on `AddMarketData(...)`, which must be registered **before** `AddAgents` (whose placeholders
stay otherwise) and requires `AddExchange` and `AddDatabase`. It adds one hosted service and connects to nothing at
registration. The series, the feed and the catalog never leave this folder.

| Need | Use |
|---|---|
| The snapshot an evaluation runs on (FR-AGT-10, FR-DATA-02..04) | `IMarketDataLoader.LoadAsync(agent, strategy, key, closed, ct)` → `MarketSnapshot`; the agents module's pipeline is the only caller |
| Follow and release an agent's series (FR-DATA-02) | `IMarketDataLoader.Track(MarketDataNeeds)` / `Untrack(agentId)`; the agents module's scheduler calls them as it tracks and untracks an agent |
| Whether a symbol is listed, and its filters (FR-DATA-01, FR-EXE-04) | `ISymbolCatalog.FindAsync(environment, market, symbol, ct)` → `SymbolInfo` with `SymbolFilters` (tick size, step size, minimum notional, order counts and the rounding helpers), `ListAsync(environment, market, ct)`; the execution module sizes against it |
| The latest mark price, funding rate and next funding time of a followed futures symbol (FR-DATA-04) | `IFuturesMetrics.Latest(key)` |
| The closed candles for the agent page's chart (FR-UI-03 c) | `ICandleReader.ReadAsync(key, interval, count, ct)`: the followed series when it is loaded, else the `candles` table, else one exchange read (a draft or untracked agent); the forming candle comes from the exchange module's `IMarketStreams.SubscribeCandleUpdates` |
| Candle retention (FR-DATA-05) | `Retention:CandleDays`, the database module's; its `RetentionSweeper` deletes by open time |

Rules the module enforces:

- **Tracking** (FR-DATA-02): an agent's series are its own symbol and timeframe, the higher timeframes its strategy
  declares on the same environment, and the reference series (BTCUSDT weekly) on production in the agent's market, so
  a testnet agent judges the regime on real-market prices. Series are shared: one stream and one backfill per
  environment, market, symbol and interval however many agents need it, the last of them releasing it. A new series is
  backfilled with `MarketData:BackfillCandles` closed candles in one read (the exchange module pages it), at most four
  backfills at once from tracking; an evaluation that finds its series not ready loads it at once. A backfill that
  fails is logged and tried again by the next evaluation, once more inline, and when it still fails the evaluation
  fails, so nothing opens (NFR-REL-03). A series the strategy needs that tracking did not know (a version upgraded
  while Paused) is acquired by the evaluation; a later `Track` with other needs releases what is no longer needed.
- **Streams** (FR-DATA-03): the exchange module's streams reconnect, poll while degraded and deliver each close once;
  they guarantee order, not contiguity. The feed's handler is registered before the scheduler's on the shared stream
  and only inserts; a close that is not the next candle marks a hole, repaired in the background with one ranged read
  (up to 1000 candles) or, wider, a fresh backfill. Whatever is still before a hole after that is dropped with a
  warning, so a series is contiguous whenever an evaluation reads it; a series a repair left short of its depth is
  refilled by the next load, and only when the exchange's own history has the hole (or fewer candles than the depth,
  a new listing) is the shorter series accepted.
- **The window** (FR-STR-01/03): exactly `BackfillCandles` candles ending with the evaluated candle (fewer only when
  the exchange has fewer, which the engine reports as insufficient history); the higher-timeframe and reference
  windows hold the candles closed at or before the evaluated candle's close, and a candle closing at that same instant
  is read from the exchange when its stream has not delivered it yet. Each series keeps `Slack` (8) candles beyond
  the depth so an evaluation of a candle that is no longer the newest still sees the same window; a candle the series
  dropped fails the evaluation rather than shortening its window. The same window size on every run keeps the
  engine's seeded indicators deterministic.
- **Mark price** (FR-DATA-04): the mark-price stream of every followed futures symbol (production for paper agents)
  keeps the latest value; before the first update, or when the stream could not be opened, one REST read per
  evaluation; a failure leaves it null and is tolerated. Spot agents have none.
- **Persistence** (FR-DATA-05): every candle a backfill, a repair or the stream added is written to `candles` when its
  open time is inside `Retention:CandleDays`, after the series is ready, so a database outage never fails a series;
  one already there is left alone. Older backfill (weekly series) is held in memory only.
- **Symbols** (FR-DATA-01): the listing of an environment and market is read on first use, one read shared by the
  callers waiting for it, and a failed read is not cached; `SymbolCatalogRefresher` re-reads every loaded listing
  five minutes after start and then every `SymbolRefreshIntervalMinutes`, a day at most; a failed refresh keeps the
  listing it has. With nothing cached and the exchange unreachable the exception propagates, so a caller can tell
  "not listed" from "could not check".
- **Logging** (FR-LOG-04): series followed and released, backfill and repair results, listing loads and refreshes,
  every failure at Warning; keys (environment, market, symbol), intervals and ids only.

Configuration (`MarketData` section; the defaults are in `src/Ecnanib/appsettings.json`):

```json
{ "MarketData": { "BackfillCandles": { "15m": 500, "1H": 500, "4H": 500, "1D": 400, "1W": 200 }, "SymbolRefreshIntervalMinutes": 360 } }
```

Each depth is between 20 and 5000; the defaults cover every Appendix A strategy at its default parameters (a
parameter at its maximum, such as a 500-candle range, needs more) and cost one request per series. The refresh interval
beyond Appendix D's minimum enforces "at least daily". Candle retention is `Retention:CandleDays`.

Pages: none of its own. The agent page's chart reads its candles through `ICandleReader` and follows the forming one on
the candle stream; the deliberation page (council module) shows the candles, higher timeframes, reference closes and the
mark price each role received; `/status` (health module) shows every stream's state from `IExchangeHealth`.

Known gaps: a series the exchange's own history leaves short (a hole in what it returns) only grows at its tail, so
the window stays short until enough candles have closed; the listing is in memory and read again after a restart; a
shadow of an agent that stopped still expires stale rather than resolving from `candles` (experience module); the
series an evaluation acquires for an agent nobody tracks (tests) are held until the host stops.

Tests: `dotnet test` runs offline (the options, the registration and placeholder replacement, the listing's
load-once, refresh and failure rules and the refresher's cadence, the mark-price leases, and the feed over faked
streams and exchange: backfill and sharing, release, reference sharing across environments and with the own weekly,
streamed closes and duplicates, gap repair off the handler, the fixed window and the slack, the higher-timeframe
cutoff and the coinciding boundary, waiting for a backfill in flight, retry then failure, mark price fallbacks,
on-demand series, dispose). Live checks run against PostgreSQL through the git-ignored `tests/.database.json` (the
feed inside the real scheduler and pipeline: rows within the retention period, a repeated close stored and evaluated
once, the configured window without exchange reads, release on stop; creation through the cached listing) and, with
`publicProduction` in `tests/.exchange-keys.json`, one real backfill of BTCUSDT 1H:
`dotnet test -- --filter-trait "Category=Live"`.
