# Reliability

Failures are safe: any internal error, timeout, or dependency outage results in no new entry. Open positions stay protected by orders resting on the exchange, which do not depend on Ecnanib being up (NFR-REL-2). An outage at one AI provider affects only the cycles that need it. Repeated failures for a provider or a key trip a breaker that pauses calls and retries later. A Binance outage pauses entries and raises an alert (NFR-REL-4). On startup the application migrates the database, reconciles every running agent with the exchange, then resumes scheduling. A restart never creates a duplicate order (NFR-REL-3). Shutdown is graceful: in-flight cycles finish or abort safely, and no entry is left without protection (NFR-REL-5).

Providers have outages, and so has an exchange (risk R5 of the specification). Nothing that fails opens a position, and what fails again and again is not called again and again: the calls to a provider, and the calls with one key, are paused for a while and then made again, and new entries wait while Binance does not answer.

A service is also restarted, for a deployment and after a fault. It starts in an order, so that nothing is scheduled on records that the exchange was not asked about, and it stops in an order, so that what a cycle was doing with the exchange is brought to its end before the process is gone.

The code is `AiCallBreakers` in `src/Ecnanib/AiProviderGateway/Adapters`, `BinanceOutages` in `src/Ecnanib/ExchangeGateway/Adapters/Binance`, `CallTrouble` in `src/Ecnanib/DecisionEngine`, and `ExchangeOutageAlerts` with its job in `src/Ecnanib/Agents`. The order of a start and of a stop is in `BackgroundJobScheduler` in `src/Ecnanib/Platform/BackgroundWork`, with `OrderSettlingJob` and `ReconciliationJob` in `src/Ecnanib/DecisionEngine`, which are what runs first and what runs last. The rules below are enforced by the tests in `tests/Ecnanib.Tests/Reliability`, and by `AiCallBreakerTests`, `OutageWatchTests`, `CallTroubleTests`, `ExchangeOutageAlertTests`, `BackgroundJobRunsFirstTests`, and `BackgroundJobRunsLastTests`.

## Safe failure

An entry is the last step of a cycle, and every step before it must have gone well. What a failure does at each step:

| What fails | What follows | The cycle |
|---|---|---|
| The exchange does not give the candle of the close within half a minute | The cycle does not start. | Has no record |
| A read of the data package: the candles of a higher timeframe, or on Futures the mark price or the open interest. Or the equity of the agent is not known | The cycle ends before a model is called. | `Failed` |
| The user has no key for a provider that can be used | The role is not called, and gives no result. | `NoDecision` |
| The Market Analyst, the Strategist, or the Risk Manager gives no result: its call fails, times out twice, is refused, or is not made because the calls are paused | The cycle ends with no decision. | `NoDecision` |
| An error that nothing names leaves a call, a read, or the database | The cycle ends there. | `Failed` |
| The decision completes later than the stale limit of its timeframe | The plan opens no position (FR-CYC-9). | `Stale` |
| The exchange does not say the price that an entry is held to, or the rules of the symbol | The Risk Guard rejects a plan that opens a position (FR-RSK-4). | As the team decided |
| Whether a global halt is on cannot be read | The cycle ends there. | `Failed` |
| Binance is out, or its clock was not read | No entry is placed: `ExchangeOutage`, or `EntriesPaused`. | As the team decided, with why no entry was placed |
| The rules of the symbol, how the account is set, what the account holds, or the margin and the leverage of the symbol cannot be read or set | No entry is placed: `NoStopOrders`, `AccountUnknown`, `BalanceUnknown`, or `MarginNotSet`. | As the team decided, with why no entry was placed |
| The database fails as the entry is written | Nothing is sent: an entry is written before it is sent. | `Failed` |

| Rule | Detail |
|---|---|
| **An error ends one cycle.** | Each cycle runs on its own, and what leaves it is caught for it alone: the cycles of the other agents go on. See "The queue" in [cycles.md](cycles.md). |
| **Nothing is made up for.** | A cycle that ended without an entry is over. The next close of a candle starts the next cycle, with new data. |
| **Only an entry is stopped.** | A plan that closes a position or adjusts its protection goes on whatever stops an entry: it is not held to the price or to the stale limit as an entry is, and neither an outage nor the clock pauses it. Risk can always be reduced. |
| **An entry that was sent is never sent again.** | When Binance does not answer the call that places it, the order is asked about, and is protected when it was executed (FR-BIN-6). See "An order is never sent twice" in [orders.md](orders.md). |
| **One role may be missing.** | A cycle goes on without its Sentiment Analyst, with sentiment marked unavailable, as the specification says of that role alone (FR-TEAM-9). Its plan is checked and placed as any other. |

