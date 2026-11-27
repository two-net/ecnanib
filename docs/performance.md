# Performance

Typical screens respond within 300 ms at the 95th percentile, measured at the server (NFR-PRF-1). A single instance supports at least 500 running agents, with every cycle at a shared candle boundary started within 30 seconds (NFR-PRF-3).

The service measures the first itself: it times every screen that it shows, and an operator reads on the status screen whether the target is met. The second was to be confirmed by a load test. That test is built, it was run, and the figure is confirmed: see "What was measured".

The two other figures of the specification are elsewhere: how long a cycle takes (NFR-PRF-2) is in "How long a cycle takes" in [cycles.md](cycles.md), and how long a catalog sync takes (NFR-PRF-4) in [catalog.md](catalog.md).

The code that measures the screens is in `src/Ecnanib/Platform/Performance`. The load test and the tests of the measure are in `tests/Ecnanib.Tests/Performance`.

## Screen response times

`ScreenTiming.UseScreenTiming` times every request that asks for a screen, and `ScreenTimesMeasure` keeps what the times add up to. `IScreenTimes` says it:

```csharp
public interface IScreenTimes
{
    ScreenTimesReading Read();
}

public sealed record ScreenTimesReading(DateTimeOffset MeasuredSince, ScreenTimes All, IReadOnlyList<ScreenTimes> Screens);

public sealed record ScreenTimes(string? Route, AnswerTimes LastHour, AnswerTimes LastDay);

public sealed record AnswerTimes(long Count, long WithinTarget, TimeSpan? Percentile, bool BeyondLongest)
{
    public bool? MeetsTarget { get; }
}
```

| Rule | Detail |
|---|---|
| **A screen is a page that was asked for and shown.** | A GET whose endpoint is a page of the Web UI, answered with a success. A page that follows what changes asks for itself again in the same way, so that counts too: see [live-updates.md](live-updates.md). |
| **Nothing else is counted.** | Not an answer that sends the browser on, such as a page for signed-in users that a visitor asks for, and not one that an error ended: neither showed the screen, and both are quick. Not what a form does, which takes what it does: a password that is hashed, a key that its provider is asked about. And nothing that is no page: a file of the application, the API, a download, the connection of a page that follows. |
| **It is measured at the server.** | From right after the request has its correlation properties until the page is written: who is signed in, the frame, the page, and what an error is answered with are all in it. What lies between the browser and the application is not. |
| **A screen is told by its route.** | As the application declares it, such as `/agents/{Id:guid}/state`: the name of what it is of, and never the value. So no id of an agent, or of anything else of a user, is kept (FR-OPS-5). |
| **The target is fixed.** | `ScreenTarget`: 95 of 100 answers within 300 ms. They are the figures of the requirement, and no setting, as the 90 seconds of a cycle are none. |
| **A time is counted in a step, and not kept.** | The steps end at 5, 10, 20, 30, 40, 50, 75, 100, 150, 200, 250, 300, 400, 500, and 750 ms, and at 1, 1.5, 2, 3, 5, 10, 30, and 60 seconds. One ends at the target exactly, so whether the target is met is counted exactly: an answer of 300 ms is within it. |
| **The percentile is the end of its step.** | `AnswerTimes.Percentile` says that 95 of 100 answers took no longer than that: they may have taken less. One that lies beyond a minute is said as that, `BeyondLongest`, and has no figure. |
| **The two spans are exact to the minute.** | The last 60 minutes, and the last 24 hours, by the clock of the application, as the counts of errors are: see "The counts of errors and warnings" in [logging.md](logging.md). |
| **Where nothing was shown, nothing is known.** | A span without an answer has no percentile, and its target is neither met nor missed. A screen that was not shown in the last 24 hours is not listed. |
| **It measures since the instance started.** | In memory, by each instance for itself: a restart begins it again, and the screen says since when it is. Nothing of it is written to the database (NFR-DAT-7). |
| **It cannot grow without end.** | A route that was shown takes about 150 kB: a number for each step of each minute of a day. The routes are those that the application declares. Should there ever be more than `ScreenTimesMeasure.MostScreens`, which is 128, the rest are counted together. |

**What it does not measure:** what a browser takes to show the page, and the network between. The connection of a page that follows is not a request, so what travels over it is not measured either: such a page shows what changed by asking for itself again, which is.

## The load test

