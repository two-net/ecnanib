# Execution module

Order execution, position management and the hard risk enforcement at order time (SRS §3.2 FR-EXE-01..12; FR-AI-11,
FR-AI-15; FR-AGT-06, FR-AGT-10, FR-AGT-11; NFR-REL-01..03; EX-01, EX-02, EX-05). The agents module's pipeline hands
every decision to `IExecutionService`, which sizes it against the symbol's filters and the balance, places it on the
agent's venue and manages the open position at every candle close; the live venue is Binance through the user's own
key. Before a live agent's first action in the process the module verifies its exchange state (NFR-REL-01), and nothing
it places can double an order or leave a position without protection (NFR-REL-02/03). Other modules depend on the
seams in `Ecnanib.Agents.Abstractions` (`IExecutionService`, `IOrderVenue`, `IDailyLossLimit`, `IAgentGates`,
`IReconciliationStatus`, `ClientOrderIds`, `ProtectionUnits`, `TradeJson`, `LimitCheck`), on `Ecnanib.Execution.Abstractions`
(`ITradeTimelineReader`, `IAgentPerformance`, `PnlMath`, read by the pages and the live islands) and on `AddExecution(...)`, which
must be registered **before** `AddAgents` (whose fail-safe execution and pending live venue stay otherwise; it throws
when `AddAgents` came first) and requires `AddExchange`, `AddDatabase` and, at call time, the keys module's resolver.
It adds two hosted services and connects to nothing at registration. The venue mechanics never leave this folder.

| Need | Use |
|---|---|
| Apply a council decision (FR-AI-11, FR-EXE-04) | `IExecutionService.ExecuteAsync(ExecutionRequest, Decision, cancellable, stopping)` → `ExecutionResult` (the orders placed, the note shown as the evaluation's detail, a `Fault` the pipeline moves the agent to Error with); the agents module's pipeline is the only caller |
| Manage what is open before the council (FR-EXE-03, FR-EXE-06, FR-EXE-08, NFR-REL-01) | `IExecutionService.ManageAsync(ExecutionRequest, cancellable, stopping)`: the restart check first, then the steps; `EntryPlaced` tells the pipeline a market fallback took the setup off the table for this candle, `EntriesBlockedReason` why nothing new may open this candle (NFR-REL-03) |
| Place, replace, cancel or close on the exchange (FR-EXE-01, FR-EXE-02, FR-EXE-10) | `IOrderVenues.For(AgentMode.Live)` → the live `IOrderVenue` (`PlaceEntryAsync`, `PlaceCloseAsync`, `PlacePartialCloseAsync`, `ReplaceStopAsync`, `ReplaceTargetsAsync`, `ExpireEntryAsync`, `CancelOpenOrdersAsync`, `CancelEntryOrdersAsync`, `CancelProtectiveOrdersAsync`, `ClosePositionsAtMarketAsync`, `GetEquityAsync`, `GetAvailableAsync`); the paper venue is the agents module's |
| What reconciliation reports for the status page (NFR-OBS-01) | `IReconciliationStatus.GetStatus()` → `ReconciliationStatus` (when the module came up, the last round, agents verified since start and still awaiting their check, adoptions, the last error as a kind: never an agent or key id, NFR-SEC-03) |
| The daily loss limit (FR-EXE-09) | `IDailyLossLimit.CheckAsync(agentId, ct)` (agents module), called after every exit fill and before every entry |
| What execution found at order time | `decisions.execution_check` (the council's `LimitCheck` shape, names in `ExecutionLimitNames`) and `decisions.size`; the deliberation page shows both lists |
| Deterministic client order ids (FR-EXE-07) | `ClientOrderIds.For(prefix, agentId, scope, purpose, sequence)`: `p` paper, `e` live |
| P&L per agent for the dashboard (FR-UI-04) | `IAgentPerformance.GetAsync(userId, agentIds)` → `AgentPnl` per agent (realized all-time and today in UTC, fees, funding, closed trades, open positions, paper equity); `PnlMath.Unrealized` for an open position at the last price |
| A trade's lifecycle for the timeline (FR-UI-03 e) | `ITradeTimelineReader.GetAsync(userId, tradeId)` / `ListRecentAsync(userId, agentId, count)` → `TradeTimelineView` with its `TimelineEvent`s from the evaluation to the close |

Rules the module enforces:

- **Sizing** (FR-EXE-04): price and stop are rounded to the tick (the stop away from the price), the quantity is
  (equity × risk per trade) ÷ |price − stop| × the strategy's size multiplier, rounded **down** to the lot step (and the
  market lot step for a market entry), then clamped to the agent's notional cap and the exchange's maximum quantity and
  notional, and refused under the minimum quantity, the minimum notional or the free balance (the whole notional on
  Spot, notional ÷ leverage on futures). Equity is the paper starting balance plus realized paper P&L, the futures
  wallet balance, or the spot quote asset plus the open positions at the last price; the risk per trade is the
  council's (already clamped to the agent's limit and `RiskCaps`). A failed check places nothing and rewrites the
  decision as no-trade with "Rejected by execution limits (…)" in front of the rationale, as the council does, so the
  experience module opens its shadow outcome (FR-EXP-03). Every check is recorded in `execution_check`.