**Open positions stay protected by orders that rest on Binance.** A position has a stop-loss there from the moment that its entry is executed: on Spot a pair of a stop-loss and a take-profit, of which one cancels the other, and on Futures a stop-loss and a take-profit that only reduce. Binance executes them whether Ecnanib runs or not. A position whose stop-loss cannot be placed is closed at market at once (FR-RSK-7). See "The stop-loss" in [orders.md](orders.md).

## One provider's outage

| Rule | Detail |
|---|---|
| **A provider that is down ends the cycles whose teams call it, and no others.** | The role that is called there gives no result, and its cycle ends with no decision. A cycle whose team has no role there is not touched: it calls its own providers, with its own keys. |
| **A provider that hangs holds back no cycle that does not use it.** | Each cycle is a task of its own, and a call runs into the time limit of its provider, `AiProviders:<Provider>:TimeoutSeconds`. The queue of a close keeps a place with each provider and each key, so a provider that is slow or full holds back the cycles that need it. See "The queue" in [cycles.md](cycles.md). |
| **Once its calls are paused, the cycles that need it no longer wait for it.** | A call that is not made takes no time. The cycle ends at once, and gives its places with the other providers back to the cycles that wait. |
| **The two analysts are called at the same time.** | A cycle whose Market Analyst is paused has called its Sentiment Analyst all the same, at that role's provider: neither waits to see how the other ends. |

## The breakers of the AI providers

The gateway counts how the calls to the models end, and pauses them after failures in a row: the calls to a provider, of all users, and the calls with one key. It is asked before a call is sent, and told how the call ended, in `ModelCall`, the one place that every adapter sends through.

| How a call ended | The provider | The key |
|---|---|---|
| No connection, an answer that broke off, a call that ran into its time limit, a status from 500 on or 408, or a success that is not the provider's answer | One failure more | Nothing |
| The provider refused the call: any other status from 400 to 499, such as 429, 402, 403, or 400 | Answers: what was counted is gone | One refusal more |
| The provider does not know the key (`AuthenticationFailed`), or has no such model (`ModelNotFound`) | Answers | Nothing: each has a handling of its own |
| A result, or an answer without a result (`Succeeded`, `InvalidResult`) | Answers | Answers: what was counted is gone |
| The caller cancelled | Nothing | Nothing |

| Rule | Detail |
|---|---|
| **A breaker pauses after failures in a row.** | `Breakers:AiProvider:Failures` calls to a provider in a row, of all users, that ended with no answer or a fault of the provider. `Breakers:AiKey:Failures` calls with one key in a row that the provider refused. |
| **A call that is paused is not made.** | Nothing is sent, nothing is used, and nothing takes any time. The call ends as `ProviderPaused` or `KeyPaused`, which no caller makes again in its cycle. A provider is asked before a key. |
| **A pause ends by itself, and the calls are made again.** | The first pause lasts `PauseSeconds`. After it every call passes again: no single call is sent ahead, because each call is one of a cycle of some user, and a provider that answers again answers them all. |
| **The first failure after a pause begins the next, which is twice as long.** | Up to `LongestPauseSeconds`. With the tracked file a provider is paused for 1, 2, 4, 8, 16, 32, and then 60 minutes. |
| **An answer ends it all.** | What was counted is gone, and the next pause is the first again. |
| **A call that was under way when a pause began says nothing new.** | The calls of a close are made at the same time. When one of them begins a pause, the failures of the others do not begin another. |
| **A provider that is paused leaves the others as they are, and a key the other keys.** | A key is of one provider: the same key text at another provider is another key. |
| **The check of a key and the list of the models are not paused, and are not counted.** | Whether a provider takes a key is how a user sees that it is right, and the list of its models is what the catalog is kept from. |
| **A test cycle counts as any cycle.** | The gateway cannot tell its calls from others, and they are made with the same key. |
| **It is kept in memory, by the instance.** | A start begins with every call passing, as the limiter of the exchange begins. |
| **A key is known by a mark, and is kept nowhere.** | The mark is worked out from the key with a secret of the process, which is made at the start. It says that two calls came with the same key, and nothing leads back from it to the key. It is in no log event and in no row. |

