# The Risk Guard

The Risk Guard is a set of fixed rules applied after the Risk Manager and before any order. Neither AI output nor a user action can bypass it for a trade (FR-RSK-3).

The Risk Guard rejects a plan to open a position unless all of these hold: a stop-loss is present and on the correct side of entry; the reward-to-risk ratio meets the minimum; the entry price is within the allowed distance of the current price; the order satisfies the exchange's rules for price step, quantity step, and minimum value; the daily loss limit and drawdown limit are not reached; the decision is not stale (FR-CYC-9); the agent's mode, market, and symbol are permitted; and no global halt is active. A plan that only closes a position or adjusts protection is checked against the exchange's rules and FR-CYC-6 only, so that risk can always be reduced (FR-RSK-4). On Futures, the estimated liquidation price must be further from entry than the stop-loss by at least the liquidation buffer factor, or the plan is rejected (FR-RSK-6).

Models do not choose quantity or leverage. The system computes quantity from the approved risk fraction, the agent's equity, and the stop distance. On Spot, position value is capped at the agent's equity. On Futures, the system uses the lowest leverage setting that fits the position's margin within the agent's equity, up to the leverage cap, and reduces quantity if the cap would be exceeded (FR-RSK-5).

Each Risk Guard check stores the outcome of every rule, so a user can see exactly why a plan was accepted or rejected (FR-RSK-9).

What is built is step 6 of the decision cycle (SRS 3.2): the rules, what computes the size of an order, what gathers the facts that both are asked of, the record of each check, and the page that shows a user the checks of an agent. What the Risk Guard accepted is handed to what places the orders: see [orders.md](orders.md).

The rules and the sizing are in `src/Ecnanib/RiskGuard`, as `GuardCheck` and `OrderSizing`, and what asks them for a cycle in `src/Ecnanib/DecisionEngine`: `PlanChecks`, `RiskCheck`, and `IRiskCheckRecords`. The page is in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by tests in `tests/Ecnanib.Tests/RiskGuard`, and in `RiskGuardCycleTests`, `RiskCheckRecordTests`, and `RiskCheckPageTests` of `tests/Ecnanib.Tests/DecisionEngine`.

What the team of an agent decides, and what goes on of its plan, is in [team.md](team.md). What an agent holds is in [cycles.md](cycles.md), the price and the rules of a symbol in [exchange.md](exchange.md), the risk settings of an agent in [agents.md](agents.md), and whether a Binance key lets an agent trade in [credentials.md](credentials.md).

## Where it stands in a cycle

`TeamCycle` hands what goes on of the plan of a cycle to `PlanChecks`, when the team has decided. See "The review" and "The stale limit" in [team.md](team.md) for what goes on.

| The team decided | What is checked |
|---|---|
| `Hold`, `Vetoed`, `NoDecision` | Nothing: nothing goes on. |
| `Stale`, for a plan that opens long or short | Nothing: nothing goes on. |
| `Approved`, `Reduced` | The plan, with the risk fraction that the Risk Manager left it. |
| `Unreviewed` | The plan to close a position, or to adjust protection. |
| `Stale`, for a plan that reverses | The close alone: a plan to close the position. |

| Rule | Detail |
|---|---|
| **The Risk Guard is rules only.** | `Ecnanib.RiskGuard` may use `Shared` and `Platform`, and no other component: see [architecture.md](architecture.md). `GuardCheck.Of` is one function of a `GuardCase`, which is the plan and the facts. It reads nothing, stores nothing, and has no service. |
| **The Decision Engine gathers the facts.** | `PlanChecks`, as the plan is checked: see "The facts". |
| **The Risk Guard computes the size of the order.** | As it checks a plan that opens a position, from the plan and the facts: see "The size of an order". Nothing hands it one. |
| **Every plan that goes on is checked.** | `TeamCycle` has one place where a plan goes on, and hands it over there. A plan that does not go on goes nowhere. |
| **It is after the Risk Manager.** | No fact is read while a role is called: the facts are as they are when the last answer of the team has been read. |
| **The outcome of a cycle stays the decision of its team.** | `CycleOutcome` says `Approved` for a plan that the Risk Manager approved, also when the Risk Guard rejected it. What the Risk Guard made of it is in the record beside it: see "The record of a check". |
| **It makes no model call.** | Neither the rules nor `PlanChecks` uses anything of the AI Provider Gateway: `RiskGuardRulesTests` holds both to it. A cycle is the four calls of its team, with a check or without. |
| **It places nothing.** | `PlanChecks.CheckAsync` answers with what the Risk Guard accepted, which is what an order is made from. `TeamCycle` hands it to `IPlanOrders`, and hands it nothing else: see "Where it stands in a cycle" in [orders.md](orders.md). |

