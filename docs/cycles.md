# Cycles

A cycle is one run of an agent's team, triggered when a candle closes. A cycle starts when a candle of the agent's timeframe closes, on UTC boundaries, and only closed candles are used (FR-CYC-1). Only an agent in the Running state gets cycles (SRS 3.3). When many agents share a candle boundary, their cycles are queued with concurrency limits per provider and per user key, shortest timeframe first (FR-CYC-11). Cycles missed while the service was down are skipped, not replayed: after a restart the system reconciles first, then waits for the next candle close (FR-CYC-8).

A cycle that starts builds its data package: the most recent closed candles of the agent's timeframe, the higher-timeframe context, indicators that the system computes and no model does, the position, the orders, and the equity of the agent, its limits, and on Futures the mark price, the funding, and the open interest (FR-CYC-2).

The Sentiment Analyst gathers news and social context using the live-search capability of its model where the provider offers one. If the selected model has none, the role still runs, its report is marked "no live data", and the Strategist is told to give it no weight (FR-CYC-3).

A plan does only what a plan may do: the actions of its market, for an agent that holds at most one position, with protection that only tightens, and with a stop-loss and a take-profit where it opens a position (FR-CYC-4 to FR-CYC-7). A decision that completes later than the stale limit for its timeframe cannot open a position (FR-CYC-9). Both are in [team.md](team.md): "What a plan may do" and "The stale limit".

What is built is what starts a cycle and lets it wait for its turn, the data package that the cycle then builds, what the call of the Sentiment Analyst and its report go by, which is in "Live search", and the calls of the team, which are in [team.md](team.md): the four roles in their order, the validation of what each answers, what a plan may do, the stale limit, and the record of the cycle (SRS 3.2, steps 3 to 5). What goes on of the plan of the team is checked by the Risk Guard, which is in [risk-guard.md](risk-guard.md) (step 6). What the Risk Guard accepted is handed to what places the orders, which is in [orders.md](orders.md) (step 7): a cycle that starts settles the orders of its agent with the exchange, builds its package, runs its team, has what goes on of its plan checked, and places the orders of what was accepted, or does nothing.

The code is in `src/Ecnanib/DecisionEngine`. The rules below are enforced by tests in `tests/Ecnanib.Tests/DecisionEngine`, in the classes whose names begin with `Cycle`, and in `DataPackageTests`, `IndicatorTests`, and `SentimentLiveSearchTests`. Those of the team are in the classes whose names begin with `Team`.

How a job is scheduled, and what becomes of a boundary that passed, is in [background-work.md](background-work.md). The states of an agent are in [agents.md](agents.md). The candles are in "Market data" in [exchange.md](exchange.md), and the settings in [configuration.md](configuration.md).

## When a cycle starts

The Decision Engine has one job, `decision-cycles`, in `DecisionCyclesJob`. It runs at every quarter hour in UTC, which is the close of the shortest candle. **One run is for one close, and has the cycles of every timeframe of which a candle closed then:**

| The close | Timeframes of which a candle closes |
|---|---|
| Every quarter hour | 15m |
| Every hour | 15m, 1H |
| 00:00, 04:00, 08:00, 12:00, 16:00, 20:00 | 15m, 1H, 4H |
| 00:00 | 15m, 1H, 4H, 1D |

- `Timeframes.Length` in `Shared` says how long a candle of a timeframe lasts, and `Timeframes.ClosingAt` which timeframes close at a time. Each length divides a day into equal parts and is a multiple of the shortest, which `AgentRuleTests` holds them to. So every close is a quarter hour, and one job has them all.
- **One job, and not one for each timeframe,** because the cycles that share a boundary are in one queue then, and only one queue has an order.
- **The run is told its close by the scheduler,** as `BackgroundJobRun.ScheduledFor`, and a cycle is told it as `CycleStart.ClosedAt`. It is the boundary itself, and not the moment the run began.

| Rule | Detail |
|---|---|
| **An agent whose daily halt is over runs again first** (FR-RSK-8). | Before the agents are read, `IAgentHalts.EndDailyHaltsAsync` of Agents resumes every agent that its daily loss limit halted on an earlier UTC day. So such an agent has its cycle of the first close of the day, which is the close of midnight for every timeframe. An error there is said once in the log, and keeps no cycle from starting. See "Halting an agent" in [agents.md](agents.md). |
| **Only an agent that runs gets a cycle** (SRS 3.3). | `IRunningAgents.OfTimeframesAsync` of Agents says which agents run on a timeframe that closed, of every user. It goes by `AgentStates.RunsCycles`, which is where the table of the states is. A draft, and an agent that is paused, halted, stopped, or requires attention, gets none. |
| **It is asked again as the turn of a cycle comes.** | An agent is paused or stopped by its user at any moment, also while its cycle waits in the queue. `IRunningAgents.FindAsync` reads the agent again then: one that no longer runs gets no cycle, and neither does one that was stopped, given another market, symbol, or timeframe, and started again meanwhile. |
| **A cycle is for the agent as it is stored when its turn comes.** | Its settings and the models of its roles are those of that moment: a change applies from the agent's next cycle (FR-AGT-8). |
| **Nothing is asked for while no agent runs.** | A run that finds no agent on a timeframe that closed ends at once, and asks the exchange for nothing. |
| **A pause does not stop a cycle that runs.** | Whether an agent runs is asked when its cycle starts. What places an entry asks `AgentStates.TakesNewEntries` again, under the lock of the account, when it is built. |

## The candle of the close

**Which candle is closed is the exchange's word, and not the clock's** (FR-CYC-1). The gateway gives closed candles only, and never the one in progress: see "Candles" in [exchange.md](exchange.md). Just after a close Binance may not have begun the next candle. The candle that has closed by the clock of the service is then still in progress to Binance, and the candles come one short. A cycle that started on them would decide without the candle that it is for.

So `ClosedCandles` asks for the candles of a close until the newest of them closed at that close, before any cycle of that close starts:

| Rule | Detail |
|---|---|
| **The candles are asked for once for all agents that share them** (FR-BIN-3). | One ask for a symbol and a timeframe, on a market in an environment. It is one ask, and not one for each cycle: candles that came short are held for nobody, so every ask for them is a read of Binance. |
| **They are asked for again a second later,** while they come one short, and while the exchange gives no answer. | A read that failed is shared for some seconds and is no call then, so one pace does for both. |
| **A candle that is not there half a minute after the close is not waited for.** | The cycles of that symbol and timeframe are skipped, with one warning that says how the last read ended. The other symbols are not held up. The next close starts the next cycles. `ClosedCandles.Wait` is that half minute, which is what the cycles of a close have to start in (NFR-PRF-3). |
| **A cycle is handed the candles.** | `CycleStart.Candles` is the reading as the gateway gave it: the newest candle closed at `CycleStart.ClosedAt`. The agents that share a symbol and a timeframe are handed the same. |
| A symbol that cannot be asked for costs its own cycles only. | The gateway serves the symbols of `Binance:Symbols` alone. An agent whose symbol the file no longer lists has its cycles skipped, with an error in the log, and the others go on. |

The candles are of the agent's own timeframe, as `CandleIntervals.Of` says, and of the market and the environment that its orders go to.

**How long Binance takes** was measured once, on 16 November 2026, over one close of 15 minutes that the job ran for and six closes of one-minute candles, for `BTCUSDT` and `ETHUSDT`. It is what the half minute and the 5 seconds of "The queue" were held against, and not a promise of Binance's:

| Market and environment | The next candle had begun after |
|---|---|
| Spot, the real exchange | 1 to 2 seconds |
| Spot, the test environment | 2 to 5 seconds |
| Futures, the real exchange | 4 to 6 seconds |
| Futures, the test environment | 9 to 16 seconds |

So the cycles of an agent on Spot start within about 5 seconds of the close, and those of an agent on Futures in the test environment about a quarter of a minute after it. Half a minute covered every close that was measured, with the least to spare on Futures in the test environment, where every new agent on that market begins.

## The queue

The cycles of a close wait in one queue, `CycleQueue`, which one run of the job has for itself. Nothing of it is stored.

**The order** (FR-CYC-11):

1. The shortest timeframe first.
2. Within a timeframe, the users take turns: the first agent of every user comes before the second of any. So a user with many agents does not keep the others waiting for all of them.
3. Then by the id of the agent, which is the order the agents were created in.

**The places.** A cycle starts when it finds a place with everything that it uses, and keeps its places until it ends, however it ends:

| A place with | Limit | Why |
|---|---|---|
| Each AI provider that a role of the agent's team uses | `Trading:Cycles:MaxPerProvider`, for all users together | So many cycles call one provider at once, and no more. |
| The key of the agent's user for that provider | `Trading:Cycles:MaxPerKey` | A user has one key for a provider (FR-KEY-2), which all their agents are called with, and a provider limits what one key may ask. |

| Rule | Detail |
|---|---|
| **A provider that holds two roles of a team counts once.** | A cycle is one place with a provider, whatever the number of its roles there (FR-TEAM-5). The two analysts are called at the same time, so one cycle can be two calls with one key. |
| **A cycle that finds no place waits, and reserves none.** | Whenever something changes, the queue is gone through in its order, and every cycle that finds all its places starts. So a cycle of a longer timeframe that needs other providers starts before a cycle of a shorter one that waits for a provider that is full. |
| **A provider that is slow or full holds back the cycles that need it, and no others** (NFR-REL-4). | It is why nothing is reserved: a cycle that held the places of its other providers while it waited for one would keep cycles waiting that never call that one. The order is strict among cycles that need the same places. |
| **The first cycles start once the candles of every symbol are in.** | Or 5 seconds after the run began, whichever is first: `DecisionCyclesJob.Gather`. Binance closes the candles of two symbols a moment apart, and a cycle of a day must not take a place ahead of a cycle of 15 minutes whose candle arrives a moment later. A symbol whose candle is late joins the queue when it has it, at its place in the order. |
| **A place is free again however a cycle ends.** | An error that leaves a cycle ends that cycle alone. It is logged once, as an error, with the user, the agent, and the cycle, and the others go on. |

In the tracked file the limits are 250 for a provider and 4 for a key. A team of four different providers takes a place with each, so 250 cycles run at once then, and the cycles of 500 agents that share a boundary start in two rounds. Four of a user's agents are called with one key at once, and a fifth waits for one of them.

## When time runs out

**The cycles of a close are over before the next close.** The scheduler runs a job once at a time, and skips a boundary whose run cannot start: see [background-work.md](background-work.md). A run that went on past the quarter hour would cost every agent of every user its next cycle. So a run ends in time, by two rules, which are fixed and are no settings:

| Rule | When | What happens |
|---|---|---|
| **A cycle that could not start within 5 minutes of its close is skipped.** | `DecisionCyclesJob.StartsWithin` | Every cycle that still waits is taken out of the queue, with one warning that says how many. They are not made up for: the next close starts the next ones. |
| **A cycle that still runs 45 seconds before the next close is stopped.** | `DecisionCyclesJob.EndsBefore` | Its token is canceled, as it is when the application stops. A warning says how many were stopped, and one for each says which. |

- **Why 5 minutes.** The longest cycle is three calls of the team one after the other, each made twice at most (FR-TEAM-9). With the time limit of 60 seconds of the tracked file that is 6 minutes, so a cycle that starts within 5 is over long before it would be stopped. With time limits that are raised far above that, a cycle can reach the stop.
- **Why 45 seconds.** A cycle whose entry is on its way when it is stopped lets that call end, which takes its time limit at most, 15 seconds: an order that is sent is not given up. It then needs one more call to the exchange to protect the entry, which takes its wait at the limiter and its time limit at most, 25 seconds. Both are over, and the cycle is recorded, before the next run is due. What is left undone then is done by what settles the orders: see "The stop-loss" in [orders.md](orders.md).
- **A cycle is stopped, and never left behind.** The run returns only when every cycle that it started has ended. A cycle that ran on beside the next run would be a second cycle of its agent, outside the lock of the background work. A cycle that does not end when it is told to keeps the run going, and the scheduler then skips the next close, with its warning.
- **A run that begins 5 minutes after its close or later starts no cycle.** The scheduler starts a run that is late for its boundary, after the machine slept or the clock was set, and the job judges that it is too late.
- **When the application stops, or is no longer the instance that runs the background work, the cycles stop with it** (NFR-REL-5). Nothing more starts, the cycles that run are told to stop, and the run ends when they have, as `Stopped`. Nothing of the queue is kept: a cycle that did not start is not made up for.
- Every limit is looked at by the clock at least once a second, as the scheduler looks at it.