- **Entries** (FR-EXE-03): a limit entry at the decided price expires `entryTimeoutCandles` candles after the evaluated
  close (the strategy parameter, `Agents:DefaultEntryTimeoutCandles` otherwise); the management step cancels it at the
  first evaluation past that and, with `marketFallback` on, re-checks the time-dependent guards (entries allowed, no
  no-entry window, the position cap, the daily loss) and the venue-bound checks at the last close with the decision's
  stop before placing a market entry for the same decision (sequence 1).
- **Protection** (FR-EXE-01, FR-EXE-02, NFR-REL-03): a filled entry opens the trade and is protected per *unit*: the
  partial unit, `partialTakeProfitFraction` of the size on the lot grid, takes the first target, the remainder the second
  or trails without one; a unit too small to split merges into the other. Spot: one OCO per unit with a target (a
  limit-maker take-profit and a stop-loss-limit leg whose limit sits `Execution:SpotStopLimitOffsetPercent` under the
  stop) or a plain stop-loss-limit without; when the symbol's `MAX_NUM_ALGO_ORDERS` headroom cannot take one order per
  unit, one OCO at the final target covers the whole size and the partial is taken at the first candle close beyond the
  first target; a unit's stop leg closes that unit, and the trade closes when nothing is left. Futures: one reduce-only
  stop-market conditional order for the whole remaining size (never
  close-position, which would flatten a position the agent does not own) plus one reduce-only take-profit-market
  conditional order per unit with a target, all through Binance's algo endpoint, which is the only one that accepts
  them since December 2025; the stop and the targets are re-placed new-before-old, a conditional order not being held
  against the position until it triggers. Protection places only the units no open row covers yet, so a retry or a
  re-protection never doubles an order (NFR-REL-02); it is always asked for the trade's whole desired set, each open
  row covering one unit, so a target moved onto a sibling's is placed for the unit without a row and the sibling's row
  stays. A protective order that cannot be placed is retried
  `Execution:ProtectiveOrderAttempts` times, then the position is closed at market and the failure logged at Critical
  (FR-EXE-05): on futures the reduce-only close is placed beside the stop, which goes once the trade is closed; a
  hedge-mode refusal is a fault instead (FR-EXE-11). An attempt whose answer was lost and could not be read back is
  neither retried nor flattened (see Idempotency).
- **Management at candle close** (FR-EXE-06, FR-AI-15), before the council: once the partial filled, the stop moves to
  breakeven and then trails behind the engine's `trailLong`/`trailShort` level (swing or EMA per `trailingMethod`)
  less `stopBufferAtr` × ATR, rounded away from the price; a stop only ever tightens and never sits across the last
  close; every move is a `StopHistory` entry (`breakeven`, `trail`, `council`). The council may then tighten further,
  replace targets or take a partial (a fraction that takes everything closes; one that would leave either part under
  the exchange minimum is skipped, so no dust stays behind). Each step is guarded on its own: a failure leaves the
  others and what was already placed in the result, and blocks new entries for this candle (NFR-REL-03: an open state
  that could not be managed is uncertain). The step runs under `Execution:ManageBudgetSeconds`; past the budget it
  records "management skipped", leaves protection as it is and blocks entries the same way. Futures replace a stop
  new-before-old, so a replacement that cannot be placed leaves the old stop and is reported. Spot is the one venue
  where protection goes before its replacement, because an open sell locks its quantity: a replacement, a close or a
  partial cancels the OCOs it needs, tolerantly, and protects what was freed again; an OCO the exchange would not cancel
  keeps its unit at the previous stop and the move is reported (and retried at the next close), so no unit is ever left
  uncovered; a replacement that cannot be placed after the cancel follows the retry-then-flatten rule.
