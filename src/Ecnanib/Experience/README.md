# Experience module

Provider and model trading experience (SRS §3.2 FR-EXP-01..08, A-04; FR-AI-06, FR-SYNC-05; NFR-SEC-03). Every council
turn is already attributed to the provider, model and role that produced it (`decision_role_outputs`, FR-EXP-01) and
every trade to the decision chain that opened and managed it (`trades.entry_decision_id`, `decisions.trade_id`, with
realized P&L, R multiple, fees, funding, excursions and timestamps, FR-EXP-02). This module turns those rows into
experience: shadow outcomes for the setups the council did not take (FR-EXP-03), statistics per provider, model, role,
timeframe and market at agent, user and system scope (FR-EXP-04, FR-EXP-05), the summary each role prompt carries
(FR-EXP-06), the leaderboard and provider pages (FR-EXP-07) and the council suggested when an agent is created
(FR-EXP-08). Other modules depend on `Ecnanib.Experience.Abstractions` and `AddExperience(...)`, which must be
registered **before** `AddCouncil` and `AddAgents` (their placeholders would otherwise stay) and requires `AddDatabase`.
It adds one hosted service and connects to nothing at registration.

| Need | Use |
|---|---|
| Feed experience into role prompts (FR-AI-06, FR-EXP-06) | `IExperienceSummaries` (council abstractions), provided here: the headline statistics for the provider, model and role at the agent's timeframe and market (this agent, all the user's agents, all users) and the `Experience:RecentOutcomes` most recent outcomes, read live; null when nothing is recorded |
| Follow the setups the council did not take (FR-EXP-03) | `IExperienceTracker` (agents abstractions), provided here and called by the pipeline after every evaluation with the market snapshot it used |
| Suggest a council at creation (FR-EXP-08) | `ICouncilSuggestions.SuggestAsync(userId, timeframe, market, ct)` (agents abstractions), provided here → `CouncilSuggestion` per role with a qualifying row; `IAgentService.CreateAsync` uses it, the agent page shows it |
| Show the leaderboard and the detail views (FR-EXP-07, FR-UI-01) | `IExperienceReader`: `GetLeaderboardAsync(userId, ExperienceFilter)` → `LeaderboardView` (the user's rows and the system-wide rows per provider and model), `GetProviderAsync` → `ProviderExperienceView` (per model and role, and per agent of the user), `GetAgentAsync` → `AgentExperienceView` (per role, and the last 20 closed trades with their FR-EXP-02 fields), `GetShadowOutcomeAsync(userId, evaluationId)` → `ShadowOutcomeView` |
| The stored figures | `ExperienceStat` rows with `ExperienceMetrics` (database entities): counts and sums with the ratios derived from them (`Derive()`), so they roll up exactly (`Add()`) |

Rules the module enforces:

- **Attribution** (FR-EXP-04). A *turn* is a role output whose role was called (Succeeded, Failed or TimedOut; not
  Disabled or Skipped): `Decisions` counts turns, `FailureRate` is failed-or-timed-out turns over turns, and
  `AverageLatencyMs` is over turns. `Approvals` are turns that proposed open_long or open_short; `Rejections` are turns
  that proposed no_trade while an entry was on the table, which is when the decision opened one or a shadow outcome
  exists for it. A closed trade is attributed to the roles that **proposed its entry** on the opening decision, at the
  trade's own timeframe and market; a role that said no_trade on a decision the Risk Manager opened anyway is an
  *overruled rejection* and gets the real trade's R under the rejection figures instead. Managing decisions (hold,
  modify, close) count as turns only.
- **Figures**. `AverageR` is the mean gross R (`trades.r_multiple`: price move over the risk at entry). `Expectancy` is
  the mean **net** R: realized P&L (fees and funding included) over the money at risk, `|entry − initial stop| × size`,
  with the initial stop from the trade's stop history (the stop actually placed) or else the decision's stop; a trade
  without one is left out of net R only. `WinRate` is trades with R > 0 over trades (breakeven is not a win).
  `ProfitFactor` is gross profit over gross loss in R, null without a loss. `DrawdownContribution` is the role's gross
  loss over the owner's gross loss at the same timeframe and market, the latter over distinct trades so a loss counts
  once however many roles proposed it. `ClampRate` is Risk Manager decisions whose limits check holds a `clamped`
  entry (FR-AI-11) over its succeeded turns, null for other roles. `RejectionAccuracy` (beyond the FR-EXP-04 list, the
  point of FR-EXP-03) is the share of a role's resolved rejections that avoided a loss: shadows that hit the stop and
  overruled rejections that lost, over resolved. Every ratio is 0..1, every R is unitless: a system-wide row holds
  counts and ratios only and names no user, agent, symbol or key (FR-EXP-05).
