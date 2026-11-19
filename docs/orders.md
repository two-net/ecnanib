# Orders

Every order carries a unique client order identifier. If the outcome of a request is unknown, the system queries the order's status before any retry, so an order is never sent twice (FR-BIN-6).

Every open position has a stop-loss order resting on the exchange. If the protective order cannot be placed after an entry fills, the system closes the position at market at once and alerts the user (FR-RSK-7).

The Exchange Gateway places the entry and the protective orders on Binance, or does nothing (SRS 3.2, step 7). Failures are safe: any internal error, timeout, or dependency outage results in no new entry, and open positions stay protected by orders resting on the exchange (NFR-REL-2). A restart never creates a duplicate order (NFR-REL-3), and no entry is left without protection (NFR-REL-5).

Spot agents are long-only: they buy the base asset with the quote asset and sell to exit. They do not borrow or short (FR-BIN-S1). Entries are market or limit orders. An unfilled limit entry is cancelled at the next cycle. Protection is a stop-loss and take-profit pair resting on the exchange, where filling one cancels the other (FR-BIN-S2). A Spot agent sells only the quantity it bought and never touches other holdings in the account. If the quote balance is below what the plan needs, the entry is skipped and the user is notified (FR-BIN-S3).

Futures agents trade USDⓈ-margined perpetual contracts and can be long or short (FR-BIN-F1). They use isolated margin and one-way position mode. The system sets margin type and leverage for the agent's symbol before entry, and does not change account-wide settings: if the account is in a conflicting mode or already holds a position in the symbol, the agent does not start and the user is told what to change (FR-BIN-F2). Protection is a reduce-only stop-loss and a reduce-only take-profit resting on the exchange, triggered on the mark price (FR-BIN-F3). An entry is skipped, and the user notified, when the Futures wallet lacks the margin the plan needs (FR-BIN-F6).

The current funding rate and the next funding time are included in the data package, and funding paid or received is recorded per position (FR-BIN-F4). The system checks the margin state of open positions at each reconciliation and alerts the user when a position approaches liquidation. It never adds margin on its own (FR-BIN-F5).

Order and position changes are tracked through the exchange's account event feed where available, and by reconciliation at startup and every 5 minutes. On any mismatch the exchange is treated as the source of truth, records are corrected, and the mismatch is logged and shown (FR-BIN-7). Actions the user takes directly on Binance (for example closing a position by hand) are detected by reconciliation and reflected in the agent's records (FR-BIN-8).

What is built is step 7 of the decision cycle, on both markets. Both have an entry at the market, an entry at a price, which rests, a stop-loss that rests, and an order that closes a position at the market. Spot has the pair of a stop-loss and a take-profit. Futures has a take-profit that is an order of its own, the margin and the leverage of the symbol, which are set before an entry, and the trigger on the mark price. With them a plan opens a position, closes it, reverses it, and moves its protection, and a user closes a position by hand. The positions of Futures are watched while they are open: their margin is checked, their user is told when one approaches liquidation, and what each received and paid as funding is stored. The agents are reconciled with the exchange, as the application starts, every few minutes, and at once when the account event feed says that something changed in an account: what the account holds is held against the records, the records are corrected, and the user of the agent is told what did not match.

What places the orders is in `src/Ecnanib/DecisionEngine`: `IPlanOrders`, `AgentOrders`, `StoredOrders`, `Order`, `Fill`, `Position`, `AgentPositions.cs`, and `OrderSettlingJob`. What watches the positions is there as well: `PositionWatch`, `PositionWatchJob`, and `Funding`. So is what reconciles: `AccountReconciliation`, `ReconciliationJob`, `AccountFeed`, and `AccountFeedJob`. The orders of the exchange are in `src/Ecnanib/ExchangeGateway/Trading`, and Binance's in `BinanceOrders`. The rules below are enforced by `OrderIdempotencyTests`, `ProtectiveStopTests`, `ProtectivePairTests`, `LimitEntryTests`, `SpotEntryTests`, `FuturesEntryTests`, `FuturesProtectionTests`, `PlanOrderTests`, `OrderSettlingTests`, `OrderRecordTests`, `FundingRecordTests`, `LiquidationWarningTests`, `PositionWatchJobTests`, `ReconciliationTests`, `ReconciliationJobTests`, and `AccountFeedJobTests` of `tests/Ecnanib.Tests/DecisionEngine`, and by `OrderPlacementTests`, `FuturesAccountTests`, `FuturesPositionTests`, `FundingReadTests`, and `AccountFeedTests` of `tests/Ecnanib.Tests/ExchangeGateway`.

The calls that the gateway makes for an order are in [exchange.md](exchange.md). What the Risk Guard accepts, and the size of an order, is in [risk-guard.md](risk-guard.md). What a cycle does before it comes to its orders is in [team.md](team.md) and [cycles.md](cycles.md). What Agents asks about a position, and the states of an agent, is in [agents.md](agents.md), the key that an order is placed with in [credentials.md](credentials.md), and what a user is told in [notifications.md](notifications.md).

## Where it stands in a cycle

`TeamCycle` hands what the Risk Guard accepted to `IPlanOrders`, and nothing else: the only path from what a model answered to an order is schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6).

