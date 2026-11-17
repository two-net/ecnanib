# Orders

Every order carries a unique client order identifier. If the outcome of a request is unknown, the system queries the order's status before any retry, so an order is never sent twice (FR-BIN-6).

Every open position has a stop-loss order resting on the exchange. If the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user (FR-RSK-7).

The Exchange Gateway places the entry and the protective orders on Binance, or does nothing (SRS 3.2, step 7). Failures are safe: any internal error, timeout, or dependency outage results in no new entry, and open positions stay protected by orders resting on the exchange (NFR-REL-2). A restart never creates a duplicate order (NFR-REL-3), and no entry is left without protection (NFR-REL-5).

What is built is step 7 of the decision cycle with the orders that both markets have: an entry at the market, a stop-loss that rests, and an order that closes a position at the market. With them a plan opens a position, closes it, reverses it, and moves its stop-loss, and a user closes a position by hand. The orders that only one market has are not built: a limit entry, an order for the take-profit, the pair of stop-loss and take-profit of Spot, and the margin, the leverage, and the trigger on the mark price of Futures.

What places the orders is in `src/Ecnanib/DecisionEngine`: `IPlanOrders`, `AgentOrders`, `StoredOrders`, `Order`, `Fill`, `Position`, `AgentPositions.cs`, and `OrderSettlingJob`. The orders of the exchange are in `src/Ecnanib/ExchangeGateway/Trading`, and Binance's in `BinanceOrders`. The rules below are enforced by `OrderIdempotencyTests`, `ProtectiveStopTests`, `PlanOrderTests`, `OrderSettlingTests`, and `OrderRecordTests` of `tests/Ecnanib.Tests/DecisionEngine`, and by `OrderPlacementTests` of `tests/Ecnanib.Tests/ExchangeGateway`.

The calls that the gateway makes for an order are in [exchange.md](exchange.md). What the Risk Guard accepts, and the size of an order, is in [risk-guard.md](risk-guard.md). What a cycle does before it comes to its orders is in [team.md](team.md) and [cycles.md](cycles.md). What Agents asks about a position, and the states of an agent, is in [agents.md](agents.md), the key that an order is placed with in [credentials.md](credentials.md), and what a user is told in [notifications.md](notifications.md).

## Where it stands in a cycle

`TeamCycle` hands what the Risk Guard accepted to `IPlanOrders`, and nothing else: the only path from what a model answered to an order is schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6).

