# Orders

Every order carries a unique client order identifier. If the outcome of a request is unknown, the system queries the order's status before any retry, so an order is never sent twice (FR-BIN-6).

Every open position has a stop-loss order resting on the exchange. If the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user (FR-RSK-7).

The Exchange Gateway places the entry and the protective orders on Binance, or does nothing (SRS 3.2, step 7). Failures are safe: any internal error, timeout, or dependency outage results in no new entry, and open positions stay protected by orders resting on the exchange (NFR-REL-2). A restart never creates a duplicate order (NFR-REL-3), and no entry is left without protection (NFR-REL-5).

Spot agents are long-only: they buy the base asset with the quote asset and sell to exit. They do not borrow or short (FR-BIN-S1). Entries are market or limit orders. An unfilled limit entry is cancelled at the next cycle. Protection is a stop-loss and take-profit pair resting on the exchange, where filling one cancels the other (FR-BIN-S2). A Spot agent sells only the quantity it bought and never touches other holdings in the account. If the quote balance is below what the plan needs, the entry is skipped and the user is notified (FR-BIN-S3).

What is built is step 7 of the decision cycle with the orders that both markets have, and with those of Spot. Both markets have an entry at the market, a stop-loss that rests, and an order that closes a position at the market. Spot has an entry at a price, which rests, and the pair of a stop-loss and a take-profit. With them a plan opens a position, closes it, reverses it, and moves its protection, and a user closes a position by hand. The orders that only Futures has are not built: an entry at a price there, an order for the take-profit, and the margin, the leverage, and the trigger on the mark price.

What places the orders is in `src/Ecnanib/DecisionEngine`: `IPlanOrders`, `AgentOrders`, `StoredOrders`, `Order`, `Fill`, `Position`, `AgentPositions.cs`, and `OrderSettlingJob`. The orders of the exchange are in `src/Ecnanib/ExchangeGateway/Trading`, and Binance's in `BinanceOrders`. The rules below are enforced by `OrderIdempotencyTests`, `ProtectiveStopTests`, `ProtectivePairTests`, `LimitEntryTests`, `SpotEntryTests`, `PlanOrderTests`, `OrderSettlingTests`, and `OrderRecordTests` of `tests/Ecnanib.Tests/DecisionEngine`, and by `OrderPlacementTests` of `tests/Ecnanib.Tests/ExchangeGateway`.

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
| **An order is made from a result of the Risk Guard** | `PlaceAsync` takes the `GuardResult`, and refuses one that is not accepted. The quantity of an entry is `GuardResult.Size`, which the check computed (FR-RSK-5). The price of an entry at a price, the stop-loss, and the take-profit are those of `GuardResult.Case`. |
| **One type uses the orders of the gateway** | `AgentOrders`. A test pins it, so another shows in a review as one more way to an order. |
| **The orders of the agent are settled first** | Before the team is run, `TeamCycle` asks `SettleAsync`: a stop-loss or a take-profit may have been executed since the last cycle, an entry at a price of the cycle before is taken off the exchange, and the team decides on what the agent holds now. See "Settling" and "An entry at a price". |
| **The outcome of a cycle is the decision of its team** | Whatever becomes of the orders. What was placed is in the rows of the orders, under the id of the cycle. A cycle whose orders fail with an error is recorded with its decision, and the error is said once. |
| **What settles and what closes for a user take no result** | They open nothing, so that risk can always be reduced (FR-RSK-4, FR-AGT-12). |

## What a plan places

| The Risk Guard accepted | What is placed |
|---|---|
| Open long, open short | The entry: a buy for long, a sell for short. It is placed at the market, or on Spot at the price of a plan that names one, where it rests: see "An entry at a price". When it is executed, the stop-loss, on the other side, at the stop-loss of the plan. On Spot the take-profit of the plan is placed with it, as one pair: see "The pair of the Spot market". |
| Close | The protection is taken off the exchange, and the position is closed at the market. See "Closing a position". |
| Reverse | The close, as above. When the position is closed, and only then, the entry on the other side, with its stop-loss. Only Futures has it. |
| Adjust protection | The stop-loss that rests is taken off, and one is placed at the new price. On Spot it is the pair that is taken off and placed anew, when the stop-loss or the take-profit moves. On Futures the take-profit of the plan is kept as a price. See "Moving the stop-loss". |

