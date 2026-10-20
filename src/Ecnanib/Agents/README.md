# Agents module

User-owned trading agents and the runtime that evaluates them (SRS §1.3, §3.2 FR-AGT-01..15, Appendix E; FR-AI-08,
FR-AI-12; NFR-PERF-01/02; NFR-REL-01/03; C-03, A-06, A-07). An agent trades one symbol on one market at one timeframe
with one strategy and one council; Running and Paused agents are evaluated server-side once per UTC-aligned candle
close, while the user is offline. Other modules depend on `Ecnanib.Agents.Abstractions` and `AddAgents(...)`; the
scheduler, pipeline and paper simulator never leave this folder. `AddAgents` requires `AddDatabase`, `AddExchange`,
`AddAccounts` and `AddStrategies` (the catalog and engine) registered before it and `AddKeys` registered **after** it
(so the runtime's own key handling is kept), and adds two hosted services.

| Need | Use |
|---|---|
| List, create, edit or act on the user's agents (FR-AGT-01..04, FR-AGT-07) | `IAgentService` → `AgentSummary`, `AgentDetail`, `AgentSaveResult` (field errors), `AgentActionResult`; user mistakes are results, exceptions mean the database or the exchange failed |
| Which action or edit a state allows (Appendix E, FR-AGT-07) | `AgentStateMachine.Allows(state, action)`, `CanEdit(state, field)` |
| The strategy catalog filtered by timeframe (FR-AGT-03, Appendix A) | `IStrategyCatalog.ForTimeframe(timeframe)` → `StrategyDescriptor` with versioned, bounded parameters (provided by the Strategies module; `Find(id)` is the newest version, `Find(id, version)` the pinned one); `StrategyParameters` reads and writes `agents.parameters` |
| Pin and upgrade the strategy version (FR-STR-06) | `AgentDetail.StrategyVersion` / `LatestStrategyVersion`, `IAgentService.UpgradeStrategyAsync` |
| "No new entry" windows (FR-STR-05) | `IAgentService.AddEntryWindowAsync(EntryWindowInput)` / `RemoveEntryWindowAsync`, `AgentDetail.EntryWindows` → `EntryWindowView`; `EntryWindowSchedule.IsActive` is the one implementation of their semantics |
| React to agent events in the UI (FR-AGT-10, FR-UI-04) | `IAgentEvents.Subscribe(userId, handler)` → `AgentStateChanged`, `EvaluationFinished`, `OrderChanged`, `TradeChanged` |
| Provide a pipeline stage (council, market data, execution, live venue) | implement `ICouncil`, `IMarketDataLoader`, `IExecutionService` or an `IOrderVenue` and register it **before** `AddAgents`; the fail-safe default is replaced |
| Place, cancel or close simulated or real orders (FR-AGT-06, FR-AGT-11) | `IOrderVenues.For(mode)` → `IOrderVenue` (`PlaceEntryAsync` with a mandatory stop, `PlaceCloseAsync`, `CancelOpenOrdersAsync`, `ClosePositionsAtMarketAsync`, `GetEquityAsync`) |
| The candle boundary arithmetic (FR-AGT-09) | `CandleIntervalExtensions.OpenTimeOf` / `NextCloseAfter` in the exchange abstractions |

Rules the module enforces:

- Creation needs a name, market, symbol, one of the four timeframes, a mode and, for live mode, one of the user's
  Binance keys that allows the market; futures agents need leverage within `RiskCaps:MaxLeverage` and default to
  isolated margin (FR-AGT-01/02/13). The symbol is checked against the exchange's listing (production for paper agents,
  the key's environment for live ones); an unreachable exchange is `ExchangeUnavailable` and nothing is saved. The
  draft gets the timeframe's first strategy at its current version, the Appendix B council with each provider's newest
  catalog model (or an empty model to choose), and default risk limits. The agents-per-user limit applies at creation
  and cloning, the running limit at start (FR-AGT-15); both run under a per-user gate so concurrent starts cannot exceed them.
- Edits follow the state (FR-AGT-07): market, symbol, timeframe, mode, strategy, leverage and margin only in Draft or
  Stopped; strategy parameters, the strategy version, no-entry windows, council and risk limits also in Paused and
  Error (they take effect at the next evaluation, which re-reads the agent); the exchange key also in Error; a Running
  agent accepts a new name only. A timeframe change switches to the new timeframe's default strategy and clears the
  parameters. Parameters are stored as the values that differ from the defaults, so blank means default and a reset is
  an empty object; they are validated against the **pinned** version's descriptor. Choosing another strategy pins that
  strategy's newest version and resets the parameters; a parameters-only save never touches the version (FR-STR-06),
  and an explicit upgrade moves the agent to the newest version, keeping the parameters it still accepts and resetting
  the rest. Windows (at most 20 per agent): a one-off window needs a start before an end still ahead, a recurring one
  its four day/time fields and may wrap past Sunday; a duplicate is refused. Every changed field, every state transition
  and the creation are rows in `agent_changes` with the old and new value as JSON (`entryWindows` records the whole
  list before and after).
- Risk limits stay within `RiskCaps` (risk per trade, open positions, daily loss) at edit and start time; the council
  has all four roles, the Risk Manager enabled and a model for every enabled role (FR-AI-03/05).
- Start (Draft, Stopped or Error → Running) requires the running limit, a strategy still in the catalog at the pinned
  version with parameters that still validate, the risk caps, a model per enabled role, a Valid provider key for every enabled role's provider (FR-KEY-08) and, for live
  mode, the accepted risk disclaimer (FR-ACC-08) and a Valid exchange key allowed for the market. Pause keeps the agent
  evaluated but opens nothing (FR-AGT-05). Stop cancels the agent's open orders first and then either closes its
  positions at market or leaves them flagged unmanaged (FR-AGT-06); if the orders cannot be cancelled the state is
  unchanged and the user is told. Delete needs Draft or Stopped. Error is entered by the runtime only: an unknown
  strategy or version, parameters no longer valid, a live agent whose key is gone or no longer resolves (which also covers a start that raced a key
  deletion), or a key the keys module found invalid; the agent is untracked until the user fixes the cause and starts it.
- The scheduler follows one candle stream per tracked agent and evaluates it under a per-agent gate that lifecycle
  actions also take, with at most `Agents:MaxConcurrentEvaluations` evaluations at once, shorter timeframes first. The
  deadline per timeframe is measured from the candle close (NFR-PERF-01) and cancels everything up to the council;
  execution and persistence never observe it, because a cancelled token does not recall an order. A candle that closed
  more than a minute before the agent was tracked is ignored, one older than the newest seen on its stream is recorded
  as Skipped (superseded), and one whose deadline has already passed is recorded as Skipped (too late). The evaluation
  row is inserted first with `InProgress`; its unique (agent, candle) index makes any second run of the same candle a
  no-op (FR-AGT-09). On start the scheduler records every evaluation left unfinished by the last stop as failed, resumes
  every Running and Paused agent (NFR-REL-01) and passes "interrupted previously" to the execution stage; once a minute it
  lifts daily-loss pauses whose time has come (FR-EXE-09) and finalises evaluation rows nobody will finish.
- The pipeline is FR-AGT-10 in order: load market data → strategy engine → council when a setup exists and the agent is
  Running, a position is open, or discretionary mode is on and the agent is Running (FR-AI-08) → execute → persist →
  log → notify. No Risk Manager decision opens nothing and leaves protective orders alone (FR-AI-12). The strategy
  engine is the Strategies module's; the agent is loaded with its no-entry windows so the engine can apply them. Until
  their issues land, the other stages are seeded fail-safe: the council answers that it is not available (FR-AI, #9)
  and execution records the decision and places nothing (FR-EXE, #13). Market data (FR-DATA, #12) is, until then, the
  most recent 300 closed candles of the agent's timeframe plus every higher timeframe and reference series the strategy
  declares (FR-STR-03), each read from the exchange and kept only up to the evaluated candle's close (no look-ahead),
  written through to `candles`; reference series (BTCUSDT weekly) always come from production public data; the futures
  mark price is read once and a failure of it or of a reference series is logged and tolerated, while the own and
  higher-timeframe series must load. A Paused agent with a setup and no position does not consult the council, and no
  shadow outcome is recorded for it (FR-EXP-03 is #11's).
- Paper mode (FR-AGT-11, FR-AGT-12): paper agents price against **production** public data and need no key. Orders are
  rows with `is_paper` and deterministic client ids (FR-EXE-07); the simulator follows the last-price stream of every
  symbol with open paper orders or positions, in any agent state, so a Stop's close still fills. An order fills on the
  next tick after it is registered (tick timestamps are never compared with order times): market and close orders at
  the price with `Paper:SlippagePercent` against the trader and the taker fee; a buy limit at its price once the tick is
  at or below it (sell at or above) with the maker fee; a stop at the stop price with slippage once the tick trades
  through it; a target at its price. A tick that crosses both stop and target fills the stop. An entry fill creates
  the trade and its stop-loss and take-profit orders in one transaction (the entry order's `stop_price` carries the
  stop until then, the decision's targets the target, so the books rebuild from the database after a restart); an exit
  fill records exit price, fees, realized P&L net of fees, R multiple against the initial stop, excursions as fractions
  of the entry price and the reason (stop, target, kill switch for a Stop's close, council for a decided close) and
  cancels the sibling order. Spot entries are long only. Futures stops trigger on the last price; the mini-ticker is a
  one-second snapshot, so intra-second wicks are invisible. Equity per agent is `Paper:StartingBalance` plus the agent's
  realized paper P&L; sizing against it belongs to the execution issue.
- The runtime's `IKeyDependentAgents`: a key being deleted stops its agents with orders cancelled and positions flagged
  unmanaged (nothing can be closed without the key); an invalid key moves them to Error and halts their evaluation
  (FR-KEY-06/07). Each agent is bounded by thirty seconds so one slow venue never delays other keys' checks.
- Logging (FR-LOG-04): creation, every edit with its field names, every action, every evaluation with its outcome,
  every simulated fill and every Error entry are logged with ids only; names, symbols' owners and key material never are.

Configuration (`Agents` runtime settings beside the accounts module's fair-use limits; `RiskCaps` and `Paper` are this module's):

{ "Agents": { "MaxAgentsPerUser": 10, "MaxRunningAgentsPerUser": 3, "EvaluationDeadlineSeconds": { "15m": 90, "1H": 180, "4H": 300, "1D": 300 }, "MaxConcurrentEvaluations": 10, "DefaultEntryTimeoutCandles": 1 },
  "RiskCaps": { "MaxRiskPerTradePercent": 2, "MaxLeverage": 20, "MaxDailyLossPercent": 10, "MaxOpenPositions": 5 },
  "Paper": { "StartingBalance": 10000, "SpotMakerFeePercent": 0.1, "SpotTakerFeePercent": 0.1, "FuturesMakerFeePercent": 0.02, "FuturesTakerFeePercent": 0.05, "SlippagePercent": 0.05 } }

A deadline must be shorter than its timeframe; percentages are plain percents (0.1 means 0.1%).

Pages (static server rendering, plain forms): `/agents` (the user's agents with state and last evaluation),
`/agents/new` (name, market, symbol, timeframe, mode, key, leverage and margin; creates a draft) and `/agents/{id}`
(actions, positions and orders, settings, strategy and parameters generated from the pinned version's descriptor with an
upgrade button when a newer version exists, no-entry windows, council, risk limits, recent evaluations and change
history; fields a state forbids are disabled). The disclaimer is shown on both forms
(FR-UI-05). Another user's agent and a missing one both answer not found.

Known gaps: choosing council models from the synced catalog, deprecation warnings and single-model mode arrive with
the council and model-sync issues; backfill on start, candle caching and streamed mark prices with market data (#12),
until which the loader issues up to four REST reads per evaluation; the dashboard, agent detail with chart and live
updates with the UI issue. `Order.WorkingType` has no column, so paper futures stops
are last-price only.

Tests: `dotnet test` runs offline (options, registration and placeholder replacement, the Appendix E table and edit
rules, parameter parsing, input rules including windows, the window schedule, change sets, events, the scheduler's
staleness rules and plumbing, the paper book's fill rules). Live checks run against PostgreSQL through the same git-ignored
`tests/.database.json` as the database module, with the exchange, providers and streams faked:
`dotnet test -- --filter-trait "Category=Live"`.
