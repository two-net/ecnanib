# The team

Roles run in the order of the decision cycle (SRS 3.2): the Market Analyst and the Sentiment Analyst in parallel, then the Strategist, then the Risk Manager. Each role receives only the inputs defined for it and returns a structured result that is validated against a fixed schema, plus a short rationale (FR-TEAM-6). Results outside the schema, or with values outside the allowed ranges, are rejected (NFR-SEC-7).

The Risk Manager may approve a plan, reduce its risk fraction, or veto it. It cannot increase risk, change direction, or widen the stop-loss, and it cannot block a plan that closes a position or tightens protection (FR-TEAM-7). Each role call records the provider, model, prompt template version, latency, token usage, structured result, and validation outcome (FR-TEAM-8). If a role call times out or returns an invalid result, it is retried once. If the Sentiment Analyst still fails, the cycle continues with sentiment marked unavailable. If any other role still fails, the cycle ends with no decision and no new entry (FR-TEAM-9).

Allowed actions on Spot are open long, hold, close, and adjust protection. Futures adds open short and reverse, which closes the position and then opens the opposite side (FR-CYC-4). An agent holds at most one position, and adding to an open position is not supported (FR-CYC-5). Adjust protection may move the stop-loss only in the direction that reduces risk, and the take-profit in either direction (FR-CYC-6). A plan to open a position always includes a stop-loss price and a take-profit price (FR-CYC-7). A decision that completes later than the stale limit for its timeframe cannot open a position. It can still close a position or tighten protection (FR-CYC-9).

Models cannot place orders or call internal functions. The only path from a model's output to an order is schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6).

What is built is steps 3 to 5 of the decision cycle: the calls of the four roles, the validation of what each answers, what a plan may do, what the review of the Risk Manager does to a plan, what the stale limit does to a decision, the record of a cycle, which is complete, and the timeline that shows it to its user (FR-CYC-10). What goes on of a plan is checked by the Risk Guard, which is in [risk-guard.md](risk-guard.md), and what the Risk Guard accepted is handed to what places the orders, which is in [orders.md](orders.md).

The code is in `src/Ecnanib/DecisionEngine`. The rules below are enforced by tests in `tests/Ecnanib.Tests/DecisionEngine`, in the classes whose names begin with `Team` and `Role`, and in `RiskManagerReviewTests`, `PlanRuleTests`, `StaleDecisionTests`, `CycleRecordTests`, and `CycleTimelinePageTests`.

What starts a cycle, and the data package that it builds, are in [cycles.md](cycles.md). The templates of the roles and what composes a prompt are in [prompts.md](prompts.md). How a model is called is in [ai-providers.md](ai-providers.md), and how a stored key is handed to a call in [credentials.md](credentials.md). Who chooses the model of a role is in [agents.md](agents.md).

## The order

`TeamCycle` is what a cycle does once it has started: it is the registered `IDecisionCycle`.

| Step of SRS 3.2 | What happens |
|---|---|
| 2 | The orders of the agent are settled with the exchange, so that the cycle decides on what the agent holds now: see "Settling" in [orders.md](orders.md). Then the data package is built, by `IDataPackages`. A package that cannot be built ends the cycle as failed. |
| 3 | **The Market Analyst and the Sentiment Analyst are called at the same time.** Neither reads what the other answers. |
| 4 | **The Strategist is called when both are over,** with both reports. |
| 5 | **The Risk Manager is called when the Strategist is over,** with its plan, for a plan that opens a position and whose decision is not stale by then. |
| 6 | **The Risk Guard checks what goes on of the plan,** when the team has decided, and calls no model: see [risk-guard.md](risk-guard.md). It computes the size of the order that the plan opens, from the risk fraction that the Risk Manager left it (FR-RSK-5). |
| 7 | **What the Risk Guard accepted is handed to `IPlanOrders`,** and nothing else is: it places the entry and the stop-loss, closes the position, or moves its stop-loss, or does nothing. See [orders.md](orders.md). |
| 8 | The cycle is recorded, as a complete record: the summary of its data package, the calls of its roles, the checks of the Risk Guard, and its outcome, with why it placed no entry where it placed none. See "The record of a cycle". |

| Rule | Detail |
|---|---|
| **An agent runs with its whole team** (FR-TEAM-1). | A cycle of an agent that lacks the model of a role fails before any model is called, so no model is paid for a cycle that cannot be completed. Agents starts an agent only with its whole team: see "The controls" in [agents.md](agents.md). |
| **The Risk Manager is called for a plan that opens a position, and for no other.** | Open long, open short, and reverse: `PlanReview.Reviews`. A plan that holds changes nothing, and a plan that closes a position or adjusts protection reduces risk. Neither is handed to the Risk Manager, so nothing that its model answers, and no call of it that fails, stands in the way of reducing risk (FR-TEAM-7). Such a cycle is three calls, on the user's own keys. |
| **A plan that is stale before it is reviewed is not reviewed.** | It opens no position whatever a review says (FR-CYC-9), so no model is called, and paid, to review it. See "The stale limit". |
| **A cycle is two calls at once at most.** | The two analysts. The places that a cycle takes in the queue are of cycles, and not of calls: see "The queue" in [cycles.md](cycles.md). |
| One object runs the cycles of every agent. | `TeamCycle` holds nothing of a cycle: everything of a cycle is local to its run. |

## What a role is given

**Each role receives only the inputs defined for it** (FR-TEAM-6, SRS 4.5). The context of the agent, which is its symbol, its equity, its position, and its limits, is a part of every prompt (FR-PRM-1). What differs is the part under `# Data package`, and the lessons:

| Role | Its data package | Lessons |
|---|---|---|
| Market Analyst | The text of the data package: the candles, the indicators, the open orders, and on Futures the mark price, the funding, and the open interest. | None |
| Sentiment Analyst | One line: `Candle close: 2026-11-18T10:15Z`. It reads news and social context, and no candle: its read is its own, and not a second read of the price. | None |
| Strategist | The text of the data package, the report of the Market Analyst, and the report of the Sentiment Analyst with its mark. | Those that were recalled (FR-EXP-7). None yet. |
| Risk Manager | The text of the data package, and the plan of the Strategist. | Those that were recalled. None yet. |