```csharp
public interface IPlanOrders
{
    Task PlaceAsync(CycleStart cycle, GuardResult accepted, CancellationToken cancellationToken);
    Task SettleAsync(Guid agentId, CancellationToken cancellationToken);
    Task<bool> ReconcileAsync(Guid agentId, CancellationToken cancellationToken);
    Task<PositionCloseOutcome> CloseAtMarketAsync(Guid agentId, CancellationToken cancellationToken);
    Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **An order is made from a result of the Risk Guard** | `PlaceAsync` takes the `GuardResult`, and refuses one that is not accepted. The quantity of an entry is that of `GuardResult.Size`, which the check computed, and so is the leverage that the symbol is given on Futures (FR-RSK-5). The price of an entry at a price, the stop-loss, and the take-profit are those of `GuardResult.Case`. |
| **One type uses the orders of the gateway** | `AgentOrders`. A test pins it, so another shows in a review as one more way to an order. |
| **The orders of the agent are settled first** | Before the team is run, `TeamCycle` asks `SettleAsync`: a stop-loss or a take-profit may have been executed since the last cycle, an entry at a price of the cycle before is taken off the exchange, and the team decides on what the agent holds now. See "Settling" and "An entry at a price". |
| **The outcome of a cycle is the decision of its team** | Whatever becomes of the orders. What was placed is in the rows of the orders, under the id of the cycle. A cycle whose orders fail with an error is recorded with its decision, and the error is said once. |
| **What settles and what closes for a user take no result** | They open nothing, so that risk can always be reduced (FR-RSK-4, FR-AGT-12). |
| **What reconciles settles, and then holds the position against the account** | `ReconcileAsync` does what `SettleAsync` does, and holds the open position of the agent against what the account of its user holds, which is the source of truth (FR-BIN-7, FR-BIN-8). It opens nothing either: the one thing that it places is the protection of a position that it found unprotected, or holding more than its protection is for. See "Reconciling". |
| **What reads the account for Agents places nothing** | `AccountConflictAsync` says what in the account of an agent's user stands in the way of the agent, before the agent is started or resumed: see "The account of Futures". It only reads. |

## What a plan places

| The Risk Guard accepted | What is placed |
|---|---|
| Open long, open short | The entry: a buy for long, a sell for short. It is placed at the market, or at the price of a plan that names one, where it rests: see "An entry at a price". On Futures the symbol is given isolated margin and the leverage of the order before it: see "The margin of Futures". When it is executed, the stop-loss, on the other side, at the stop-loss of the plan. On Spot the take-profit of the plan is placed with it, as one pair: see "The pair of the Spot market". On Futures it is placed once the stop-loss rests, as an order of its own: see "The take-profit of Futures". |
| Close | The protection is taken off the exchange, and the position is closed at the market. See "Closing a position". |
| Reverse | The close, as above. When the position is closed, and only then, the entry on the other side, with its stop-loss. Only Futures has it. |
| Adjust protection | The stop-loss that rests is taken off, and one is placed at the new price. On Spot it is the pair that is taken off and placed anew, when the stop-loss or the take-profit moves. On Futures each price moves its own order, and the other rests on. See "Moving the stop-loss". |

An order that is not an entry only closes a position. The gateway sends it to Futures as an order that only reduces, so that it opens nothing when the position is gone already. On Spot it sells what the position holds, which is what the agent bought, and no more (FR-BIN-S3).

### When no entry is placed

Each is said in the log, with its reason, and leaves no order behind. They are asked in this order, and the first that holds is the reason.

| Reason | When |
|---|---|
| `SpotLongOnly` | The entry sells, on Spot. An agent there buys, and sells what it bought to exit (FR-BIN-S1). A plan that shorts on Spot is refused when it is read: see "What a plan may do" in [team.md](team.md). What places the orders holds to it as well, for a sale that opens would sell what else the account holds. |
| `EntriesPaused` | `IExchangeClock.EntriesPaused` says so for the market and the environment of the agent: the clock of the service is not near Binance's, or was not read (FR-BIN-5). |
| `NoStopOrders` | The symbol takes no stop, or the exchange did not say its rules. No position is opened where no stop-loss can rest. See "Symbols" in [exchange.md](exchange.md). |
| `NoPairOrders` | The symbol takes no pair of a stop-loss and a take-profit, on Spot. No position is opened there whose protection cannot rest as a pair (FR-BIN-S2). |
| `NoTakeProfitOrders` | The symbol takes no take-profit as an order of its own, on Futures. No position is opened there whose take-profit cannot rest beside its stop-loss (FR-BIN-F3). |
| `NotRunning` | The agent no longer opens positions: its user paused or stopped it, or it waits for its user, while its team decided. It is asked before the account is read, and again under the lock of the account, as the row of the entry is written. |
| `Holding` | The agent holds a position, or has an order that is not over, an entry that rests among them. The Risk Guard rejects such a plan. This holds to it as well, at the same two moments. |
| `AccountUnknown` | How the account stands on Futures could not be read. A failure leads to no entry (NFR-REL-2), and nobody is told. See "The account of Futures". |
| `AccountConflict` | The account is in hedge mode or in Multi-Assets mode, or holds a position in the symbol that the agent did not open, on Futures. Its user is told what to change, and the agent waits for them (FR-BIN-F2). See "The account of Futures". |
| `QuoteBalance` | The account holds less of the asset that the symbol is priced in than the entry needs, on Spot. The entry is skipped, and its user is notified. See "The quote balance". |
| `Margin` | The wallet of Futures has less of that asset available than the margin of the entry. The entry is skipped, and its user is notified (FR-BIN-F6). See "The margin of Futures". |
| `BalanceUnknown` | What the account holds could not be read, on either market. A failure leads to no entry (NFR-REL-2). |
| `MarginRefused` | Binance refused to give the symbol isolated margin, or the leverage of the order, on Futures. Its user is told. See "The margin of Futures". |
| `MarginNotSet` | It is not known whether the symbol has isolated margin and the leverage of the order: a call did not say. No entry is placed at a leverage that the Risk Guard did not check (FR-RSK-6). |

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
| The Risk Guard does not ask it. | The size of an order is held to the agent's equity, which is the capital that its user allocated: see "The size of an order" in [risk-guard.md](risk-guard.md). |

### The account of Futures

Futures agents use isolated margin and one-way position mode. The system does not change account-wide settings: if the account is in a conflicting mode or already holds a position in the symbol, the agent does not start and the user is told what to change (FR-BIN-F2). `FuturesEntryTests` holds what places the orders to the rules below, and `AgentAccountTests` of `tests/Ecnanib.Tests/Agents` the start of an agent.

| Rule | Detail |
|---|---|
| **The account is read before an entry, and before an agent runs** | `IExchangeAdapter.GetFuturesAccountAsync` reads the settings of the account and its positions in the symbol of the agent, with the key of the agent's user: two calls, of 5 of request weight each. See "The account of Futures" in [exchange.md](exchange.md). |
| **Three things stand in the way** | `AccountConflict.HedgeMode`: the account keeps a long and a short position of a symbol apart, and an agent has one position. `MultiAssetsMode`: the account margins with more than one asset, in which Binance takes no isolated margin. `PositionInSymbol`: the account holds a position in the symbol that the agent did not open. |
| **The first two are of the whole account, and are never changed** | The service has no call that sets either. What is to change is its user's to change at Binance, and they are told what. |
| **A position in the symbol is the agent's own when its records hold one** | Or an order that is not over, which may have opened one. So an agent that was paused with its position open is resumed. What the account holds beside the records is not compared: see "Not built yet". Before an entry the agent holds nothing, so a position there is not its own. |
| **An account that cannot be read is not one that nothing stands in the way of** | `AccountConflict.NotKnown`. No entry is placed, as `AccountUnknown`, and nobody is told: it is not known that anything is to change. An agent is not started or resumed: its page says to try again. |
| **A position that is still listed right after a reverse closed it alarms nobody** | A plan that reverses closes the position of the agent, and enters on the other side at once. A position that the account still says then may be the one that was closed a moment ago, which Binance has not taken off its list yet. No entry is placed, as `AccountUnknown`, nobody is told, and the agent runs on: a position that is still there is found by its next entry. |
| **A conflict that an entry meets has the agent wait for its user** | The entry is skipped, as `AccountConflict`, its user is told what to change, with a notification of the type `AttentionRequired`, and `IAgentTrading.RequireAttentionAsync` moves the agent to Attention required, with the cause `ExchangeSettingConflict` (SRS 3.3). Its user resumes it once the account is changed: a resume asks again. See [agents.md](agents.md) and [notifications.md](notifications.md). |
| **It is asked for Agents by what places the orders** | `IPlanOrders.AccountConflictAsync`, behind `IAgentExposure.AccountConflictAsync`: only `AgentOrders` is handed the key of a user. For an agent on Spot nothing stands in the way, and Binance is asked nothing. |

### The margin of Futures

The system sets margin type and leverage for the agent's symbol before entry (FR-BIN-F2). An entry is skipped, and the user notified, when the Futures wallet lacks the margin the plan needs (FR-BIN-F6).

| Rule | Detail |
|---|---|
| **The wallet is read before the entry is begun** | `IExchangeAdapter.GetBalancesAsync` reads the wallet of Futures of the agent's user, in the mode of the agent. It is one call for each entry, of 5 of request weight. |
| **The entry needs its margin** | Its quantity times its price, as for Spot, divided by the leverage of the order: `GuardResult.Size.Leverage`. What counts is `ExchangeBalance.Available` of `Agent.QuoteAsset`, which is what a new order can use. A wallet that has exactly the margin covers it. |
| **The user is told, once in a day for an agent** | With a notification of the type `EntrySkipped`, which says the margin that the entry needs, its leverage, and what the wallet has available. A wallet that cannot be read covers nothing, and nobody is told. An entry that Binance refuses has the wallet read once more. All as in "The quote balance". |
| **Then the symbol is given isolated margin, and the leverage of the order** | `IExchangeOrders.SetIsolatedMarginAsync`: two calls, each a setting of the symbol in the account, and neither one of the whole account. The leverage is the one that the Risk Guard computed for this order, which is what its liquidation buffer was checked against (FR-RSK-5, FR-RSK-6). It is set anew for each entry: the next order may take another. |
| **The entry is placed only once Binance has said that both are set** | A symbol that is isolated already is one that is: Binance answers it with an error of its own. A leverage is set when Binance answers with that leverage. Anything else is `MarginNotSet`: no entry is placed, and the next cycle decides anew. |
| **A setting that Binance refuses is said to the user** | As `MarginRefused`, with a notification of the type `EntrySkipped`, once in a day: Binance does not change the margin of a symbol while the account has open orders or a position in it, which are for its user to cancel or close. |
| **Nothing is set for an entry that is not placed** | The account, the state of the agent, and the wallet are asked first. So an agent whose entry is skipped changes nothing at Binance. |
| No margin is added to a position, and none is moved. | The margin of a position is taken from the wallet by the order that opens it. The gateway has no call that adds margin to one (FR-BIN-9, FR-BIN-F5). |

## An entry at a price

An entry is an order at the market, or an order at a price, which rests on the exchange until it is executed (FR-BIN-S2). It is the same on both markets, but for what the last three rules say. `LimitEntryTests` holds it to the rules below.

| Rule | Detail |
|---|---|
| **It is one order, good until it is taken off** | At the price of the plan, for the quantity that the Risk Guard computed. Binance executes it at once where the market has reached the price, and it is protected then as an entry at the market is. |
| **It opens no position while it rests** | Its row says `Open`, or `PartiallyFilled` with what is executed so far. The position is opened when the entry is over, for what was executed of it. Until then the agent has an entry that has not filled, which its data package says, and no second entry: see "What an agent holds". |
| **It is asked about every minute, and at once when the account says a change** | Where the exchange has an account event feed, the feed says that an order of the account changed, and the agent is reconciled at once, which asks about the entry: see "The account event feed". Where it has none, or the feed is not followed at that moment, nothing tells the service that an order was executed. So the job that settles the orders asks about an entry that rests with every run as well, and what it bought has its stop-loss and its take-profit within a minute of the execution at the latest. |
| **It is taken off when the next candle of its agent has closed** | `orders.cancel_after` is that close: the close that the cycle of the entry was for, and the length of a candle of the agent after it. The cycle that starts then settles the orders of the agent before its team decides, which takes the entry off: the team decides on an agent that holds nothing. The job takes it off as well, so a cycle that does not start, because it came too late, the candle was not there, or the service was down, does not leave an entry of the day before resting. |
| **A part that is executed is protected at once** | When a part is executed that an order can be for, the rest of the entry is taken off, and the part is a position with its protection. Left to rest, the entry would leave what it bought without a stop-loss until the rest is executed or its time is up, which is a day on the longest timeframe. On Futures any part is one that an order can be for: an order that only reduces a position is held to no least value, and the part is leveraged. |
| **On Spot a part that no order can be for is no position** | Below the least quantity of the symbol, or worth less than its least value. Such a part is left to grow while the entry rests. When the entry is over with it, it opens no position: nothing could protect it or sell it, and an agent that held it would wait for its user for good. What was bought stays in the account, the executions are stored, and the log says it as a warning. |
| **It is taken off when its agent no longer opens positions** | An agent that its user paused or stopped, or that waits for its user, gets no cycle that would take it off. The job asks the state of the agent, under the lock of its account, and takes the entry off. |
| **It is taken off when its user closes the position** | An agent has a position, as far as its user is asked, from the moment that its entry is written: see "What an agent holds". "Close position now" takes an entry that rests off, and sells what it bought by then. |
| **What it bought is what the agent holds** | On Spot what was executed, less a fee that Binance took in the asset that was bought, down to the quantity step: see "The stop-loss". The answer about an order that rested has no executions, so they are read. On Futures it is what was executed: no fee is taken in the contract. |

## An order is never sent twice

| Rule | Detail |
|---|---|
| **Every order carries an identifier of its own** | The id of its row, as 32 hexadecimal digits. Binance takes it on both markets. A unique index holds the column `client_order_id` to it. |
| **The row is written before the order is sent** | With the status `Sending`. So whatever happens to the call, and to the application, there is a row that says that an order may be with Binance, and by which identifier it is asked about. |
| **A call is made once** | The gateway makes no call again. How the call ended says whether there is an order: see "The orders" in [exchange.md](exchange.md). |
| **An unknown outcome is asked about before anything else** | The order is asked about by its identifier. Nothing else is done with it, and nothing is placed beside it, until Binance has said what became of it. |
| **Binance's word that it has no such order counts once the order could no longer arrive** | 10 seconds after the call ended (`ExchangeOrderTimes.FinalAfter`). Binance takes a call for 5 seconds after the time that the call carries, and no longer. Before that the answer says nothing: the order may still arrive. The same holds for an order that rests: that Binance has none counts once the order has rested for that long, so that no order is placed a second time beside one that Binance has just taken. |
| **An entry is never sent again** | An entry that Binance does not have was not placed, and the next cycle decides anew. No failure leads to an entry (NFR-REL-2). |
| **A stop-loss and a close are sent once more** | As the same order, with the same identifier, and only after Binance said that it does not have it once it could no longer arrive. An order that rests keeps its identifier taken, so Binance would refuse the second one if it had the first after all. When Binance refuses the second one, the order is asked about once more before the refusal is believed. The take-profit of Futures, which is an order of its own, is sent once more in the same way. |
| **Nothing is sent once more for a position that is closed** | On Futures the other order of a position may have closed it meanwhile. An order that protects it would rest with nothing to protect, and act on the next position of the agent. |
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
| **It is placed when the entry is executed** | For what the agent holds of the entry, at the stop-loss of the plan, as a stop that is placed at the market when the price reaches it. On Spot it is one order of a pair with the take-profit, and everything below holds for the pair: see "The pair of the Spot market". On Futures it only reduces the position, and is triggered on the mark price (FR-BIN-F3). |
| **On Spot it is for what was bought, less the fee** | Binance may take the fee of a purchase in the asset that was bought. What the account then holds is less than what was executed, and an order for more would be refused. The executions say the fee: they are in the answer to the entry, or are read. The quantity is rounded down to the quantity step of the symbol. |
| **It is placed at most twice** | A second time when the first was not placed: it was not sent, or Binance does not have it. A stop-loss that Binance refused is not placed again. |
| **Before a position is given up on, the account is asked** | A stop-loss that Binance refuses may be one for a position that the account no longer holds: its user closed it at Binance, or sold what it held. So what the account holds is asked before what still rests for the position is taken off and the position is closed at market. And where Binance had not said, it is asked once more before its user is told that the position is open with no stop-loss, 10 seconds later, by when an order that closed a part of it is in what Binance says of the account. A position that is gone is closed in the records, one that holds less is protected for what it holds, and nobody is told of a failure that was none: see "Reconciling". An account that cannot be read stops nothing. |
| **When it cannot be placed, the position is closed at market at once** | And its user is told, with a notification (FR-RSK-7). The agent runs on. The position says `ProtectionFailed` as how it ended. |
| **When it cannot be closed either** | The position is open with no stop-loss. Its user is told that, once. An agent that runs moves to Attention required, with the cause `PositionUnprotected`, and one that its user paused or stopped stays as it is. What settles the orders tries again every minute: the stop-loss first, and then the close. |
| **One that is gone is placed again beside a take-profit that rests** | On Futures, where the take-profit is an order of its own: a stop-loss is begun whatever has become of the take-profit of the same position, and the take-profit is not touched for it. |
| **A cycle that is being stopped still protects its entry** | Once the entry is sent, the stop-loss is sent as well, once. What is left after that, an unknown outcome, a second try, and the close at market, is done by what settles the orders (NFR-REL-5). |
| **A stop-loss that rests is asked about** | At the start of each cycle of the agent, before a position is closed, by the job that settles, when it was last asked about longer ago than `Binance:ReconcileMinutes`, with every reconciliation of the agent, and at once when the account event feed says a change. Executed: the position is closed in the records, as `StopLoss`. Taken off or ended at Binance, with nothing of it executed: the position is unprotected, and gets a new stop-loss, and its user is told that the records did not match: see "What is corrected". |

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
| Futures has no such pair. | A position there has its stop-loss, and its take-profit as an order of its own: see "The take-profit of Futures". A position on Spot whose plan named no take-profit, which no plan that opens a position does (FR-CYC-7), has its stop-loss alone, and so has one that was opened before the pair was built, until its protection is next moved. |

## The take-profit of Futures

On Futures protection is a reduce-only stop-loss and a reduce-only take-profit resting on the exchange, triggered on the mark price (FR-BIN-F3). Binance has no pair there: the two are conditional orders of their own, and it ends neither for the other. `FuturesProtectionTests` holds it to the rules below.

| Rule | Detail |
|---|---|
| **The stop-loss comes first, and alone** | It is placed as in "The stop-loss", and nothing that becomes of the take-profit takes it off, but for a take-profit that Binance refuses: see below. Every open position has a stop-loss resting on the exchange (FR-RSK-7), and an agent is settled when its position has one, whatever has become of its take-profit. |
| **The take-profit is placed once the stop-loss rests** | For what the position holds, at the take-profit of the plan, on the side that closes: a take-profit that is placed at the market when the mark price reaches it. It only reduces the position, so it opens nothing when the position is gone. |
| **They are two rows, and no pair** | Each with the identifier that its order carries, and neither with a `pair_id`. Each is sent, asked about, sent once more, and taken off as the one order that it is: see "An order is never sent twice". The row of the take-profit is written only while the stop-loss of its position rests, and the agent has no other order that is not over. |
| **A take-profit that Binance refuses closes the position at market** | Binance refuses one that would be triggered at once: the mark price has passed it, which is what the take-profit waits for. So the stop-loss is taken off, the position is closed at market, as `ProtectionFailed`, and its user is told that the take-profit could not be placed. It is what Spot does with a pair that Binance refuses. Protection that a plan moved goes back to its old price first: see "Moving the stop-loss". |
| **When it cannot be closed, the stop-loss goes back, and the take-profit is given up** | The position says no take-profit from then on, until a plan names one, and its user is told that it is open with its stop-loss alone. Tried again with every run, the take-profit would have the stop-loss taken off every time. When the stop-loss cannot be placed again either, the position is one with no stop-loss: see "The stop-loss". |
| **A take-profit that was not placed is owed, and closes nothing** | It was not sent, its outcome is not known, the key of its user is not there, or the cycle was stopped once the stop-loss was sent, which is the one more call that such a cycle has (NFR-REL-5). The position has its stop-loss. The job that settles the orders places the take-profit with its next run, which is within a minute. No row is written for one that cannot be sent. |
| **When one of the two has closed the position, the other is taken off** | Binance leaves it resting. Left there, it would reduce a position that the agent opens later. What settles the orders takes it off in the pass that finds the position closed, and with every run until it is gone. Until then the agent is not settled, so it gets no cycle, and it holds an order that is not over, so it gets no entry. |
| **A take-profit that was executed closes the position** | In the records, as `TakeProfit`, when it is next asked about: see "A stop-loss that rests is asked about". Nobody is told: it is no failure. |
| **One that was taken off at Binance is placed again** | The stop-loss beside a take-profit that rests, and the take-profit beside a stop-loss that rests: what settles the orders places what is missing. |
| **The take-profit is taken off before the stop-loss** | Before a close. So what is left when Binance does not take one of the two off is the stop-loss. |

### Moving the stop-loss

A plan that adjusts protection names a stop-loss that reduces the risk: the Risk Guard holds it to that (FR-CYC-6). The take-profit moves in either direction.

1. The stop-loss that rests is taken off the exchange. When Binance does not take it off, nothing changes: the position is protected at the old price.
2. The new price is written to the position, and a stop-loss is placed there.
3. When it cannot be placed there, the old price is written back, and a stop-loss is placed at the old price again.
4. When that cannot be placed either, the position is closed at market, and its user is told.

On Spot it is the pair that is taken off and placed, when the plan moves the stop-loss, the take-profit, or both, and both old prices that are written back. So a take-profit that Binance refuses, because the price has passed it, leaves the position with the pair that it had.

On Futures each price moves its own order. The stop-loss is moved first, as above, while the take-profit rests. Then the take-profit that rests is taken off, its new price is written, and one is placed there, while the stop-loss rests: a plan that moves the take-profit alone leaves the position with its stop-loss all the while. A take-profit that Binance refuses at its new price is placed at the old one again, and when Binance refuses that as well, the position is closed at market: see "The take-profit of Futures".

## Closing a position

For a plan that closes or reverses, for the user of an agent (FR-AGT-11, FR-AGT-12), and for a position whose stop-loss cannot be placed.

| Step | Rule |
|---|---|
| 1 | The stop-loss is taken off the exchange first, and on Spot the take-profit with it, as the one pair that they are. On Spot it holds what the close sells, and on Futures it would be left behind. There the take-profit is taken off before the stop-loss, each as the order that it is. When Binance has no stop-loss to take off, it is asked what became of it: a stop-loss or a take-profit that was executed has closed the position already, and nothing more is placed. When it cannot be taken off, the close fails, and the position is open as it was, with its stop-loss. |
| 2 | The order that closes the position is placed at the market, for what the position holds, at most twice: a second time when the first was not placed, or ended with a part of the position. |
| 3 | When the position is not closed, its stop-loss is placed again, as in "The stop-loss", and its take-profit with it or after it. |

Between step 1 and step 2 the position has no stop-loss. That cannot be avoided on Spot.

`CloseAtMarketAsync` settles the orders of the agent first, and on Futures asks what the account holds of its position, as a reconciliation does: a position that its user closed at Binance already is written as closed, and is not closed a second time. Then it takes an entry that rests off the exchange, and answers with an outcome, and never with an error:

| Outcome | When |
|---|---|
| `Closed` | The position is closed. |
| `NothingOpen` | The agent holds no position: it never did, its stop-loss or its take-profit was executed, its entry rested and was taken off with nothing executed, or its account no longer holds it, which the records say from then on. |
| `Failed` | The position is open, with its stop-loss, or the outcome of an order of the agent is not known yet, or its entry could not be taken off. |

Two closes of the same position at the same moment send one order: the second finds nothing open.

## Settling

`SettleAsync` settles the orders of one agent with the exchange, as far as the exchange says. It waits for nothing, and sends no order that Binance may have already.

| Step | What |
|---|---|
| 1 | Each order that is not over is asked about by its identifier, and what Binance says is written. The two orders of a pair are asked about together. An order that is being sent now, by another instance, is left to that one. |
| 2 | An entry that still rests is taken off the exchange when its time is up, when a part of it is executed that can be protected, or when its agent no longer opens positions: see "An entry at a price". |
| 3 | A stop-loss or a take-profit of Futures that still rests for a position which is closed is taken off the exchange: see "The take-profit of Futures". |
| 4 | An open position that no order is under way for has its stop-loss, or gets it now, as in "The stop-loss". On Futures a position whose stop-loss rests gets the take-profit that it lacks. |
| 5 | The executions of the orders that are over are read and stored, where they are not stored yet. |

| Rule | Detail |
|---|---|
| **A job settles every minute** | `OrderSettlingJob`, named `order-settling`, for the agents that have something to settle: an order whose outcome is unknown, an entry that rests, an open position without a stop-loss that rests, a stop-loss or a take-profit that was last asked about longer ago than `Binance:ReconcileMinutes`, executions that are not stored, and on Futures a position whose take-profit does not rest beside its stop-loss, and an order that protected a position which is closed. Most runs find none. |
| **A restart settles first** | The job is due as the background work starts when there is anything to settle. |
| **An agent that is not settled gets no cycle** | `IReconciliation.HasReconciledAsync` answers from the rows: an agent with an order in `Sending` or `Unknown`, with an open position without a stop-loss that rests, or with an order of no pair that protected a position which is closed and is not over, is not reconciled. So no order is placed for it until Binance was asked. That is what keeps a restart from placing an order twice (NFR-REL-3). An entry that rests is settled: its agent gets the cycle that takes it off. What reconciles answers beside it, for an agent that holds something from before the application started: see "Reconciling", and "After a restart" in [cycles.md](cycles.md). |
| **No agent waits for another** | The agents are settled a few at a time, each on its own. One that cannot be settled, because its key is gone or refused, keeps no other waiting. |
| **A refused key is checked again** | When Binance answers a call about an order as it answers a wrong credential, `IKeyValidation.RevalidateExchangeKeyAsync` is asked. The check decides whether the key is invalid (FR-KEY-3). |

## Reconciling

Order and position changes are tracked by reconciliation at startup and every 5 minutes. On any mismatch the exchange is treated as the source of truth, records are corrected, and the mismatch is logged and shown (FR-BIN-7). Actions the user takes directly on Binance (for example closing a position by hand) are detected by reconciliation and reflected in the agent's records (FR-BIN-8). `ReconciliationTests` and `ReconciliationJobTests` hold it to the rules below.

`ReconcileAsync` reconciles one agent: it settles the orders of the agent, as in "Settling", which asks about every order that is not over, and then holds the open position of the agent against what the account of its user holds. It says whether the agent is reconciled: Binance answered for every order, the account holds the position as the records say now, and nothing of the agent is left to settle.

| Rule | Detail |
|---|---|
| **A job reconciles at the time between reconciliations** | `ReconciliationJob`, named `reconciliation`, every `Binance:ReconcileMinutes`, which is 5 in the tracked file, for every agent that holds something: an open position, or an order that is not over. An agent that holds neither has nothing that an account could differ from, and nobody is asked about it. |
| **A start reconciles first** | The job is due as the background work starts when an agent holds anything (NFR-REL-3). An agent that holds something from before the application took up the background work gets no cycle until it was reconciled: see "After a restart" in [cycles.md](cycles.md). |
| **The account event feed reconciles at once** | When the feed says that something changed in an account, the agent that holds something there is reconciled then, and not at the next run of the job: see "The account event feed". |
| **No agent waits for another** | The agents are reconciled a few at a time, each on its own. One that cannot be reconciled, because its key is gone or Binance does not answer for it, keeps no other waiting. It is tried once more before the run ends, `ReconciliationJob.Again` later, which is 15 seconds: a call that the limiter held back is often answered then. After that it is tried with the next run. |
| **Whatever state its agent is in** | A position is reconciled while it is open: whether its agent runs, is paused, waits for its user, or was stopped with its position kept. |
| **A refused key is checked again** | As in "Settling". |

### What the account holds

| Market | What the position is held against | When |
|---|---|---|
| Futures | The position of the account in the symbol: `IExchangeAdapter.GetFuturesPositionAsync`, one call of 5 of request weight. An account in one-way mode has one position in a symbol, and a user has one active agent for a symbol (FR-AGT-9): what the account holds on the side of the position is the position, and nothing of it where the account holds the other side. | With every reconciliation, before a position is closed for its user, and before a position is given up on because its protection cannot be placed. |
| Spot | What the account has of the asset that the position holds: `IExchangeAdapter.GetBalancesAsync`, one call of 20 of request weight. It is `ExchangeBalance.Total`, with what orders hold of it, less what the other open positions of the user hold of the same asset. | Only once nothing that the agent placed holds it: before a position is given up on because its protection cannot be placed. |

- **The Spot market has no positions.** While the pair of a position rests, or its stop-loss, Binance holds what that sells, and nothing of it can have been sold: there is nothing to compare. Once it no longer rests, an account that has at least what the position holds, holds the position, whatever else it has. What its user holds beside that is not the agent's, and is never counted as the agent's (FR-BIN-S3). An account that has less holds what an order can still be for, rounded down to the quantity step, at the price that the symbol last traded at, and nothing of it otherwise: see "An entry at a price".
- **It is what is with orders as well.** An order that was taken off a moment ago may not have let go of what it held yet. That is no sale.
- **What cannot be told apart is not compared.** Where another open position of the user is in a symbol whose asset Binance did not say, the account is not held against either.

### When the account is believed

Closing the records of a position that is open would take its stop-loss off the exchange. So the exchange is the source of truth, and it is believed only once it is certain what it said.

| Rule | Detail |
|---|---|
| **A position whose stop-loss does not rest is protected first** | It is held against the account with the next pass, and the agent is not reconciled until then: an account that differs is asked a second time, a while later, and no position waits that long for its stop-loss. What protects it asks the account itself before it gives the position up: see "The stop-loss". |
| **Nothing is compared while an order of the agent could explain the account** | While the position is newer than `ExchangeOrderTimes.FinalAfter`, which is 10 seconds, while an order of the agent that is over was executed that recently, and while anything of the agent is under way but the orders that protect the position, each resting with nothing of it executed. Binance may not say yet in the account what an order did a moment ago. |
| **An account that differs is asked a second time** | No sooner than 10 seconds after the first. In between every order of the agent is asked about, and each must answer that it rests as it did: under the number that it had, with nothing executed. A stop that Binance has triggered carries the number of the order that it became, so it does not pass for one that rests. |
| **Only the same difference twice is a mismatch** | An account that says something else the second time, an order that does not answer, and an order or a position that moved meanwhile leave the records as they are. The agent is not reconciled then, and is asked about again with the next run. |
| **An account that cannot be read says nothing** | It is a warning in the log. Nothing is taken for a position that is gone because Binance did not say, and what protects a position is never held back for it. |
| **The records are written under the lock of the account** | And only while the position and the orders of its agent still are as Binance was asked about them: an order that another instance of the application began or saw executed meanwhile is what explains the account. On Spot the other open positions of the user must still hold what they held as well: two positions that share an asset are worked out against each other, so of two that are corrected at the same moment the second is worked out anew, and one sale is not counted against both. |
| **Nothing is corrected for what is being stopped** | Taking the protection of a position off, to place it anew for more, is nothing to begin as the application stops. A pass that has taken it off protects the position before it ends, whatever else becomes of it. |

### What is corrected

| The account | The records | What follows |
|---|---|---|
| Holds nothing of a position of Futures, or a position on the other side | The position is closed, with `ClosedAtExchange` as how it ended. | What rests of its stop-loss and its take-profit is taken off the exchange, as what one order of a position leaves behind is: see "The take-profit of Futures". |
| Holds less of a position of Futures | The position holds what the account holds, at the entry price that Binance says. | Its orders only reduce the position, so they close no more than the account holds. They rest on. |
| Holds more of a position of Futures | The position holds what the account holds, at the entry price that Binance says. The agent manages all of it from then on. | What protects it was for what the records said: it is taken off first, the records are written only once that is gone, and the protection is placed anew for all that is held, as in "The stop-loss". When it cannot be placed, the whole position is closed at market, as any position is (FR-RSK-7). |
| Has less of the asset than a position of Spot holds, with nothing resting for it | The position holds what an order can still be for, or is closed, with `ClosedAtExchange`, where that is nothing. | The pair is placed for what the position holds. |
| No longer has a stop-loss or a take-profit resting that the records said rests, with nothing of it executed | The order is over, as `Canceled` or `Expired`. | The protection is placed again: every open position has a stop-loss resting on the exchange (FR-RSK-7). |

| Rule | Detail |
|---|---|
| **Every mismatch is logged and shown** | It is a warning in the log, which names the agent, the symbol, and which mismatch it was. And its user is told, with a notification of the type `ReconciliationMismatch`, which says what Binance holds, what the records said, and what they say now: see [notifications.md](notifications.md). It is on the page of the agent. |
| **The state of the agent stays** | A running agent runs on, and its team decides anew at its next cycle. An agent that its user paused or stopped stays so. |
| **An order of the agent that turns out to have closed the position says why** | A position that was closed as `ClosedAtExchange`, of which an order of the agent is found executed later, has the exit reason of that order from then on: `StopLoss`, `TakeProfit`, or that of its close. |
| **What the account holds that no agent placed is left alone** | Orders that its user placed by hand, and what the account holds of an asset beside a position. A position of Futures in the symbol of an agent that holds none is found before an entry, where it stands in the way: see "The account of Futures". |
| **Whether its user or Binance closed a position is not told apart** | A liquidation is a position that the account no longer holds, as one that was closed by hand is. The notification says that no order of the agent closed it. |

What it costs of Binance's limit on requests: for each agent that holds something, with each run, what asking about its orders costs, which is 4 for an order of Spot and 1 for one of Futures, and 5 for the position of an agent of Futures.

## The account event feed

Order and position changes are tracked through the exchange's account event feed where available (FR-BIN-7). The gateway follows the feed of an account, and says when something changed in it: see "The account event feed" in [exchange.md](exchange.md). `AccountFeed` says which accounts are followed, and reconciles an agent when its account says a change. `AccountFeedJobTests` holds it to the rules below.

| Rule | Detail |
|---|---|
| **An event is a signal, and no record** | The gateway says that something of a symbol changed in the account of a user, on a market in an environment, and nothing of what. The agents of that user that hold something there, in that symbol, are reconciled, as in "Reconciling": Binance is asked about their orders, and about what the account holds, and what it answers is written. Nothing of an event is written. So an event that is missed, late, or not what Binance's documentation shows changes when Binance is asked, and never what the records say. |
| **An account is followed while an agent holds something in it** | An open position, or an order that is not over, where the configuration file names the address of a feed for the market in the environment. What changes in any other account is nothing that the records could differ from. |
| **A job follows every minute** | `AccountFeedJob`, named `account-feed`, and as the background work starts when there is an account to follow. A run tells the gateway that each such account is still wanted, has it follow those that are not followed yet, and then listens to what the feeds say until `AccountFeedJob.Listens` after it began, which is 40 seconds. What the feeds said is taken once at least, however long following took. A run with nothing to follow is over at once. |
| **A run lasts for most of its minute** | So a change is taken as it arrives, and its agent is reconciled then. What the feeds say between two runs waits with the gateway, for 20 seconds at most, and is taken by the next. It is a job of the scheduler, so that it runs on the one instance that does the background work, and stops with it: see [background-work.md](background-work.md). |
| **A stored key is asked for when the gateway needs one** | `IKeyHandOver.ExchangeKeyAsync`, to begin following an account, and to renew its feed, which Futures wants every half hour. It is handed to the gateway with that call, and kept nowhere (FR-KEY-9). `AccountFeed` is the third type that is handed a Binance credential, beside `AgentOrders` and `PositionWatch`: `TeamRulesTests` pins the three. It is handed nothing of the orders of the gateway. |
| **A feed that begins or ends is a change of everything in its account** | What happened before, or meanwhile, was not heard. Every agent that holds something in the account is reconciled. So is one whose account said an event that names no symbol. |
| **Many events are one reconciliation** | One execution is several events at Binance. A change is taken once however often it was said, and an agent is reconciled once for all the changes of a moment. |
| **A refused key is checked again** | As in "Settling". |
| **When the gateway opens no more feeds for now, the rest wait** | The limit on requests has no room, enough feeds were opened in the minute, or the address of the feed does not answer: no key is handed over for another account on that market in that environment, which would not be followed with it either, and the next run asks again. |
| **An account that cannot be followed keeps no other waiting** | An error of the application for one account is said once, with the account, and the others are followed. |
| Where Binance has no feed, or it cannot be followed | The account is reconciled on the schedule alone, every `Binance:ReconcileMinutes`. |

## Watching the positions of Futures

`PositionWatchJob`, named `position-watch`, watches the positions of Futures: every open one, and every one that was closed within a day and whose funding is not all read. It is apart from what settles the orders, which every cycle asks first and which is to be quick: this reads the exchange for every open position, and places nothing. It is apart from what reconciles as well, which corrects the records: this corrects nothing.

| Rule | Detail |
|---|---|
| **It runs at the time between reconciliations** | Every `Binance:ReconcileMinutes`, which is 5 in the tracked file, and as the background work starts when there is a position to watch: a position may have come near its liquidation price while the application was down. The job that reconciles runs at the same time between two runs, and each reads the position of an account in a symbol for itself: see "Not built yet". |
| **It only reads the exchange** | `PositionWatch` is handed `IExchangeAdapter`, which reads, and the market data. It is handed nothing of `ExchangeGateway.Trading`, which places orders and sets the margin of a symbol: `AgentOrders` is the one type that is, which `RiskGuardRulesTests` holds to. |
| **A stored key is asked for with each position** | `IKeyHandOver.ExchangeKeyAsync`, as for an order: it is handed to the gateway with the calls of that position, and kept nowhere (FR-KEY-9). `PositionWatch` is the second type that is handed a Binance credential, beside `AgentOrders`: `TeamRulesTests` pins the two. Without a key nothing is asked, and what was not read is read when there is one. |
| **A refused key is checked again** | When Binance answers a read as it answers a wrong credential, `IKeyValidation.RevalidateExchangeKeyAsync` is asked, as in "Settling", and nothing more is asked with that key in the run. |
| **No position waits for another** | The positions are watched a few at a time, each on its own. One that cannot be read keeps no other waiting, and is read with the next run. |
| **Whatever state its agent is in** | A position is watched while it is open: whether its agent runs, is paused, waits for its user, or was stopped with its position kept. |

### The margin of a position

| Rule | Detail |
|---|---|
| **The margin of every open position is checked with every run** | `IExchangeAdapter.GetFuturesPositionAsync` reads the position that the account holds in the symbol: what it holds, the price that it was entered at, the mark price, and the price that Binance liquidates it at, which is where its margin runs out. It is one call for each position, of 5 of request weight. |
| **A position approaches liquidation when little of the way to its liquidation price is left** | The way is from the price that the position was entered at to the price that Binance liquidates it at. What is left of it is from the mark price to the liquidation price. The position approaches liquidation when what is left is no more than `Trading:LiquidationWarningPercent` of the way: a fifth, in the tracked file. All three prices are Binance's, of one answer. |
| **A stop-loss that works closes the position first** | With the tracked settings. The liquidation buffer of an agent is 1.5 at the least, so its stop-loss is no further from the entry than two thirds of the way that the Risk Guard estimated, which is about seven tenths of the way to the price that Binance liquidates at, with a leverage of 10. Both bounds are of the configuration file: a lower buffer or a higher leverage cap brings a stop-loss nearer to the warning. So a warning says that something is not as it was planned: the stop-loss was taken off or was not executed, or the margin of the position was changed at Binance. |
| **A mark price that has reached the liquidation price approaches it** | And so does a position whose liquidation price is not on the losing side of its entry, where there is no such way. |
| **The user is told, once in an hour for an agent** | With a notification of the type `LiquidationWarning`, which names the symbol and the mode, the mark price, the liquidation price, and how far the two are apart, in percent of the mark price. It says that no margin is added, and what the user can do: close or reduce the position, or add margin to it at Binance. The check itself says it in the log with every run. See [notifications.md](notifications.md). |
| **No margin is added, and nothing else is done** | The service never adds margin on its own (FR-BIN-F5): the gateway has no call for it. The agent stays in its state, the position and its protection stay as they are, and the team of the agent decides at its next cycle what becomes of the position. |
| **A position without a liquidation price has nothing to approach** | Binance names none for a position that it cannot liquidate, such as a long one without leverage. |
| **What the account does not hold as the records say is said, and not corrected here** | An account that holds nothing in the symbol, or a position on the other side, where the records say an open position. Most often its stop-loss or its take-profit closed it a moment ago, which what settles the orders sees with its next run. It is said in the log, nobody is told, and the funding of the position is not read with that run: see "Funding". What reconciles the agent corrects the records, and tells its user (FR-BIN-7, FR-BIN-8): see "Reconciling". |
| **A margin that cannot be read is read with the next run** | It is a warning in the log. Nobody is told that a position approaches liquidation because the exchange did not answer. |

### Funding

The funding rate and the next funding time of the data package are in "The data package" of [cycles.md](cycles.md). This is the record of what a position received and paid.

| Rule | Detail |
|---|---|
| **What a position received and paid is stored row by row** | Each as a row of `fundings` that names the position: the number that Binance gave it, the amount, which is above zero when the position received it and below zero when it paid, the asset, and the funding time. It is what Binance says, as the fee of an execution is. Nothing adds the rows up: the results of a position are not built. |
| **It is read when it is due, and not with every run** | `IExchangeAdapter.GetFundingAsync` is the heaviest call that the service makes, at 30 of request weight. A position of Futures is due 5 minutes after it was opened, then 5 minutes after each funding time, and once more 5 minutes after it was closed. `positions.funding_due_at` says when: the time that the position was opened at first, and then the next funding time. |
| **The next funding time is the one that the mark price says** | `IExchangeMarketData.GetMarkPriceAsync`, which the gateway reads once for everyone (FR-BIN-3). Without one that is ahead, the funding of an open position is not read: the read did not succeed, the time that it gave has passed, or the symbol is no longer in `Binance:Symbols`. Such a position is read with a later run, and at the latest when it is closed. |
| **It is asked for from the newest row that is stored** | Which Binance says again, and which is stored once: the number that Binance gave it is unique for the position. So what Binance wrote later than a read is read the next time. A position with no row yet is asked about from when its entry was begun, which is before anything of it was executed: `positions.opened_at` is when the application saw the execution, and an entry at a price may have paid funding before that. |
| **A closed position is asked about up to the execution that closed it** | The newest execution of its orders that are no entry, by the clock of Binance, where the executions are stored, and otherwise `positions.closed_at`. A stop-loss is seen executed up to a few minutes after it was: by then the account may hold the symbol for another agent of its user, whose funding is not this position's. What Binance answers outside the time that was asked for is not stored. |
| **A position that the account no longer holds is not asked about until its close is seen** | The check of the margin comes first. Where Binance says that the account does not hold the position that the records say is open, its funding is not read with that run: up to now is not up to when it was closed. |
| **The read after the close is the last** | `funding_due_at` is empty from then on, and the position is not watched any more. It is empty for a position of Spot from the start, which has no funding. |
| **A read that does not succeed changes nothing** | The position stays due, and the next run reads again. It is a warning in the log. |
| **An answer of 1000 rows may not be all** | That is the most that Binance gives. The rows are stored, the position stays due, and the next run reads on from the newest of them. It is a warning in the log. |
| **A run reads the funding of 20 positions at most** | A funding time comes for every position at once, and all of them in one run would take the part of Binance's limit on requests that the orders and the cycles need. The rest are read with the next runs. A run goes on where the one before stopped, so positions whose funding cannot be read keep no other waiting. |
| **A closed position that still says a time is one whose funding is not all stored** | Its funding is asked for until a day after it was closed, as the executions of an order are. After that it is not asked for any more, and `funding_due_at` stays as it is. Binance keeps the income of an account for three months. |

What it costs of Binance's limit on requests: 5 for each open position of Futures with each run, and 30 for a position once after it was opened, once for each funding time while it is open, and once after it was closed.

Three things are taken for granted about Binance that its documentation does not say. No test settles them: each needs a position that is held over a funding time.

| Taken for granted | What follows when it is not so |
|---|---|
| Binance has written what a position received or paid within 5 minutes of the funding time. | The row of an open position is read after the next funding time. A position that was closed within those minutes lacks the row. |
| The time of a row is the funding time itself, and not when Binance wrote it. | A row of a position that was closed just after a funding time would be dated after the execution that closed it, and would not be stored. `LiveBinanceTests` holds every row that the account of its run has to a whole hour. |
| An answer of 1000 rows holds the oldest of the time that was asked for. | The rows that it left out would be older than the newest that is stored, and would not be asked for again. Only a position whose funding was not read for more than a month comes to 1000 rows. |

## What an agent holds

The Decision Engine keeps the positions and the orders, so it answers those that waited for them.

| Interface | Answer |
|---|---|
| `IAgentExposure` of Agents | An agent has an open position or orders while it has a position without a time of closing, or an order that is not over. It has an open position, as far as stopping it goes, also while an entry of it is not over: such an entry may open one. `ClosePositionAtMarketAsync` is `IPlanOrders.CloseAtMarketAsync`, and `AccountConflictAsync` is `IPlanOrders.AccountConflictAsync`: see "The account of Futures". See [agents.md](agents.md). |
| `IAgentHoldings` | The open position, with the prices of the stop-loss and of the take-profit that rest for it, and the orders that rest: an entry that has not filled, a stop-loss, and a take-profit. A take-profit is said when an order rests for it, on either market. The equity is the capital that the user allocated: no result is stored yet. See "What an agent holds" in [cycles.md](cycles.md). |
| `IReconciliation` | Whether the agent is settled, and whether it was reconciled since the application took up the background work, where it holds something from before. See "Settling" and "Reconciling". |

## One after the other

| Rule | Detail |
|---|---|
| **One at a time for an agent** | `AgentOrders` does what it does for one agent one after the other: a cycle, what settles, and a close of the agent's user take turns. |
| **A row is begun under the lock of the account** | The row of an order is written in a transaction that first takes the lock of the agent's account, as the controls of its user do (`IAgentTrading.StateUnderLockAsync`). Under it the state of the agent and what it holds are asked. So an entry and a stop of the agent's user cannot cross, and two instances of the application begin an order of the same agent one after the other. |
| **What becomes of an order is written by comparing and setting** | A write names the status that the row is expected to have, and writes nothing when the row has moved on. What follows from it, a position that is opened or closed, is written in the same transaction. |
| **The database holds to the rest** | One open position for an agent, one order of each purpose that is not over, and no two orders with the same identifier. So an agent of Futures has one stop-loss and one take-profit that are not over at most, as one on Spot has. |
| **No transaction is held while Binance is asked** | |

## The record of an order

The migration `AddOrders` adds three tables of the Decision Engine, and `AddOrderPairs` the columns `pair_id` and `cancel_after` of `orders`. `AddFundings` adds the table `fundings` and the column `funding_due_at` of `positions`, which it sets for the positions of Futures that are open then, so that their funding is read from when they were opened. Each row carries its owner along: an order and a position to their agent, with `(agent_id, user_id)`, an execution to its order, with `(order_id, user_id)`, and what a position received and paid as funding to the position, with `(position_id, user_id)`. They go with the agent when it is deleted, and with the account. See [ownership.md](ownership.md).

| Table | Columns |
|---|---|
| `orders` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `cycle_id`, `position_id`, `client_order_id`, `exchange_order_id`, `purpose`, `type`, `side`, `price`, `stop_loss`, `take_profit`, `quantity`, `status`, `executed_quantity`, `average_price`, `exit_reason`, `sends`, `fills_recorded`, `created_at`, `updated_at`, `checked_at`, `pair_id`, `cancel_after` |
| `fills` | `id`, `order_id`, `user_id`, `trade_id`, `price`, `quantity`, `fee`, `fee_asset`, `at` |
| `positions` | `id`, `agent_id`, `user_id`, `mode`, `market`, `symbol`, `side`, `entry_price`, `quantity`, `stop_loss`, `take_profit`, `opened_at`, `closed_at`, `exit_reason`, `alerted_at`, `funding_due_at` |
| `fundings` | `id`, `position_id`, `user_id`, `transaction_id`, `amount`, `asset`, `at` |

| Column | What it holds |
|---|---|
| `orders.mode`, `market`, `symbol` | Those of the agent when the order was placed. What an agent did in Demo is Demo for good (FR-BIN-11), and an order is asked about where it was placed. |
| `orders.cycle_id` | The cycle that placed it, or nothing for an order that no cycle placed. It points at no row: a cycle is recorded when it ends, which is after its orders. |
| `orders.purpose` | `Entry`, `StopLoss`, `TakeProfit`, or `Close`. |
| `orders.type` | `Market`, `Stop`, or `Limit`. A take-profit is a `Stop`, as a stop-loss is: its purpose says which of the two it is, and so which way its trigger is held against the price. |
| `orders.price` | The price that a stop is triggered at, or the price of an entry at a price. |
| `orders.stop_loss`, `take_profit` | Of an entry: the protection of the position that it opens. They are kept with the entry, so that the position is protected at that price also when the entry is found executed later, by what settles it. |
| `orders.exchange_order_id` | The number that Binance knows the order by. A stop of Futures that was triggered is known by the order that it became. |
| `orders.exit_reason` | Of an order that closes: why. |
| `orders.sends` | 1, or 2 when it was sent once more. |
| `orders.checked_at` | When Binance last said what has become of it. |
| `orders.pair_id` | Of a stop-loss and a take-profit of Spot: the pair that they are, which both carry. Written without its hyphens, it is the identifier that the pair carries at Binance. It is the id of no row. The two orders of Futures carry none: they are no pair. |
| `orders.cancel_after` | Of an entry at a price: when it is taken off the exchange if it still rests, which is the next close of the candles of its agent. |
| `fills.fee`, `fee_asset` | The fee of an execution, in the asset that Binance took it in. |
| `positions.quantity` | What the position holds: what an order that protects or closes it is for. |
| `positions.stop_loss`, `take_profit` | Prices. The order of the stop-loss is a row of `orders`, and so is the order of the take-profit. |
| `positions.exit_reason` | `StopLoss`, `TakeProfit`, `TeamClose`, `UserClose`, `ProtectionFailed`, or `ClosedAtExchange`: the account no longer held the position, and no order of the agent closed it. See "What is corrected". |
| `positions.quantity`, `entry_price` | Also what Binance says that the account holds of a position, and the price that it says the position was entered at, once the two were found to differ from the records. |
| `positions.alerted_at` | When its user was told that it is open with no stop-loss and could not be closed. |
| `positions.funding_due_at` | When its funding is read next: see "Funding". Empty for a position of Spot, and for a closed position whose funding is all read. |
| `fundings.transaction_id` | The number that Binance gave what the position received or paid. It is stored once for a position. |
| `fundings.amount`, `asset` | How much, above zero when the position received it and below zero when it paid, in the asset that Binance says. |
| `fundings.at` | The funding time, by the clock of Binance. |

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
The take-profit of the position of agent {AgentId} could not be placed, so the position is closed at market
The take-profit of the position of agent {AgentId} could not be placed at its new price, so it is placed at the old one again
The position of agent {AgentId} could not be closed at market, so it is open with its stop-loss, and with no take-profit
The account of agent {AgentId} does not hold in {Symbol} what the records say, as the exchange says it: it is asked once more before the records are corrected
What the account of agent {AgentId} holds in {Symbol} could not be read: {ExchangeCallOutcome}
The records of agent {AgentId} in {Symbol} did not match the exchange: {Mismatch}. They were corrected
```