## After a restart

**Cycles missed while the service was down are skipped, not replayed** (FR-CYC-8). The job is on UTC boundaries, so the scheduler makes up for no boundary that passed while the application was down, or while the machine slept: see "Schedules" in [background-work.md](background-work.md). After a restart the job waits for the next close. The queue is in memory, so there is no cycle from before to go on with.

**The system reconciles first** (FR-CYC-8, NFR-REL-3). What reconciles the records of an agent with the exchange answers through `IReconciliation`, which the Decision Engine declares:

```csharp
public interface IReconciliation
{
    Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **An agent that is not reconciled yet gets no cycle.** | It is asked as the turn of a cycle comes, after the agent was found to run. The cycle of that close is skipped for it, and the next close asks again. |
| **An agent that holds something from before the start is reconciled first.** | `ReconciledSinceStart` answers from what the application has reconciled since it took up the background work: an agent with a position that was open before that, or an order that was not over, gets no cycle until the exchange was asked about its orders, and about what the account of its user holds (FR-BIN-7, NFR-REL-3). Its records are from before, and its user may have closed its position at the exchange meanwhile. The job that reconciles is due as the background work starts when an agent holds anything: see "Reconciling" in [orders.md](orders.md). |
| **An agent that holds nothing from before waits for nothing.** | One that holds nothing has nothing that an account could differ from. What an agent came to hold since the start, the application placed, and saw placed. |
| **No other agent waits for it.** | The question is about one agent, and is answered at once. An account that cannot be reconciled, because its key is refused, keeps nobody else from trading. |
| **An agent whose orders are not settled is not reconciled.** | `OrderReconciliation` answers from the rows of the orders: an agent with an order of which it is not known whether Binance has it, or with an open position without a stop-loss that rests, gets no cycle. Every order is written before it is sent, so an order that was under way when the application stopped is such an order, and nothing is placed for its agent until Binance was asked about it. That is what keeps a restart from placing an order twice (NFR-REL-3). A job settles the orders every minute, and as the background work starts when there is anything to settle: see "Settling" in [orders.md](orders.md). |
| **An agent passes when everything that answers says so.** | Two answer: what settles the orders, and what reconciles the agents. |
| **"Reconciled" is since this instance took up the background work.** | At the start of the application, and again after the lock of the background work was lost and taken back: another instance may have placed orders meanwhile. What was reconciled is kept in memory, for one process. The job that reconciles says that the work is taken up when the scheduler asks it whether it is due as the work starts, when one of its runs is stopped, and when it runs after more than twice its time between two runs. |
| **An agent that cannot be reconciled gets no cycle, and its position stays protected.** | Its key is gone, or the exchange does not answer for its account. The orders that protect its position rest on the exchange, and do not depend on the service (NFR-REL-2). The job tries again with every run. |

## What a cycle is handed

What a cycle does once it has started is behind `IDecisionCycle`, which is called as the turn of a cycle comes:

```csharp
public interface IDecisionCycle
{
    Task RunAsync(CycleStart cycle, CancellationToken cancellationToken);
}

public sealed record CycleStart(
    Guid CycleId,
    Agent Agent,
    IReadOnlyList<AgentRoleView> Roles,
    DateTimeOffset ClosedAt,
    ExchangeCandles Candles);
```

| Part | What it is |
|---|---|
| `CycleId` | The id of the cycle: a version 7 UUID. Everything logged for the cycle carries it, and what stores a cycle stores it under it. |
| `Agent`, `Roles` | The agent and the roles of its team that have a model, as stored when the turn came. |
| `ClosedAt` | The close of the candle that the cycle is for. |
| `Candles` | The closed candles through that close. |

- **The token is canceled** when the application stops, when it stops being the instance that runs the background work, and 45 seconds before the next close. The cycle then ends soon, by letting `OperationCanceledException` out, and leaves no entry without its protection (NFR-REL-5).
- An error that leaves `RunAsync` ends the cycle with no decision.
- **The registered implementation builds the data package of the cycle, and runs the team of the agent.** `TeamCycle` asks `IDataPackages` for the package, hands each role its part of it, and ends with the decision of the team: see [team.md](team.md). A cycle whose role gave no valid result ends without an error: it returns, and is counted as ended. It changes nothing of when a cycle starts.
- **A cycle that started is recorded when it ends,** however it ends, as a complete record, which its user views as a timeline (FR-CYC-10): see "The record of a cycle" and "The timeline" in [team.md](team.md).
- The interfaces are public so that a test can put a stand-in behind them: the tests see no internal type.

## The data package

The data package is what the team of an agent decides on, and the first thing that a cycle builds (SRS 3.2, step 2). `IDataPackages` builds it from what the cycle was handed:

```csharp
public interface IDataPackages
{
    Task<DataPackage> BuildAsync(CycleStart cycle, CancellationToken cancellationToken);
}

public sealed record DataPackage(
    DateTimeOffset ClosedAt, AgentContext Agent, IReadOnlyList<OpenOrder> Orders,
    CandleSeries Candles, IReadOnlyList<CandleSeries> Context, FuturesData? Futures, string Text);

public sealed record CandleSeries(
    CandleInterval Interval, IReadOnlyList<ExchangeCandle> Candles, int ComputedFrom, IndicatorValues Indicators);