- **The Risk Manager is given neither report.** It reviews the plan independently (SRS 4.5): it forms its own view from the market data. What a search found reaches it only in the words of the Strategist's plan, so untrusted content has one role less to reach (risk R6).
- `DataPackage.ForMarketAnalyst`, `ForSentimentAnalyst`, `ForStrategist`, and `ForRiskManager` make the parts of each prompt. Everything is placed in the data package, which the instructions of every role name as data: see "Untrusted content" in [prompts.md](prompts.md).

What the Strategist reads under the text of the package, and what the Risk Manager reads there:

```
Market report:
{"bias":"bullish","keyLevels":[{"price":60000,"kind":"support"}],"volatility":"normal","rationale":"Higher lows."}

Sentiment report: live data
{"bias":"neutral","notableEvents":["A spot ETF saw record inflows."],"confidence":0.6,"liveDataAvailable":true,"rationale":"Mixed headlines."}
```

```
Trade plan:
{"action":"open_long","entryType":"market","entryPrice":null,"stopLoss":59000,"takeProfit":64000,"riskFraction":0.01,"rationale":"A pullback into support."}
```

| Rule | Detail |
|---|---|
| **A result is one line of JSON, whatever the model wrote.** | A line break inside a text is written as `\n`. So no line of a report or of a plan begins as a heading of a prompt does, or as the line above another result does: a result cannot pose as either. Nothing of it is lost. |
| **Only a result that was validated is placed.** | `ForStrategist` and `ForRiskManager` take the result of the validation, and throw for one that was rejected. |
| **The mark of the sentiment report is the service's.** | `live data`, `no live data`, or `unavailable`: see "Live search" in [cycles.md](cycles.md), and "When a role gives no valid result" below. |

## The schemas

The schema of what a role answers is fixed: `IRoleResults.SchemaOf(role)`. The provider of the role's model is sent it with the call, and the answer is validated against the same document.

| Role | Fields |
|---|---|
| Market Analyst | `bias`: `bullish`, `bearish`, or `neutral`. `keyLevels`: a list of `price` and `kind`, which is `support` or `resistance`. `volatility`: `low`, `normal`, or `high`. `rationale`. |
| Sentiment Analyst | `bias`. `notableEvents`: a list of texts. `confidence`. `liveDataAvailable`. `rationale`. |
| Strategist | `action`: `open_long`, `open_short`, `hold`, `close`, `reverse`, or `adjust_protection`. `entryType`: `market`, `limit`, or null. `entryPrice`, `stopLoss`, `takeProfit`, `riskFraction`: a number or null. `rationale`. |
| Risk Manager | `verdict`: `approve`, `reduce`, or `veto`. `riskFraction`: a number or null. `rationale`, which is its reasons. |

- **They are the structured results of the specification** (SRS 4.5), each with the rationale that every role returns. `RoleSchemaTests` holds them to it.
- **A schema stays inside what all four providers take:** every object lists all of its properties in `required` and sets `additionalProperties` to `false`, a value that may be absent is null, and nothing says a range. See "Result schemas" in [ai-providers.md](ai-providers.md).
- **No schema has a place for a quantity or a leverage.** Models choose neither (FR-RSK-5).
- **The instructions of a role name every field of its schema,** with what it means and what it may be: see "The templates" in [prompts.md](prompts.md). `RoleSchemaTests` holds the two to each other.
- A schema is one line in the source file, which is the same in every checkout.

## Validating a result

```csharp
public interface IRoleResults
{
    string SchemaOf(Role role);

    RoleResult<MarketReport> ReadMarketReport(string answered);

    RoleResult<SentimentRead> ReadSentiment(string answered);

    RoleResult<TradePlan> ReadPlan(string answered, AgentContext agent);

    RoleResult<RiskReview> ReadReview(string answered, TradePlan plan);
}

public sealed record RoleResult<T>
{
    public T? Value { get; }
    public string? Json { get; }
    public RoleRejection? Rejection { get; }
    public bool IsValid { get; }
}

public sealed record RoleRejection(RejectionRule Rule, string Field);
```

**An answer is valid when all of these hold.** Otherwise it is rejected:

| Check | What is asked |
|---|---|
| **The schema** | `SchemaCheck` checks the answer against the document that was sent: every field is there, once, with a value of its type and, where the schema lists values, one of them, and no field is there that the schema does not define. |
| **The ranges** | What a schema cannot say: see "The ranges". |
| **What a plan may do** | The plan of the Strategist is held to the market and the position of its agent: see "What a plan may do". |
| **What a prompt never holds** | A report or a plan that holds a secret, an email address, or an identifier is rejected, as `PromptScreen` finds it: a later role reads it in its prompt (FR-PRM-9). It is asked of the text as it is placed, so an escape of the answer is the character that it stands for. |

| Rule | Detail |
|---|---|
| **The answer is checked against the very schema that was sent.** | `SchemaCheck` knows `type`, also as a list of types, `enum`, `properties`, `required`, `additionalProperties` that is `false`, and `items`. A schema that says anything else throws: nothing that a schema says goes unchecked. |
| **A field that is there twice is rejected.** | A parser keeps the last of the two, and a prompt would hold both. |
| **A rejection is the service's own words.** | `RoleRejection` is a rule and a field of the fixed schema, such as `keyLevels[1].price`, or `answer` for the answer as a whole. It is stored and logged as `confidence: OutOfRange`. A field that the schema does not define is never named, and no value is repeated: what a model wrote is in no log event. |
| **Only the validation makes a result.** | `MarketReport`, `SentimentRead`, `TradePlan`, and `RiskReview` have no public constructor. `TeamRulesTests` finds every type of the application that makes one, and fails for one that is not `RoleResults`. So whatever holds a plan holds an answer that was validated. |
| **A rejected answer is rejected whole.** | Nothing of it is taken out, repaired, or clamped. The role is called once more. |
| **A link to a source can reject a report.** | An address of a page may hold an identifier. The instructions of the Sentiment Analyst ask for sources in words, with no link, no address, and no identifier. |
| **The review of the Risk Manager is not asked what it holds.** | No prompt reads it. A review that holds an address is valid, and is not kept. |

### The ranges

The ranges are fixed, in `RoleRanges`, and are no settings.