An order that is not an entry only closes a position. The gateway sends it to Futures as an order that only reduces, so that it opens nothing when the position is gone already. On Spot it sells what the position holds, which is what the agent bought, and no more (FR-BIN-S3).

### When no entry is placed

Each is said in the log, with its reason, and leaves no order behind. They are asked in this order, and the first that holds is the reason.

| Reason | When |
|---|---|
| `SpotLongOnly` | The entry sells, on Spot. An agent there buys, and sells what it bought to exit (FR-BIN-S1). A plan that shorts on Spot is refused when it is read: see "What a plan may do" in [team.md](team.md). What places the orders holds to it as well, for a sale that opens would sell what else the account holds. |
| `LimitEntry` | The plan enters at a price, on Futures. Such an entry rests until it is executed, and is protected from then on, which takes the orders that Futures has for it. They are not built. |
| `EntriesPaused` | `IExchangeClock.EntriesPaused` says so for the market and the environment of the agent: the clock of the service is not near Binance's, or was not read (FR-BIN-5). |
| `NoStopOrders` | The symbol takes no stop, or the exchange did not say its rules. No position is opened where no stop-loss can rest. See "Symbols" in [exchange.md](exchange.md). |
| `NoPairOrders` | The symbol takes no pair of a stop-loss and a take-profit, on Spot. No position is opened there whose protection cannot rest as a pair (FR-BIN-S2). |
| `NotRunning` | The agent no longer opens positions: its user paused or stopped it, or it waits for its user, while its team decided. It is asked before the account is read, and again under the lock of the account, as the row of the entry is written. |
| `Holding` | The agent holds a position, or has an order that is not over, an entry that rests among them. The Risk Guard rejects such a plan. This holds to it as well, at the same two moments. |
| `QuoteBalance` | The account holds less of the asset that the symbol is priced in than the entry needs, on Spot. The entry is skipped, and its user is notified. See "The quote balance". |
| `BalanceUnknown` | What the account holds could not be read, on Spot. A failure leads to no entry (NFR-REL-2). |

An entry whose user has no Binance key for the mode is not sent either, and its row says `NotPlaced`.

### The quote balance

If the quote balance is below what the plan needs, the entry is skipped and the user is notified (FR-BIN-S3).

| Rule | Detail |
|---|---|
| **The account is read before the entry is begun** | `IExchangeAdapter.GetBalancesAsync` reads the account of the agent's user on Spot, in the mode of the agent, with their own key. It is one call for each entry, of 20 of request weight. |
| **What counts is what is free** | `ExchangeBalance.Available` of `Agent.QuoteAsset`: what orders of the account hold already is not there for this one. An asset that is not among the balances is one that the account holds none of. |
| **The entry needs its quantity at its price** | The quantity that the Risk Guard computed, times the price of an entry at a price, or the price that the symbol last traded at for an entry at the market: `GuardCase.CurrentPrice`. An account that holds exactly that covers it. |
| **The user is told, once in a day for an agent** | With a notification of the type `EntrySkipped`, which says what the entry needs and what the account has: see [notifications.md](notifications.md). A cycle that decides to enter comes with every candle, and the page of an agent shows the latest 20 that are not read, so one for each skipped entry would leave no room for a protection that failed. `AgentOrders.SkippedSaidEvery` is the day. Every skipped entry is in the log. |
| **An account that cannot be read covers nothing** | The entry is not placed, and nobody is told: it is not known that the account is short. A key that Binance refuses is checked again (FR-KEY-3). |
| **An entry that Binance refuses has the account read once more** | Two agents of one account may both find it covered at the same close, and the price moves between the read and the order. The user is told when the account is short then. |
| The Risk Guard does not ask it. | The size of an order is held to the agent's equity, which is the capital that its user allocated: see "The size of an order" in [risk-guard.md](risk-guard.md). The margin that the Futures wallet lacks is not asked (FR-BIN-F6). |

