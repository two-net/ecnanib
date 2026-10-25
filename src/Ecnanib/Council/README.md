# Council module

The multi-model AI council (SRS §3.2 FR-AI-01..15, Appendix B, Appendix C; C-04, A-05; AI-02..04). Four roles run in
sequence at each evaluation that needs them (FR-AI-08 is the agents module's), each on the provider and model the
agent assigns it, every call under the user's own key; the Risk Manager runs last and is the only role whose decision
is executed. Other modules depend on `Ecnanib.Council.Abstractions` and `AddCouncil(...)`, which must be registered
**before** `AddAgents` (whose fail-safe council stays otherwise) and requires `AddDatabase`, `AddAiProviders` and
`AddKeys` (both resolved at evaluation time) and the agents module's `RiskCaps`. It adds no hosted service and connects
to nothing at registration. The roles themselves, the prompts, the schemas and the limits never leave this folder.

| Need | Use |
|---|---|
| Run the council for one evaluation (FR-AI-02..07, FR-AI-09..13) | `ICouncil.DeliberateAsync(CouncilContext, ct)` → `CouncilResult` (the unsaved `Decision` graph, a `Failure` when the Risk Manager decided nothing, a `Fault` when the agent cannot run as configured); the agents module's pipeline is the only caller |
| Feed experience into prompts (FR-AI-06, FR-EXP-06) | `IExperienceSummaries`, provided by the experience module registered before `AddCouncil`; the default here answers "no experience recorded yet" |
| Show a deliberation (FR-AI-14) | `IDeliberationReader.GetAsync(userId, evaluationId, ct)` → `DeliberationView` (the evaluation, each role's `RoleTurnView`, the `FinalDecisionView` with its limits); another user's evaluation answers null |
| An agent's token usage (NFR-COST-01) | `IDeliberationReader.GetAgentUsageAsync(userId, agentId, ct)` → `AgentTokenUsage` (tokens in and out, all-time and today, decisions, per role) |
| Follow a deliberation as it happens (FR-UI-03 b, FR-UI-04) | the agents module's `IAgentEvents`: `CouncilRoleStarted` and `CouncilRoleFinished` per role in order, published here before and after each call, then the pipeline's `DecisionRecorded` |
| The roles, their order, defaults and responsibilities (Appendix B) | `CouncilRoles` in the agents abstractions |
| What a role returned (Appendix C) | `RoleOutputDocument` / `FinalDecisionDocument`; `LimitCheck` entries of the decision's limits check |

Rules the module enforces:

- **Order and roles** (FR-AI-02, FR-AI-05): Market Analyst, Sentiment & Flow, Cross-Market Context, Risk Manager, each
  with the agent's `CouncilAssignment`. A disabled role is recorded as such and skipped; without an enabled Risk
  Manager nothing is called. Each role is a `DecisionRoleOutput` row (status, proposed action, confidence, tokens,
  latency) and, when it was called, a `DeliberationTranscript` (the message it received and
  `{ promptVersion, status, attempts, finishReason, reasoningTokens, document, error, raw }`), swept after
  `Retention:DeliberationTranscriptDays` while the row stays (FR-AI-14, NFR-DATA-03).
- **Every role receives** (FR-AI-06), in this order: the agent configuration (market, symbol, timeframe, mode, strategy
  and parameters, risk limits, leverage, discretionary mode, whether entries are allowed), the strategy engine output
  (the same JSON as `evaluations.strategy_output`), the last `Council:RecentCandles` candles and
  `Council:HigherTimeframeCandles` of each higher timeframe, the reference series' last closes, the futures mark price
  and funding, the open position and orders, the experience summary, the preceding roles' documents (or why a role is
  not available, FR-AI-12) and the task. The system prompt carries the role's instructions. No code path reaches a key
  value; the role name goes to the provider call as its purpose, so the AI module's call log names it (FR-LOG-04).
- **Structured output** (FR-AI-07, Appendix C): every call asks for the role-output schema, the Risk Manager for the
  final-decision schema, both in the subset all four providers accept (root object, every property required,
  `additionalProperties: false`, nullable numbers as `["number", "null"]`, enums). The answer must finish normally
  and parse: confidence in 0..1, positive prices, an entry with a stop (and a price when limit), a modify with something
  to change; text is cut to `Council:MaxAssessmentLength` / `Council:MaxRationaleLength`, lists capped. A rejected
  answer is asked for once more with the reason; a second rejection fails the role. The AI module's own retries
  (rate limits, outages) run inside the role's budget.
- **Deadline budget** (FR-AI-13, NFR-PERF-01): a role may use `min(Council:RoleTimeoutSeconds, time left before the
  evaluation deadline − Council:ExecutionReserveSeconds − Council:RiskManagerReserveSeconds)`; the Risk Manager gets the
  execution reserve too, which leaves the decision's checks and placement their time inside the deadline. A role with
  less than `Council:MinimumRoleBudgetSeconds` is skipped (the Risk Manager: timed out). A role that overruns its
  budget is recorded as timed out and the next role runs; the deadline passing inside a role ends the council with the
  rest skipped. At the defaults a 15m agent (90 s deadline, 35 s per non-final role) has room for one or two non-final
  roles: disable roles or raise `Agents:EvaluationDeadlineSeconds:15m`.
- **Failures** (FR-AI-12): a failed, timed-out or skipped non-final role is noted to the roles after it and the
  deliberation goes on. Without a Risk Manager document the result is a `Failure` carrying the full deliberation
  (final action no-trade, rationale "No decision: …"), which the pipeline persists but never executes, so nothing new
  opens and protective orders stay. A missing or invalid provider key fails the role with the resolver's reason; the
  keys module's re-validation moves the agent to Error later (FR-KEY-07). A model identifier the provider rejects
  (`AiErrorKind.UnknownModel`) is a `Fault` naming the role and model: the council stops at that role and the pipeline
  moves the agent to Error after recording the evaluation (FR-SYNC-04).
- **Hard limits after the Risk Manager** (FR-AI-11, FR-AI-15; FR-EXE-04 and FR-EXE-09 as far as no venue is needed),
  each recorded in `decisions.limits_check` as pass, clamped or fail. An entry must have entries allowed (Running,
  not paused), no active no-entry window, a strategy setup on its side or discretionary mode, a long side on Spot, no
  daily-loss pause, room under min(agent, `RiskCaps`) open positions counting pending entry orders, a stop on the
  losing side within the strategy's `maxStopDistanceAtr` (when the ATR and the bound are known), risk per trade at
  most min(agent limit, `RiskCaps:MaxRiskPerTradePercent`) (clamped, or the agent's limit when not given) and
  leverage within the cap; targets on the wrong side are dropped. Any failure records the decision as no-trade with
  the rationale prefixed by the limits that failed, the Risk Manager's proposal staying on its role row. With a
  position, hold is price-free; modify may only tighten the stop (a widening stop is dropped, one that would trigger
  at once fails into hold), replace targets and take a partial (`decisions.partial_fraction`); close passes through.
  Sizing, notional, minimum notional, balance and the daily loss in money are the execution module's (#13), recorded in
  `decisions.execution_check` in the same shape (`LimitCheck`, `LimitNames` and `ExecutionLimitNames` live in the agents
  abstractions); a rejection there rewrites the decision as no-trade the way a failed limit does here.
- **Logging** (FR-LOG-04): one line per role with agent, evaluation, role, provider, model, status, attempts,
  latency and tokens, one line for the final action and the limits that were not passed; never prompt text or key
  material. The AI module logs each attempt.

Configuration (`Council` section; the defaults are in `src/Ecnanib/ecnanib.json`):

```json
{ "Council": { "RoleTimeoutSeconds": 60, "RiskManagerReserveSeconds": 45, "MinimumRoleBudgetSeconds": 5, "ExecutionReserveSeconds": 10,
  "RecentCandles": 30, "HigherTimeframeCandles": 10, "MaxOutputTokens": 8192, "MaxAssessmentLength": 1200, "MaxRationaleLength": 600 } }
```

`MaxOutputTokens` may only lower `Providers:MaxOutputTokens`; reasoning tokens count against it on GPT, Grok and
Gemini. The rationale bound is at most 3000 so the limits prefix fits the 4000-character column.
`ExecutionReserveSeconds` plus `MinimumRoleBudgetSeconds` must leave the Risk Manager time within the shortest
`Agents:EvaluationDeadlineSeconds` (checked at start); the Risk Manager reserve is not checked against it, so a
council with too little room skips its non-final roles instead.

Pages: the agent page's council section offers each role the synced catalog (`IModelCatalog`, one group per provider,
deprecated models flagged) or a provider and model id typed by hand, the Appendix B defaults by strength (each
provider's newest catalog model) and single-model mode (FR-AI-03, FR-AI-04); every recent evaluation links to
`/agents/{id}/evaluations/{evaluationId}`, which shows the strategy output, each role's turn (status, provider, model,
cost, proposal, assessment, flags, the document and the input it received) and the final decision with its limits
check (FR-AI-14), the execution check appended at order time, and, for a setup the council did not take, its shadow outcome (the experience module's, FR-EXP-03).
While a council runs, the agent page's decisions list shows the roles taking their turns in order and the verdict last
(FR-UI-03 b), driven by the role events; the deliberation page replays the recorded turns at a pace set by each role's
latency, skippable and skipped under reduced motion. The agent page's council usage section shows the tokens per role,
all-time and today (NFR-COST-01).

Tests: `dotnet test` runs offline (the sequence over faked providers: order, disabled roles, the retry, failures,
budgets and the deadline with a fake clock, faults, keys never reaching a prompt, the limits one by one, the parser,
the schema subset, the prompts, the options, the registration). Live checks run the council inside the real pipeline
against PostgreSQL through the git-ignored `tests/.database.json` with the providers faked:
`dotnet test -- --filter-trait "Category=Live"`.