| Value | Allowed |
|---|---|
| The answer as a whole | At most 16 000 characters. |
| `rationale` | Not empty, and at most 2 000 characters. The instructions ask for 60 or 80 words. |
| `keyLevels` | At most 12. |
| `notableEvents` | At most 10, each not empty and at most 500 characters. |
| `confidence` | From 0 to 1. |
| A price | Above zero, and a number that an exact decimal can be. |
| `riskFraction` of a plan | Above zero, and at most the agent's risk per trade, as a fraction: 0.02 for an agent that risks 2 percent. |
| `riskFraction` of a review | Above zero, and below the one of the plan. |

- **The risk fraction is a share of the agent's equity** (FR-RSK-5): what is lost if the stop-loss is hit. A plan that asks for more than its agent allows is rejected, and is not cut down to what is allowed. A fraction that was written as a percentage, such as `2`, is rejected in the same way. The Risk Guard computes the quantity of the order from the fraction, and holds it to the agent's risk per trade once more as it does: see "The size of an order" in [risk-guard.md](risk-guard.md).
- **The limit is that of the agent of the cycle,** as its prompt stated it: `DataPackage.Agent`, which `ReadPlan` is handed, has the limits.

**A plan has the fields that its action needs, and no others:**

| Action | `entryType` | `entryPrice` | `stopLoss`, `takeProfit` | `riskFraction` |
|---|---|---|---|---|
| `open_long`, `open_short`, `reverse` | `market` or `limit` | The price of a limit entry, and null for a market entry | Both | A fraction |
| `adjust_protection` | null | null | One of them, or both | null |
| `hold`, `close` | null | null | null | null |

- **So a plan that opens a position always includes a stop-loss price and a take-profit price** (FR-CYC-7). One that lacks either is rejected, as `stopLoss: Required` or `takeProfit: Required`, and a price is above zero.
- **A review names a fraction to reduce a plan, and for nothing else.** An approval and a veto with a fraction are rejected, and so is a reduction with none.
- On which side of the entry a stop-loss stands is not asked here. The Risk Guard asks it, with the price: see "The rules" in [risk-guard.md](risk-guard.md).

### What a plan may do

**A plan is held to the agent as the prompt of the Strategist stated it:** its market and its open position, which are in `DataPackage.Agent`. A plan that breaks one of these rules is rejected, like an answer outside a range:

| Rule | A plan is rejected when | Rejection |
|---|---|---|
| **The action is one that the market allows** (FR-CYC-4). | It opens short or reverses on Spot. | `action: NotOnMarket` |
| **An agent holds at most one position, and adds to none** (FR-CYC-5). | It opens long or opens short, and the agent has a position. | `action: PositionOpen` |
| What closes, reverses, or adjusts needs a position. | It closes, reverses, or adjusts protection, and the agent has none. | `action: NoPosition` |
| **Adjust protection moves the stop-loss only in the direction that reduces risk** (FR-CYC-6). | Its stop-loss is below the one of a long position, or above the one of a short position. | `stopLoss: AddsRisk` |

| Market | With no position | With a position |
|---|---|---|
| Spot | `open_long`, `hold` | `hold`, `close`, `adjust_protection` |
| Futures | `open_long`, `open_short`, `hold` | `hold`, `close`, `reverse`, `adjust_protection` |

- **`PlanActions.AllowedOn` is the one list of the actions of a market.** The module of the market in the prompt names the same actions, and `PlanRuleTests` holds the two to each other. The schema of a plan has the actions of both markets: a schema is fixed, and is the same for every agent.
- **The way to the other side is a reverse.** A plan that opens short while the agent is long is rejected, and so is one that opens long: it would add to the position.
- **A stop-loss that stays where it is adds no risk,** and is taken. A position that has no stop-loss resting on the exchange takes any: whatever is placed reduces the risk of having none.
- **The take-profit moves in either direction** (FR-CYC-6). Nothing is asked of it.
- **The order of the rules:** the market, then the position, then the ranges of a price and of the risk fraction, then the fields of the action, then the stop-loss of an adjustment. An answer is rejected by the first that it breaks: a plan to open long with no stop-loss, which risks more than its agent allows, is rejected as `riskFraction: OutOfRange`.
- **A plan that breaks a rule is no valid result.** The Strategist is called once more, and a cycle whose Strategist breaks a rule again ends with no decision and no new entry (FR-TEAM-9). Nothing of such a plan is taken out or repaired: a short on Spot is not read as a hold.
- **The Strategist is told the rules.** Its instructions say which actions go with a position and which with none, and where a new stop-loss may stand: see "The roles" in [prompts.md](prompts.md).
- **The position is the one of the data package,** which is what the model was told. What an agent holds may change while its cycle runs: a stop-loss that is hit closes the position. So the Risk Guard asks again what the agent holds, when it checks the plan: see "The facts" in [risk-guard.md](risk-guard.md).
- An entry that rests on the exchange and has not filled is no position. An unfilled limit entry is cancelled at the next cycle (FR-BIN-S2): the cycle settles the orders of its agent before its team is run, which takes the entry off the exchange. See "An entry at a price" in [orders.md](orders.md).

## The review

`PlanReview.Of(plan, review)` is what the review of the Risk Manager does to a plan (FR-TEAM-7):

| Plan | Review | What goes on to the Risk Guard | Outcome |
|---|---|---|---|
| `hold` | None | Nothing | `Hold` |
| `close`, `adjust_protection` | None | The plan, as planned | `Unreviewed` |
| `open_long`, `open_short`, `reverse` | `approve` | The plan, as planned | `Approved` |
| | `reduce` | The plan, with the lower risk fraction, and with nothing else of it changed | `Reduced` |
| | `veto` | Nothing | `Vetoed` |
| | No valid review | Nothing | `NoDecision` |

| What the Risk Manager cannot do | Why it cannot |
|---|---|
| **Increase risk** | A reduction to a fraction that is not below the one of the plan is rejected, and no other verdict names a fraction. `PlanReview.Of` raises no risk either: it throws for a review that was read for another plan. |
| **Change direction, or widen the stop-loss** | A review is a verdict, a risk fraction, and reasons. It has no direction, no entry, no stop-loss, and no take-profit, and an answer with a field that the schema does not define is rejected. What goes on is the plan of the Strategist. |
| **Block a plan that closes a position or tightens protection** | It is not asked about one. |