## An entry at a price

On Spot an entry is an order at the market, or an order at a price, which rests on the exchange until it is executed (FR-BIN-S2). `LimitEntryTests` holds it to the rules below.

| Rule | Detail |
|---|---|
| **It is one order, good until it is taken off** | At the price of the plan, for the quantity that the Risk Guard computed. Binance executes it at once where the market has reached the price, and it is protected then as an entry at the market is. |
| **It opens no position while it rests** | Its row says `Open`, or `PartiallyFilled` with what is executed so far. The position is opened when the entry is over, for what was executed of it. Until then the agent has an entry that has not filled, which its data package says, and no second entry: see "What an agent holds". |
| **It is asked about every minute** | Nothing tells the service that an order was executed: the account event feed is not built. So the job that settles the orders asks about an entry that rests with every run, and what it bought has its stop-loss and its take-profit within a minute of the execution. See "Not built yet". |
| **It is taken off when the next candle of its agent has closed** | `orders.cancel_after` is that close: the close that the cycle of the entry was for, and the length of a candle of the agent after it. The cycle that starts then settles the orders of the agent before its team decides, which takes the entry off: the team decides on an agent that holds nothing. The job takes it off as well, so a cycle that does not start, because it came too late, the candle was not there, or the service was down, does not leave an entry of the day before resting. |
| **A part that is executed is protected at once** | When a part is executed that an order can be for, the rest of the entry is taken off, and the part is a position with its pair. Left to rest, the entry would leave what it bought without a stop-loss until the rest is executed or its time is up, which is a day on the longest timeframe. |
| **A part that no order can be for is no position** | Below the least quantity of the symbol, or worth less than its least value. Such a part is left to grow while the entry rests. When the entry is over with it, it opens no position: nothing could protect it or sell it, and an agent that held it would wait for its user for good. What was bought stays in the account, the executions are stored, and the log says it as a warning. |
| **It is taken off when its agent no longer opens positions** | An agent that its user paused or stopped, or that waits for its user, gets no cycle that would take it off. The job asks the state of the agent, under the lock of its account, and takes the entry off. |
| **It is taken off when its user closes the position** | An agent has a position, as far as its user is asked, from the moment that its entry is written: see "What an agent holds". "Close position now" takes an entry that rests off, and sells what it bought by then. |
| **What it bought is what the agent holds** | What was executed, less a fee that Binance took in the asset that was bought, down to the quantity step: see "The stop-loss". The answer about an order that rested has no executions, so they are read. |

## An order is never sent twice

| Rule | Detail |
|---|---|
| **Every order carries an identifier of its own** | The id of its row, as 32 hexadecimal digits. Binance takes it on both markets. A unique index holds the column `client_order_id` to it. |
| **The row is written before the order is sent** | With the status `Sending`. So whatever happens to the call, and to the application, there is a row that says that an order may be with Binance, and by which identifier it is asked about. |
| **A call is made once** | The gateway makes no call again. How the call ended says whether there is an order: see "The orders" in [exchange.md](exchange.md). |
| **An unknown outcome is asked about before anything else** | The order is asked about by its identifier. Nothing else is done with it, and nothing is placed beside it, until Binance has said what became of it. |
| **Binance's word that it has no such order counts once the order could no longer arrive** | 10 seconds after the call ended (`ExchangeOrderTimes.FinalAfter`). Binance takes a call for 5 seconds after the time that the call carries, and no longer. Before that the answer says nothing: the order may still arrive. The same holds for an order that rests: that Binance has none counts once the order has rested for that long, so that no order is placed a second time beside one that Binance has just taken. |
| **An entry is never sent again** | An entry that Binance does not have was not placed, and the next cycle decides anew. No failure leads to an entry (NFR-REL-2). |
| **A stop-loss and a close are sent once more** | As the same order, with the same identifier, and only after Binance said that it does not have it once it could no longer arrive. An order that rests keeps its identifier taken, so Binance would refuse the second one if it had the first after all. When Binance refuses the second one, the order is asked about once more before the refusal is believed. |
| **A pair is sent once more as a pair** | Both of its orders with one call, with the identifiers that they had and the one of the pair, and only after Binance said of both that it has neither. No order of a pair is ever sent alone: see "The pair of the Spot market". |
| **A sent order is not given up by its caller** | Cancelling ends the wait at the limiter, and then nothing was sent. From the moment that the call is sent it runs to its answer or to its time limit. |