`LoadTests` runs one instance of the application with 500 agents that run, has a close come that all of them share, and says when each cycle started. It is a test like the others, on the application as the tests start it, so it needs nothing but Docker.

**The application is its own from the close to the record of the cycle.** Only the network is the test's:

| Its own | Detail |
|---|---|
| What starts the cycles | The scheduler starts the job `decision-cycles` by itself, when the clock shows the close. The job reads the agents that run from the database, asks for the candles of the close, and lets the cycles wait in its queue for their places. See [cycles.md](cycles.md). |
| The cycle | `TeamCycle`, whole: it settles the orders of the agent, evaluates its loss limits, builds the data package, composes the prompts, calls the four roles with the stored keys of the user, validates what they answer, and records the cycle, which tells the open pages of the user. |
| The gateways | The AI Provider Gateway with its adapters and its breakers, and the Exchange Gateway with the market data that it shares and its limiter. |
| The database | PostgreSQL, with the users, their keys, and their agents as rows. |
| The pages | Some of the users, and the operator, have a browser open, and keep asking for screens while the cycles run. |

| The test's | Detail |
|---|---|
| The AI providers | `LoadProviders` answers every call with a valid result of the role, in the form of the provider, after the time that the plan gives a model. The Strategist plans to open a position, and the Risk Manager vetoes it: so a cycle is the three calls of its team in a row, the first of them two at once, which is the longest that a cycle holds its places for, and no order is placed. |
| Binance | `LoadBinance` answers the market data that the cycles read. It gives the candle of a close only once it has begun the next candle, which it does as long after the close as the plan says for the market. Until then the gateway asks again every second, as it does of Binance: see "The candle of the close" in [cycles.md](cycles.md). |
| The clock | A `TestClock` that stands before the close while the users and their agents are stored, and is let run five seconds before it. From then on a second of the clock is a second, so what the application says of when a cycle started is how long that took. |

**The agents.** Each user has four, in the test environment: one on each of two symbols of each market. The timeframes take turns from user to user, so that every symbol of every market has agents of all four timeframes. The close is a midnight in UTC, when a candle of every timeframe closes, so all of them are due at once. Each has the team of a new agent: four roles at four providers.

**The settings** are those of the file of the tests, with what bears on the load as the tracked file has it: `Trading:Cycles`, `Trading:DataPackage`, `Trading:StaleSeconds`, the time limits of the calls to the models, `Binance:LimitSharePercent`, `Breakers`, and the levels of the log.

**What a run is given,** by variables of the environment:

| Variable | Without it | What it is |
|---|---|---|
| `ECNANIB_LOAD` | The run is skipped | Asks for the load test. Any value does. |
| `ECNANIB_LOAD_AGENTS` | 500 | How many agents run. A user has four of them. |
| `ECNANIB_LOAD_MAX_PER_PROVIDER` | What the tracked file says | `Trading:Cycles:MaxPerProvider` for the run. |
| `ECNANIB_LOAD_MODEL_SECONDS` | 15 | How long a model takes to answer. A cycle is three calls in a row, so it takes 45 seconds, which is what [cycles.md](cycles.md) reckons with. |
| `ECNANIB_LOAD_SPOT_CANDLE_SECONDS` | 5 | How long after the close Binance gives its candle on Spot. |
| `ECNANIB_LOAD_FUTURES_CANDLE_SECONDS` | 16 | The same on Futures. Both are the longest that was measured in the test environment: see "The candle of the close" in [cycles.md](cycles.md). |
| `ECNANIB_LOAD_BROWSERS` | 10 | How many users have a browser open, beside the operator. |
| `ECNANIB_LOAD_SCREEN_SECONDS` | 0.25 | How long a browser waits between two screens. |

It runs only where it is asked for, as the tests that call a provider or drive a browser do. It takes about a minute and a half, and wants the machine to itself, because what it measures is time:

```
ECNANIB_LOAD=1 dotnet test --solution Ecnanib.slnx --filter-class "*Performance.LoadTests"
```

**The report** is written to `TestResults/load` beside the tests as they were built, in a file that is named after the agents and the limit, and to the output of the test. It says what ran and on what machine, when the cycles started and ended, by the market and the timeframe of their candles, how the close ended as its summary in the log says it, the calls to the models and to Binance with the most that was used of each request limit, the errors and warnings that were logged, and the response times of the screens meanwhile. Its first sentence is what the run is for: every cycle started within 30 seconds of the close, or how many did not.