- **A vetoed plan does not go on, in whole.** A reverse that is vetoed closes nothing: the position stays as it is, with its protective orders, and the Strategist plans again at the next close.
- **A veto is a decision, and no failure.** The cycle ended, and its record says `Vetoed`. The plan that was vetoed is in the record, with its entry, its stop-loss, and its take-profit (FR-EXP-5).

## The stale limit

**A decision that completes later than the stale limit for its timeframe cannot open a position. It can still close a position or tighten protection** (FR-CYC-9). `StaleLimit` is what the limit does to a decision:

```csharp
public static class StaleLimit
{
    public static bool IsStale(DateTimeOffset closedAt, DateTimeOffset completedAt, TimeSpan limit);

    public static TeamDecision Of(TradePlan plan);
}
```

| Plan | Decision | What goes on to the Risk Guard | Outcome |
|---|---|---|---|
| `hold`, `close`, `adjust_protection` | In time, or stale | As in "The review": nothing of a hold, and the plan as planned of the other two | `Hold`, `Unreviewed` |
| `open_long`, `open_short` | Stale | Nothing | `Stale` |
| `reverse` | Stale | **The close alone:** a plan to close the open position | `Stale` |

| Rule | Detail |
|---|---|
| **The limit counts from the close of the candle** that the cycle is for. | It is the one of the agent's timeframe in `Trading:StaleSeconds`, which `StaleSecondsSettings.Of` gives: in the tracked file 120 seconds for 15m, 300 for 1H, 600 for 4H, and 900 for 1D (SRS Appendix B). |
| **A decision is stale when it completes later than the limit.** | One that completes at the limit itself is in time: `StaleLimit.IsStale`. |
| **A decision completes with the last answer that it takes.** | It is asked twice. When the plan of the Strategist is read: a plan that opens a position and is stale by then is not reviewed. And when the review of the Risk Manager is read: a plan that would go on, and is stale by then, opens nothing. |
| **Only a plan that opens a position is held to it.** | Open long, open short, and reverse. A plan that holds, closes a position, or adjusts protection goes on as it would in time. An adjustment moves the stop-loss only in the direction that reduces risk, so nothing that is late adds risk. |
| **Of a reverse, the close goes on.** | A reverse closes the open position, and then opens one on the opposite side. The second half opens a position, and is dropped. The first reduces risk, and goes on as a plan to close: `StaleLimit.Of`. A reverse that is stale before it is reviewed is not reviewed, as no plan to close is. Of one that the Risk Manager approved or reduced, and that is stale by then, the close goes on as well. |
| **A veto is a veto, in time or not.** | A plan that the Risk Manager vetoed does not go on, in whole, also when the decision is stale: the outcome is `Vetoed`, and a reverse closes nothing. |
| **A plan that had no valid review ends with no decision,** in time or not. | The outcome is `NoDecision`. |
| **A stale decision is a decision, and no failure.** | The cycle ended, its record says `Stale`, and the log says once, as a warning, how late the decision was. |

- **A cycle may be stale as it starts.** A cycle starts up to 5 minutes after its close, which is later than the 120 seconds of 15m in the tracked file. Its team is called all the same: it may close a position or tighten protection.
- **The limit is asked with the clock of the application,** when the answer of a role is read. The Risk Guard asks it once more when it checks the plan, which is later: a decision that was in time for the team, and is stale by then, opens no position. See "The rules" in [risk-guard.md](risk-guard.md).

## A role call

`RoleRuns` runs one role of a team in a cycle:

1. It composes the prompt of the role, with `IPromptComposition`, from what the role is given.
2. `IRoleCalls` says what the model is called with: the model that the agent's user gave the role, the prompt, the schema, and whether the model may search live. See "Live search" in [cycles.md](cycles.md).
3. It asks for the key of the agent's user for the provider of the model, with `IKeyHandOver` of Credentials: see "Handing a key to a call" in [credentials.md](credentials.md).
4. It calls the model, through `IAiProviderGateway`.
5. It validates what the model answered, with `IRoleResults`.
6. It keeps the call for the record of the cycle.

| Rule | Detail |
|---|---|
| **The prompt is composed once.** | A call that is made once more is made with the same prompt and the same schema. |
| **The key is asked for as each call is made,** and for the next call again. | It is handed to the gateway with that call, and is kept nowhere: not in a field, not in the record, and not in a log event (FR-KEY-9). |
| **A call is made with the key of the agent's own user** (FR-KEY-1). | The user is that of the agent's row. There is no key of the service to fall back on. |
| **A role whose user has no key that can be used is not called.** | No key, a key that its provider refused, or one that does not decrypt. The role gave no result, and is not asked again. The call is recorded as `NoKey`. |
| **A key that a provider refuses is checked again.** | A call that ends as `AuthenticationFailed` starts `IKeyValidation.RevalidateAiKeyAsync`, once in a cycle for each provider. A key that the provider then refuses is marked invalid, and the agents that run with it move to Attention required (FR-KEY-3, FR-KEY-10). The check is started and not waited for, and is over when the cycle is. |
| Every event of a call says its role. | The call is made inside `Correlation(Role: ...)`, so what the gateway logs of it carries the role with the user, the agent, and the cycle (NFR-LOG-3). |

## When a role gives no valid result

**A call that times out or returns an invalid result is made once more** (FR-TEAM-9):

| How the call ended | Made once more |
|---|---|
| `TimedOut`: the provider did not answer within its time limit. | Yes |
| `InvalidResult`: the answer was no JSON object. | Yes |
| `Succeeded`, and the answer was rejected. | Yes |
| `Failed`: no connection, a limit of the provider, or a fault of it. | No |
| `AuthenticationFailed`, `ModelNotFound`: the same key and the same model would be refused again. | No |
| `NoKey`: no call was made. | No |

A role is called twice at most: `RoleRuns.Attempts`. **What becomes of the cycle when a role gave no valid result:**