public sealed record FuturesData(decimal MarkPrice, decimal FundingRate, DateTimeOffset NextFundingAt, decimal OpenInterest);
```

**What a package contains** (FR-CYC-2):

| Part | In the package | Where it comes from |
|---|---|---|
| The most recent closed candles of the agent's timeframe | `Candles` | `CycleStart.Candles`, which what starts the cycles read through the close. They are not read again. The package holds the newest `Trading:DataPackage:Candles` of them: 200 in the tracked file. |
| The higher-timeframe context (SRS 4.6) | `Context`, shortest first | A read of the candles of each higher timeframe, through `IExchangeMarketData`, on the market and in the environment of the agent. The package holds the newest `Trading:DataPackage:ContextCandles` of each: 50 in the tracked file. |
| Indicators computed by the system, not by models | `Indicators` of each series | `Indicators`, from the candles of the series. See "The indicators". |
| The current position, orders, and equity | `Agent.Position`, `Orders`, `Agent.Equity` | Whatever keeps positions and orders, through `IAgentHoldings`. See "What an agent holds". |
| The agent's limits | `Agent.Limits` | The risk settings of the agent, as they were stored when the turn of the cycle came. A prompt states them with the two limits that the specification fixes: see "Composing a prompt" in [prompts.md](prompts.md). |
| On Futures: the mark price, the funding rate with the time of the next funding, and the open interest (FR-BIN-F4) | `Futures` | A read of each through `IExchangeMarketData`, in the environment of the agent. An agent on Spot has none of them, and asks for none. |

**The context of a timeframe** is the higher timeframes that its module of the prompt names (SRS 4.6):

| Timeframe of the agent | Context |
|---|---|
| 15m | 1H and 4H |
| 1H | 4H and 1D |
| 4H | 1D and 1W |
| 1D | 1W |

`TimeframeContext.Of` says which they are, as lengths of a candle that the gateway reads. `AgentRuleTests` holds it to the module of each timeframe that is in use, so what a role is told and what it is handed are the same.

| Rule | Detail |
|---|---|
| **A package holds closed candles only, and nothing that closed after its close** (FR-CYC-1). | `ClosedAt` is the close of the cycle. The gateway never gives the candle in progress. The turn of a cycle may come minutes after its close: a candle that the exchange closed meanwhile is left out, and no indicator counts it. |
| **Everything is asked for at once.** | The candles of the higher timeframes, what Futures adds, and what the agent holds. A package takes as long as the slowest of its reads. |
| **The reads are shared** (FR-BIN-3). | The gateway reads the candles of a symbol and a length once for everyone who asks while they count, and the mark price and the open interest in the same way: see "Market data" in [exchange.md](exchange.md). So the cycles of the agents that share a symbol cause one read of each part, however many they are. At a close that is at most two reads of candles for each symbol and timeframe, and two more reads for each symbol on Futures. |
| **A candle of a higher timeframe that closes with the cycle's close may come one short.** | At 10:00 the candle of 15 minutes and the candle of the hour close together. When Binance has not begun the next hour as the cycle asks, the hour that just closed is still in progress to it, and is not given. It is not waited for: the candles of the agent's own timeframe cover the same time, and the package says through when each series reaches. |
| **A higher timeframe that has no closed candle yet is an empty series.** | A symbol that is new to the exchange has no closed week. That is no failure: the series has no candle and no indicator, and the text says so. |
| **A read that does not succeed gives no package.** | `BuildAsync` throws `DataPackageException`, which says which part the exchange did not give and how the read ended, and nothing of the agent. It leaves the cycle as an error, so the cycle ends as failed, with no decision. Nothing is asked again for that close: the gateway shares a read that failed for some seconds, and the next close starts the next cycle. An open position stays as it is, with its protective orders. |
| **A package is built from candles through its close, and from no others.** | A cycle is handed them: see "The candle of the close". `BuildAsync` refuses candles that come one short, or none, with an error. |
| **A package says nothing of whose agent it is** (FR-PRM-9). | It has no id and no name. `Agent` is the context of the agent as a prompt states it, made by `AgentContext.Of` from the settings of the agent. |
| **Nothing of a package is stored but its summary.** | It is built for one cycle, and lives as long as the cycle. The database is not a market-data warehouse (NFR-DAT-6). The record of the cycle keeps a summary of it (FR-CYC-10): what the agent was and held, its limits, and for each series how many candles it had, the latest of them, and the indicators at its close. No other candle is kept. See "The summary of the data package" in [team.md](team.md). |

### The indicators

**Indicators are computed by the system, and not by models** (FR-CYC-2). A model is handed the numbers, and works none of them out. `Indicators` computes them, for the candles of the agent's own timeframe and for those of each higher timeframe:

| Indicator | What it is | Candles it takes |
|---|---|---|
| `Sma20`, `Sma50`, `Sma200` | The simple moving average of the close: the mean of the latest 20, 50, and 200 closes. | 20, 50, 200 |
| `Ema20`, `Ema50` | The exponential moving average of the close, with a period of 20 and of 50. It starts as the simple average of the first closes of the period, and moves towards each later close by `2 / (period + 1)` of what it differs by. | 20, 50 |
| `Rsi14` | The relative strength index of Wilder, with a period of 14, from 0 to 100. The average gain and the average loss of the close start as the means of the first 14 changes, and each later change counts for one part in 14. The index is the share of the gain in both: `100 - 100 / (1 + gain / loss)`. | 15 |
| `Atr14` | The average true range of Wilder, with a period of 14, in the asset that the symbol is priced in. The true range of a candle is the greatest of its high less its low, and how far its high and its low are from the close before it. The average starts as the mean of the first 14, and each later one counts for one part in 14. | 14 |
| `VolumeAverage20` | The mean volume of the latest 20 candles, in the asset that is traded. The text has the volume of the latest candle beside it. | 20 |

| Rule | Detail |
|---|---|
| **Each is of the latest close of its series.** | For the agent's own timeframe that is the close of the cycle. |
| **Each is computed from every closed candle that the exchange gave, and not only from those that the package holds.** | One read gives up to 498 candles. An exponential average and the two of Wilder go by all that came before, so more candles make them more exact, and an average of 200 candles is there with a package of 120. `CandleSeries.ComputedFrom` says how many candles it was. A smaller `Trading:DataPackage:Candles` makes a prompt shorter, and no indicator less exact. |
| **An indicator that has too few candles is null.** | A symbol that is new to the exchange has no average of 200 candles. It is never a number that stands for one. The text writes `not available`. |
| **The periods are fixed, and are no settings.** | They are constants of `Indicators`, in one place: 20, 50, and 200 for the averages, 14 for the two of Wilder, and 20 for the volume. |
| **Everything is a decimal,** as the candles are. | No digit of a price is lost on the way. |
| The relative strength is 100 with no loss, 0 with no gain, and 50 when the close did not move. | A loss that has all but faded away divides nothing: the index is worked out as the share of the gain. |
| The true range of the first candle is its high less its low. | It has no close before it. |

**`IndicatorTests` holds each to known values.** A short series is worked out in the comment beside it. A long one is the series of a worked example that is widely used to explain the indicator, with prices to two decimal places, and its values were worked out from the definition, apart from the code, in exact decimals. Tables that are published for the same examples differ from them by a few hundredths here and there: they were made from prices with more decimal places than they show.

### What an agent holds

What a package says of the position and the orders of an agent comes through `IAgentHoldings`, which the Decision Engine declares, and which what keeps the positions and the orders answers: `StoredHoldings`, from its rows. See "What an agent holds" in [orders.md](orders.md).

```csharp
public interface IAgentHoldings
{
    Task<AgentHoldings> OfAsync(Agent agent, CancellationToken cancellationToken);
}