What a caller does with a call that was not made:

| Caller | What it does |
|---|---|
| A role of a cycle | Records the call as `ProviderPaused` or `KeyPaused`, with no time and no tokens, and does not call once more. The role gave no result. See "A role call" in [team.md](team.md). |
| The AI usage | Does not count it: no call was made. See "AI usage" in [team.md](team.md). |
| The lesson of a closed position | Is left as it is, and is due again an hour later. A call that was made and failed ends the lesson for good, as before. See "Writing a lesson" in [experience.md](experience.md). |

## Calls that fail repeatedly

An agent enters Attention required when calls fail repeatedly (SRS 3.3). That is when a provider keeps refusing the key of its user: `CallTrouble` in the Decision Engine moves the agent, with the cause `CallsFailRepeatedly`, and tells its user.

| Rule | Detail |
|---|---|
| **The call that begins a pause of a key says how many the key has had in a row.** | With no answer between them. A call that was not made because of a pause says nothing. |
| **As many as `Breakers:AiKey:TripsBeforeAttention` move the agent whose cycle made the call.** | The provider is not going to take the key by itself: a limit on its requests that is used up, an account that has run out, or a model that does not take the call. With the tracked file, an agent with one role at that provider is moved by its fifth close in a row that the provider refused. |
| **Its user is told once, when the agent was moved.** | A notification of the kind "Agent needs attention", which names the agent and the role, and no provider and nothing of the key. An agent that its user paused or stopped meanwhile stays as it is, and nothing is said. |
| **A provider that answers nobody moves no agent.** | Its calls are paused and made again, and its agents go on running: they decide again when it answers. There is nothing for a user to do. |
| **Many keys at once are no trouble of a key.** | When `Breakers:AiKey:ProviderWideKeys` keys of one provider or more are refused at the same time, no agent is moved: the provider refuses the service, as when it blocks the address that the service calls from. It is a warning in the log. |
| **Each agent is moved by a call of its own.** | Another agent of the same user with the same key runs on until a call of its own cycle is refused. |
| **A test cycle moves no agent.** | It changes nothing of its agent (FR-CYC-12). |
| **Its user resumes the agent.** | Nothing resumes it for them. The pause of the key may still last then: the calls are not made until it is over, and the agent is not moved for that. It is moved again by the next call that the provider refuses. |
| **A failure to move an agent fails no cycle.** | It is an error in the log, and the cycle ends as it would have. |

## A Binance outage

The gateway hears of every call that it sends to Binance, in `BinanceCall`, and counts for each market in each environment how many in a row had no answer.

| Rule | Detail |
|---|---|
| **An answer is whatever Binance itself says.** | A result, a refusal of a credential, an error of its own, and its signal of too many requests, which the limiter sees to. |
| **No answer is a call that reached nothing.** | No connection, an answer that broke off, a call that ran into its time limit of 15 seconds, and a fault in Binance's place: a status from 500 on, or 408. |
| **`Binance:OutageFailures` calls in a row without an answer are an outage.** | On that market in that environment, which has an address of its own: the others are counted on their own. |
| **An outage pauses new entries there, and nothing else.** | What places an entry asks, before it asks the watch on the clock, and places none: `ExchangeOutage`. The reads, the protective orders, and the closes go on being sent. |
| **The first answer ends it.** | The clocks of Binance are read every `Binance:ClockCheckSeconds`, on every market in every environment, so a call is made there at least that often: that is how the service finds that Binance answers again. |
| **Two calls are not counted.** | A call that the limiter did not send was left without no answer. The call that asks what a key may do is of Binance's wallet API, and says nothing of a market. |
| **It is kept in memory, by the instance.** | A start begins with no outage. |

The watch on the clock pauses new entries as well when a clock was not read for three of its intervals: see "The clock" in [exchange.md](exchange.md). The watch on outages finds an outage sooner, from every call, and says that it is one.