| Role | The cycle |
|---|---|
| Sentiment Analyst | **Continues with sentiment marked unavailable.** The Strategist reads `Sentiment report: unavailable`, with no report under it, and its instructions say what that means: `SentimentReport.Unavailable`. |
| Market Analyst, Strategist | **Ends with no decision and no new entry.** No later role is called. |
| Risk Manager | **Ends with no decision and no new entry.** It is asked only about a plan that opens a position, so a review that fails blocks no close. |

- **A cycle with no decision has ended, and has not failed.** `RunAsync` returns, the job counts the cycle as ended, and the record says `NoDecision`. It is a warning in the log, and no error: a provider that does not answer is no fault of the service.
- An open position stays as it is, with its protective orders, which rest on the exchange.

## The outcome of a cycle

`CycleOutcome` is how a cycle ended. It is stored by its name.

| Outcome | When |
|---|---|
| `Hold` | The plan is to hold. |
| `Unreviewed` | The plan closes a position or adjusts protection, and goes on as planned. |
| `Approved`, `Reduced`, `Vetoed` | The plan opens a position, and this is what the Risk Manager made of it. |
| `Stale` | The plan opens a position, and the decision completed later than the stale limit of the agent's timeframe: no position is opened. Of a reverse, the close goes on. |
| `NoDecision` | A role other than the Sentiment Analyst gave no valid result. A plan that does what a plan may not do is none. |
| `Failed` | An error ended the cycle: its data package could not be built, its team is not whole, a prompt was refused, or its record could not be stored. |
| `Stopped` | The cycle was stopped: with the application, or because the next close was near. |
| `Halted` | A loss limit of the agent was reached as the cycle began: the agent is halted, and its position is closed (FR-RSK-8). Its team was not called, and decided nothing. See "Halting an agent" in [risk-guard.md](risk-guard.md). |

- **What goes on, goes on to the Risk Guard,** which checks it against the fixed rules (FR-RSK-3). **The outcome of a cycle stays the decision of its team:** a plan that the Risk Manager approved ends its cycle as `Approved`, also when the Risk Guard rejected it. What the Risk Guard made of the plan is in the record beside it: see [risk-guard.md](risk-guard.md).
- **The outcome stays the decision of the team whatever becomes of the orders** as well. What was placed is in the rows of the orders, under the id of the cycle: see "The record of an order" in [orders.md](orders.md). Why no entry was placed for a plan that the Risk Guard accepted is in the record of the cycle, beside its outcome: see "The record of a cycle". A cycle that is stopped once its team has decided keeps the decision, and one whose orders fail with an error is recorded with it too.
- An error that ends a cycle leaves `RunAsync`, as before, and the job logs it once and counts the cycle as failed: see "What a cycle is handed" in [cycles.md](cycles.md).

## The record of a cycle

**Each cycle is stored as a complete record: data package summary, every role output, Risk Guard results, orders, and final outcome** (FR-CYC-10). **Each role call records the provider, model, prompt template version, latency, token usage, structured result, and validation outcome** (FR-TEAM-8).

| Part of the record | Where it is |
|---|---|
| The data package summary | `decision_cycles.data_package`: see "The summary of the data package". |
| Every role output | `role_outputs`, a row for each call. |
| The Risk Guard results | `risk_checks` and `risk_check_rules`: see "The record of a check" in [risk-guard.md](risk-guard.md). |
| The orders | `orders`, under the id of the cycle: see "The record of an order" in [orders.md](orders.md). Why no entry was placed is `decision_cycles.no_entry`. |
| The final outcome | `decision_cycles.outcome`: see "The outcome of a cycle". |