- **Shadow outcomes** (FR-EXP-03). After every evaluation whose market data loaded, the tracker first advances this
  agent's pending shadows whose decision has the same symbol, market and timeframe by re-walking the candles the
  evaluation just saw, from the candle after the decision's: a market entry fills at the next candle's open, a limit
  entry on the first candle within the strategy's `entryTimeoutCandles` whose range touches the entry price (else it
  expires unfilled); from the fill candle on, a candle touching the stop ends it at −1R, else one touching the first
  target ends it at the setup's reward, a candle touching both counts as the stop (the paper venue's rule); after
  `Experience:ShadowHorizonCandles` candles with neither touched it expires with the mark-to-market R kept for the
  page but not the statistics. Then it opens a shadow for the decision just made when the strategy had a setup,
  entries were allowed and nothing was opened: the Risk Manager vetoed it (`veto`), the hard limits rejected its entry
  (`limits`) or no decision was made (`no-decision`, FR-AI-12). The walk is deterministic (`shadow_outcomes.detail`
  carries the setup and the progress), uses no market data beyond the evaluation's own snapshot and never looks ahead.
  A pending shadow the snapshot no longer reaches back to (older than the candles an evaluation sees) or without a
  usable detail is expired as `stale` or `no-detail`; the refresher expires the ones no evaluation will walk any more
  (the agent stopped or trades something else) once their candles plus a day of grace have passed. A failure of the
  tracker is logged at Warning and never changes the evaluation's outcome (NFR-REL-03).
- **Refresh** (FR-EXP-04, FR-EXP-05). `ExperienceRefresher` recomputes every row four minutes after start and then
  every `Experience:RefreshIntervalMinutes`: turns are grouped by the database (with a jsonb containment for the
  clamped limits), closed trades and resolved shadows are joined to the role outputs that proposed them, the agent
  rows are rolled up to one row per user and one system-wide set, and the whole table is replaced in one transaction,
  so a reader sees the old set or the new one. A failing step is logged at Warning and retried next run; the host is
  never stopped. The leaderboard re-aggregates the stored counts and sums over whatever the filter leaves open, so a
  filtered figure is exact, not an average of ratios.
- **Suggestions** (FR-EXP-08). Per role, the user's own row (user scope, the agent's timeframe and market) with the
  best expectancy over at least `Experience:MinimumTradesForSuggestion` closed trades, never a model the catalog has
  deprecated (FR-SYNC-03); without one the role keeps its Appendix B default on the provider's newest catalog model
  (FR-SYNC-05). "Use the defaults by strength" stays Appendix B by definition.
- **Isolation** (NFR-SEC-03). Agent and user rows carry the user; every read of them is scoped to the signed-in user
  (another user's agent and a missing one both answer null), while system rows are shared by design. The prompt
  summary names no agent, user, symbol or key; logs carry ids only.

Configuration (`Experience` section; the defaults are in `src/Ecnanib/ecnanib.json`):

```json
{ "Experience": { "RefreshIntervalMinutes": 15, "ShadowHorizonCandles": 30, "RecentOutcomes": 5, "MinimumTradesForSuggestion": 10 } }
```

The horizon is at most 200 so that it and the entry timeout (at most 100) fit the 300 candles an evaluation sees.

Pages (static server rendering, plain forms): `/providers` carries the leaderboard (FR-UI-01) with a GET filter by
role, timeframe and market in the query string, a table for the user's own agents and one for all users, each
provider linking to `/providers/{provider}`, which shows every model of that provider in every role for the user and
system-wide, then the user's agents one by one at their own timeframe and market. `/agents/{id}` gains an Experience
section (the agent's rows per role with the current assignment marked, and its last 20 closed trades with duration,
R, P&L, fees, funding, excursions, exit reason and the number of decisions in the chain, each linking to the entry
evaluation's deliberation) and a council hint naming the roles where the user's experience suggests another provider
or model. `/agents/{id}/evaluations/{evaluationId}` shows the decision's shadow outcome when it has one.

Known gaps: the refresh is a full recompute (the counts and sums make an incremental one possible later). Until
execution (#13) lands no trade is opened in the running application, so the trade figures, the recent outcomes and the
suggestions stay empty while turns, clamps, failures, latency and shadow outcomes are real at once; funding per trade
comes from exchange data with execution (FR-EXE-12). A shadow of an agent that stopped expires stale rather than
resolving from the candle table, a follow-up once market data (#12) backfills. Paper excursions are tracked per tick
in memory and lost on a restart (agents module).

Tests: `dotnet test` runs offline (the walk's rules, the metrics' arithmetic and roll-ups, the summary text, when a
decision leaves a shadow, the suggestion choice, the leaderboard aggregation and order, the options, the registration
and placeholder replacement, the refresher's cadence and failure handling, the page texts and the filter). Live checks
run against PostgreSQL through the git-ignored `tests/.database.json`: the refresher over seeded decisions, trades and
shadows at the three scopes and its stale sweep; the tracker inside the real pipeline with faked providers (a vetoed
setup opens a shadow the next candle resolves, a Risk Manager failure opens a no-decision one, a failing tracker leaves
the evaluation alone); the summaries; creation with suggestions; the reader's views and isolation:
`dotnet test -- --filter-trait "Category=Live"`.