**The users whose agents run there are told.** `ExchangeOutageAlerts` in Agents asks the gateway where Binance is out, every minute:

| Rule | Detail |
|---|---|
| **An outage is told of when it has lasted `Binance:OutageAlertSeconds`.** | A connection that broke for a moment is told to nobody. |
| **Each user who has an agent that runs there is told once.** | However many agents they have there. The notification is about no agent, and about the environment: the kind "Exchange outage". It says since when, that new entries wait, that open positions keep their protective orders, and that there is nothing to do. |
| **They are told again when Binance answers.** | The users whose agents run there then. |
| **It is a critical event.** | A user who opted in is sent an email for it. See [notifications.md](notifications.md). |
| **What was told is kept in memory.** | An instance that starts during an outage finds it anew and tells of it again, and one that starts after it tells nobody that it is over. |

Whoever runs the service is told by the log, where the beginning of an outage is an error, and by the operator status screen.

## Starting

On startup the application migrates the database, reconciles every running agent with the exchange, then resumes scheduling (NFR-REL-3). It does so in these steps, each after the one before it:

| Step | What | See |
|---|---|---|
| 1 | **The database is migrated,** under a lock, before the host starts. A migration that fails stops the start. | "At startup" in [persistence.md](persistence.md) |
| 2 | **The host starts, and its scheduler takes the lock of the background work.** An instance that finds the lock taken stands by, and does none of what follows. | "The lock" in [background-work.md](background-work.md) |
| 3 | **The orders are settled.** `order-settling` runs first: an order that was under way when the application stopped is asked about by its identifier, and an open position without a stop-loss that rests is protected. | "Settling" in [orders.md](orders.md) |
| 4 | **The agents are reconciled.** `reconciliation` runs next, for every agent that holds an open position or an order that is not over: its orders are asked about, and what the account of its user holds is held against its records, which are corrected. | "Reconciling" in [orders.md](orders.md) |
| 5 | **The schedule starts:** every other job, the cycles among them. | "Schedules" in [background-work.md](background-work.md) |

| Rule | Detail |
|---|---|
| **No other job starts before the two have ended.** | They are jobs that run first: the scheduler runs them one after the other, in the order that the Decision Engine registers them in, and starts nothing else meanwhile. See "A job that runs first" in [background-work.md](background-work.md). |
| **They run at every start, whatever their rows say.** | Also when their time has not come, and when the process ended during their latest run: a start after a process that was cut off is the one that needs them most. |
| **Every agent that holds something is reconciled, whatever state it is in.** | One that runs, is paused, waits for its user, or was stopped with its position kept. An agent that holds nothing has nothing that an account could differ from, and nobody is asked about it. |
| **A restart never creates a duplicate order.** | Every order is written before it is sent, with an identifier of its own, and an entry is never sent again (FR-BIN-6). A restart goes on with no cycle: the queue of a close is in memory, and a close that passed is not made up for (FR-CYC-8). So all that a start finds of what was under way is rows, and it asks Binance about each before anything is placed. See "An order is never sent twice" in [orders.md](orders.md). |
| **The schedule waits for two minutes at the most.** | `BackgroundJobScheduler.FirstRunsWithin`, from when the instance took up the work. It is fixed, and no setting. An exchange that does not answer keeps no backup, no email, and no alert of the outage waiting for longer: the other jobs then start beside the run that still goes on, which is said once, as a warning. |
| **An agent that is not settled or not reconciled gets no cycle, after that as before it.** | What starts a cycle asks for each agent: see "After a restart" in [cycles.md](cycles.md). So nothing is placed for an agent that the exchange was not asked about, however the start went. Its position is protected by what rests on the exchange (NFR-REL-2). |
| **What fails for one agent, or for one run, keeps nothing waiting.** | Each agent is settled and reconciled on its own. A run that fails is an error in the log, and what comes after it starts. |
| **A close that comes meanwhile has its cycles late, or not at all.** | The run of a close that waited starts when the schedule does, and starts no cycle five minutes after its close or later: see "When time runs out" in [cycles.md](cycles.md). A decision that completes late opens no position: the stale limit of its timeframe holds (FR-CYC-9). |
| **The same holds when the lock was lost and taken back.** | Another instance may have placed orders meanwhile. The scheduler tells the job that reconciles that the work is taken up, and what it reconciled before counts no more. |
| **Requests are served meanwhile.** | The web server starts with the host. A page shows the records as they are, and what a user asks for, such as a close, settles the orders of its agent with the exchange first. |