## The rules

`GuardRule` names them, each for what must hold. **Every rule is asked in every check, and none is skipped after one that failed:** a check says the outcome of each.

| Rule | It holds when | `measured` | `threshold` |
|---|---|---|---|
| `StopLossSide` | A stop-loss is present, and stands below the entry of a long position, or above the entry of a short one. One that stands at the entry does not. | The stop-loss | The entry |
| `RewardToRisk` | The distance from the entry to the take-profit is at least the agent's minimum times the distance from the entry to the stop-loss. Both are counted in the direction of the trade. | The ratio | The agent's minimum |
| `EntryDistance` | The entry is within the agent's maximum distance of the current price, in percent of the current price. | The distance, in percent | The agent's maximum |
| `PriceStep` | Every price that the plan names is a multiple of the price step of the symbol. | The price that is not | The step |
| `QuantityStep` | The quantity of every order is a multiple of the quantity step of the symbol. | The quantity that is not | The step |
| `MinimumQuantity` | The entry is for at least the least quantity of an order in the symbol. | The quantity | The least quantity |
| `MinimumValue` | The entry is worth at least the least value of an order in the symbol: its quantity times its price. | The value | The least value |
| `HeldPosition` | The plan goes with what the agent holds now: see below. | How many positions it holds | `FixedLimits.OpenPositionsPerAgent`, for a plan that opens |
| `ProtectionTightens` | An adjustment moves the stop-loss only in the direction that reduces risk (FR-CYC-6). | The new stop-loss | The one that rests |
| `DailyLossLimit` | The agent has lost less than its daily loss limit since the UTC day began, in percent of its equity then. | The loss, in percent | The agent's limit |
| `DrawdownLimit` | The agent's equity is less than its drawdown limit below its peak, in percent of the peak. | The fall, in percent | The agent's limit |
| `NotStale` | The plan is checked no later than the stale limit of the agent's timeframe after the close. | Seconds since the close | The limit, in seconds |
| `ModePermitted`, `MarketPermitted`, `SymbolPermitted` | See "Permitted". | | |
| `NoGlobalHalt` | No global halt is on. | | |
| `LiquidationBuffer` | On Futures, the estimated liquidation price is at least the agent's liquidation buffer times as far from the entry as the stop-loss. | How many times as far | The agent's buffer |

**A plan is held to the rules that go with what it does** (FR-RSK-4):

| Plan | Held to |
|---|---|
| Open long, open short, reverse | Every rule but `ProtectionTightens`. `LiquidationBuffer` on Futures only: a position on Spot is not liquidated. |
| Close | `PriceStep`, `QuantityStep`, and `HeldPosition`. |
| Adjust protection | The same three, and `ProtectionTightens`. |

- **The entry of a plan at the market is the current price,** and the entry of a limit plan its own price. So a stop-loss that the price has passed since the plan was made is on the wrong side of an entry at the market.
- **`PriceStep` is asked of the prices that a plan names:** the entry of a limit plan, the stop-loss, and the take-profit. A plan to close names none, and holds to no step.
- **`QuantityStep` is asked of the quantity of each order:** of an entry, the quantity that the check computed, which is rounded onto the step, and of what closes or protects the open position, what the position holds. A reverse has both.
- **`MinimumQuantity` and `MinimumValue` are asked of an entry, and not of a close.** Binance takes an order that only reduces a position on Futures below the least value. Whether it takes a close on Spot is the exchange's to say, with the orders. **A quantity is never raised to either:** an entry that is too small is rejected, because a larger one would risk more than was approved.
- **The quantity and the leverage that the rules are asked of are those that the check computed:** see "The size of an order". An order that comes to no quantity keeps none of `QuantityStep`, `MinimumQuantity`, and `MinimumValue`.
- **`HeldPosition` is the limit of one open position** (SRS Appendix B): a plan that opens long or short needs an agent that holds none. A plan that reverses, closes, or adjusts needs a position: there is nothing else to close or to protect. It is asked of every plan, because the rules of the exchange and FR-CYC-6 are asked of the position that the agent holds.
- **`ProtectionTightens` is FR-CYC-6, asked again of the position as it is now.** A stop-loss that stays where it is adds no risk, a position with no stop-loss resting takes any, and the take-profit moves in either direction: as where the plan is read. See "What a plan may do" in [team.md](team.md).
- The other fixed limit, a position on Spot worth at most the agent's equity, is no rule that rejects: the size of an order is held to it as it is computed, as it is held to the leverage cap on Futures. See "The size of an order".