| Note | Detail |
|---|---|
| **Levels** | A change of an order and a position that is opened or closed are information. A stop-loss or a take-profit that cannot be placed is a warning, and so is an entry that bought less than an order can be for. A position that is left open with no stop-loss is an error. A mismatch that was corrected is a warning, and so is an account that could not be read. That an account is asked once more is information. |
| **Which mismatch** | `Mismatch` is `PositionClosed`, `PositionReduced`, `PositionGrown`, or `ProtectionTakenOff`: see "What is corrected". No event says what the account holds, or what the records said. |
| **Why no entry was placed** | `NoEntryReason` is one of "When no entry is placed". No event says an amount of the wallet, or the leverage of an order. |
| **Why an entry was taken off** | `EntryTakenOffReason` is `TimeIsUp`, `PartlyExecuted`, `NotRunning`, or `UserClose`: see "An entry at a price". |
| **A pair that is sent once more** | Is said with the event of an order that is, with `Pair` as its `OrderPurpose`. |
| **The calls themselves** | Each call to Binance is in the log of outbound calls, with its duration and its outcome, and the number of an error of Binance under the gateway. See [exchange.md](exchange.md). |
| **The job** | `The orders of {Agents} agents are settled with the exchange`, under `Ecnanib.DecisionEngine.OrderSettlingJob`, when there are some. An agent that cannot be settled for an error of the application is said once, as an error. |
| **The jobs that reconcile** | `{Agents} agents were reconciled with the exchange, and {NotReconciled} were not: those are tried again with the next run`, under `Ecnanib.DecisionEngine.ReconciliationJob`, when an agent holds something. `The account event feed said {Changes} changes, and agents were reconciled {Reconciliations} times for them`, under `Ecnanib.DecisionEngine.AccountFeedJob`, for a run in which a feed said one. An agent that cannot be reconciled for an error of the application is said once, as an error. What the gateway says of a feed is in "The log" of [exchange.md](exchange.md). |