**The test passes** when every agent that runs was due and its cycle started within 30 seconds of the close, no cycle failed, was stopped, or was skipped, every cycle was recorded, and the screens met their target meanwhile. It writes the report first, however the run ends.

**A close of a few agents is run with every run of the tests,** by the same steps and with the same report: eight agents, models that answer within a fifth of a second, and candles a second after the close. So what the load test is made of is known to work when it is asked for.

## What was measured

On 27 November 2026, on a machine with 10 processors and 32 GB of memory (macOS, Arm64, .NET 10), with PostgreSQL 18 in a container on the same machine. Models took 15 seconds, and Binance gave the candle of the close after 5 seconds on Spot and after 16 on Futures. Each run is of one close.

| | 500 agents, 250 for a provider | 500 agents, 500 for a provider | 1,000 agents, 1,000 for a provider |
|---|---|---|---|
| Cycles that started within 30 seconds | 250 of 500 | **500 of 500** | 1,000 of 1,000 |
| The last cycle started after | 52.0 s | 16.4 s | 16.6 s |
| Cycles that ended within 90 seconds (NFR-PRF-2) | 250 of 500 | 500 of 500 | 1,000 of 1,000 |
| The last cycle ended after | 98.0 s | 62.5 s | 63.7 s |
| Cycles that failed, were stopped, or were skipped | 0 | 0 | 0 |
| What a cycle took beside the calls of its team | 1.1 s for half, 1.6 s at the most | 1.2 s for half, 1.5 s at the most | 2.2 s for half, 2.5 s at the most |
| Screens within 300 ms | 3,755 of 3,780 | 2,469 of 2,497 | 2,383 of 2,431 |
| 95 of 100 screens within | 150 ms | 150 ms | 150 ms |
| Errors and warnings in the log | The warning of the close | None | None |
| Processors that worked, on average | 0.7 of 10 | 0.8 of 10 | 0.8 of 10 |
| Memory of the process at the most | 756 MB | 1,006 MB | 1,699 MB |

The processors and the memory are of the process of the test: the application, and with it what stands in for the providers and for Binance, and the browsers. The database is not in them.

**With 250 cycles for a provider, half of the cycles started too late.** A team of four providers takes a place with each, so 250 cycles ran at once. The agents on Spot had their candles first, after 5 seconds, and took every place. The 250 on Futures had theirs after 16 seconds, waited for a cycle to end, and started 52 seconds after the close. They ended 98 seconds after it, so the close also missed what a cycle has to end in (NFR-PRF-2), and its summary was a warning.

**With 500 for a provider, every cycle started within 17 seconds.** Those on Spot started 5.3 to 5.5 seconds after the close, and those on Futures 16.2 to 16.4: each within half a second of when Binance gave its candle. So what the service adds to the wait for the candle is under a second for 500 cycles, and the 30 seconds are Binance's to use.

**So the tracked file now has 500,** in place of 250, and the figure of the specification is confirmed: a single instance supports at least 500 running agents, with every cycle at a shared candle boundary started within 30 seconds (NFR-PRF-3). The run of 1,000 agents shows that the instance itself carries twice that on this machine: it is the limit of the file that says how many start at once, and not the instance.

**The screens were within their target in every run** (NFR-PRF-1). In the run that confirms the figure, by screen:

| Screen | Shown | Within 300 ms | 95 of 100 within |
|---|---|---|---|
| All screens | 2,497 | 2,469 | 150 ms |
| `/dashboard` | 407 | 398 | 150 ms |
| `/agents/{Id:guid}/state` | 407 | 405 | 100 ms |
| `/agents` | 203 | 200 | 50 ms |
| `/agents/{Id:guid}/cycles` | 205 | 203 | 40 ms |
| `/experience` | 203 | 200 | 75 ms |
| `/usage` | 204 | 202 | 50 ms |
| `/catalog` | 204 | 203 | 40 ms |
| `/notifications` | 205 | 202 | 75 ms |
| `/account` | 203 | 203 | 30 ms |
| `/operator` | 234 | 231 | 75 ms |

**The exchange was asked little** (risk R2 of the specification). The candles and the rest of the market data are read once for all agents that share them (FR-BIN-3), so the close cost 192 calls whether 500 agents ran or 1,000: at the most 280 of the 1,680 of request weight that the service allows itself in a minute on Futures, and 100 of 4,200 on Spot. No call waited at the limiter. Most of the calls are the candles of Futures, asked for again every second until Binance gave them.

