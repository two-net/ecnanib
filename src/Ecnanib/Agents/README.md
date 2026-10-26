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
| One agent's state, open positions and orders, or recent evaluations, for the live islands (FR-UI-04) | `IAgentService.GetStatusAsync` → `AgentStatusView`, `GetOpenAsync` → `AgentOpenView`, `ListEvaluationsAsync(userId, agentId, count)`; `AgentSummary` carries the environment and the evaluation in flight (`InFlightEvaluationId`, `InFlightCandleOpenTime`) |
| The scheduler's own state, for the status page (NFR-OBS-01) | `ISchedulerStatus.GetStatus()` → `SchedulerStatus` (phase, started, last tick, tracked agents, evaluations in flight and queued, deadline misses since start, evaluations the last stop left in flight, the last error) |
| Warn about a council model the catalog deprecated (FR-SYNC-03) | `CouncilAssignmentView.CatalogStatus` (null for a model the catalog never listed), `AgentSummary.HasDeprecatedModel` (an enabled role only) |
| React to agent events in the UI (FR-AGT-10, FR-UI-04) | `IAgentEvents.Subscribe(userId, handler)` → `AgentStateChanged`, `EvaluationStarted`, `CouncilRoleStarted` / `CouncilRoleFinished` (published by the council module), `DecisionRecorded`, `EvaluationFinished`, `OrderChanged`, `TradeChanged` (also on a stop or target move); every event is published after its row is committed and carries ids only, so a page re-reads what it shows |
| Provide a pipeline stage (council, market data, execution, experience, live venue) or the symbol listing | implement `ICouncil`, `IMarketDataLoader` (`Track`, `Untrack`, `LoadAsync`), `ISymbolCatalog`, `IExecutionService` (`ManageAsync` before the council, whose `EntriesBlockedReason` keeps this candle from opening anything new, `ExecuteAsync(request, decision, cancellable, stopping)` for the decision), `IExperienceTracker`, `IReconciliationStatus` (what the execution module's reconciliation reports for the status page) or an `IOrderVenue` and register it **before** `AddAgents`; the fail-safe default is replaced |
| The per-agent gate and the daily loss limit, for the execution module (FR-EXE-08, FR-EXE-09) | `IAgentGates.LockAsync` / `TryLock` (the scheduler's gate); `IDailyLossLimit.CheckAsync(agentId)` (gate-free, pauses a Running agent until the next UTC day when today's realized loss reaches the limit) |
| Client order ids, protection units and the trade JSON shapes shared by both venues (FR-EXE-07, FR-EXE-06, FR-EXP-02) | `ClientOrderIds.For(prefix, agentId, scope, purpose, sequence)`; `ProtectionUnits.Split(quantity, targets, partialFraction, stepSize, minQuantity)` and `ProtectionUnits.PartialFraction(parameters)`; `TradeJson.Targets` / `StopHistory` / `StopHistoryEntries` (typed, for the timeline and the chart) / `AppendStop` / `InitialStop` for `trades.targets` and `trades.stop_history` |
| Suggest a council from experience at creation (FR-EXP-08) | implement `ICouncilSuggestions` and register it **before** `AddAgents`; the default suggests nothing, so every role gets its Appendix B default |
| The council roles, their order and Appendix B defaults | `CouncilRoles` (`Order`, `DefaultProvider`, `Display`, `Responsibility`) |
| Council presets (FR-AI-04) | `IAgentService.ResetCouncilToDefaultsAsync` (defaults by strength with each provider's newest catalog model), `UseSingleModelAsync` (one model for every role); both save through `UpdateCouncilAsync` |
| Place, cancel, close or manage simulated or real orders (FR-AGT-06, FR-AGT-11, FR-EXE-06) | `IOrderVenues.For(mode)` → `IOrderVenue` (`PlaceEntryAsync` with a mandatory stop and the targets, `PlaceCloseAsync`, `PlacePartialCloseAsync`, `ReplaceStopAsync`, `ReplaceTargetsAsync`, `ExpireEntryAsync`, `CancelOpenOrdersAsync`, `CancelEntryOrdersAsync`, `CancelProtectiveOrdersAsync`, `ClosePositionsAtMarketAsync`, `GetEquityAsync`, `GetAvailableAsync`) |
| "Stop all my agents" (FR-EXE-10) | `IAgentService.StopAllAsync(userId, closePositions)` → `AgentBulkActionResult` |
| The candle boundary arithmetic (FR-AGT-09) | `CandleIntervalExtensions.OpenTimeOf` / `NextCloseAfter` in the exchange abstractions |

Rules the module enforces:

- Every read and every mutation is scoped to the user (NFR-SEC-03): another user's agent id is NotFound and nothing
  happens to it. Stop reads the agent as the user's own before it cancels the evaluation in flight or takes the gate.
  The scheduler's and the reconciliation's last error are kinds (`ErrorSummary.Describe`), never messages, because the
  status page shows them to every signed-in user.
- Creation needs a name, market, symbol, one of the four timeframes, a mode and, for live mode, one of the user's
  Binance keys that allows the market; futures agents need leverage within `RiskCaps:MaxLeverage` and default to
  isolated margin (FR-AGT-01/02/13). The symbol is checked against the listing (`ISymbolCatalog`: the market-data
  module's cached one, or the default that reads the exchange per call) of the environment the agent trades in
  (production for paper agents, the key's environment for live ones); an unreachable exchange is `ExchangeUnavailable`
  and nothing is saved. The
  draft gets the timeframe's first strategy at its current version, a council where each role takes the provider and
  model the user's own experience suggests (`ICouncilSuggestions`, FR-EXP-08) or else its Appendix B default with the
  provider's newest catalog model (or an empty model to choose), and default risk limits. The agents-per-user limit applies at creation
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
  mode, the accepted risk disclaimer (FR-ACC-08), a Valid exchange key allowed for the market and no other Running, Paused or Error live agent of the user on the same
  key, market and symbol (one position per key, market and symbol belongs to one agent, FR-EXE-08). Pause keeps the agent
  evaluated but opens nothing (FR-AGT-05). Stop cancels the agent's pending entries first, then either closes its
  positions at market while their protection still stands (futures place the reduce-only close beside the stop; spot
  cancels only the OCO it needs and protects again on refusal) or leaves them flagged unmanaged, and cancels the
  protective orders last (FR-AGT-06, NFR-REL-03); if the orders cannot be cancelled the state is unchanged and the user
  is told. Delete needs Draft or Stopped. Error is entered by the runtime only: an unknown
  strategy or version, parameters no longer valid, a live agent whose key is gone or no longer resolves (which also covers a start that raced a key
  deletion), or a key the keys module found invalid; the agent is untracked, its pending entry orders are cancelled best
  effort under its gate once the evaluation that raised the fault has finished (protection stays; a venue that cannot be
  reached is logged, NFR-REL-03) and it stays so until the user fixes the cause and starts it.
- The scheduler hands each tracked agent's needs (its key, timeframe and pinned strategy) to the market-data loader
  and then follows one candle stream per agent, so on a shared stream the loader's handler runs ahead of the
  scheduler's; untracking releases the loader's series too. It evaluates under a per-agent gate that lifecycle
  actions also take, with at most `Agents:MaxConcurrentEvaluations` evaluations at once, shorter timeframes first. The
  deadline per timeframe is measured from the candle close (NFR-PERF-01) and cancels everything up to the council and
  execution's checks before its first order; a position is not opened once it has passed (the decision is recorded,
  nothing placed), a late Close or Modify still executes, and a placement under way is never interrupted, because a
  cancelled token does not recall an order. Every outcome line carries the run's duration and its distance from the
  close; a run that ends at or past its deadline is a Warning, and the scheduler counts deadline misses (late finishes
  and candles too late to start) for the status page. A candle that closed
  more than a minute before the agent was tracked is ignored, one older than the newest seen on its stream is recorded
  as Skipped (superseded), and one whose deadline has already passed is recorded as Skipped (too late). The evaluation
  row is inserted first with `InProgress`; its unique (agent, candle) index makes any second run of the same candle a
  no-op (FR-AGT-09). On start the scheduler records every evaluation left unfinished by the last stop as failed (the
  count is on the status page) and resumes every Running and Paused agent (NFR-REL-01); the execution module verifies
  every live agent's exchange state before its first action in the process, so the scheduler keeps no per-agent flag;
  once a minute it lifts daily-loss pauses whose time has come (FR-EXE-09) and finalises evaluation rows nobody will finish.
- The pipeline is FR-AGT-10 in order: load market data → strategy engine → with open orders or trades the execution
  module's management step (`IExecutionService.ManageAsync`: the restart check of a live agent, expiry and market
  fallback, breakeven and trailing; a failed check or step is logged, leaves protection as it is and blocks new entries
  for this candle, NFR-REL-03, so the council is told entries are off and is not asked when nothing is open; a fallback
  entry placed there takes the setup off the table for this candle) → council when a setup exists and entries are
  allowed, a position is open, or discretionary mode is on and entries are allowed (FR-AI-08) → execute → persist →
  log → notify. The council receives the pinned strategy with its resolved parameters, the engine output, the
  snapshot, the open position and orders and the evaluation deadline; its decision graph is persisted whenever it
  returns one, also when the Risk Manager produced no decision, in which case the evaluation is Failed and nothing is
  executed, so nothing new opens and protective orders stay (FR-AI-12, FR-AI-14). A council fault (a model the
  provider rejected) moves the agent to Error with the reason once the evaluation is recorded (FR-SYNC-04). The
  strategy engine is the Strategies module's; the agent is loaded with its no-entry windows so the engine can apply
  them. The council is the Council module's (FR-AI, #9); execution is the execution module's (FR-EXE, #13), and
  without it the seeded default records the decision and places nothing. Market data is the market-data module's (FR-DATA, #12): the
  evaluation receives the window the module backfilled and streams. Without it, the default loader reads the most
  recent 300 closed candles of the agent's timeframe plus every higher timeframe and reference series the strategy
  declares (FR-STR-03) from the exchange on each evaluation, kept only up to the evaluated candle's close (no
  look-ahead); reference series (BTCUSDT weekly) always come from production public data; the futures mark price is
  read once and a failure of it or of a reference series is logged and tolerated, while the own and higher-timeframe
  series must load; nothing is persisted. After the council and execution, whenever market data loaded, the experience
  step (`IExperienceTracker`, the experience module's) advances and opens shadow outcomes on the evaluation's own
  snapshot (FR-EXP-03); its failure is logged and never changes the outcome. A Paused agent has no setup (the engine
  suppresses them), so it consults the council only with an open position and leaves no shadow.
- Paper mode (FR-AGT-11, FR-AGT-12): paper agents price against **production** public data and need no key. Orders are
  rows with `is_paper` and deterministic client ids (FR-EXE-07); the simulator follows the last-price stream of every
  symbol with open paper orders or positions, in any agent state, so a Stop's close still fills. An order fills on the
  next tick after it is registered (tick timestamps are never compared with order times): market and close orders at
  the price with `Paper:SlippagePercent` against the trader and the taker fee; a buy limit at its price once the tick is
  at or below it (sell at or above) with the maker fee; a stop at the stop price with slippage once the tick trades
  through it; a unit's target at its price. A tick that crosses both stop and target fills the stop. An entry fill
  creates the trade and its protection in one transaction: one stop-loss order for the whole size and, per unit
  (`ProtectionUnits.Split`: the partial unit at the first target, the remainder at the second or trailing without one),
  a take-profit order (the entry order's `stop_price` carries the stop until then, the decision's targets and the
  strategy's partial fraction the units, so the books rebuild from the database after a restart). A target fill takes
  its unit off: the trade keeps its size, `remaining_size` shrinks, the fill's fee and realized P&L go on the order
  row and accumulate on the trade (the first exit also takes the entry fee off, so the P&L is net of every fee), the
  stop shrinks to what is left; the stop, a close or the last unit's target closes the trade with exit price, fees,
  realized P&L, R multiple against the initial stop, excursions as fractions of the entry price and the reason (stop,
  target, kill switch for a Stop's close, council for a decided close or partial) and cancels the sibling orders; every
  exit fill runs the daily loss check (FR-EXE-09). A partial market exit (`PartialClose`) fills at the next tick off the
  trailing remainder first. Stop and target replacements are one transaction each (old row cancelled, new row placed)
  and move the book. Spot entries are long only. Futures stops trigger on the last price; the mini-ticker is a
  one-second snapshot, so intra-second wicks are invisible. Equity per agent is `Paper:StartingBalance` plus the agent's
  realized paper P&L (partial exits of open trades included); the available balance is the equity less what the open
  positions and pending entries hold (the notional on spot, the initial margin on futures).
- The daily loss limit (FR-EXE-09): `DailyLossLimit` reads today's realized loss as −Σ `orders.realized_pnl` of the
  agent's exit fills of the current UTC day plus the entry fees paid today, against the limit on the venue's equity
  before today's realized P&L
  (unrealized P&L never counts) and, reached, pauses a Running agent with one atomic conditional update (no gate, so it
  is safe from inside an evaluation and from the fill loops) with `PauseCause.DailyLoss`, `AutoResumeAt` at the next
  00:00 UTC and the reason as `StateReason`; the minute tick lifts the pause. "Stop all my agents" (FR-EXE-10) puts every
  Running, Paused or Error agent of the user through Stop and reports the ones that could not be.
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

Pages (statically rendered with plain forms; the parts that change without a reload are the interactive islands under
`Components/Live`, FR-UI-04): `/agents` is the dashboard, one card per agent with its state, the evaluating indicator,
realized P&L all-time and today, the unrealized P&L of its open positions at the last price, paper equity, open positions
and the last evaluation, updated from the agent events and the price streams, plus "Stop all my agents" with the
close-or-leave choice and a confirmation. `/agents/new` is the first step of the setup wizard (FR-UI-01: name, market,
symbol, timeframe, mode, key, leverage and margin; it creates a draft and continues to the strategy step) and
`/agents/{id}/setup/{step}` holds the rest: strategy and parameters generated from the pinned version's descriptor with
an upgrade button when a newer version exists; the council with the synced catalog per role, the two presets and a hint
naming the roles the user's experience would assign differently (FR-EXP-08); risk limits and no-entry windows; review
and start. `/agents/{id}` is the operational view: state with pause cause and the countdown to the next candle close,
actions, the price chart with the forming candle and trade markers, positions and orders with unrealized P&L, the trade
lifecycle timeline, recent evaluations each linking to its deliberation with the council's progress shown while one
runs, the council's token usage, the newest log rows, the experience section (the experience module's), the setup
summary and the change history. Fields a state forbids are disabled. A council role whose model the catalog marks
deprecated is flagged on the dashboard and prompted for a replacement on the council step, with the provider's newest
available model suggested (FR-SYNC-03); saving and starting are not blocked, the provider's own rejection at call time
is FR-SYNC-04. The disclaimer is shown on every setup step and on the agent page (FR-UI-05). Another user's agent and a
missing one both answer not found.

Known gaps: `Order.WorkingType` has no column, so paper futures stops are last-price only. Paper excursions are
tracked per tick in memory and lost on a restart. A decision the deadline or a Stop kept from executing leaves neither a
trade nor a shadow outcome, and a candle whose management failed opens nothing and leaves no shadow outcome either, like
a Paused one.

Tests: `dotnet test` runs offline (options, registration and placeholder replacement, the Appendix E table and edit
rules, parameter parsing, input rules including windows, the window schedule, change sets, events, the scheduler's
staleness rules and plumbing, the default loader's alignment, the paper book's fill rules with units and partials). Live checks (every mutation on another user's agent NotFound with no evaluation cancelled; the scheduler end to
end; the deadline cutting a council and refusing a late entry while a late close still executes, the concurrency cap
with shorter timeframes first and a hundred running agents, NFR-PERF-01/02; an agent entering Error cancelling its pending
entries and keeping its protection, NFR-REL-03) run against PostgreSQL through the same git-ignored
`tests/.database.json` as the database module, with the exchange, providers and streams faked:
`dotnet test -- --filter-trait "Category=Live"`.