### What becomes of an order

| Status | The order |
|---|---|
| `Sending` | Its row is written, and it is being sent. A row that stays so for longer than 35 seconds was cut off, and is asked about as one whose outcome is unknown. |
| `Unknown` | The call ended without saying whether Binance took it: no answer in time, a connection that broke, a fault of Binance, or an answer that does not read. |
| `NotPlaced` | It was not sent, or Binance does not have it once it could no longer arrive. |
| `Rejected` | Binance refused it. |
| `Open`, `PartiallyFilled` | It rests on the exchange: a stop-loss or a take-profit, or an entry at a price. |
| `Filled` | All of it is executed. |
| `Canceled`, `Expired` | It was taken off the exchange, or Binance ended it. A part of it may be executed. |

An order in the first two statuses is not settled: see "Settling". An order in the first two or in `Open` or `PartiallyFilled` is not over, and an agent has one such order of each purpose at most, which an index holds it to: one entry, one stop-loss, one take-profit, and one close.

## The stop-loss

| Rule | Detail |
|---|---|
| **It is placed when the entry is executed** | For what the agent holds of the entry, at the stop-loss of the plan, as a stop that is placed at the market when the price reaches it. On Spot it is one order of a pair with the take-profit, and everything below holds for the pair: see "The pair of the Spot market". |
| **On Spot it is for what was bought, less the fee** | Binance may take the fee of a purchase in the asset that was bought. What the account then holds is less than what was executed, and an order for more would be refused. The executions say the fee: they are in the answer to the entry, or are read. The quantity is rounded down to the quantity step of the symbol. |
| **It is placed at most twice** | A second time when the first was not placed: it was not sent, or Binance does not have it. A stop-loss that Binance refused is not placed again. |
| **When it cannot be placed, the position is closed at market at once** | And its user is told, with a notification (FR-RSK-7). The agent runs on. The position says `ProtectionFailed` as how it ended. |
| **When it cannot be closed either** | The position is open with no stop-loss. Its user is told that, once. An agent that runs moves to Attention required, with the cause `PositionUnprotected`, and one that its user paused or stopped stays as it is. What settles the orders tries again every minute: the stop-loss first, and then the close. |
| **A cycle that is being stopped still protects its entry** | Once the entry is sent, the stop-loss is sent as well, once. What is left after that, an unknown outcome, a second try, and the close at market, is done by what settles the orders (NFR-REL-5). |
| **A stop-loss that rests is asked about** | At the start of each cycle of the agent, before a position is closed, and by the job that settles, when it was last asked about longer ago than `Binance:ReconcileMinutes`. Executed: the position is closed in the records, as `StopLoss`. Taken off or ended at Binance: the position is unprotected, and gets a new stop-loss. |

`DecisionCyclesJob.EndsBefore` is 45 seconds for this: a cycle whose entry is on its way when it is stopped lets that call end, and has one more call to protect the entry, before the next close. The pair of Spot is one call as well.

## The pair of the Spot market

On Spot the protection of a position is a stop-loss and a take-profit that rest on the exchange as one pair, where filling one cancels the other (FR-BIN-S2). Binance holds the two as one list, which it calls an OCO: see "The orders" in [exchange.md](exchange.md). `ProtectivePairTests` holds it to the rules below.