What watches the positions writes under `Ecnanib.DecisionEngine.PositionWatch`. Its events name the agent and the symbol. No event holds a key, a price, or an amount: one about the margin says how much of the way from the entry to the liquidation price is left, in percent of that way, which is what the check goes by.

```
The position of agent {AgentId} in {Symbol} approaches liquidation: {WayLeftPercent} percent of the way from its entry to its liquidation price is left
The margin of the position of agent {AgentId} in {Symbol} was checked: {WayLeftPercent} percent of the way from its entry to its liquidation price is left
The margin of the position of agent {AgentId} in {Symbol} was checked: the exchange names no liquidation price
The margin of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}
The account of agent {AgentId} does not hold the {PositionSide} position in {Symbol} that the records say, as the exchange says it: its margin is not checked, and its funding is not read
{Fundings} rows of funding were stored for the position of agent {AgentId} in {Symbol}
The funding of the position of agent {AgentId} in {Symbol} could not be read: {ExchangeCallOutcome}
The funding of the position of agent {AgentId} in {Symbol} is more than one answer of the exchange holds: the rest is read with the next run
```

| Note | Detail |
|---|---|
| **Levels** | A position that approaches liquidation is a warning, with every run that finds it. A check that finds nothing is at the debug level. What could not be read is a warning, and so is funding that one answer did not hold. An account that does not hold what the records say is information: it is most often a position that was closed a moment ago. Funding that was stored is information as well: it says how many rows, and not what they hold. |
| **The job** | `{Positions} positions of the Futures market are watched, and the funding of {FundingReads} of them is read`, under `Ecnanib.DecisionEngine.PositionWatchJob`, when there are some. A position that cannot be watched for an error of the application is said once, as an error. |