## Stopping

Shutdown is graceful: in-flight cycles finish or abort safely, and no entry is left without protection (NFR-REL-5). The application stops when its process is told to, by a signal or by Ctrl+C, in these steps:

| Step | What |
|---|---|
| 1 | **Nothing more starts.** The scheduler hears that the application begins to stop, and tells every run to stop. It does not wait for the host to get to it: the host stops its services one after the other, and the web server, which waits for its requests, before the scheduler. |
| 2 | **The cycles that run end.** One that has not sent its entry sends none. One whose entry is on its way lets that call end, and sends the stop-loss of what it bought, once. Each is recorded. See "When time runs out" in [cycles.md](cycles.md), and "The stop-loss" in [orders.md](orders.md). |
| 3 | **The scheduler waits until every run has ended.** A run never outlives the lock that it began under. |
| 4 | **The orders are settled once more.** `order-settling` does last what must not wait for the next start. |
| 5 | **The lock of the background work is let go,** and the process ends. |

**What is settled last** is every agent with something to settle, through `IPlanOrders.SettleBeforeStopAsync`:

| Rule | Detail |
|---|---|
| **An order of unknown outcome is asked about.** | An entry that a stopped cycle left so is found executed, and is then a position with its protection, or was not placed. Binance's word that it has no such order counts ten seconds after the call: that moment is waited for, once, and the order is asked about again. |
| **An open position without a stop-loss that rests gets it.** | As with every run of the job: it is placed at most twice, and where it cannot be placed the position is closed at market. On Futures the take-profit that a stopped cycle owes is placed beside the stop-loss. |
| **Every entry that rests is taken off the exchange.** | An entry at a price can be executed while the service is down, and what it bought would have no stop-loss until the next start. So none rests on, whatever time it has left, and the log says `Stopping` as why. A part of it that was executed by then is a position, and has its protection before the application has stopped. An entry is never sent again: the next cycle of its agent decides anew. |
| **It opens nothing.** | What it places is the protection of a position, or its close at market where no protection can be placed. |
| **Only the instance that runs the background work does it.** | Under the lock, as a run. An instance that stands by stops, and takes nothing off. And it is not done when the lock is lost: the application runs on then. |
| **No position is closed because the application stops.** | Maintenance never closes positions (NFR-REL-6). A position whose stop-loss rests is left as it is: its protection does not depend on the service (NFR-REL-2). |

**How long a stop may take:**

| Rule | Detail |
|---|---|
| **The application has `Application:ShutdownSeconds` to stop in.** | 120 in the tracked file, from 90 to 3600. It is how long the host waits for its services, counted from when the application began to stop. The host's own default, 30 seconds, is less than a cycle that is being stopped may take, which is 45: see "When time runs out" in [cycles.md](cycles.md). |
| **What is done last ends 45 seconds before that.** | `BackgroundJobScheduler.EndsBefore`, which is fixed. Nothing more is begun then. A call to the exchange that is under way is not given up: it ends within 15 seconds, and one more call that protects takes 25 at the most. So what was left is in the log before the host waits no longer. |
| **Nothing is waited for that time does not change.** | An exchange that does not answer is asked once for each agent, and the application stops. Only an order whose outcome may still come is waited for. |
| **What is left is said, and the next start settles it first.** | A warning for each agent that is not settled, and one that says how many. Such an agent gets no cycle until it is settled, as after every start. |
| **When the runs have not ended in time, nothing is done last.** | A warning says which still run. They are waited for all the same, for as long as the host waits. |
| **When the host waits no longer, the process ends.** | With an event at the critical level that names the jobs which still ran. Their rows say a start with no end, which the next start reports. |
| **Whatever supervises the process must wait for longer than the setting.** | A container runtime or a service manager that kills the process sooner cuts the stop short. Docker waits for 10 seconds unless it is told otherwise (`docker stop --time`, `stop_grace_period`), and systemd for 90 (`TimeoutStopSec`). |
| **The platform's own variable keeps its default behavior.** | Where a deployment sets `DOTNET_SHUTDOWNTIMEOUTSECONDS`, the host waits for as long as that says, and not for as long as the file says (NFR-CFG-3). |