```csharp
public interface IPlanOrders
{
    Task PlaceAsync(CycleStart cycle, GuardResult accepted, CancellationToken cancellationToken);
    Task SettleAsync(Guid agentId, CancellationToken cancellationToken);
    Task<PositionCloseOutcome> CloseAtMarketAsync(Guid agentId, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **An order is made from a result of the Risk Guard** | `PlaceAsync` takes the `GuardResult`, and refuses one that is not accepted. The quantity of an entry is `GuardResult.Size`, which the check computed (FR-RSK-5). The stop-loss and the take-profit are those of `GuardResult.Case`. |
| **One type uses the orders of the gateway** | `AgentOrders`. A test pins it, so another shows in a review as one more way to an order. |
| **The orders of the agent are settled first** | Before the team is run, `TeamCycle` asks `SettleAsync`: a stop-loss may have been executed since the last cycle, and the team decides on what the agent holds now. See "Settling". |
| **The outcome of a cycle is the decision of its team** | Whatever becomes of the orders. What was placed is in the rows of the orders, under the id of the cycle. A cycle whose orders fail with an error is recorded with its decision, and the error is said once. |
| **What settles and what closes for a user take no result** | They open nothing, so that risk can always be reduced (FR-RSK-4, FR-AGT-12). |

## What a plan places

| The Risk Guard accepted | What is placed |
|---|---|
| Open long, open short | The entry at the market: a buy for long, a sell for short. When it is executed, the stop-loss, on the other side, at the stop-loss of the plan. |
| Close | The stop-loss is taken off the exchange, and the position is closed at the market. See "Closing a position". |
| Reverse | The close, as above. When the position is closed, and only then, the entry on the other side, with its stop-loss. |
| Adjust protection | The stop-loss that rests is taken off, and one is placed at the new price. The take-profit of the plan is kept as a price. See "Moving the stop-loss". |

An order that is not an entry only closes a position. The gateway sends it to Futures as an order that only reduces, so that it opens nothing when the position is gone already.

### When no entry is placed

Each is said in the log, with its reason, and leaves no order behind.

| Reason | When |
|---|---|
| `LimitEntry` | The plan enters at a price. Such an entry rests until it is executed, and is protected from then on, which takes the orders that each market has for it. They are not built. |
| `EntriesPaused` | `IExchangeClock.EntriesPaused` says so for the market and the environment of the agent: the clock of the service is not near Binance's, or was not read (FR-BIN-5). |
| `NoStopOrders` | The symbol takes no stop, or the exchange did not say its rules. No position is opened where no stop-loss can rest. See "Symbols" in [exchange.md](exchange.md). |
| `NotRunning` | The agent no longer opens positions: its user paused or stopped it, or it waits for its user, while its team decided. It is asked under the lock of the account, as the row of the entry is written. |
| `Holding` | The agent holds a position, or has an order that is not over. The Risk Guard rejects such a plan. This holds to it as well, under the same lock. |

An entry whose user has no Binance key for the mode is not sent either, and its row says `NotPlaced`.

## An order is never sent twice

| Rule | Detail |
|---|---|
| **Every order carries an identifier of its own** | The id of its row, as 32 hexadecimal digits. Binance takes it on both markets. A unique index holds the column `client_order_id` to it. |
| **The row is written before the order is sent** | With the status `Sending`. So whatever happens to the call, and to the application, there is a row that says that an order may be with Binance, and by which identifier it is asked about. |
| **A call is made once** | The gateway makes no call again. How the call ended says whether there is an order: see "The orders" in [exchange.md](exchange.md). |
| **An unknown outcome is asked about before anything else** | The order is asked about by its identifier. Nothing else is done with it, and nothing is placed beside it, until Binance has said what became of it. |
| **Binance's word that it has no such order counts once the order could no longer arrive** | 10 seconds after the call ended (`ExchangeOrderTimes.FinalAfter`). Binance takes a call for 5 seconds after the time that the call carries, and no longer. Before that the answer says nothing: the order may still arrive. |
| **An entry is never sent again** | An entry that Binance does not have was not placed, and the next cycle decides anew. No failure leads to an entry (NFR-REL-2). |
| **A stop-loss and a close are sent once more** | As the same order, with the same identifier, and only after Binance said that it does not have it once it could no longer arrive. An order that rests keeps its identifier taken, so Binance would refuse the second one if it had the first after all. When Binance refuses the second one, the order is asked about once more before the refusal is believed. |
| **A sent order is not given up by its caller** | Cancelling ends the wait at the limiter, and then nothing was sent. From the moment that the call is sent it runs to its answer or to its time limit. |

### What becomes of an order

| Status | The order |
|---|---|
| `Sending` | Its row is written, and it is being sent. A row that stays so for longer than 35 seconds was cut off, and is asked about as one whose outcome is unknown. |
| `Unknown` | The call ended without saying whether Binance took it: no answer in time, a connection that broke, a fault of Binance, or an answer that does not read. |
| `NotPlaced` | It was not sent, or Binance does not have it once it could no longer arrive. |
| `Rejected` | Binance refused it. |
| `Open`, `PartiallyFilled` | It rests on the exchange. |
| `Filled` | All of it is executed. |
| `Canceled`, `Expired` | It was taken off the exchange, or Binance ended it. A part of it may be executed. |

An order in the first two statuses is not settled: see "Settling". An order in the first two or in `Open` or `PartiallyFilled` is not over, and an agent has one such order of each purpose at most, which an index holds it to.

## The stop-loss

| Rule | Detail |
|---|---|
| **It is placed when the entry is executed** | For what the agent holds of the entry, at the stop-loss of the plan, as a stop that is placed at the market when the price reaches it. |
| **On Spot it is for what was bought, less the fee** | Binance may take the fee of a purchase in the asset that was bought. What the account then holds is less than what was executed, and an order for more would be refused. The executions say the fee: they are in the answer to the entry, or are read. The quantity is rounded down to the quantity step of the symbol. |
| **It is placed at most twice** | A second time when the first was not placed: it was not sent, or Binance does not have it. A stop-loss that Binance refused is not placed again. |
| **When it cannot be placed, the position is closed at market at once** | And its user is told, with a notification (FR-RSK-7). The agent runs on. The position says `ProtectionFailed` as how it ended. |
| **When it cannot be closed either** | The position is open with no stop-loss. Its user is told that, once. An agent that runs moves to Attention required, with the cause `PositionUnprotected`, and one that its user paused or stopped stays as it is. What settles the orders tries again every minute: the stop-loss first, and then the close. |
| **A cycle that is being stopped still protects its entry** | Once the entry is sent, the stop-loss is sent as well, once. What is left after that, an unknown outcome, a second try, and the close at market, is done by what settles the orders (NFR-REL-5). |
| **A stop-loss that rests is asked about** | At the start of each cycle of the agent, before a position is closed, and by the job that settles, when it was last asked about longer ago than `Binance:ReconcileMinutes`. Executed: the position is closed in the records, as `StopLoss`. Taken off or ended at Binance: the position is unprotected, and gets a new stop-loss. |

`DecisionCyclesJob.EndsBefore` is 45 seconds for this: a cycle whose entry is on its way when it is stopped lets that call end, and has one more call to protect the entry, before the next close.

### Moving the stop-loss

A plan that adjusts protection names a stop-loss that reduces the risk: the Risk Guard holds it to that (FR-CYC-6).

1. The stop-loss that rests is taken off the exchange. When Binance does not take it off, nothing changes: the position is protected at the old price.
2. The new price is written to the position, and a stop-loss is placed there.
3. When it cannot be placed there, the old price is written back, and a stop-loss is placed at the old price again.
4. When that cannot be placed either, the position is closed at market, and its user is told.

## Closing a position

For a plan that closes or reverses, for the user of an agent (FR-AGT-11, FR-AGT-12), and for a position whose stop-loss cannot be placed.

| Step | Rule |
|---|---|
| 1 | The stop-loss is taken off the exchange first. On Spot it holds what the close sells, and on Futures it would be left behind. When Binance has no stop-loss to take off, it is asked what became of it: a stop-loss that was executed has closed the position already, and nothing more is placed. When it cannot be taken off, the close fails, and the position is open as it was, with its stop-loss. |
| 2 | The order that closes the position is placed at the market, for what the position holds, at most twice: a second time when the first was not placed, or ended with a part of the position. |
| 3 | When the position is not closed, its stop-loss is placed again, as in "The stop-loss". |

Between step 1 and step 2 the position has no stop-loss. That cannot be avoided on Spot.

`CloseAtMarketAsync` settles the orders of the agent first, and answers with an outcome, and never with an error:

| Outcome | When |
|---|---|
| `Closed` | The position is closed. |
| `NothingOpen` | The agent holds no position: it never did, or its stop-loss was executed. |
| `Failed` | The position is open, with its stop-loss, or the outcome of an order of the agent is not known yet. |

Two closes of the same position at the same moment send one order: the second finds nothing open.

## Settling

`SettleAsync` settles the orders of one agent with the exchange, as far as the exchange says. It waits for nothing, and sends no order that Binance may have already.

| Step | What |
|---|---|
| 1 | Each order that is not over is asked about by its identifier, and what Binance says is written. An order that is being sent now, by another instance, is left to that one. |
| 2 | An open position that no order is under way for has its stop-loss, or gets it now, as in "The stop-loss". |
| 3 | The executions of the orders that are over are read and stored, where they are not stored yet. |

| Rule | Detail |
|---|---|
| **A job settles every minute** | `OrderSettlingJob`, named `order-settling`, for the agents that have something to settle: an order whose outcome is unknown, an open position without a stop-loss that rests, a stop-loss that was last asked about longer ago than `Binance:ReconcileMinutes`, and executions that are not stored. Most runs find none. |
| **A restart settles first** | The job is due as the background work starts when there is anything to settle. |
| **An agent that is not settled gets no cycle** | `IReconciliation.HasReconciledAsync` answers from the rows: an agent with an order in `Sending` or `Unknown`, or with an open position without a stop-loss that rests, is not reconciled. So no order is placed for it until Binance was asked. That is what keeps a restart from placing an order twice (NFR-REL-3). See "After a restart" in [cycles.md](cycles.md). |
| **No agent waits for another** | The agents are settled a few at a time, each on its own. One that cannot be settled, because its key is gone or refused, keeps no other waiting. |
| **A refused key is checked again** | When Binance answers a call about an order as it answers a wrong credential, `IKeyValidation.RevalidateExchangeKeyAsync` is asked. The check decides whether the key is invalid (FR-KEY-3). |

## What an agent holds

The Decision Engine keeps the positions and the orders, so it answers those that waited for them.

| Interface | Answer |
|---|---|
| `IAgentExposure` of Agents | An agent has an open position or orders while it has a position without a time of closing, or an order that is not over. It has an open position, as far as stopping it goes, also while an entry of it is not over: such an entry may open one. `ClosePositionAtMarketAsync` is `IPlanOrders.CloseAtMarketAsync`. See [agents.md](agents.md). |
| `IAgentHoldings` | The open position, with the price of the stop-loss that rests for it, and the orders that rest. A take-profit is not said: no order rests for one. The equity is the capital that the user allocated: no result is stored yet. See "What an agent holds" in [cycles.md](cycles.md). |
| `IReconciliation` | Whether the agent is settled. See "Settling". |

## One after the other

| Rule | Detail |
|---|---|
| **One at a time for an agent** | `AgentOrders` does what it does for one agent one after the other: a cycle, what settles, and a close of the agent's user take turns. |
| **A row is begun under the lock of the account** | The row of an order is written in a transaction that first takes the lock of the agent's account, as the controls of its user do (`IAgentTrading.StateUnderLockAsync`). Under it the state of the agent and what it holds are asked. So an entry and a stop of the agent's user cannot cross, and two instances of the application begin an order of the same agent one after the other. |
| **What becomes of an order is written by comparing and setting** | A write names the status that the row is expected to have, and writes nothing when the row has moved on. What follows from it, a position that is opened or closed, is written in the same transaction. |
| **The database holds to the rest** | One open position for an agent, one order of each purpose that is not over, and no two orders with the same identifier. |
| **No transaction is held while Binance is asked** | |

## The record of an order

The migration `AddOrders` adds three tables of the Decision Engine. Each row carries its owner along: an order and a position to their agent, with `(agent_id, user_id)`, and an execution to its order, with `(order_id, user_id)`. They go with the agent when it is deleted, and with the account. See [ownership.md](ownership.md).

| Table | Columns |
|---|---|
| `orders` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `cycle_id`, `position_id`, `client_order_id`, `exchange_order_id`, `purpose`, `type`, `side`, `price`, `stop_loss`, `take_profit`, `quantity`, `status`, `executed_quantity`, `average_price`, `exit_reason`, `sends`, `fills_recorded`, `created_at`, `updated_at`, `checked_at` |
| `fills` | `id`, `order_id`, `user_id`, `trade_id`, `price`, `quantity`, `fee`, `fee_asset`, `at` |
| `positions` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `side`, `entry_price`, `quantity`, `stop_loss`, `take_profit`, `opened_at`, `closed_at`, `exit_reason`, `alerted_at` |

| Column | What it holds |
|---|---|
| `orders.mode`, `market`, `symbol` | Those of the agent when the order was placed. What an agent did in Demo is Demo for good (FR-BIN-11), and an order is asked about where it was placed. |
| `orders.cycle_id` | The cycle that placed it, or nothing for an order that no cycle placed. It points at no row: a cycle is recorded when it ends, which is after its orders. |
| `orders.purpose` | `Entry`, `StopLoss`, or `Close`. |
| `orders.type` | `Market` or `Stop`. |
| `orders.price` | The price that a stop is triggered at. |
| `orders.stop_loss`, `take_profit` | Of an entry: the protection of the position that it opens. They are kept with the entry, so that the position is protected at that price also when the entry is found executed later, by what settles it. |
| `orders.exchange_order_id` | The number that Binance knows the order by. A stop of Futures that was triggered is known by the order that it became. |
| `orders.exit_reason` | Of an order that closes: why. |
| `orders.sends` | 1, or 2 when it was sent once more. |
| `orders.checked_at` | When Binance last said what has become of it. |
| `fills.fee`, `fee_asset` | The fee of an execution, in the asset that Binance took it in. |
| `positions.quantity` | What the position holds: what an order that protects or closes it is for. |
| `positions.stop_loss`, `take_profit` | Prices. The order of the stop-loss is a row of `orders`. No order is placed for the take-profit. |
| `positions.exit_reason` | `StopLoss`, `TeamClose`, `UserClose`, or `ProtectionFailed`. |
| `positions.alerted_at` | When its user was told that it is open with no stop-loss and could not be closed. |

The rows are not in the export of a user's data: a user exports their trades as CSV (FR-RPT-4), which is not built. See [accounts.md](accounts.md).

## The log

Under `Ecnanib.DecisionEngine.AgentOrders`. An event names the agent, and an order by the number that Binance gave it (NFR-LOG-3). No event holds a key, a price, or a quantity.

```
No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}
The {OrderPurpose} of agent {AgentId} in {Symbol} is {OrderStatus}, as the order {ExchangeOrderId}
Agent {AgentId} opened a {PositionSide} position in {Symbol} with the order {ExchangeOrderId}
The stop-loss of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}
The exchange does not have the {OrderPurpose} of agent {AgentId}, which could no longer arrive, so it is sent once more
The stop-loss of the position of agent {AgentId} could not be placed, so the position is closed at market
The position of agent {AgentId} was closed at market: {ExitReason}, with the order {ExchangeOrderId}
The position of agent {AgentId} is open with no stop-loss, and could not be closed at market
```

| Note | Detail |
|---|---|
| **Levels** | A change of an order and a position that is opened or closed are information. A stop-loss that cannot be placed is a warning. A position that is left open with no stop-loss is an error. |
| **The calls themselves** | Each call to Binance is in the log of outbound calls, with its duration and its outcome, and the number of an error of Binance under the gateway. See [exchange.md](exchange.md). |
| **The job** | `The orders of {Agents} agents are settled with the exchange`, under `Ecnanib.DecisionEngine.OrderSettlingJob`, when there are some. An agent that cannot be settled for an error of the application is said once, as an error. |

## Testing

The tests run the application on a database of their own, with a Binance that keeps orders behind the gateway (`TradingBinance` in `OrderFixtures.cs`): it takes an order, answers what became of it, takes it off, and lists its executions, in the form that Binance's documentation gives. A test says what becomes of a call that places an order: taken, taken with an answer that never comes, lost, refused, or answered with a fault. The clock is the test's, so a test moves it on while the application waits for a moment of it.

| Requirement | Tests |
|---|---|
| Every order carries a unique client order identifier | `OrderIdempotencyTests`, `OrderRecordTests`, `OrderPlacementTests` |
| A simulated timeout never produces a second order | `OrderIdempotencyTests`: an entry and a stop-loss that Binance took before the call timed out, one that it does not have, and a restart |
| Every open position has a stop-loss resting on the exchange | `ProtectiveStopTests`, `PlanOrderTests`, `OrderSettlingTests` |
| A protective order that cannot be placed closes the position at market and alerts the user | `ProtectiveStopTests`, and `NotificationTests` for what the user sees |

A check against Binance's own test environment is in `LiveBinanceTests`: see "Checking against Binance itself" in [exchange.md](exchange.md).

## Not built yet

- The orders of Spot (FR-BIN-S1 to FR-BIN-S3): a limit entry and its cancellation at the next cycle, the pair of stop-loss and take-profit where filling one cancels the other, and the check of the quote balance. Until then a plan with a limit entry places nothing, and no order rests for a take-profit.
- The orders of Futures (FR-BIN-F2, FR-BIN-F3, FR-BIN-F6): isolated margin, one-way mode, the leverage that the Risk Guard computed, the trigger on the mark price, and the check of the margin. Until then an entry uses the leverage that the account has for the symbol, so the liquidation buffer was checked against a leverage that is not applied.
- The account event feed and reconciliation (FR-BIN-7, FR-BIN-8). Until then a stop-loss that was executed shows in the records when it is next asked about, and what a user does at Binance by hand is not detected, but for a stop-loss that they take off.
- The results of a position: its profit and loss, its fees, its funding, and its result in R (FR-BIN-10). The equity of an agent, and with it the loss limits (FR-RSK-8), still go by the allocated capital.
- The orders of a cycle in its record and its timeline (FR-CYC-10). Why a plan placed nothing is in the log only.
- The count of the orders that an account places, which Binance limits beside the request weight. A protective order has no turn before other calls at the limiter: one that is held back was not sent, and is settled within a minute.
- A close by hand that is told its user as closed, or failed, by a notification (FR-NTF-1). The page that asked says so.