## Testing

The tests run the application on a database of their own, with a Binance that keeps orders behind the gateway (`TradingBinance` in `OrderFixtures.cs`): it takes an order, answers what became of it, takes it off, and lists its executions, in the form that Binance's documentation gives. An order at a price rests there until the test has it executed, in full or in part. A pair is two stops of one list: it is taken while the price is between its two triggers, when one is executed the other ends, and taking one off takes both. On Futures a stop and a take-profit are conditional orders of their own, which it has at the calls of those orders only: one whose trigger the price has passed is refused, and neither ends when the other is executed. The symbol has isolated margin there once it was asked to have, after which that call is answered with Binance's error that nothing is to change, and the leverage that was last asked for. A test says what becomes of a call that places an order or a pair: taken, taken with an answer that never comes, lost, refused, or answered with a fault. How the account stands on Futures is what the test says: in one-way mode, with one asset, and with no position in the symbol, unless it says otherwise. What the account holds is what the orders that this Binance executed add up to, with more USDT than a plan of the tests needs (`TradingBinance.Holds`, `Position`, and `Balances`): the position of the account in the symbol on Futures, and what it has of BTC on Spot. A test has its user buy or sell at Binance by hand with `HoldByHand`, which no order of the application did. So this Binance refuses a sale on Spot that the account does not cover, and an order at the market that only reduces a position where the account holds none. The clock is the test's, so a test moves it on while the application waits for a moment of it.