## The settings

| Setting | Tracked file | What it is |
|---|---|---|
| `Breakers:AiProvider:Failures` | 5 | The failures in a row that pause the calls to a provider: from 1 to 100. |
| `Breakers:AiProvider:PauseSeconds` | 60 | How long the first pause lasts: from 1 to 86400. |
| `Breakers:AiProvider:LongestPauseSeconds` | 3600 | How long a pause lasts at the most: from `PauseSeconds` to 86400. |
| `Breakers:AiKey:Failures` | 3 | The refusals in a row that pause the calls with a key: from 1 to 100. |
| `Breakers:AiKey:PauseSeconds` | 60 | How long the first pause lasts. |
| `Breakers:AiKey:LongestPauseSeconds` | 3600 | How long a pause lasts at the most. |
| `Breakers:AiKey:TripsBeforeAttention` | 3 | The pauses in a row of a key that move an agent to Attention required: from 1 to 100. |
| `Breakers:AiKey:ProviderWideKeys` | 3 | The keys of one provider that are refused at the same time from which no agent is moved: from 2 to 10000. |
| `Binance:OutageFailures` | 5 | The calls in a row without an answer that are an outage: from 1 to 1000. |
| `Binance:OutageAlertSeconds` | 60 | How long an outage lasts before users are told: from 0 to 3600. |
| `Application:ShutdownSeconds` | 120 | How long the application has to stop in: from 90 to 3600. |

Each is read at startup. See [configuration.md](configuration.md).

## The operator status screen

Two parts of the screen show what is paused (FR-OPS-2):

| Part | What it shows |
|---|---|
| Calls to the AI providers | Each provider of the configuration file: whether its calls are made, paused until when, or made again after a pause, the failures and the pauses in a row, and how many keys the provider refuses and how many of them are paused now. A key is a number, and nothing else. |
| Outages of Binance | Each market in each environment: whether Binance answers, or since when it is out, the calls in a row without an answer, and when it last answered. |

The pages may not use the AI Provider Gateway, so the Decision Engine says on what the gateway keeps, as `ICallPauses`. Both are of the instance that answered the page. See "The operator status screen" in [ownership.md](ownership.md).

## The jobs

| Job | Component | What a run does |
|---|---|---|
| `exchange-outage-alerts` | Agents | Tells the users whose agents run where Binance has been out for long enough, and tells them when it answers again. It runs every minute, asks the gateway what it keeps in memory, and reads which agents run only when there is something to tell. It calls no exchange. |
| `order-settling` | Decision Engine | Settles the orders of the agents that have something to settle, every minute. It runs first as the background work starts, and it settles once more as the application stops: see "Starting" and "Stopping". |
| `reconciliation` | Decision Engine | Reconciles every agent that holds something with the exchange, every `Binance:ReconcileMinutes`. It runs first as the background work starts, once the orders are settled: see "Starting". |

The breakers and the watch on outages have no job: each is asked and told as a call is made. See [background-work.md](background-work.md).

## The log

`Ecnanib.AiProviderGateway.Adapters.AiCallBreakers`:

```
The calls to the AI provider {Provider} are paused until {PausedUntil}, after {Failures} failures in a row: pause {Trips} in a row
The AI provider {Provider} answers again. Its calls are made as before
The calls with a key for the AI provider {Provider} are paused until {PausedUntil}, after {Failures} refusals in a row: pause {Trips} in a row, with {KeysInTrouble} keys of the provider in trouble
The AI provider {Provider} takes the calls with a key again that it refused
```

`Ecnanib.DecisionEngine.CallTrouble`:

```
The AI provider {Provider} keeps refusing the key of user {UserId} for the {Role} of agent {AgentId}, as it refuses {KeysInTrouble} keys at the same time. That is no trouble of one key, so the agent is not moved
What follows from the AI provider {Provider} refusing the key of user {UserId} could not be done for agent {AgentId}
```

`Ecnanib.ExchangeGateway.Adapters.Binance.BinanceOutages`:

```
Binance left {Unanswered} calls in a row without an answer on {Market} {Environment}. New entries are paused there until it answers again
Binance answers again on {Market} {Environment}, after {OutageSeconds} s without an answer. New entries go on there
```