**The database kept up.** What a cycle took beside the calls of its team, from its start to its record, was 1.5 seconds at the most with 500 cycles at once, and 2.5 with 1,000, on the pool of 100 connections that the driver has by default. The transactions that tell the open pages of a change commit one after the other: with 500 cycles at one close that held nothing up that could be measured. See "Signals" in [live-updates.md](live-updates.md).

**What the figure holds for:**

- **The agents of one close, up to the limit of the file.** The cycles of more than `Trading:Cycles:MaxPerProvider` agents whose teams use the same provider start in rounds, and a round waits for cycles to end, which takes as long as the models do.
- **Four agents of one user at one close.** `Trading:Cycles:MaxPerKey` is 4, so a fifth agent of the same user waits for one of the first four to end, whatever the instance carries: the limits of the queue are a requirement of their own (FR-CYC-11). The users of the load test have four agents each.
- **This machine.** A deployment on less runs the test again on what it has.
- **A Binance that gives the candle of the close within half a minute.** A candle that is later is not waited for, and its cycles are skipped: see "The candle of the close" in [cycles.md](cycles.md).

## The operator status screen

One part of the screen is of this document: "Response times of the screens", which is the second, after the availability. They are the two readings that are held against a target.

| Part | What it shows |
|---|---|
| Response times of the screens | The target, what counts as a screen that was shown, and since when the instance measures. Then all screens together, and each screen by its route: how often it was shown, the time that 95 of 100 answers were within, and whether the target is met, for the last hour and for the last 24 hours. A span in which nothing was shown says "Not known". |

It is read through `IScreenTimes` of the platform, and is of the instance that answered the page. See "The operator status screen" in [ownership.md](ownership.md).

## The log

| Event | Level | When |
|---|---|---|
| `The screen {Route} was answered in {DurationMs} ms` | Debug | A screen was shown. It is under `Ecnanib.Platform.Performance.ScreenTiming`, with the request id. The tracked file logs from `Information` on, so it leaves these out: an override for that area lets them in. |

The cycles say when each started and ended, and each close says how many ended in time: see "The log" in [cycles.md](cycles.md). The load test reads its figures from those events.

## Testing

`ScreenTimesTests` holds the measure to its rules, on the application: the operator status screen with a reading that takes as long as the test says, for an answer within the target and one beyond it, the two spans on a clock that the test moves, what is counted and what is not, the route in place of the address, and the event of the log. `ScreenResponseTests` is the requirement itself: it asks for every screen of the screen map 40 times, in the seeded application of the accessibility tests, where every screen has something to show, and has the measure say that each screen and all of them together met the target. `OperatorStatusPageTests` reads the part of the screen, with a measure of the test's that says each case.

`LoadTests` is the load test, and the close of a few agents that runs every time. `LoadFixtures.cs` has what they are made of: `LoadPlan`, which reads the variables, `LoadApplication`, which stores the users and their agents and runs the close, `LoadProviders` and `LoadBinance`, and `LoadResult`, which writes the report.

`CycleCapacityTests` passes the cycles of 500 agents through the queue alone, with cycles that end at once: it runs every time, and is what says that the queue itself takes no time worth counting. `TrackedFileTests` holds the tracked file to the limits that were confirmed.

`TestClock` can be let run, with `Run`: only the load test does that. Every other test moves it.

## Not built yet

- Orders in the load test. Its teams decide to open nothing, so no entry is placed and no order is settled, and the request limits are used by the market data alone.
- The other jobs beside the close. The application of a test keeps the jobs that settle, reconcile, watch positions, and clean up off its schedule, and so does the load test.
- A user with more agents at one close than `Trading:Cycles:MaxPerKey`. The fifth waits by design, which no run shows.
- Models that take different times, or fail. `ProviderIsolationTests` has the provider that is down, for two agents.
- More than one instance, and a machine other than the one that it was run on.
- The response times as a browser sees them, with the network between.
- Telling an operator that the screens have fallen under their target, other than on the status screen.
- The response times of what a form does, and of the API.
- Keeping the times across a restart, or for more than a day.
- When the cycles of a close started, on the operator status screen. Every cycle says it in the log.