The job that watches the positions runs in no application of the tests on a schedule: a test runs it itself, with `OrdersApplication.WatchAsync`. What the account received and paid as funding is what the test says (`StandInProviders.Funding`): none, unless it says otherwise. A test of the margin says the position of the account with its prices itself (`StandInProviders.FuturesPosition`, `HeldPositions`).

The job that reconciles runs in no application of the tests on a schedule either, unless the test says that its application reconciles, and neither does what holds a cycle back for an agent that was not reconciled since the start: a test runs the job itself, with `OrdersApplication.ReconcileAllAsync`, or reconciles one agent with `ReconcileAsync`. The job that follows the account event feed runs in none: a test runs it with `FollowFeedAsync`, with a `StandInFeed` in place of the feed of the gateway, which follows every account that it is asked to and says the changes that the test has it say. The next funding time is the test's as well (`StandInFundingTimes` in `OrderFixtures.cs`): the gateway holds a mark price that it read for a second of real time, whatever the clock of a test shows.

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
| Futures agents trade perpetual contracts, long or short | `PlanOrderTests`, `FuturesProtectionTests`, and `LimitEntryTests` for an entry at a price |
| The system sets margin type and leverage for the agent's symbol before entry | `FuturesEntryTests`, and `FuturesAccountTests` for the calls |
| It changes no account-wide setting, and an account that conflicts gets no entry and no start | `FuturesEntryTests`, `AgentAccountTests` of `tests/Ecnanib.Tests/Agents`, and `NoFundMovementTests` for what the gateway can send |
| Protection is a reduce-only stop-loss and a reduce-only take-profit, triggered on the mark price | `FuturesProtectionTests`, `ProtectiveStopTests`, `PlanOrderTests`, and `OrderPlacementTests` for the calls |
| An entry that the margin of the Futures wallet does not cover is skipped, and the user is notified | `FuturesEntryTests` |
| The current funding rate and the next funding time are included in the data package | `DataPackageTests` and `CycleDataPackageTests`, and `MarketDataTests` for the call |
| Funding paid or received is recorded per position | `FundingRecordTests`, `OrderRecordTests` for whose the rows are, and `FundingReadTests` for the call |
| The margin state of open positions is checked at each reconciliation, and the user is alerted when a position approaches liquidation | `LiquidationWarningTests`, `PositionWatchJobTests`, `FuturesPositionTests` for the call, and `NotificationTests` for what the user sees |
| The system never adds margin on its own | `LiquidationWarningTests`, `NoFundMovementTests` for what the gateway can send, and `RiskGuardRulesTests` for what may use the calls that change anything |
| Order and position changes are tracked through the exchange's account event feed where available | `AccountFeedJobTests`, and `AccountFeedTests` for the gateway |
| Reconciliation runs at startup and every 5 minutes | `ReconciliationJobTests` |
| On any mismatch the exchange is treated as the source of truth: records are corrected, and the mismatch is logged and shown | `ReconciliationTests`, and `NotificationTests` for what the user sees |
| Actions the user takes directly on Binance are detected by reconciliation and reflected in the agent's records | `ReconciliationTests`: a position that was closed, reduced, added to, or reversed by hand, a stop-loss that was taken off, and what was sold on Spot |