`Ecnanib.Agents.ExchangeOutageAlerts`:

```
{UserCount} users whose agents run on {Market} {Environment} were told that Binance is out there
{UserCount} users whose agents run on {Market} {Environment} were told that Binance answers again there
The user {UserId} could not be told of the outage of Binance on {Market} {Environment}
```

`Ecnanib.Platform.BackgroundWork.BackgroundJobScheduler`, as the work starts and as the application stops:

```
Background job {Job} did not finish the run it started at {StartedAt}. It runs first all the same, as the background work starts
The background jobs that run first have ended, so the other jobs start
The background jobs that run first did not end within {Minutes} minutes of the start of the background work, so the other jobs start beside them. Still to end: {Jobs}
Background job {Job} did what it does last as the application stops, in {DurationMs} ms
Background job {Job} did not end what it does last as the application stops, after {DurationMs} ms: the time to stop in is nearly over
Background job {Job} failed at what it does last as the application stops, after {DurationMs} ms
The time that the application has to stop in is nearly over before the runs of its background jobs have ended, so nothing is done last: {Jobs}. Still running: {Running}
The application stops before its background work has ended, because the time that it has to stop in is over. Still running: {Jobs}
```

`Ecnanib.DecisionEngine.OrderSettlingJob`, as the application stops:

```
The orders of {Agents} agents are settled with the exchange as the application stops, and their entries that rest are taken off
The orders of agent {AgentId} could not be settled with the exchange as the application stops
Agent {AgentId} is not settled with the exchange as the application stops: it has an order of unknown outcome, an entry that rests, or an open position without a stop-loss that rests
{NotSettled} of {Agents} agents are not settled with the exchange as the application stops. The next start settles them first
```

| Event | Level |
|---|---|
| The jobs that run first have ended, and the schedule starts | Information |
| The schedule starts beside a run that comes first and has not ended in two minutes | Warning, once |
| A job that runs first did not finish its run before the start | Warning |
| A job did what it does last, and the orders are settled as the application stops | Information |
| An agent is not settled as the application stops, and how many are not | Warning |
| What is done last did not end in time, or was not begun for the runs that had not ended | Warning |
| What is done last failed, or failed for an agent | Error |
| The application stops before its background work has ended | Critical |
| An entry that rests is taken off as the application stops | Information, by what places the orders, with `Stopping` as `EntryTakenOffReason`: see "The log" in [orders.md](orders.md) |
| A pause of a provider or of a key begins | Warning, once: not with every call that is not made |
| A pause is over, because the provider answered | Information |
| A call that was not made because of a pause | Information, by the gateway: `AI call ProviderPaused in 0 ms` |
| A lesson that is left for a later run | Information |
| Many keys are refused at once, and no agent is moved | Warning |
| An agent is moved to Attention required | Warning, by Agents: see "The log" in [agents.md](agents.md) |
| An outage of Binance begins | Error |
| An outage is over | Information |
| Users were told of an outage, or of its end | Information, with how many, when there was anyone to tell |
| A user could not be told, or an agent could not be moved | Error |

A pause of a key is logged inside the scope of the call that began it, so it carries the user, the agent, and the cycle of that call. No event holds a key, or anything worked out from one.

## Testing

`SafeFailureTests` has a cycle that is missing each thing in turn, each role that gives no result in each way, and what places the orders missing each thing that it reads before an entry: none of them ends with an entry. It starts with the cycle that is missing nothing, which every other test takes something away from.

`ProviderIsolationTests` runs the cycles of two agents through the gateway itself, with the stored keys of their users: one team calls a provider that is down, that hangs, or whose calls are paused, and the other team does not. `CallTroubleTests` does the same for a key that a provider refuses, and has the many keys, the test cycle, the agent that was paused meanwhile, and the failure to move an agent. A test of these that has more than one close runs the job itself for each, and moves its clock by half an hour between them, once the scheduler works: the scheduler starts the job by itself when the clock passes one quarter hour, and skips a quarter hour that it finds passed together with the next. See "Testing" in [cycles.md](cycles.md).