public sealed record AgentHoldings(
    decimal Equity,
    PositionContext? Position,
    IReadOnlyList<OpenOrder> Orders,
    decimal EquityAtDayStart,
    decimal PeakEquity,
    bool Valued = true);

public sealed record OpenOrder(OpenOrderKind Kind, OrderSide Side, decimal Quantity, decimal Price);
```

| Rule | Detail |
|---|---|
| **The orders of the agent are settled before its package is built.** | `TeamCycle` asks `IPlanOrders.SettleAsync` first: a stop-loss may have been executed since the last cycle, and then the agent holds no position any more. |
| **Its loss limits are evaluated next, before any model is called** (FR-RSK-8). | `TeamCycle` asks `ILossLimits.EvaluateAsync`. An agent that has reached its daily loss limit or its drawdown limit is halted, and its position is closed: its cycle builds no package and calls no model, and ends as `Halted`. See "Halting an agent" in [risk-guard.md](risk-guard.md). |
| **An agent that has placed nothing holds nothing.** | No position and no orders, and its equity is the capital that its user allocated to it. `AgentHoldings.NothingYet` is that, and it is what an agent holds where nothing answers. |
| **The stop-loss of a position is said when an order for it rests on the exchange.** | The price alone protects nothing. The same holds for its take-profit: an order rests for one on both markets, as one pair with the stop-loss on Spot, and as an order of its own on Futures. |
| **The equity is the allocated capital plus the net profit or loss to date.** | In the asset that the symbol is priced in, with unrealized profit and loss: what the positions of the agent made and lost, less the fees, with their funding, and with what its open position would make at the price now. See "The equity of an agent" in [orders.md](orders.md). |
| **The equity as the UTC day began, and the peak equity, are what the loss limits are counted from.** | No prompt states them: the Risk Guard holds a plan that opens a position to the daily loss limit and to the drawdown limit (FR-RSK-4), and what watches the loss limits halts an agent by them (FR-RSK-8). See "The loss limits" in [risk-guard.md](risk-guard.md). |
| **An equity that is not known gives no package.** | Where the exchange did not say the price that the open position is valued at, `Valued` is false, and `Equity` is without what that position would make. So it is where an order that closed a part of a position says no price that it was executed at yet. No prompt states an equity that is a guess: the package is not built, as for any read that did not succeed, and the cycle makes no decision. On Futures the price of the open position is the mark price, which the package needs itself. |
| An agent has one position at most. | `PositionContext` is the position as a prompt states it, with the prices of its stop-loss and its take-profit. The plan of the Strategist is held to it: a plan opens a position only for an agent that has none, and moves a stop-loss only to where it reduces risk (FR-CYC-5, FR-CYC-6). See "What a plan may do" in [team.md](team.md). |
| **An order is one that rests on the exchange.** | An entry that has not filled, a stop-loss, or a take-profit: `OpenOrderKind`. `Price` is the price of a limit entry, or the price that a protective order is triggered at. |
| It is asked for one agent, as its package is built, and again as the Risk Guard checks its plan, and only reads. | What it answers has no id and no name, as a prompt has none. The implementation that was registered last answers, as a service is found. |

### What a role reads

`DataPackage.Text` is the package as a role reads it, under the heading `# Data package` of a prompt. `DataPackage.PartsWith(lessons)` is the parts of a prompt from a package: the context of the agent, the text, and the lessons that were recalled for the call. See "Composing a prompt" in [prompts.md](prompts.md).

For an agent on Futures that works on 1D, with a package of two candles of each timeframe:

```
Closed 1D candles: 2, oldest first, through 2026-11-16T00:00Z
Each line: close time (UTC), open, high, low, close, volume
2026-11-15T00:00Z, 60250.25, 61000, 60100, 60900, 980
2026-11-16T00:00Z, 60900, 61420, 60880.25, 61310, 1842.337
Indicators at that close, computed by the system from 498 closed 1D candles:
- Moving averages of the close: SMA20 61122.48, SMA50 60310.9, SMA200 59876.12, EMA20 61190.33, EMA50 60402.71
- Relative strength: RSI14 58.2
- Average true range: ATR14 512.75
- Volume: 1842.337 in the latest candle, average of 20 candles 1930.11

Context, closed 1W candles: 2, oldest first, through 2026-11-16T00:00Z
2026-11-09T00:00Z, 57200, 59900, 56800.5, 58000, 8800
2026-11-16T00:00Z, 58000, 61420, 57500.75, 61310, 9500.125
Indicators at that close, computed by the system from 61 closed 1W candles:
- Moving averages of the close: SMA20 52110.4, SMA50 47020.15, SMA200 not available, EMA20 53340.92, EMA50 48811.07
- Relative strength: RSI14 63.41
- Average true range: ATR14 4120.6
- Volume: 9500.125 in the latest candle, average of 20 candles 8930.25

Open orders:
- stop-loss: sell 0.015 at 59500
- take-profit: sell 0.015 at 64000

Futures: mark price 61888.1, funding rate 0.01%, next funding 2026-11-16T08:00Z, open interest 82450.5 BTC
```

| Rule | Detail |
|---|---|
| **The same package gives the same text.** | A line ends with a line feed on every system. A number is written the same in every country, with every digit that it has and no zero after the last: `1.50` is `1.5`. A time is in UTC, to the minute. |
| A number that was worked out is rounded. | An average and the average true range to eight significant digits, and the relative strength to two decimal places. The package itself has them unrounded. |
| **The equity, the position, and the limits are not in the text.** | The same prompt states them under `# Agent context`, from `DataPackage.Agent`. The open orders are in the text: the context of the agent has no place for them. |
| An agent that has no open orders reads `Open orders: none`. | An agent on Spot reads no line of Futures. |
| No line reads as a heading of a prompt. | None begins with `#`. |
| **Every package is composed into a prompt.** | `DataPackageTests` builds the package of each market and timeframe and composes the prompt of every role from it: `PromptScreen` finds no secret, no address, and no identifier in it (FR-PRM-9). |

## Live search

The Sentiment Analyst reads news and social context for the symbol and the wider market (SRS 4.5). The service supplies neither: the model of the role searches for them itself, with the live search of its own provider, on the key of the agent's user (FR-CYC-3). How a provider is asked for its search is in "Live search" in [ai-providers.md](ai-providers.md).

What the call of the Sentiment Analyst goes by is behind two public types, which what runs the roles of a cycle uses: see "A role call" in [team.md](team.md).

```csharp
public interface IRoleCalls
{
    RoleCall Of(AgentRoleView role, ComposedPrompt prompt, string resultSchema);
}

public sealed record RoleCall(AgentRoleView Role, AiCallRequest Request);

public sealed record SentimentReport
{
    public const string NoLiveData = "no live data";

    public const string NotAvailable = "unavailable";

    public string? ResultJson { get; }
    public bool LiveData { get; }
    public string Text { get; }

    public static SentimentReport Of(RoleCall call, AiCallResult result);
    public static SentimentReport Unavailable { get; }
}
```

**The call.** `IRoleCalls.Of` says what the model of a role is called with: the model that the agent's user gave the role, the prompt that was composed for it, the schema of what the role answers, and whether the model may search live.

| Rule | Detail |
|---|---|
| **Only the Sentiment Analyst searches live.** | The request of every other role asks for no search, whatever its model can do. What the web says reaches the team through one role, whose report the Strategist reads as untrusted content (FR-PRM-8). The Risk Manager is not given the report: see "What a role is given" in [team.md](team.md). And only one call of a cycle can add charges for searching. |
| **Which models have live search comes from the capability flags in the configuration file.** | `IModelCapabilities.SearchesLive` of the Catalog says it, from the `LiveSearch` patterns of the model's provider. It is what the selector showed the user who chose the model. Nothing is stored, and no provider is asked: see "Which models can be selected" in [catalog.md](catalog.md). |
| **A Sentiment Analyst whose model has none still runs.** | It is called with the same prompt and the same schema, without a search. Nothing skips the role. |
| It makes no call. | What runs the roles hands the request to the gateway with the key of the agent's user, and decides whether a call is made again (FR-TEAM-9): `RoleRuns`. |

**The mark.** `SentimentReport.Of` makes the report of a call of the Sentiment Analyst that succeeded, and marks it.

| Rule | Detail |
|---|---|
| **A report rests on live data when its call offered a live search and the provider ran at least one.** | `AiCallResult.LiveSearches` says how many ran. |
| **Every other report is marked "no live data".** | That of a model that has no live search. And that of a model that could search and did not, or whose provider did not say: a report that nothing shows to rest on a search counts as resting on none. |
| **The mark is the service's, and not the model's.** | The structured result of the role says whether live data was available (SRS 4.5). The mark does not go by it. So content that a search found cannot make a report count for more than it does. |
| **Only `Of` makes a report.** | `SentimentReport` has no public constructor, and its `LiveData` cannot be set. It throws for a call of another role, and for a call that did not succeed. |
| **A Sentiment Analyst that gave no valid result has no report.** | `SentimentReport.Unavailable` stands in its place: the cycle continues with sentiment marked unavailable (FR-TEAM-9). It is the line of the mark, `Sentiment report: unavailable`, with no report under it, and it rests on no live data. See "When a role gives no valid result" in [team.md](team.md). |

**What the Strategist reads.** `DataPackage.PartsWith(lessons, sentiment)` is the parts of a prompt with the report under the text of the package, as `SentimentReport.Text` writes it:

```
Sentiment report: no live data
{"bias":"bullish","notableEvents":["A spot ETF saw record inflows."],"confidence":0.7}
```

| Rule | Detail |
|---|---|
| **The mark is a line of its own, above the report.** | `Sentiment report: no live data`, or `Sentiment report: live data`. Where there is no report, it is `Sentiment report: unavailable`, alone. |
| **The report is one line of JSON, whatever the model wrote.** | A line break inside a text of the report is written as `\n`, as JSON writes it. So no line of a report begins as a heading of a prompt does, or as the line of the mark does, and a report cannot pose as either. Nothing of it is lost: letters beyond ASCII and signs such as `<` stay as they are. |
| **The Strategist is told to give a report without live data no weight.** | In its instructions, and not in the data package: a role follows no instruction that it finds in the data package (FR-PRM-8). The mark is a fact there, and the instructions say what to do with it, and what a report that is unavailable means. See "Live data" in [prompts.md](prompts.md). |

