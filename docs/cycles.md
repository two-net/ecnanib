# Cycles

A cycle is one run of an agent's team, triggered when a candle closes. A cycle starts when a candle of the agent's timeframe closes, on UTC boundaries, and only closed candles are used (FR-CYC-1). Only an agent in the Running state gets cycles (SRS 3.3). When many agents share a candle boundary, their cycles are queued with concurrency limits per provider and per user key, shortest timeframe first (FR-CYC-11). Cycles missed while the service was down are skipped, not replayed: after a restart the system reconciles first, then waits for the next candle close (FR-CYC-8).

What is built is what starts a cycle and lets it wait for its turn. What a cycle does once it has started is not built: the data package, the calls of the team, the Risk Guard, and the orders (SRS 3.2, steps 2 to 8). A cycle that starts ends at once, with no decision and no order. See "Not built yet".

The code is in `src/Ecnanib/DecisionEngine`. The rules below are enforced by tests in `tests/Ecnanib.Tests/DecisionEngine`, in the classes whose names begin with `Cycle`.

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
| **A cycle that still runs half a minute before the next close is stopped.** | `DecisionCyclesJob.EndsBefore` | Its token is canceled, as it is when the application stops. A warning says how many were stopped, and one for each says which. |

- **Why 5 minutes.** The longest cycle is three calls of the team one after the other, each made twice at most (FR-TEAM-9). With the time limit of 60 seconds of the tracked file that is 6 minutes, so a cycle that starts within 5 is over long before it would be stopped. With time limits that are raised far above that, a cycle can reach the stop.
- **Why half a minute.** A cycle that has just placed an entry needs one more call to the exchange to protect it, which takes its time limit and its wait at the limiter at most, 25 seconds. It is over before the next run is due.
- **A cycle is stopped, and never left behind.** The run returns only when every cycle that it started has ended. A cycle that ran on beside the next run would be a second cycle of its agent, outside the lock of the background work. A cycle that does not end when it is told to keeps the run going, and the scheduler then skips the next close, with its warning.
- **A run that begins 5 minutes after its close or later starts no cycle.** The scheduler starts a run that is late for its boundary, after the machine slept or the clock was set, and the job judges that it is too late.
- **When the application stops, or is no longer the instance that runs the background work, the cycles stop with it** (NFR-REL-5). Nothing more starts, the cycles that run are told to stop, and the run ends when they have, as `Stopped`. Nothing of the queue is kept: a cycle that did not start is not made up for.
- Every limit is looked at by the clock at least once a second, as the scheduler looks at it.

## After a restart

**Cycles missed while the service was down are skipped, not replayed** (FR-CYC-8). The job is on UTC boundaries, so the scheduler makes up for no boundary that passed while the application was down, or while the machine slept: see "Schedules" in [background-work.md](background-work.md). After a restart the job waits for the next close. The queue is in memory, so there is no cycle from before to go on with.

**The system reconciles first** (FR-CYC-8, NFR-REL-3). Reconciliation is not built. What reconciles the records of an agent with the exchange answers through `IReconciliation`, which the Decision Engine declares:

```csharp
public interface IReconciliation
{
    Task<bool> HasReconciledAsync(RunningAgent agent, CancellationToken cancellationToken);
}
```

| Rule | Detail |
|---|---|
| **An agent that is not reconciled yet gets no cycle.** | It is asked as the turn of a cycle comes, after the agent was found to run. The cycle of that close is skipped for it, and the next close asks again. |
| **No other agent waits for it.** | The question is about one agent, and is answered at once. An account that cannot be reconciled, because its key is refused, keeps nobody else from trading. |
| "Reconciled" is since this instance took up the background work. | At the start of the application, and again after the lock of the background work was lost and taken back. What implements it keeps that. |
| **Nothing implements it yet, and every agent passes.** | No position and no order is stored that could differ from the exchange. `CycleReconciliationTests` has a test that fails as soon as a table of positions or of orders is in the model and nothing answers. |

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

- **The token is canceled** when the application stops, when it stops being the instance that runs the background work, and half a minute before the next close. The cycle then ends soon, by letting `OperationCanceledException` out, and leaves no entry without its protection (NFR-REL-5).
- An error that leaves `RunAsync` ends the cycle with no decision.
- **The registered implementation does nothing.** `NoDecisionYet` ends at once. What is built behind `IDecisionCycle` takes the cycle from there, and changes nothing of when a cycle starts.
- The interfaces are public so that a test can put a stand-in behind them: the tests see no internal type.