| Rule | Detail |
|---|---|
| **Both are stops that sell what the position holds** | The stop-loss below the price and the take-profit above it, each at the price of the plan, each placed at the market when the price reaches it. So the take-profit is executed when the price touches it, as the stop-loss is, at what the market gives then. |
| **Both sell the same quantity, which is what the agent bought** | What the position holds: what was executed of the entry, less a fee that Binance took in the asset (FR-BIN-S3). Binance ends one when the other is triggered, so the two never sell twice. |
| **The pair is two rows, and three identifiers** | A row for the stop-loss and one for the take-profit, each with the identifier that its order carries, as every order has. Both rows carry `pair_id`, which is the identifier that the pair carries at Binance, and the id of no row. All three are written before the pair is sent. |
| **It is sent with one call, and Binance takes both or neither** | So what the call says is written to both rows: that the pair was not sent, was refused, or that it is not known. What Binance answered of each order is written to the row of that order. |
| **An unknown outcome is asked about by the two orders** | Each by its own identifier. Binance has the pair when it has either. It has none when it says of both that it has no such order, once they could no longer arrive: then the pair is sent once more, as the same pair, and after that it was not placed. |
| **No order of a pair is ever sent alone** | `AgentOrders.SendAsync` refuses a row that carries a `pair_id`. A stop-loss that was sent alone would rest outside its pair, where nothing that takes the pair off would find it, and no close or adjustment of the position would work again. |
| **What became of an order is written when Binance said it of that order** | Or when it said that there is no pair. An order that was written off while it rests could be executed later: the position would look unprotected when the pair ends, and a second pair would be placed for what the agent no longer holds, which would sell what else the account holds. |
| **That Binance lacks one of the two says nothing while it has the other resting** | It holds them as one list, and ends neither while the other rests. Only when it has neither are both gone, and that counts once the pair has rested for as long as an order takes to arrive, as for every order that rests: see "An order is never sent twice". |
| **When one has ended, the other is asked about again** | In the same pass. Binance ends the other when one is triggered, so the other was asked about a moment too early when it still said that it rests. |
| **A take-profit that was executed closes the position** | In the records, as `TakeProfit`, with its executions, when it is next asked about: see "A stop-loss that rests is asked about". Nobody is told: it is no failure. A part of it that was executed is no longer held, and what is left of the position gets a new pair. |
| **It is taken off with one call** | Before a close, and before the protection is moved. Binance reports both orders, each under the identifier that it carried. Whatever the call says but that, both orders are asked about: a pair of which one order was executed is over at Binance, and has nothing to take off. |
| **A take-profit that rests without its stop-loss protects nothing** | The records never come to that by what Binance says. Where they do, what rests is taken off, and only then the position gets its pair anew. |
| **When the pair cannot be placed, the position is closed at market** | As in "The stop-loss": a pair that Binance refuses, because the price has passed one of its two triggers, or that is not placed at the second try. The notification says that the stop-loss could not be placed. |
| Futures has no such pair. | A position there has its stop-loss, and its take-profit as a price. So has a position on Spot whose plan named no take-profit, which no plan that opens a position does (FR-CYC-7), and one that was opened before the pair was built, until its protection is next moved. |

### Moving the stop-loss

A plan that adjusts protection names a stop-loss that reduces the risk: the Risk Guard holds it to that (FR-CYC-6). The take-profit moves in either direction.

1. The stop-loss that rests is taken off the exchange. When Binance does not take it off, nothing changes: the position is protected at the old price.
2. The new price is written to the position, and a stop-loss is placed there.
3. When it cannot be placed there, the old price is written back, and a stop-loss is placed at the old price again.
4. When that cannot be placed either, the position is closed at market, and its user is told.

On Spot it is the pair that is taken off and placed, when the plan moves the stop-loss, the take-profit, or both, and both old prices that are written back. So a take-profit that Binance refuses, because the price has passed it, leaves the position with the pair that it had. On Futures the take-profit is a price only: it is written, and no order is moved for it.

## Closing a position

For a plan that closes or reverses, for the user of an agent (FR-AGT-11, FR-AGT-12), and for a position whose stop-loss cannot be placed.