### The verdict

`RuleOutcome` is what a check made of a rule: `Passed`, `Failed`, `Unknown`, or `NotApplied` for a rule that the plan is not held to. `GuardVerdict` is `Accepted` or `Rejected`.

| Plan | Accepted when |
|---|---|
| **One that opens a position** | **Every rule that it is held to is known to hold.** A rule whose outcome is `Unknown` rejects it, as one that failed does. |
| **One that only closes a position or adjusts protection** | **No rule that it is held to is known not to hold.** A rule whose outcome is `Unknown` stands in the way of no close: so that risk can always be reduced. |

- **`Unknown` is a fact that was not there:** a price or the rules of the symbols that the exchange did not give, the size of an order that could not be computed without them, a position to reverse that is gone. It is no error: the check answers, and the cycle ends as its team decided.
- `GuardResult.InTheWay` is the rules that stand in the way of a plan, which the log and the page say.

### How a rule counts

| Rule | Detail |
|---|---|
| **A limit is inclusive.** | A ratio at the minimum, a distance at the maximum, a value at the least value, a liquidation at the buffer, and a check at the stale limit itself all hold, as a setting at its bound is taken and a decision at the stale limit is in time. |
| **A loss limit is reached at the limit.** | The requirement is that the limit is not reached: a loss of exactly the limit fails. |
| **Nothing is divided to decide.** | A rule compares products: `reward >= minimum * risk`. So no divisor is zero, and no rounding decides. `measured` is worked out apart, to 8 decimal places. |
| **Distances are counted in the direction of the trade.** | A stop-loss on the wrong side is no risk to measure a reward against, and none to size an order by: `RewardToRisk`, the rules of a quantity, and `LiquidationBuffer` fail with `StopLossSide`. |
| **A step of zero is no rule.** | Binance writes zero where it has none. So is a least quantity, a most quantity, and a least value of zero. |
| **A number that makes no sense is a rule that does not hold.** | A price that is not above zero, a quantity of a position that is not, an order that comes to no quantity, and numbers that are beyond what can be worked out with. Nothing that a check is handed makes it throw. |

### The liquidation estimate

On Futures, a position is liquidated when the price has moved against it by its margin. With isolated margin that is the entry divided by the leverage: a long at 60000 with a leverage of 5 is liquidated near 48000, and a short near 72000. The rule holds when that distance is at least the buffer times the distance to the stop-loss.

- **It is an estimate, and leaves out the maintenance margin,** which brings the liquidation nearer to the entry by the maintenance margin rate of the position. With a leverage of 10 at most and a buffer of 1.5 at least, the rate would have to be above 3 percent to change what the rule says of a plan at its limit. Reading the rate of a symbol comes with Futures trading: see "Not built yet".
- **The leverage is that of the order, which the check computes** (FR-RSK-5): the lowest that fits the margin of the position within the agent's equity. See "The size of an order".
- **So the rule rarely decides.** A position that takes a leverage above 1 has a stop-loss near its entry: its liquidation is more than 1 / (2 × the risk fraction) times as far, which is 10 times at a risk of 5 percent. At a leverage of 1 the rule fails for a stop-loss further from the entry than the entry divided by the buffer, which is half the price at the default buffer of 2. It rejects a plan of an agent whose buffer is set far above the default.

## The size of an order

Models do not choose quantity or leverage. The system computes quantity from the approved risk fraction, the agent's equity, and the stop distance. On Spot, position value is capped at the agent's equity. On Futures, the system uses the lowest leverage setting that fits the position's margin within the agent's equity, up to the leverage cap, and reduces quantity if the cap would be exceeded (FR-RSK-5).

`OrderSizing` computes it as `GuardCheck.Of` checks a plan that opens a position: once for a check, from the plan and the facts as the rules read them. The rules of a quantity and the liquidation buffer are asked of that size, and `GuardResult.Size` says it, as an `OrderSize` of a quantity and a leverage.