A check against Binance's own test environment is in `LiveBinanceTests`: see "Checking against Binance itself" in [exchange.md](exchange.md).

## Not built yet

- Protection that Binance places by itself when an entry at a price is executed. The pair is placed by the service, within a minute of the execution, and later when the service is down then: until it is, what the entry bought has no stop-loss. Binance has a list of three orders for it, which places a pair when its entry is executed in full. Its documentation does not say how the quantity of that pair is held to what the account has once the fee of the purchase is taken in the asset, so it is not used.
- One read of the positions of an account for everything that asks. The margin of a position is read for each position, and what reconciles reads the position again, at the same time between two runs. The positions of an account come in one answer of the same weight.
- The funding of a position that could not be read within a day of its close. The position says so: see "Funding".
- Finding a mode of the account that its user changes while a position is open. The account of Futures is read before an entry and before an agent runs, and not in between. Binance takes no change of the position mode while the account has a position or an order.
- The executions of what was done at Binance by hand. A position that its user closed, reduced, or added to there is corrected in the records, and the orders that did it have no row, so their executions, their prices, and their fees are not stored. The results of such a position need them (FR-BIN-10).
- Telling a liquidation from a position that was closed by hand. Both are a position that the account no longer holds, and end as `ClosedAtExchange`.
- Orders in the account of a user that no agent placed. They are not listed, and so not compared: what reconciles asks about the orders that have a row.
- The leverage brackets of a symbol, by which Binance allows less leverage for a larger position. A leverage that Binance refuses for that gets no entry, and its user is told: see "The margin of Futures".
- The results of a position: its profit and loss, the sums of its fees and of its funding, and its result in R (FR-BIN-10). The fees and the funding are stored row by row. The equity of an agent, and with it the loss limits (FR-RSK-8), still go by the allocated capital.
- The funding and the margin of a position on the page of its agent (FR-RPT-2).
- The orders of a cycle in its record and its timeline (FR-CYC-10). Why a plan placed nothing is in the log only.
- The count of the orders that an account places, which Binance limits beside the request weight. A protective order has no turn before other calls at the limiter: one that is held back was not sent, and is settled within a minute.
- A close by hand that is told its user as closed, or failed, by a notification (FR-NTF-1). The page that asked says so.