**Whether the Sentiment Analyst relies on each provider's own live search, or the service supplies a news feed, is an open question of the specification (Q10).** Until it is decided, it relies on the search of its provider, as the requirement says.

What runs the roles of a cycle has three things to mind with this:

- **A report can hold what a prompt never holds.** A link that a search found may hold a UUID or an address. Such a report is rejected when it is validated, so the Sentiment Analyst is called once more, and the cycle then continues with sentiment marked unavailable: see "Validating a result" in [team.md](team.md). The instructions of the role ask for sources in words, with no link.
- **A call that searches takes longer,** and the Sentiment Analyst is one of the two calls that a cycle starts with. Its share of the 90 seconds of "How long a cycle takes" is the time limit of its provider at most, and twice that when it is called once more.
- **A `LiveSearch` pattern that names a model which does not take the search fails every call of that model as a Sentiment Analyst.** The provider refuses the request, which is a call that failed, and is not made again. The cycle continues with sentiment marked unavailable. The tracked file names `grok-4*` for Grok, and no model of another provider.

## How long a cycle takes

With providers responding normally, 95% of cycles finish within 90 seconds of candle close (NFR-PRF-2). A cycle is three calls of the team one after the other, the first of them two at once, so it takes what the providers of its team take: no test shows that with the providers themselves. What starts the cycles has its share of it, and measures the rest:

- **A run begins within about a second of the close,** which is the beat of the scheduler, and the cycles start when their candles are in.
- **The queue takes no time worth counting.** `CycleCapacityTests` passes the cycles of 500 agents that share a boundary through it, with the limits of the tracked file, in seconds.
- **A data package takes its reads of the exchange, and nothing else worth counting.** They are made at the same time and shared by the cycles of a symbol, and each says how long it took: see "The log".
- **Where a limit binds, cycles wait for a round, and the target follows by arithmetic.** With cycles of 45 seconds, a second round ends within 90 seconds, and a third does not.
- **Every cycle says when it started and when it ended,** in milliseconds since its close, and how long it ran.
- **Every close says how many of its cycles ended within 90 seconds,** once, in its summary. The summary is a warning when more than one in twenty did not: those that ended later, that were stopped before the next close, and that could not start in time.

## The settings

| Key | In the tracked file | What it is |
|---|---|---|
| `Trading:Cycles:MaxPerProvider` | 250 | The most cycles that use one AI provider at once, of all users together. 1 or more. |
| `Trading:Cycles:MaxPerKey` | 4 | The most cycles that use the key of one user for one AI provider at once. 1 or more. |
| `Trading:DataPackage:Candles` | 200 | The most recent closed candles of the agent's timeframe that a data package holds (FR-CYC-2). From 1 to 498. |
| `Trading:DataPackage:ContextCandles` | 50 | The most recent closed candles of each higher timeframe that a data package holds. From 1 to 498. |
| `Trading:StaleSeconds:15m`, `1H`, `4H`, `1D` | 120, 300, 600, 900 | The stale limit of each timeframe: how many seconds after the close of a candle a decision may still open a position (FR-CYC-9). 1 or more. See "The stale limit" in [team.md](team.md). |

The quarter hour, the 5 minutes, the half minutes, and the 5 seconds are fixed, as the time limit of a call to the exchange is. So are the periods of the indicators.

498 is what one read of the exchange gives: `ExchangeCandles.Most`. There is no range of the past to ask for, so a package cannot hold more. A package holds all that the exchange has when those are fewer than the setting.

## The log

The job logs under the area `Ecnanib.DecisionEngine.DecisionCyclesJob`. What is logged for one cycle carries `UserId`, `AgentId`, and `CycleId`, as [logging.md](logging.md) describes.

```
{CycleCount} cycles are due for the close at {ClosedAt}, with candles of {GroupCount} symbols and timeframes
The cycle of agent {AgentId} for the close at {ClosedAt} ({Timeframe}) started {StartDelayMs} ms after the close
The cycle of agent {AgentId} for the close at {ClosedAt} ended {EndDelayMs} ms after the close, in {DurationMs} ms
The cycle of agent {AgentId} for the close at {ClosedAt} failed after {DurationMs} ms, with no decision
The cycle of agent {AgentId} for the close at {ClosedAt} could not start
The cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms: the next close is near
The cycle of agent {AgentId} for the close at {ClosedAt} was stopped after {DurationMs} ms, with the application
Agent {AgentId} no longer runs, so it gets no cycle for the close at {ClosedAt}
Agent {AgentId} was given other settings since the close at {ClosedAt}, so it gets no cycle for that close
Agent {AgentId} is not reconciled with the exchange yet, so it gets no cycle for the close at {ClosedAt}
The exchange did not give the candle of {Symbol} on {Market} in {TradingMode} that closed at {ClosedAt} ({Timeframe}) within {CandleWaitSeconds} seconds of the close: {ExchangeCallOutcome}. {CycleCount} cycles are skipped
The candles of {Symbol} on {Market} in {TradingMode} could not be asked for ({Timeframe})
{CycleCount} cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, and are skipped
{CycleCount} cycles of the close at {ClosedAt} still run {EndsBeforeSeconds} seconds before the next close, and are stopped
The cycles of the close at {ClosedAt} could not start within {StartsWithinMinutes} minutes of it, so none starts
The agents that their daily loss limit halted on an earlier UTC day could not be resumed at the close at {ClosedAt}
The cycles of the close at {ClosedAt} are over after {DurationMs} ms: {Ended} ended within {TargetSeconds} seconds of the close and {EndedLate} later, {Failed} failed, {OutOfTime} were stopped before the next close, {TooLate} could not start in time, {WithoutCandle} had no candle, {NotReconciled} were not reconciled, {NotRunning} no longer ran, and {Stopped} were stopped with the application
```