| Step | Rule |
|---|---|
| **The risk** | The agent's equity times the risk fraction of the plan, as the Risk Manager left it: what is lost when the stop-loss is hit. |
| **The quantity** | The risk divided by the distance from the entry to the stop-loss, counted in the direction of the trade. The entry is the limit price of the plan, or the current price for an entry at the market. |
| **The most that the position is worth** | On Spot the agent's equity: `FixedLimits.SpotPositionPercentOfEquity`, which is 100 percent (SRS Appendix B). On Futures the equity times the agent's leverage cap, which is what the equity is the margin of at the cap. A quantity that would be worth more is reduced to what that buys. |
| **The most that the exchange takes** | A quantity above the most quantity of an order in the symbol is reduced to it. |
| **Rounding** | Down to the quantity step of the symbol, and never up: rounding adds no risk and breaks no cap. Where the symbol has no step, down to 8 decimal places. |
| **The leverage** | 1 on Spot. On Futures the lowest whole leverage at which the margin of the position, its value divided by the leverage, is within the agent's equity. It is the cap at most, because the position is worth the equity times the cap at most. |

With a price of 60000, a quantity step of 0.001, an equity of 1000, and a risk fraction of 0.01, which is a risk of 10:

| Plan | Its order comes to |
|---|---|
| A stop-loss 1000 from the entry | 10 over 1000: 0.01, worth 600. No leverage on either market. |
| On Spot, a stop-loss 200 from the entry | 0.05 would be worth 3000. The equity buys 0.0166, which is 0.016 on the step. |
| On Futures, a stop-loss 200 from the entry | 0.05, worth 3000, at a leverage of 3. |
| On Futures with a leverage cap of 4, a stop-loss 100 from the entry | 0.1 would be worth 6000, which takes a leverage of 6. Four times the equity buys 0.0666: 0.066, at a leverage of 4. |
| An equity of 950 | 9.5 over 1000 is 0.0095: 0.009. |

**A check has a size, has none, or has one of no quantity:**

| The size | When | The rules of a quantity |
|---|---|---|
| None | The plan opens nothing: a plan to close or to adjust protection, and the close of a reverse. | Asked of the position that the agent holds, or not asked. |
| None | A fact that the size follows from is not there: the current price for an entry at the market, the rules of the symbol, or the side to open, for a reverse whose position is gone. | `Unknown` |
| A quantity of zero, with a leverage of 1 | The facts are there, and give no order: a plan with no stop-loss on the losing side of its entry, a limit price that is no price, an equity that is not above zero, a quantity that comes to less than one step, and numbers that are beyond what can be worked out with. | `Failed` |
| A quantity above zero | Every other plan that opens a position. | Asked of it. |

- **The risk fraction counts as the agent's risk per trade at most.** Validation rejects a plan that asks for more, so this cuts no plan of a cycle down: see "The ranges" in [team.md](team.md). The Risk Guard holds to the limit itself, whatever was validated before it.
- **A quantity is never raised:** not to the least quantity of the exchange, and not to its least value. `MinimumQuantity` and `MinimumValue` reject an order that is too small.
- **Products decide, as they do for the rules.** A division rounds in its last digit. So the quantity that was rounded down is held to the risk and to the cap by multiplying, and is taken one step down where it does not keep them. The leverage is found by dividing, and held by multiplying in the same way.
- **The size is computed for every plan that opens a position,** also for one that a rule rejects: a check says what its rules were asked of.
- **The equity is the agent's own, as it is now:** see "The facts". Whether the account of its user holds the balance or the margin for the order is asked where the order is placed (FR-BIN-S3, FR-BIN-F6): see "The quote balance" in [orders.md](orders.md) for Spot. The margin of Futures is not asked yet.
- **The entry of a plan at the market is the current price.** The order fills where the market is then, which is for what places it.
- **The leverage is a number here, and no setting of the exchange.** Setting the leverage of the symbol before the entry (FR-BIN-F2) is not built, so an entry on Futures uses the leverage that the account has for the symbol: see "Not built yet".

## The facts

`PlanChecks` gathers them as the plan is checked, all at once, and takes the time when they have been read. **No fact is taken from the data package,** which is as old as the calls of the team, and none from what a model answered but the plan itself, which was validated.