`AiCallBreakerTests` and `OutageWatchTests` are of the gateways alone, behind a provider and a Binance of the test's, on a clock that the test moves: each way that a call ends, the pauses and how they grow, the calls under way, and what is not counted. `ExchangeOutageTests` has an outage from one end to the other: the calls without an answer, the entry that is not placed, the users who are told, and the entry that is placed when Binance answers again.

`StartupSequenceTests` starts an application again over what an earlier one left, with the background work as it runs outside the tests, and reads the order of what it did from its log: the database, the orders, the agents, and then a job of the test's that is due at once. It has the entry that was under way, which Binance holds once after the restart as before it, and the exchange that does not answer, for which the schedule waits for two minutes and no longer. `UnderWay.EntryAsync` leaves an entry as an application leaves it that was stopped between sending the order and its answer.

`GracefulShutdownTests` tells an application to stop as a signal to its process does, with `Stops`, and reads what it left through an application that starts over the same database and the same `TradingBinance`: an application of a test disposes of itself once it has stopped. A run of a job of the test's stands for a cycle that has decided: it hands what stops it on to what places the orders, as a cycle does, and the application is told to stop from inside the call that places the entry, so the stop begins while Binance has the order. The tests have the entry that is executed, the one whose answer never comes, and the one that never arrived, the entry that rests, with a part of it executed and with none, the exchange that does not answer, and the instance that stands by. That the cycles themselves are stopped and waited for is in `CycleOverloadTests`.

`BackgroundJobRunsFirstTests` and `BackgroundJobRunsLastTests` hold the scheduler to its part, with jobs of the tests: see "Testing a job" in [background-work.md](background-work.md). The file of the tests gives the application 90 seconds to stop in, which is the least, and a test of a stop that takes too long gives its host less.

The file of the tests asks for more failures before a pause than the tracked file, and for 50 calls without an answer before an outage: many tests have a provider or their Binance fail a call for a reason of their own. A test that is about a pause sets what it needs. `OrdersApplication` stands `StandInOutages` in for the watch unless a test asks for the gateway's own, and no test application runs the job that tells of outages on a schedule: `EcnanibApplicationFactory` keeps it under a key, and `AccountsApplication.TellOfOutagesAsync` runs it.

## Not built yet

- Checking the stale limit and the global halt once more just before an entry is sent. Both are checked by the Risk Guard, and the reads that what places an entry makes after that can take seconds.
- Moving an agent to Attention required when a call reports that its model does not exist (FR-CAT-8). See "Not built yet" in [agents.md](agents.md).
- Telling the trouble of one key from a provider that refuses the service, where fewer than `Breakers:AiKey:ProviderWideKeys` users call the provider. Their agents are moved, and they resume them.
- Ending the pause of a key when its user has seen to the account. It ends by itself, within `Breakers:AiKey:LongestPauseSeconds`.
- Reading how long a provider asks the service to wait, from its answer, in place of the pause of the file.
- Sending one call ahead to try a provider after a pause. Every call of the next close is made, and the first that fails pauses the rest.
- Keeping the pauses and the outages across a restart, or for more than one instance. Each is in the memory of an instance.
- Telling an operator of an outage other than in the log and on the status screen.
- Telling a user that the calls to a provider are paused. The timeline of a cycle says it of each call that was not made.
- A record of a cycle that never started, for the candle that Binance did not give. See "Not built yet" in [cycles.md](cycles.md).
- A place at the limiter for protective orders ahead of the other calls. See "Not built yet" in [exchange.md](exchange.md).
- Anything for a stop that is no graceful one. A process that is killed, or a machine that loses power, settles nothing last. What it left is settled first by the next start, and an open position is protected meanwhile by what rests on the exchange (NFR-REL-2). An entry at a price that rests then can be executed while the service is down: see "Not built yet" in [orders.md](orders.md).
- Waiting beyond the time to stop in for a close that is under way. A close that a user asked for is a request, which the web server waits for within the same time, and a close for a loss limit is waited for with the run that it is part of. A process that ends between taking a stop-loss off and closing the position leaves a position that the next start protects first.
- Leaving the entries that rest to an instance that stands by and is about to take the work over. An instance that stops takes them off whether another one stands by or not: version 1 targets a single running instance.
- Asking about an order that rests once at a start. What settles and what reconciles both ask about it, one after the other.
- A banner that announces maintenance in advance (NFR-REL-6).