| Step | Rule |
|---|---|
| 1 | The stop-loss is taken off the exchange first, and on Spot the take-profit with it, as the one pair that they are. On Spot it holds what the close sells, and on Futures it would be left behind. When Binance has no stop-loss to take off, it is asked what became of it: a stop-loss or a take-profit that was executed has closed the position already, and nothing more is placed. When it cannot be taken off, the close fails, and the position is open as it was, with its stop-loss. |
| 2 | The order that closes the position is placed at the market, for what the position holds, at most twice: a second time when the first was not placed, or ended with a part of the position. |
| 3 | When the position is not closed, its stop-loss is placed again, as in "The stop-loss". |

Between step 1 and step 2 the position has no stop-loss. That cannot be avoided on Spot.

`CloseAtMarketAsync` settles the orders of the agent first, takes an entry that rests off the exchange, and answers with an outcome, and never with an error:

| Outcome | When |
|---|---|
| `Closed` | The position is closed. |
| `NothingOpen` | The agent holds no position: it never did, its stop-loss or its take-profit was executed, or its entry rested and was taken off with nothing executed. |
| `Failed` | The position is open, with its stop-loss, or the outcome of an order of the agent is not known yet, or its entry could not be taken off. |

Two closes of the same position at the same moment send one order: the second finds nothing open.

## Settling

`SettleAsync` settles the orders of one agent with the exchange, as far as the exchange says. It waits for nothing, and sends no order that Binance may have already.

| Step | What |
|---|---|
| 1 | Each order that is not over is asked about by its identifier, and what Binance says is written. The two orders of a pair are asked about together. An order that is being sent now, by another instance, is left to that one. |
| 2 | An entry that still rests is taken off the exchange when its time is up, when a part of it is executed that can be protected, or when its agent no longer opens positions: see "An entry at a price". |
| 3 | An open position that no order is under way for has its stop-loss, or gets it now, as in "The stop-loss". |
| 4 | The executions of the orders that are over are read and stored, where they are not stored yet. |

| Rule | Detail |
|---|---|
| **A job settles every minute** | `OrderSettlingJob`, named `order-settling`, for the agents that have something to settle: an order whose outcome is unknown, an entry that rests, an open position without a stop-loss that rests, a stop-loss or a take-profit that was last asked about longer ago than `Binance:ReconcileMinutes`, and executions that are not stored. Most runs find none. |
| **A restart settles first** | The job is due as the background work starts when there is anything to settle. |
| **An agent that is not settled gets no cycle** | `IReconciliation.HasReconciledAsync` answers from the rows: an agent with an order in `Sending` or `Unknown`, or with an open position without a stop-loss that rests, is not reconciled. So no order is placed for it until Binance was asked. That is what keeps a restart from placing an order twice (NFR-REL-3). An entry that rests is settled: its agent gets the cycle that takes it off. See "After a restart" in [cycles.md](cycles.md). |
| **No agent waits for another** | The agents are settled a few at a time, each on its own. One that cannot be settled, because its key is gone or refused, keeps no other waiting. |
| **A refused key is checked again** | When Binance answers a call about an order as it answers a wrong credential, `IKeyValidation.RevalidateExchangeKeyAsync` is asked. The check decides whether the key is invalid (FR-KEY-3). |

## What an agent holds

The Decision Engine keeps the positions and the orders, so it answers those that waited for them.

| Interface | Answer |
|---|---|
| `IAgentExposure` of Agents | An agent has an open position or orders while it has a position without a time of closing, or an order that is not over. It has an open position, as far as stopping it goes, also while an entry of it is not over: such an entry may open one. `ClosePositionAtMarketAsync` is `IPlanOrders.CloseAtMarketAsync`. See [agents.md](agents.md). |
| `IAgentHoldings` | The open position, with the prices of the stop-loss and of the take-profit that rest for it, and the orders that rest: an entry that has not filled, a stop-loss, and a take-profit. A take-profit is said when an order rests for it, which is on Spot. The equity is the capital that the user allocated: no result is stored yet. See "What an agent holds" in [cycles.md](cycles.md). |
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

The migration `AddOrders` adds three tables of the Decision Engine, and `AddOrderPairs` the columns `pair_id` and `cancel_after` of `orders`. Each row carries its owner along: an order and a position to their agent, with `(agent_id, user_id)`, and an execution to its order, with `(order_id, user_id)`. They go with the agent when it is deleted, and with the account. See [ownership.md](ownership.md).