| Fact | From | Detail |
|---|---|---|
| The current price | `IExchangeMarketData.GetPriceAsync` | The price that the symbol last traded at, on the agent's market in its environment. A read counts for a second, and is shared: see "Market data" in [exchange.md](exchange.md). |
| The rules of the symbol | `IExchangeMarketData.GetSymbolsAsync` | The price step, the quantity step, the least and the most quantity, the least value, and whether the exchange takes orders in it. A read counts for an hour. |
| What the agent holds | `IAgentHoldings.OfAsync` | Asked again: its position, with its side, its quantity, and the stop-loss that rests, its equity, its equity as the UTC day began, and its peak equity. See "The loss limits". |
| Whether the key lets it trade | `IKeyValidation.BlockForAsync` | From what the last check of the key recorded. Binance is asked nothing. See "Permitted". |
| Whether a halt is on | `IGlobalHalt.IsOnAsync` | See "The global halt". |
| The limits | The agent of the cycle | Its settings as they were stored when the turn of the cycle came: a change applies from the agent's next cycle (FR-AGT-8). The risk per trade and the leverage cap are among them, for the size of the order. |
| The stale limit | `Trading:StaleSeconds` | That of the agent's timeframe, counted from the close of the candle of the cycle. |
| The risk fraction | The plan | As the Risk Manager left it: lower than the Strategist planned where the review reduced it. See "The review" in [team.md](team.md). |

- **An exchange that gives no answer is a fact that is not known,** and a warning in the log. The gateway does not read again for 5 seconds, and the next close starts the next cycle.
- **The facts are read once for a cycle.** The two checks of a reverse that is rejected are made on the same facts, at the same time.
- **The size of the order is no fact.** Nothing gathers it, and a case has no place for it: the check computes it from the facts. See "The size of an order".
- An error that is no outcome of the exchange, such as what keeps positions failing, ends the cycle as failed, as an error of the team does.

### Permitted

**The agent's mode, market, and symbol are permitted** (FR-RSK-4):

| Rule | It holds when |
|---|---|
| `ModePermitted` | The agent's user has a valid Binance key for the environment that the agent trades in: Demo or Live. |
| `MarketPermitted` | That key may trade on the agent's market. With no valid key, no market is permitted. |
| `SymbolPermitted` | The agent's market has its symbol in that environment, among the symbols of `Binance:Symbols`, and the exchange takes orders in it now. |

- `IKeyValidation.BlockForAsync` of Credentials answers the first two, as it does when an agent is started (FR-KEY-5): `NoKey` and `NotValid` fail both, and `SpotTradingMissing` and `FuturesTradingMissing` the market. See "What an agent needs" in [credentials.md](credentials.md).
- A key that is deleted or refused while an agent runs moves the agent to Attention required, which ends its cycles: see "When a key goes" in [agents.md](agents.md). The rule is for the cycle that is under way then.
- **Whether the agent still runs is not asked here.** What places an entry asks `AgentStates.TakesNewEntries` again, under the lock of the account, as it writes the entry: see "When no entry is placed" in [orders.md](orders.md).

### The global halt

An Operator can switch a global halt on or off. While it is on, no agent opens a new position; open positions are still managed and protected (FR-OPS-3). The operator functions are not built, so nothing stores a halt. The Risk Guard declares what it asks:

```csharp
public interface IGlobalHalt
{
    Task<bool> IsOnAsync(CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **Nothing implements it yet, and no halt is on.** | `RiskGuardRulesTests` has a test that fails as soon as the operator functions are built and nothing answers. It tells that they are built as the audit trail does: see "The audit trail" in [accounts.md](accounts.md). |
| **The halt is on when any implementation says so.** | Whatever keeps the halt implements and registers it. The Risk Guard is a leaf, so a component that may use it implements it: see "When a lower component must trigger a higher one" in [architecture.md](architecture.md). |
| **A halt stops what opens a position, and nothing else.** | `NoGlobalHalt` is asked of a plan that opens. A plan that closes a position or adjusts protection goes on, and of a reverse the close: see "A reverse that is rejected". |
| The halt is a runtime flag of the database, and no setting of the file (NFR-CFG-6). | The file has no key for it, and none for the Risk Guard. |

### The loss limits

**The daily loss limit and drawdown limit are not reached** (FR-RSK-4). The rules count from what `IAgentHoldings` says of an agent: its equity now, with unrealized profit and loss, its equity as the UTC day began, and its peak equity.

- **Nothing keeps the results of an agent yet,** so `AgentHoldings.NothingYet` says that an agent has lost nothing in the day and is at its peak: its equity is its allocated capital. What keeps positions says all three when it is built, and `DataPackageTests` has the test that waits for it.
- **The rules reject an entry. They close no position and halt no agent** (FR-RSK-8): see "Not built yet".

## A reverse that is rejected

A reverse closes the open position, and then opens one on the opposite side. It is checked as a plan that opens a position: against every rule, for the side opposite to the position that the agent holds, with the quantity of the close and the size of the entry.

**Of a reverse that the Risk Guard rejects, the close alone is checked again, and goes on when it is accepted.** What was rejected is what the reverse opens. The close reduces risk: it goes on as it does of a reverse whose decision is stale (FR-CYC-9), and as open positions are still managed while a global halt is on (FR-OPS-3).

| Rule | Detail |
|---|---|
| The close is a check of its own. | `TradePlan.ClosingOnly()`, checked as a plan to close is. The record of the cycle has two checks: the reverse, as `Rejected`, and its close. |
| Both are made on the same facts. | Nothing is read again. |
| A reverse whose position is gone closes nothing. | `HeldPosition` fails for both. |
| **A veto is different.** | A reverse that the Risk Manager vetoed does not go on, in whole, and closes nothing: see "The review" in [team.md](team.md). |

## What cannot bypass it

**Neither AI output nor a user action can bypass it for a trade** (FR-RSK-3). `RiskGuardRulesTests` and `RiskGuardCycleTests` hold the code to it:

| What holds it | How |
|---|---|
| **Only the check makes a result.** | `GuardResult` has no public constructor, and nothing of it can be changed. `RiskGuardRulesTests` finds every type of the application that makes one, and fails for one that is not `GuardCheck`. So whatever holds a result that says `Accepted` holds a plan that every rule was asked of. |
| **Only what checks the plan of a cycle says the facts.** | `GuardCase` is made by `PlanChecks`, and by nothing else of the application: no page, no endpoint, and nothing that reads the answer of a model hands the Risk Guard facts of its own. |
| **A check has no switch.** | The members of `GuardCase` are pinned: the plan, and facts. Which rules a plan is held to follows from what it does and from its market. The configuration file has no key for the Risk Guard, and an agent has no setting that turns a rule off: its limits are within the hard bounds (FR-RSK-2). |
| **A model answers a plan, and nothing more.** | The plan is validated against its schema before it goes on, and has no quantity and no leverage: see "What a model cannot do" in [team.md](team.md). A plan that the Risk Manager approved is rejected all the same when a rule does not hold. |
| **Nothing hands a check a size.** | `GuardCase` has the risk fraction of the plan, and no place for a quantity or a leverage: the check computes both (FR-RSK-5). `RiskGuardRulesTests` finds every type of the application that makes an `OrderSize`, and fails for one that is not `OrderSizing`. So the size that a result says is within the agent's risk per trade, within its equity on Spot, and within its leverage cap on Futures. |
| **The page takes nothing.** | It has no form. A form that is posted at it changes nothing. |
| **An order is made from what was accepted.** | `GuardResult.Case` is the plan that was checked, with the facts that it was checked on, and `GuardResult.Size` the quantity and the leverage that the check computed for its order. `IPlanOrders.PlaceAsync` takes the result, and refuses one that is not accepted. `RiskGuardRulesTests` pins what uses `Ecnanib.ExchangeGateway.Trading` outside the gateway, which is `AgentOrders` alone, and fails for a type that uses it and takes no `GuardResult`. What settles the orders and what closes a position for its user take none: they open nothing. |

## The record of a check

**Each check stores the outcome of every rule** (FR-RSK-9), **and the computed quantity and leverage are stored with it** (FR-RSK-5). `ICycleRecords.StoreAsync` stores the checks of a cycle with the cycle and the calls of its roles, in one transaction: see "The record of a cycle" in [team.md](team.md).

| Table | Columns |
|---|---|
| `risk_checks` | `id`, `cycle_id`, `user_id`, `sequence`, `action`, `checked_at`, `verdict`, `quantity`, `leverage`. One row for each `(cycle_id, user_id, sequence)`. |
| `risk_check_rules` | `check_id`, `user_id`, `rule`, `outcome`, `measured`, `threshold`. One row for each rule of a check: `(check_id, user_id, rule)` is its key. |

| Column | What it holds |
|---|---|
| `risk_checks.sequence` | Which check of its cycle: 1, or 2 for the close of a reverse that was rejected. |
| `risk_checks.action` | What the plan that was checked does, as the name of a member of `PlanAction`. |
| `risk_checks.checked_at` | When the facts had been read. |
| `risk_checks.verdict` | The name of a member of `GuardVerdict`. |
| `risk_checks.quantity`, `leverage` | The size that the check computed for the order that its plan opens: an exact decimal, and a whole number, which is 1 on Spot. Null for a plan that opens nothing, and where no size was computed. A quantity of zero where the facts gave no order. See "The size of an order". |
| `risk_check_rules.rule`, `outcome` | The names of a member of `GuardRule` and of `RuleOutcome`. |
| `measured`, `threshold` | Exact decimals, as "The rules" says them for each rule, or null where a rule has none. |

- **A check has a row for every rule,** also for one that its plan is not held to, as `NotApplied`. A check that was stored before `MinimumQuantity` was a rule has no row for it, and the page shows the rules that a check has.
- **The size is stored also of a plan that was rejected:** it is what the rules were asked of.
- **A cycle has no check when nothing of its plan went on,** and two when the Risk Guard rejected a reverse.
- **The rows are their user's:** `RiskCheck` and `RiskCheckRule` implement `IUserOwned`, and carry their owner along: `(cycle_id, user_id)` points at `decision_cycles (id, user_id)`, and `(check_id, user_id)` at `risk_checks (id, user_id)`. They go with their cycle, and so with the agent and with the account. See [ownership.md](ownership.md).
- **The mode is that of the cycle:** `decision_cycles.mode`, which stays as it was written when the agent is switched (FR-BIN-11).
- **They hold no text:** names of the service's own, numbers, and a time. Nothing that a model wrote is in them, and nothing personal.
- **They are not in the export of a user's data.** A user exports their trades and decisions as CSV (FR-RPT-4), which is not built. `ExportRules` lists both tables with that.

The migration that added `quantity` and `leverage` gave the checks that were stored then neither: nothing computed a size before it.

## The page

`/agents/<id>/risk-checks` shows a user the checks of an agent of theirs, so that they see exactly why a plan was accepted or rejected (FR-RSK-9). The page of the state of the agent leads to it.

```csharp
public interface IRiskCheckRecords
{
    Task<RiskCheckHistory> LatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **Each check says its verdict, and every rule with its outcome and why.** | Under the close of the candle that its cycle was for, with what its plan does. An outcome is `Holds`, `Does not hold`, `Not known`, or `Not asked`, as words: it does not rely on a colour. |
| **A rule and a rejection are explained in plain language** (NFR-USA-2). | `RiskCheckPages.Says` has a sentence for every rule and every outcome, with the numbers that the check stored: "The take-profit is only 1.4 times as far from the entry as the stop-loss. The agent asks for at least 1.5." |
| **A check of a plan that opens a position says the size that was computed** (FR-RSK-5). | Under its verdict: the quantity, and on Futures the leverage, with what they follow from, and that the system computed them and no model chose them. `RiskCheckPages.Sized` has the sentences, also for an order that came to no quantity, and for one whose size could not be computed. A check of a plan that opens nothing says nothing of a size. |
| **Whether a check was made on Futures is told from the check itself.** | It is, where the liquidation buffer was asked. The market of an agent can be changed while it is stopped, and its earlier checks stay as they were made. |
| **The latest 20 checks, latest close first.** | The page says so when there are more. The two checks of a reverse stand in their order. |
| **A check says the mode it was made in,** as a badge beside its verdict (FR-BIN-11). | The badge of the mode that the agent is in now is under the heading, as on every page of an agent (FR-UI-3). |
| **The checks of an agent of another user are not found.** | The page asks `IStoredAgents` for the agent of the user who is signed in, and answers "not found" as for an agent that does not exist. `IRiskCheckRecords` asks for the checks by owner as well. |
| The Decision Engine may not use Accounts. | So the page hands `IRiskCheckRecords` the user who is signed in, by id. |
| **The page has no form.** | A user reads the checks, and changes none: see "What cannot bypass it". |

## The log

`PlanChecks` logs under the area `Ecnanib.DecisionEngine.PlanChecks`, inside a cycle, so every event carries `UserId`, `AgentId`, and `CycleId`.

```
The Risk Guard accepted the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}
The Risk Guard rejected the plan of agent {AgentId} to {PlanAction} for the close at {ClosedAt}: {GuardRules}
The exchange did not give {Missing} for the Risk Guard to check the plan of agent {AgentId} for the close at {ClosedAt} ({ExchangeCallOutcome})
```

| Level | Event |
|---|---|
| `Information` | A plan that was accepted, and one that was rejected: a rejection is a decision, and no failure. |
| `Warning` | A price, or the rules of the symbols, that the exchange did not give. |

`GuardRules` names each rule that stands in the way with its outcome, as `RewardToRisk: Failed, QuantityStep: Unknown`. An event never holds a price, a quantity, a leverage, or anything that a model wrote: the size of an order is in the record of its check, and on the page.

## Testing

`GuardCheckTests` asks the rules with no application: `GuardCases` has a plan of each kind with facts that every rule holds for, and a test changes the one fact that it is about. Each condition of FR-RSK-4 and FR-RSK-6 has a case that holds and a case that fails, and each limit a case at the limit.

`OrderSizeTests` shows what an order comes to, on known values (FR-RSK-5): for each step of "The size of an order", for the plans that have no size and those whose order comes to no quantity, and over a grid of equities, risk fractions, stop distances, and leverage caps that every size keeps the risk and the caps, is the most that does, and has the lowest leverage that fits.

- **No test hands a check a size.** A test changes a fact that the size follows from: the equity, the risk fraction, the stop-loss, a rule of the symbol, or a limit of the agent. So the liquidation buffer is shown at its limit by the buffer of the agent, and not by a leverage of the test's.

`RiskGuardCycleTests` runs cycles as the application does, on the stand-ins of [team.md](team.md) and [cycles.md](cycles.md), which have what a check asks:

| Stand-in | What a test sets |
|---|---|
| `StandInMarket` | The price that the symbol last traded at, 60000 unless the test says otherwise, and the symbols of a market with their rules. How a read of either ends. `AsThePriceIsRead` is called as the price is read: a test moves its clock there, or changes what the agent holds, so that a fact is one thing when the team decides and another when the plan is checked. |
| `StandInHoldings` | What every agent holds, which a test can change while a cycle runs. |
| `StandInKeyChecks` | What about the Binance key of every user keeps an agent from trading. |
| `StandInHalt` | Whether a global halt is on: `CyclesApplication` takes one. |

- `RecordedCycle.Checks` is the checks that a cycle was recorded with, and `GuardedCycles.Check` a check as it is stored, for a test that needs a cycle to have one. `StoredCycleRows.GiveCycleAsync` gives a cycle the check of a plan that was accepted.
- `RiskCheckRecordTests` stores and reads checks in the database, and runs a cycle through the gateway and the stored keys once.
- **`RiskGuardCycleTests` shows entries that are accepted in a cycle,** each with the quantity and the leverage that its check stored: on Spot and on Futures, with a risk fraction that the Risk Manager reduced, capped at the equity, and reduced to the leverage cap. The agent of those tests risks 1.5 percent a trade at most, and has a leverage cap of 4.

## Not built yet

- Setting the leverage of the symbol to that of the order before an entry (FR-BIN-F2), and the leverage brackets of a symbol, by which Binance allows less leverage for a larger position. Until then an entry on Futures uses the leverage that the account has for the symbol, so the liquidation buffer was checked against a leverage that is not applied. The brackets are not read.
- Skipping an entry on Futures that the margin of the wallet does not cover (FR-BIN-F6). The size is held to the agent's equity, and not to what the account holds. On Spot an entry that the quote balance does not cover is skipped where the order is placed (FR-BIN-S3).
- Bringing the prices of a plan onto the price step of its symbol. A model is not told the step, and a plan with a price off it is rejected. What makes an order from a plan decides whether to round, and which way.
- The global halt of an Operator (FR-OPS-3), which answers `IGlobalHalt`.
- Closing the position of an agent and halting it when a loss limit is reached, at every cycle and every reconciliation (FR-RSK-8), and the equity of an agent as the day began and at its peak, which what keeps the results of an agent says.
- The maintenance margin in the liquidation estimate, and warning a user when a position approaches liquidation (FR-BIN-F5).
- Checking a close that a user asks for: "Close position now", and the close of an agent that is stopped (FR-AGT-11, FR-AGT-12). Such a close has no cycle, so its check has no row to hang on. It is placed without a check: it opens nothing, and takes no result of the Risk Guard. See "Closing a position" in [orders.md](orders.md).
- The rules of a market order, `MARKET_LOT_SIZE`, and whether the exchange takes a close below the least value. The least and the most quantity that an entry is held to are those of `LOT_SIZE`, for an entry at the market as well.
- The timeline that shows a cycle with its calls and its checks (FR-CYC-10), the agent detail screen (FR-RPT-2), and the export of decisions as CSV (FR-RPT-4). The page of the risk checks is where a user reads them until then.
- Retention: decision records are kept 365 days (NFR-DAT-5), and nothing deletes yet.