What builds a data package logs under the area `Ecnanib.DecisionEngine.DataPackages`, inside a cycle, so its event carries `UserId`, `AgentId`, and `CycleId` as well:

```
The data package of agent {AgentId} for the close at {ClosedAt} was built in {DurationMs} ms: {CandleCount} candles of {Timeframe}, {ContextCandleCount} of {ContextTimeframes}, and {OrderCount} open orders
```

What runs the team of a cycle logs under `Ecnanib.DecisionEngine.TeamCycle` and `Ecnanib.DecisionEngine.RoleRuns`, inside the cycle as well: see "The log" in [team.md](team.md).

| Level | Event |
|---|---|
| `Information` | The cycles that are due, the start and the end of a cycle, a data package that was built, an agent that gets no cycle, a cycle that was stopped with the application, and the summary of a close. |
| `Warning` | A candle that the exchange did not give in time, cycles that could not start in time, cycles that were stopped before the next close, and the summary of a close of which more than one in twenty cycles did not end within 90 seconds. |
| `Error` | A cycle that failed, a cycle that could not start, candles that could not be asked for, and agents whose daily halt could not be ended before the cycles of a close, each with the error. A cycle whose data package could not be built is one that failed: its error says which part of the market data the exchange did not give, and how the read ended. |

An event names a user and an agent by their ids, and never holds the name of an agent, a price, or a setting. What the scheduler logs about the job is in [background-work.md](background-work.md): a run that began, ended, failed, or was stopped, and a boundary that was skipped.

## Testing

A test application runs without `decision-cycles`, unless the test says that its application runs cycles: see "Testing a job" in [background-work.md](background-work.md). `CyclesApplication` is the application with the job, on a clock that the test sets, with these stand-ins behind the public interfaces:

| Stand-in | In place of | What it does |
|---|---|---|
| `StandInCycles` | `IDecisionCycle` | Keeps what each cycle was handed, in the order the cycles started, and how many ran at once for each provider and each key. It holds the cycles until the test lets them go, and has a cycle fail when the test says so. |
| `StandInCandles` | `IExchangeMarketData` | Gives candles through the latest close by the clock of the test, unless the test gives it another answer. It keeps what it was asked. |
| `StandInRunningAgents` | `IRunningAgents` | Says the agents that the test gave it, with their teams, and needs no rows. A test of the agents of the database leaves it out. |
| `StandInReconciliation` | `IReconciliation` | Says that an agent is reconciled, unless the test says that it is not yet. |

A test starts the cycles of a close in one of two ways. It runs the job itself, with `CyclesApplication.RunAsync`, for a close of its choosing, and waits for the cycles of that close to be over. Or it moves the clock over a quarter hour, and the scheduler runs the job: the clock then starts between two quarter hours, and the test waits for the scheduler first, with `SchedulerStartedAsync`.

A limit is held to through what the stand-in counted while the cycles were held, and an order through the whole sequence of the starts, with one cycle at a time. Neither waits for something not to happen.

**The tests of the cycles do not build data packages:** `StandInCycles` is the inside of their cycles, so what they count is what the job asks the exchange for. The data package has stand-ins of its own:

| Stand-in | In place of | What it does |
|---|---|---|
| `StandInMarket` | `IExchangeMarketData` | Has all the market data that a package is built from: candles of every length through the latest close by the clock of the test, the week included, the mark price with its funding, and the open interest. A test gives it another answer for candles, and says how a read of Futures ends. It keeps what it was asked. |
| `StandInHoldings` | `IAgentHoldings` | Says of every agent what the test gave it: an equity, a position, and orders. Without it an agent holds nothing, as in the application. |

`PackagesApplication` is the application with both, and builds the package of a cycle of an agent that needs no row. `CyclesApplication` takes a `StandInHoldings` as well, for cycles whose agent holds a position, and other settings of the file of the tests, such as a stale limit: see "Testing" in [team.md](team.md). `CyclesApplication` runs the job with the application's own inside of a cycle, on `StandInMarket`, when a test says that its cycles build packages: `CycleDataPackageTests` does, and runs the job behind the gateway itself once, with a stand-in for Binance. Such a cycle runs the team of its agent as well, behind stand-ins for the models, the keys, and the records: see "Testing" in [team.md](team.md).

## Not built yet

- The orders that only one market has: see "Not built yet" in [orders.md](orders.md).
- Asking again for market data that a read did not give. A cycle whose package cannot be built makes no decision, and the next close starts the next one.
- Waiting for a candle of a higher timeframe that came one short. The candles of the agent's own timeframe cover the same time.
- The price that a symbol last traded at, in a package. A package has the close of the latest candle, and on Futures the mark price. What has the Risk Guard check a plan asks for the price then, and holds an entry to its distance from it (FR-RSK-4): see "The facts" in [risk-guard.md](risk-guard.md).
- Taking out of a report what a prompt never holds, so that the rest of it can be read. A report that holds it is rejected whole.
- A start that waits for reconciliation before it schedules anything (NFR-REL-3). The agents are reconciled as the background work starts, beside the other jobs, and an agent that is not reconciled gets no cycle: nothing is placed for it before. The other jobs do not wait.
- Recording a cycle that never started: one that was skipped, or whose agent no longer ran. A cycle is one run of a team, and such a one ran none: it leaves its events in the log, and is on no page. A cycle that started is recorded as a complete record, however it ends (FR-CYC-10).
- A test cycle that a user runs for an agent (FR-CYC-12). The queue is of one run of the job, so a cycle that starts outside a run takes none of its places.
- Limits on calls. The places are of cycles: a cycle is two calls with one key while the two analysts work.
- Breakers for a provider or a key that fails repeatedly (NFR-REL-4). A provider that is slow holds back the cycles that need it, and nothing pauses its calls.
- Stopping the cycle of an agent when its user pauses or stops it. What places an entry asks again whether the agent takes new entries.
- The queue depth and the health of the scheduler on the operator status screen (FR-OPS-2). The summary of each close is in the log.
- The load test at 500 running agents, with real cycles (NFR-PRF-3).