- **Fills** (EX-03, FR-EXE-12): `LiveFillTracker` follows the user-data stream of every key and market with live agents
  that have open rows (reviewed at start, after every placement and every reconciliation interval; a rotated key or a
  stopped session is re-subscribed, a key with nothing open released) and hands order, OCO and conditional-order
  updates to the fill handler by client id, one event at a time per key. The same handler serves the exchange's answer
  to a placement and the reconciler, so a fill reaches the books once however many ways it arrives. Fees come from the
  fills in the quote asset (the base asset at the fill price, another asset through the public price of
  `FEEASSET+quote`, else 0 with a warning); every exit fill's realized P&L is on its order row, the trade accumulates
  them, and futures funding over the trade's life is added at the close; `trades.realized_pnl` is net of fees and
  funding (what the experience module expects).
- **Restart check** (NFR-REL-01): before a live agent's first action in the process, in `ManageAsync` and in
  `ExecuteAsync` alike, `ExchangeReconciler.EnsureCheckedAsync` compares every row and trade written before the module
  came up with the exchange, whatever its age, and adopts what it finds (a periodic round that compared the agent counts
  as that check). A check that fails blocks everything this candle: management is skipped, the council is told entries
  are off, an entry it proposes anyway is rejected with `exchangeState`, a close or modify is not executed and the
  position stands with its protection; the failure is remembered for `Execution:ReconcileAfterSeconds`, so each candle
  tries once. Paper agents rebuild their books from the database and are never checked.
- **Reconciliation** (FR-EXE-08): `ExchangeReconciler` compares at start, every
  `Exchange:ReconciliationIntervalSeconds` and when a stream restores or a polling snapshot arrives. One read per key
  and market per round, each agent under a try-lock on its gate (an agent with an evaluation in flight waits for the next
  round); rows younger than `Execution:ReconcileAfterSeconds` belong to the evaluation placing them, except the rows of
  an agent not yet verified since start, which are compared whatever their age. The exchange is the truth and every
  adoption is a Warning: a filled order is processed as a fill, a vanished one marked cancelled, a vanished futures
  position or a sold spot holding closes the trade as External with the exchange's fills, a smaller position shrinks
  the trade and re-sizes its stop (unless an exit of the system's own is in flight, whose fill explains it), a trade
  whose open stops cover less than its size is protected again for the rest (futures re-size the stop new-before-old;
  spot cancels take-profit legs whose OCO lost its stop leg and places the uncovered quantity, the whole OCOs left
  alone), and orders or positions the system never placed are reported only. The rounds and the checks report through
  `IReconciliationStatus` (NFR-OBS-01).
- **Daily loss** (FR-EXE-09): today's realized loss is −Σ `orders.realized_pnl` of the agent's exit fills of the
  current UTC day (a partial counts the day it fills) plus the entry fees paid today, against the limit on the equity
  before today's realized P&L; unrealized P&L never counts. Reached, a Running agent is paused until 00:00 UTC with
  the reason shown; the agents module's minute tick lifts the pause.
- **Kill switches** (FR-EXE-10, NFR-REL-03): Stop cancels an agent's pending entries first, then closes its positions
  at market while their protection still stands (futures: the reduce-only close beside the stop; spot: only the OCO the
  sell needs, protected again on refusal) or leaves them flagged unmanaged, then cancels the protective orders (agents
  module, FR-AGT-06); "Stop all my agents" on `/agents` does it for every agent that can be stopped and lists the ones
  that could not be. One live agent per key, market and symbol can run at a time, so a position attributes to one agent.
- **Futures account** (FR-EXE-11, A-06): before an agent's first futures order in this process the account must be in
  one-way mode (hedge mode moves the agent to Error with guidance) and the symbol takes the agent's margin type and
  leverage while no position is open on it; the applied leverage must match. Remembered per agent and setting,
  forgotten on a leverage update from the stream or a position-mode refusal.
- **Idempotency** (FR-EXE-07, NFR-REL-02): every row is written before its request is sent and carries a deterministic
  client id; a duplicate placement returns the first row, a client id the exchange already knows (futures `-4116`, spot
  `-2010` "Duplicate order sent.") is read back and adopted, and a rejection marks the row. A lost answer (a timeout, a
  dropped connection, a cancelled token, an unclassified error) is resolved before anyone may retry: the exchange is
  asked for the order by its client id within `Execution:LostAnswerLookupSeconds`; found, it is adopted; unknown, it is
  asked for once more after Binance's receive window (5 s), beyond which a request in flight is refused, and only then
  is the row recorded as cancelled (it never reached the exchange and still counts toward its purpose's sequence, so a
  retry takes a fresh id) and the failure rethrown; unreadable, the row stays New for reconciliation and nothing is retried or
  flattened, because a second order against an exchange that may hold the first is worse than waiting. A sell whose
  answer was lost is not protected again either, since it may have executed; a close still pending is returned instead
  of a second one. Writes follow the paper simulator's shape (a transaction, a re-read, a status guard; a stale row is
  re-read once).