## How long a cycle takes

With providers responding normally, 95% of cycles finish within 90 seconds of candle close (NFR-PRF-2). That cannot be shown before a cycle calls a model. What starts the cycles has its share of it, and measures the rest:

- **A run begins within about a second of the close,** which is the beat of the scheduler, and the cycles start when their candles are in.
- **The queue takes no time worth counting.** `CycleCapacityTests` passes the cycles of 500 agents that share a boundary through it, with the limits of the tracked file, in seconds.
- **Where a limit binds, cycles wait for a round, and the target follows by arithmetic.** With cycles of 45 seconds, a second round ends within 90 seconds, and a third does not.
- **Every cycle says when it started and when it ended,** in milliseconds since its close, and how long it ran.
- **Every close says how many of its cycles ended within 90 seconds,** once, in its summary. The summary is a warning when more than one in twenty did not: those that ended later, that were stopped before the next close, and that could not start in time.

## The settings

| Key | In the tracked file | What it is |
|---|---|---|
| `Trading:Cycles:MaxPerProvider` | 250 | The most cycles that use one AI provider at once, of all users together. 1 or more. |
| `Trading:Cycles:MaxPerKey` | 4 | The most cycles that use the key of one user for one AI provider at once. 1 or more. |

The quarter hour, the 5 minutes, the half minutes, and the 5 seconds are fixed, as the time limit of a call to the exchange is.

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
The cycles of the close at {ClosedAt} are over after {DurationMs} ms: {Ended} ended within {TargetSeconds} seconds of the close and {EndedLate} later, {Failed} failed, {OutOfTime} were stopped before the next close, {TooLate} could not start in time, {WithoutCandle} had no candle, {NotReconciled} were not reconciled, {NotRunning} no longer ran, and {Stopped} were stopped with the application
```

| Level | Event |
|---|---|
| `Information` | The cycles that are due, the start and the end of a cycle, an agent that gets no cycle, a cycle that was stopped with the application, and the summary of a close. |
| `Warning` | A candle that the exchange did not give in time, cycles that could not start in time, cycles that were stopped before the next close, and the summary of a close of which more than one in twenty cycles did not end within 90 seconds. |
| `Error` | A cycle that failed, a cycle that could not start, and candles that could not be asked for, each with the error. |

An event names a user and an agent by their ids, and never holds the name of an agent. What the scheduler logs about the job is in [background-work.md](background-work.md): a run that began, ended, failed, or was stopped, and a boundary that was skipped.

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

## Not built yet

- What a cycle does once it has started: the data package and its indicators (FR-CYC-2), the calls of the team (FR-TEAM-6), the Risk Guard (FR-RSK-3), and the orders (FR-BIN-6). `NoDecisionYet` stands for all of it.
- The candles of the higher timeframes, which are the context of a cycle (SRS 4.6). A cycle is handed the candles of its own timeframe through its close. What builds the data package asks for the others.
- Reconciliation, which answers `IReconciliation` (FR-BIN-7), and the order of a start: migrate, reconcile, then schedule (NFR-REL-3).
- The stale limit: a decision that completes later than the limit of its timeframe opens no position (FR-CYC-9). `Trading:StaleSeconds` has the limits, and nothing reads them yet.
- Storing a cycle, with its outcome (FR-CYC-10). A cycle leaves its events in the log and nothing in the database, also when it was skipped.
- A test cycle that a user runs for an agent (FR-CYC-12). The queue is of one run of the job, so a cycle that starts outside a run takes none of its places.
- Limits on calls. The places are of cycles: a cycle is two calls with one key while the two analysts work.
- Breakers for a provider or a key that fails repeatedly (NFR-REL-4). A provider that is slow holds back the cycles that need it, and nothing pauses its calls.
- Stopping the cycle of an agent when its user pauses or stops it. What places an entry asks again whether the agent takes new entries.
- The queue depth and the health of the scheduler on the operator status screen (FR-OPS-2). The summary of each close is in the log.
- The load test at 500 running agents, with real cycles (NFR-PRF-3).