```csharp
public interface ICycleRecords
{
    Task StoreAsync(
        DecisionCycle cycle,
        IReadOnlyList<RoleOutput> outputs,
        IReadOnlyList<RiskCheckRecord> checks,
        CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **A cycle is recorded once, when it ends, however it ends.** | With a decision, with none, with an error, or stopped. The cycle, the calls of its roles, and the checks of the Risk Guard are stored in one transaction: all of them or none. The checks are in `risk_checks` and `risk_check_rules`: see "The record of a check" in [risk-guard.md](risk-guard.md). |
| **A cycle that ends with no decision or no order is still recorded, with its outcome.** | A plan that holds, a veto, a stale decision, a role that gave no valid result, an error, a stop, and a loss limit that halted the agent each leave a row of `decision_cycles` that says so, with whatever calls were made by then. `CycleRecordTests` holds the cycles to it. |
| **The summary of the data package is kept from the moment that the package is built.** | `TeamCycle` hands it to what the cycle keeps before any role is called, so a cycle that ends later with no decision, with an error, or stopped is recorded with it. A cycle that ends before its package is built has none: one whose market data the exchange did not give, one whose team is not whole, and one whose agent a loss limit halted. |
| **The orders of a cycle are rows of their own, which name the cycle.** | An order is written before it is sent, which is before its cycle is recorded, so `orders.cycle_id` points at no row. The record of a cycle is read with the orders that carry its id. |
| **Why no entry was placed is kept with the cycle.** | `IPlanOrders.PlaceAsync` answers with a member of `NoEntryReason` when a plan to open a position that the Risk Guard accepted led to no entry, and `TeamCycle` records it as `no_entry`. See "When no entry is placed" in [orders.md](orders.md). The outcome of the cycle stays the decision of its team. |
| **A call that is made once more is a row of its own.** | `attempt` is 1 or 2. |
| **A call that was under way when the cycle was stopped is recorded,** as `Canceled`. | The provider may have charged for it. |
| **The write has a time limit of its own:** 5 seconds, `TeamCycle.RecordsWithin`. | A cycle that is stopped has its token canceled, and the run that started it waits for it. |
| **A cycle whose record cannot be stored has failed.** | Unless it is ending with an error of its own, or is being stopped: that is then what the job is told, and the log says beside it that the record could not be stored. |
| A cycle that never started leaves no row. | One that was skipped, or whose agent no longer ran. See "Not built yet" in [cycles.md](cycles.md). |

| Table | Columns |
|---|---|
| `decision_cycles` | `id`, `agent_id`, `user_id`, `mode`, `closed_at`, `started_at`, `ended_at`, `outcome`, `data_package`, `no_entry`. Indexed by `(agent_id, user_id, closed_at)`. |
| `role_outputs` | `id`, `cycle_id`, `user_id`, `role`, `attempt`, `model_id`, `role_template_id`, `market_template_id`, `timeframe_template_id`, `profile_template_id`, `called_at`, `latency_ms`, `input_tokens`, `output_tokens`, `live_searches`, `outcome`, `validation`, `rejection`, `result`. One row for each `(cycle_id, user_id, role, attempt)`. |

| Column | What it holds |
|---|---|
| `decision_cycles.id` | The id of the cycle, which what starts the cycles assigned, and which everything logged for the cycle carries. |
| `decision_cycles.mode` | The mode that the agent was in, as the name of a member of `TradingMode`. It stays as it was written when the agent is switched (FR-BIN-11). |
| `decision_cycles.outcome` | The name of a member of `CycleOutcome`. |
| `decision_cycles.data_package` | The summary of the data package of the cycle, as one line of JSON that the service wrote. Null for a cycle that ended before its package was built, and for one that was recorded before a summary was kept. |
| `decision_cycles.no_entry` | Why no entry was placed for a plan to open a position that the Risk Guard accepted, as the name of a member of `NoEntryReason`. Null for a cycle that placed its entry, and for one whose plan opened nothing or did not go on. |
| `role_outputs.model_id` | The model of the catalog that was called. A model is of one provider and is never deleted, so the row says the provider and the model for good. |
| The four `_template_id` | The versions of the templates that the prompt of the call was composed from: the instructions of the role, and the modules of the market, the timeframe, and the profile. A version is never changed (FR-PRM-6). See "What a role output records" in [prompts.md](prompts.md). |
| `latency_ms` | How long the call took, as the gateway measured it, in whole milliseconds. |
| `input_tokens`, `output_tokens` | What the provider counted, or null when it did not say: a call that timed out or failed has none. |
| `live_searches` | How many searches the provider ran, for a call that offered the model a search (FR-CYC-3). Null for a call that offered none. |
| `outcome` | How the call ended, as the name of a member of `RoleCallOutcome`: the six outcomes of the gateway, `NoKey`, or `Canceled`. |
| `validation` | `Valid`, `Rejected`, or `NoResult` for a call that gave no answer. |
| `rejection` | Why an answer was rejected, as `RoleRejection` writes it. |
| `result` | What the model answered, on one line of JSON, with its rationale. An answer that was rejected is kept as well. Null for a call that gave no answer, and for an answer that is no JSON, is too long, or holds what a prompt never holds. |

- **The rows are their user's:** `DecisionCycle` and `RoleOutput` implement `IUserOwned`. The Decision Engine may not use Accounts, so a row carries its owner along to the row that it hangs on: `(agent_id, user_id)` points at `agents (id, user_id)`, and `(cycle_id, user_id)` at `decision_cycles (id, user_id)`. The database holds the owners to being the same. See [ownership.md](ownership.md).
- **The record of a cycle goes with its agent,** and with the account: an agent is deleted only while it is a draft, or is stopped with nothing open.
- **They hold no prompt, no key, and no personal data.** A model is sent nothing that says who the user is (FR-PRM-9), and the summary of a data package holds what a prompt holds. `PersonalDataRules` lists `data_package`, `rejection`, and `result` with what they hold.
- **They are not in the export of a user's data.** A user exports their trades and decisions as CSV (FR-RPT-4), which is not built. `ExportRules` lists both tables with that.
- A table of role outputs points at the versions of the templates: `PromptRulesTests` holds every such table to it.

The migration `AddCycleRecord` adds `data_package` and `no_entry`, and the index of `orders` that finds the orders of a cycle. It gave the cycles that were stored then neither: nothing kept a summary before it, and why no entry was placed was in the log alone.

### The summary of the data package

```csharp
public sealed record DataPackageSummary(
    DateTimeOffset BuiltAt, Market Market, Timeframe Timeframe, string Symbol, string QuoteAsset, decimal Equity,
    PositionContext? Position, AgentRisk Limits, IReadOnlyList<SeriesSummary> Series, IReadOnlyList<OpenOrder> Orders, FuturesData? Futures)
{
    public static DataPackageSummary Of(DataPackage package, DateTimeOffset builtAt);
    public string ToJson();
    public static DataPackageSummary? Read(string? stored);
}

public sealed record SeriesSummary(string Timeframe, int Candles, int ComputedFrom, CandleSummary? Latest, IndicatorValues Indicators);

public sealed record CandleSummary(DateTimeOffset ClosedAt, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
```

| Rule | Detail |
|---|---|
| **It is what the team decided on, without the candles.** | The agent as its prompt stated it: its market, its timeframe, its symbol, its equity, its position, and its limits. For the agent's own timeframe and for each higher one: how many candles the package held, how many the indicators were computed from, the latest candle, and the indicators at its close. The orders that rested on the exchange, and on Futures the mark price, the funding, and the open interest. And when the package was built. |
| **It keeps one candle of each series, and no more.** | The database is not a market-data warehouse (NFR-DAT-6). A package of 200 candles is a summary of a few hundred characters for each series. |
| **It is one line of JSON, which the service writes.** | A number has every digit that it has, a time is in UTC, and a member of a fixed set is written by its name. No model wrote any of it. |
| **It says nothing of whose agent it is** (FR-PRM-9). | It has no id and no name, as a data package has none. |
| **What does not read as a summary is none.** | `Read` answers null for a row that keeps none, and for text that nothing of the application wrote. The timeline then says that the cycle kept no summary. |
| It is made of values alone. | A series is named by the code of its length, `1H` or `1W`, and not by a type of the Exchange Gateway, so what runs the team keeps it and uses nothing of the exchange: see "What a model cannot do". |

## The timeline

**The user can view a cycle as a timeline** (FR-CYC-10). Two pages show the cycles of an agent to its user, and `IRecordedCycles` reads them:

```csharp
public interface IRecordedCycles
{
    Task<CycleHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    Task<CycleRecord?> FindAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken);
}

public sealed record CycleRecord(
    Guid Id, DateTimeOffset ClosedAt, TradingMode Mode, DateTimeOffset StartedAt, DateTimeOffset EndedAt, CycleOutcome Outcome,
    DataPackageSummary? DataPackage, IReadOnlyList<RoleCallView> Calls, IReadOnlyList<RiskCheckView> Checks,
    IReadOnlyList<CycleOrderView> Orders, NoEntryReason? NoEntry);