- **Logging** (FR-LOG-04): every placement, replacement, fill, cancellation, reconciliation adoption and the daily-loss
  pause with ids, symbols and prices; never key material.
- **Deadline** (NFR-PERF-01): `ExecuteAsync` runs every read before its first order (the filters, the daily loss,
  equity and balance) on the evaluation's cancellable token, checks it once more right before that order, and from
  then on places and records on the shutdown token only: a cancelled token does not recall an order, and a decision's
  several placements (stop, targets, partial) are finished once started. The pipeline does not call it for an entry
  decided after the deadline; a late Close or Modify is executed with the deadline waived.

Configuration (`Execution` section; the defaults are in `src/Ecnanib/ecnanib.json`):

```json
{ "Execution": { "ProtectiveOrderAttempts": 3, "ProtectiveOrderRetryDelayMs": 1000, "SpotStopLimitOffsetPercent": 0.5, "ManageBudgetSeconds": 20, "ReconcileAfterSeconds": 30, "FillTrackerStartDelaySeconds": 6, "LostAnswerLookupSeconds": 10 } }
```

The reconciliation interval is `Exchange:ReconciliationIntervalSeconds`, the risk caps `RiskCaps`, the entry timeout
default `Agents:DefaultEntryTimeoutCandles`.

Pages: the dashboard (`/agents`) shows each agent's realized P&L, all-time and today, and the unrealized P&L of its open
positions at the last price, and offers "Stop all my agents"; `/agents/{id}` shows the positions and orders with their
unrealized P&L and remaining size and the trade lifecycle timeline (evaluated, decided, entry placed and filled,
protected, stop moves, partials, exit, closed); the deliberation page shows the execution check beside the council's
limits check.

Known gaps: an exchange position or order the system did not open is reported, not adopted; a fee asset other than the
base or quote asset needs a public `FEEASSET+quote` price, else the fee is 0 with a warning; the optional 1D entry in
up to three tranches stays deferred (one entry per decision); after a key rotation the fill tracker is blind for at most
one round, which reconciliation covers; live excursions (MAE/MFE) are not tracked; the dashboard's equity is the paper
venue's only, a live agent's equity being the whole account's. Spot protection is replaced old-before-new by necessity
(an open sell locks its quantity and Binance has no atomic OCO replacement), the window kept to one cancel and one
placement with the tolerant cancel and the reconciler's repair behind it; reconciliation repairs stops, not
take-profits, so a futures target whose placement was lost waits for the next council modification; a row recorded as
never sent whose order the exchange accepts late shows up as a stranger order and is reported, not adopted.

Tests: `dotnet test` runs offline (the options, the registration and the placeholder replacement, the sizing arithmetic,
the management rules, the ids and the units, the conditional-order mappings). Live checks run against PostgreSQL through
the git-ignored `tests/.database.json` with the exchange faked by a scripted account book (the execution service over
the paper venue, the live venue on both markets, the fill tracker, the reconciler, the pipeline end to end; the restart
check adopting a fill before the council runs, blocking everything when the exchange cannot be read and running once per
live agent; a lost answer adopted by client id or left for reconciliation, a retry placing only what is missing, the
duplicate-id adoption, the pending close, the kill switch's order of operations, the under-protected spot trade and the
spot cancel that fails midway, NFR-REL-01..03) and, with
keys in `tests/.exchange-keys.json`, the conditional-order and OCO round trips on the Binance demo and testnet
environments: `dotnet test -- --filter-trait "Category=Live"`.