| Table | Columns |
|---|---|
| `orders` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `cycle_id`, `position_id`, `client_order_id`, `exchange_order_id`, `purpose`, `type`, `side`, `price`, `stop_loss`, `take_profit`, `quantity`, `status`, `executed_quantity`, `average_price`, `exit_reason`, `sends`, `fills_recorded`, `created_at`, `updated_at`, `checked_at`, `pair_id`, `cancel_after` |
| `fills` | `id`, `order_id`, `user_id`, `trade_id`, `price`, `quantity`, `fee`, `fee_asset`, `at` |
| `positions` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `side`, `entry_price`, `quantity`, `stop_loss`, `take_profit`, `opened_at`, `closed_at`, `exit_reason`, `alerted_at` |

| Column | What it holds |
|---|---|
| `orders.mode`, `market`, `symbol` | Those of the agent when the order was placed. What an agent did in Demo is Demo for good (FR-BIN-11), and an order is asked about where it was placed. |
| `orders.cycle_id` | The cycle that placed it, or nothing for an order that no cycle placed. It points at no row: a cycle is recorded when it ends, which is after its orders. |
| `orders.purpose` | `Entry`, `StopLoss`, `TakeProfit`, or `Close`. |
| `orders.type` | `Market`, `Stop`, or `Limit`. A take-profit is a `Stop`, as a stop-loss is. |
| `orders.price` | The price that a stop is triggered at, or the price of an entry at a price. |
| `orders.stop_loss`, `take_profit` | Of an entry: the protection of the position that it opens. They are kept with the entry, so that the position is protected at that price also when the entry is found executed later, by what settles it. |
| `orders.exchange_order_id` | The number that Binance knows the order by. A stop of Futures that was triggered is known by the order that it became. |
| `orders.exit_reason` | Of an order that closes: why. |
| `orders.sends` | 1, or 2 when it was sent once more. |
| `orders.checked_at` | When Binance last said what has become of it. |
| `orders.pair_id` | Of a stop-loss and a take-profit of Spot: the pair that they are, which both carry. Written without its hyphens, it is the identifier that the pair carries at Binance. It is the id of no row. |
| `orders.cancel_after` | Of an entry at a price: when it is taken off the exchange if it still rests, which is the next close of the candles of its agent. |
| `fills.fee`, `fee_asset` | The fee of an execution, in the asset that Binance took it in. |
| `positions.quantity` | What the position holds: what an order that protects or closes it is for. |
| `positions.stop_loss`, `take_profit` | Prices. The order of the stop-loss is a row of `orders`, and so is the order of the take-profit on Spot. On Futures no order is placed for the take-profit. |
| `positions.exit_reason` | `StopLoss`, `TakeProfit`, `TeamClose`, `UserClose`, or `ProtectionFailed`. |
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
The take-profit of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}
The entry of agent {AgentId} in {Symbol} rests on the exchange as the order {ExchangeOrderId}, until {CancelAfter}
The entry of agent {AgentId} in {Symbol} that still rests is taken off the exchange: {EntryTakenOffReason}
The entry of agent {AgentId} was executed as the order {ExchangeOrderId} for less than an order can be for, so it opens no position
```

| Note | Detail |
|---|---|
| **Levels** | A change of an order and a position that is opened or closed are information. A stop-loss that cannot be placed is a warning, and so is an entry that bought less than an order can be for. A position that is left open with no stop-loss is an error. |
| **Why an entry was taken off** | `EntryTakenOffReason` is `TimeIsUp`, `PartlyExecuted`, `NotRunning`, or `UserClose`: see "An entry at a price". |
| **A pair that is sent once more** | Is said with the event of an order that is, with `Pair` as its `OrderPurpose`. |
| **The calls themselves** | Each call to Binance is in the log of outbound calls, with its duration and its outcome, and the number of an error of Binance under the gateway. See [exchange.md](exchange.md). |
| **The job** | `The orders of {Agents} agents are settled with the exchange`, under `Ecnanib.DecisionEngine.OrderSettlingJob`, when there are some. An agent that cannot be settled for an error of the application is said once, as an error. |

## Testing

The tests run the application on a database of their own, with a Binance that keeps orders behind the gateway (`TradingBinance` in `OrderFixtures.cs`): it takes an order, answers what became of it, takes it off, and lists its executions, in the form that Binance's documentation gives. An order at a price rests there until the test has it executed, in full or in part. A pair is two stops of one list: it is taken while the price is between its two triggers, when one is executed the other ends, and taking one off takes both. A test says what becomes of a call that places an order or a pair: taken, taken with an answer that never comes, lost, refused, or answered with a fault. What the account holds is what the test says, and more than a plan of the tests needs unless it says otherwise. The clock is the test's, so a test moves it on while the application waits for a moment of it.

| Requirement | Tests |
|---|---|
| Every order carries a unique client order identifier | `OrderIdempotencyTests`, `OrderRecordTests`, `OrderPlacementTests` |
| A simulated timeout never produces a second order | `OrderIdempotencyTests`: an entry and a stop-loss that Binance took before the call timed out, one that it does not have, and a restart |
| Every open position has a stop-loss resting on the exchange | `ProtectiveStopTests`, `PlanOrderTests`, `OrderSettlingTests` |
| A protective order that cannot be placed closes the position at market and alerts the user | `ProtectiveStopTests`, `ProtectivePairTests`, and `NotificationTests` for what the user sees |
| Spot agents are long-only, and do not borrow or short | `SpotEntryTests`, and `PlanRuleTests` and `PromptCompositionTests` for a plan and a prompt |
| Entries are market or limit orders, and an unfilled limit entry is cancelled at the next cycle | `LimitEntryTests` |
| Protection is a stop-loss and take-profit pair, where filling one cancels the other | `ProtectivePairTests` |
| A Spot agent sells only the quantity it bought | `ProtectiveStopTests`, `ProtectivePairTests`, `LimitEntryTests` |
| An entry that the quote balance does not cover is skipped, and the user is notified | `SpotEntryTests` |

A check against Binance's own test environment is in `LiveBinanceTests`: see "Checking against Binance itself" in [exchange.md](exchange.md).

## Not built yet

- Protection that Binance places by itself when an entry at a price is executed. The pair is placed by the service, within a minute of the execution, and later when the service is down then: until it is, what the entry bought has no stop-loss. Binance has a list of three orders for it, which places a pair when its entry is executed in full. Its documentation does not say how the quantity of that pair is held to what the account has once the fee of the purchase is taken in the asset, so it is not used.
- The orders of Futures (FR-BIN-F2, FR-BIN-F3, FR-BIN-F6): an entry at a price, an order for the take-profit, isolated margin, one-way mode, the leverage that the Risk Guard computed, the trigger on the mark price, and the check of the margin. Until then a plan with a limit entry places nothing there, no order rests for a take-profit, and an entry uses the leverage that the account has for the symbol, so the liquidation buffer was checked against a leverage that is not applied.
- The account event feed and reconciliation (FR-BIN-7, FR-BIN-8). Until then a stop-loss or a take-profit that was executed shows in the records when it is next asked about, and an entry at a price within a minute. What a user does at Binance by hand is not detected, but for a stop-loss or a pair that they take off: a position that its user sold at Binance is given a new pair, which Binance refuses when the account does not hold what it sells.
- The results of a position: its profit and loss, its fees, its funding, and its result in R (FR-BIN-10). The equity of an agent, and with it the loss limits (FR-RSK-8), still go by the allocated capital.
- The orders of a cycle in its record and its timeline (FR-CYC-10). Why a plan placed nothing is in the log only.
- The count of the orders that an account places, which Binance limits beside the request weight. A protective order has no turn before other calls at the limiter: one that is held back was not sent, and is settled within a minute.
- A close by hand that is told its user as closed, or failed, by a notification (FR-NTF-1). The page that asked says so.