```

| Page | What it is |
|---|---|
| `/agents/<id>/cycles` | The cycles of the agent, latest close first: for each the close of its candle, the mode that the agent was in, its outcome in words with what that means, how long it ran, and the way to its timeline. |
| `/agents/<id>/cycles/<cycle>` | One cycle as a timeline: its entries one under another, in the order they happened, each with its time to the second and how long after the close of the candle that was. |

**The entries of a timeline:**

| Entry | What it says |
|---|---|
| The candle closed | Which candle the cycle was for. |
| The cycle started | When its turn came. |
| Data package | The summary: what the agent was and held, its limits, a table with the latest candle and the indicators of each timeframe, the open orders, and what Futures adds. A cycle that kept none says why. |
| A role, once for each call | The model and its provider, how the call ended, the structured result field by field with its rationale, how long it took, the tokens, the live searches, and the versions of the four templates of its prompt. A call that was made once more is an entry of its own. |
| Risk Guard, once for each check | The verdict, the size that was computed, and every rule with its outcome and why, as on the page of the risk checks: see "The page" in [risk-guard.md](risk-guard.md). |
| An order, once for each | What it does, what has become of it by now, how much of it is executed, and the identifiers that it carries at Binance. |
| No order | For a cycle that placed none: that nothing of a plan went on, that the Risk Guard rejected the plan, or why no entry was placed. |
| The cycle ended | The outcome, with what it means, and how long the cycle ran. |

| Rule | Detail |
|---|---|
| **The entries stand in the order they happened.** | By their times. What happened at the same moment stands in the order of a cycle: the package, the calls, the checks, and the orders. The two analysts are called at the same moment, and stand in the order of the roles. |
| **A cycle that ended with no decision or no order is shown like any other,** with its outcome. | The list has every cycle that was recorded, whatever its outcome. Its timeline has the entries of what happened, and says where a package or an order would stand why there is none. Such an entry says no time of its own. |
| **Everything is explained in plain language** (NFR-USA-2). | `CyclePages` has a word or two and a sentence for every `CycleOutcome`, every way that a call ends, every `RejectionRule`, every `NoEntryReason`, and every status of an order. An outcome is said as words, and does not rely on a colour. |
| **A structured result is shown field by field.** | A field of the schema of a role is named in words: `riskFraction` is "Risk fraction", and is said as a percentage of the agent's equity beside the number. A rejected answer is shown as well, with why it was rejected: it may hold a field that no schema defines, which is shown under the name that the model gave it. |
| **What a model wrote is text on the page.** | A rationale, an event of a sentiment report, and a field of a rejected answer are written as text, like everything else on a page, and are never markup of it. A report that a search fed is untrusted content (FR-PRM-8). |
| **The mark of a sentiment report is the service's** (FR-CYC-3). | The entry of a valid report says whether it counts as resting on live data, from the searches that its call ran, beside what the model itself says. |
| **A call says the versions of its templates, and not their texts.** | By kind, subject, and number: a version is never changed (FR-PRM-6). The page of the prompts shows the texts that an agent uses now. |
| **An order is shown as it is now.** | Its row changes after its cycle ended: a stop-loss that rested is executed later. An order that was placed for the position by what settles the orders names no cycle, and is on no timeline. |
| **The latest 50 cycles are listed.** | `RecordedCycles.Shown`. The page says so when there are more. |
| **A cycle says the mode it ran in,** as a badge (FR-BIN-11). | The badge of the mode that the agent is in now is under the heading, as on every page of an agent (FR-UI-3). |
| **The cycles of an agent of another user are not found.** | The pages ask `IStoredAgents` for the agent of the user who is signed in, and answer "not found" as for an agent that does not exist. `IRecordedCycles` asks for every part of a record by owner as well, and a cycle of another agent of the same user is not found under this one. |
| The Decision Engine may not use Accounts. | So a page hands `IRecordedCycles` the user who is signed in, by id. |
| **The parts of a record are read as they were at one moment.** | They are rows of four tables, and an order of the cycle may be written to meanwhile: `FindAsync` reads in a snapshot. See "Reads that agree" in [persistence.md](persistence.md). |
| **The pages have no form.** | A user reads the record of a cycle, and changes none. |

The page of the state of an agent leads to the list, and so does the page of its risk checks, which leads from each check to the timeline of its cycle.

## What a model cannot do

**Models cannot place orders or call internal functions** (NFR-SEC-6). `TeamRulesTests` and `TeamRecordTests` hold the code to it:

| What holds it | How |
|---|---|
| **A model is offered nothing that it could call.** | A call is made with a model, a prompt, a schema, and whether the model may search live: `AiCallRequest` has no place for a function or a tool, which is pinned. Across a cycle, no request to a provider offers a tool but the live search of the Sentiment Analyst's own provider, which reads the web and nothing of the service. |
| **What a model answers is read as data.** | Into a result with the fields of its schema and no others. An answer that asks for a function to be called is no JSON object, so it is no result: the role is called once more, and nothing is called for it. |
| **A plan is no order.** | It has no quantity and no leverage. What goes on of a plan goes on to the Risk Guard, which checks every limit, and is where both are computed (FR-RSK-3, FR-RSK-5). |
| **What runs the team uses nothing of the exchange.** | `TeamCycle`, `RoleRuns`, `RoleResults`, `SchemaCheck`, `PlanReview`, and `StaleLimit` reference no type of the Exchange Gateway. Operations that place orders belong in `Ecnanib.ExchangeGateway.Trading`: see "Placing orders" in [architecture.md](architecture.md). |
| **Only the Decision Engine is handed a stored key,** and only it calls a model. | `IKeyHandOver` is asked by `RoleRuns` alone. See "What a provider is sent" in [prompts.md](prompts.md). |

## The log

`TeamCycle` logs under the area `Ecnanib.DecisionEngine.TeamCycle`, and `RoleRuns` under `Ecnanib.DecisionEngine.RoleRuns`. Both write inside a cycle, so every event carries `UserId`, `AgentId`, and `CycleId`, and an event of a role call carries `Role`.

```
The team of agent {AgentId} decided for the close at {ClosedAt}: {CycleOutcome}, for a plan to {PlanAction}
The cycle of agent {AgentId} for the close at {ClosedAt} ended with no decision and no new entry: the {Role} gave no valid result
The decision of agent {AgentId} for the close at {ClosedAt} is stale: it completed {CompletedAfterSeconds} s after the close, and the stale limit of {Timeframe} is {StaleSeconds} s. Its plan to {PlanAction} opens no position, and what goes on is {GoesOn}
The cycle of agent {AgentId} for the close at {ClosedAt} continues with sentiment marked unavailable
The cycle of agent {AgentId} for the close at {ClosedAt} ended before its team was called: a loss limit halted the agent
The record of the cycle of agent {AgentId} for the close at {ClosedAt} could not be stored ({CycleOutcome})
The {Role} of agent {AgentId} gave no valid result for the close at {ClosedAt}
The result of the {Role} of agent {AgentId} was rejected on attempt {Attempt}: {RejectedField} is {RejectionRule}
The {Role} of agent {AgentId} is not called: user {UserId} has no key for the AI provider {Provider} that can be used ({KeyHandOverOutcome})
The AI provider {Provider} refused the key of user {UserId} in a call, so the key was checked again: {KeyCheckOutcome}
The key of user {UserId} for the AI provider {Provider} could not be checked again after a call that it failed
```

| Level | Event |
|---|---|
| `Information` | The decision of the team, and a key that was checked again. |
| `Warning` | A result that was rejected, a role that gave no valid result, a role that is not called, sentiment that is unavailable, a cycle with no decision, a decision that is stale, and a cycle that ended because a loss limit halted its agent. |
| `Error` | A record that could not be stored, and a key that could not be checked again, each with the error. |

An event names a field of a schema and a rule, an action, and an outcome, and a stale decision says how late it was and what of its plan goes on: `Close`, or `nothing`. It never holds a price, a fraction, or anything that a model wrote. What the gateway logs of each call is in "AI calls" in [logging.md](logging.md).

## Testing

`CyclesApplication` runs the job with the application's own inside of a cycle when a test says that its cycles build packages. The team is then behind stand-ins, for agents that need no rows:

| Stand-in | In place of | What it does |
|---|---|---|
| `StandInModels` | `IAiProviderGateway` | Answers each role with a valid result of its schema, unless the test says what a call of a role is answered with. It keeps every call in the order the calls came in, and holds the calls of a role until the test lets them go. |
| `StandInKeys` | `IKeyHandOver` | Hands every user a key of their own for every provider, unless the test says otherwise. |
| `StandInCycleRecords` | `ICycleRecords` | Keeps the records in the order they were stored, each with the checks of the Risk Guard that it came with, and fails when the test says so. |
| `StandInKeyChecks` | `IKeyValidation` | Keeps whose key it was asked to check again. |
| `StandInPlanOrders` | `IPlanOrders` | Places nothing, and keeps what it was handed: the results of the Risk Guard that were to be placed, and the agents whose orders were to be settled. |

- **An order is held to through what a call was made with,** and not through waiting for something not to happen: the prompt of the Strategist holds what both analysts answered, and the prompt of the Risk Manager the plan.
- `TeamRecordTests` runs cycles of agents of the database through the gateway itself, with the stored keys of their users, and with a stand-in for the providers: `CyclesApplication` with `providers`.
- `TeamAnswers` has a valid answer of each role, and `StoredCycleRows.GiveCycleAsync` gives an agent the record of a cycle without one that runs: with the summary of a data package, which `CycleSummaries.Of` makes, a call of each role, a check, and its orders, unless the test says that it placed none.
- `StandInPlanOrders.NoEntry` is what placing the orders of a plan answers, for a cycle whose entry is skipped.
- **A plan is read for an agent.** `PlanAgents.On` is an agent on a market, with the position that it holds or with none, and `PlanAgents.For` is one that may have the plan of an answer, whatever its action. A cycle whose agent holds a position is given `PlanAgents.Holding`, which is a `StandInHoldings`: see "Testing" in [cycles.md](cycles.md).
- **A decision is made stale by moving the clock while a call is held.** `StandInModels.Hold` holds the call of the Strategist or of the Risk Manager, the test sets the clock to when the role answers, and lets the call go. The limits are those of the file of the tests: 100 seconds for 15m, 200 for 1H, 400 for 4H, and 800 for 1D.
- `LiveProviderTests` sends the schema of each role to the providers themselves, and reads what they answer with `IRoleResults`. It is skipped unless a key and a model are given: see "Checking against the real providers" in [ai-providers.md](ai-providers.md).

## Not built yet

- Leaving out the calls of a cycle that can decide nothing: one that is stale as it starts, for an agent that holds no position. Its team is called, and what it plans ends as `Hold` or `Stale`.
- Recalling lessons for the Strategist and the Risk Manager (FR-EXP-7). Both are given none.
- Moving an agent to Attention required when a call reports that its model does not exist (FR-CAT-8). The call is recorded as `ModelNotFound`, and the cycle ends as its role says. The catalog marks a model that its provider no longer lists, which moves the agents that run with it: see "When a model goes" in [agents.md](agents.md).
- Breakers for a provider or a key that fails repeatedly (NFR-REL-4). A call that ends as `Failed` is not made again, and the next close calls again.
- Recording a cycle that never started: one that was skipped, or whose agent no longer ran. It is no run of a team, and leaves its events in the log: see "Not built yet" in [cycles.md](cycles.md).
- The decision timeline on the agent detail screen (FR-RPT-2), a timeline that follows a cycle while it runs (FR-NTF-2), and the cycles before the latest 50. A cycle is shown once it is recorded, on a page of its own.
- Why a plan that closes a position or adjusts its protection placed no order, in the record of its cycle. The record says why no entry was placed, and the log says the rest.
- Exporting decisions as CSV (FR-RPT-4), and showing AI usage from the recorded tokens (FR-RPT-3, FR-KEY-11).
- Retention of the records: decision records are kept 365 days and role transcripts 90 (NFR-DAT-5). `Retention` has the settings, and nothing deletes yet.
- Telling a model why its first answer was rejected. The call that is made once more has the same prompt.
- A test cycle that a user runs for an agent (FR-CYC-12).
- Sending the schemas to the real providers in the suite. `LiveProviderTests` does when it is given keys.
