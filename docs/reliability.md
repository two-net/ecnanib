# Reliability

Failures are safe: any internal error, timeout, or dependency outage results in no new entry. Open positions stay protected by orders resting on the exchange, which do not depend on Ecnanib being up (NFR-REL-2). An outage at one AI provider affects only the cycles that need it. Repeated failures for a provider or a key trip a breaker that pauses calls and retries later. A Binance outage pauses entries and raises an alert (NFR-REL-4).

Providers have outages, and so has an exchange (risk R5 of the specification). Nothing that fails opens a position, and what fails again and again is not called again and again: the calls to a provider, and the calls with one key, are paused for a while and then made again, and new entries wait while Binance does not answer.

The code is `AiCallBreakers` in `src/Ecnanib/AiProviderGateway/Adapters`, `BinanceOutages` in `src/Ecnanib/ExchangeGateway/Adapters/Binance`, `CallTrouble` in `src/Ecnanib/DecisionEngine`, and `ExchangeOutageAlerts` with its job in `src/Ecnanib/Agents`. The rules below are enforced by the tests in `tests/Ecnanib.Tests/Reliability`, and by `AiCallBreakerTests`, `OutageWatchTests`, `CallTroubleTests`, and `ExchangeOutageAlertTests`.

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

Each is read at startup. See [configuration.md](configuration.md).

## The operator status screen

Two parts of the screen show what is paused (FR-OPS-2):

| Part | What it shows |
|---|---|
| Calls to the AI providers | Each provider of the configuration file: whether its calls are made, paused until when, or made again after a pause, the failures and the pauses in a row, and how many keys the provider refuses and how many of them are paused now. A key is a number, and nothing else. |
| Outages of Binance | Each market in each environment: whether Binance answers, or since when it is out, the calls in a row without an answer, and when it last answered. |

The pages may not use the AI Provider Gateway, so the Decision Engine says on what the gateway keeps, as `ICallPauses`. Both are of the instance that answered the page. See "The operator status screen" in [ownership.md](ownership.md).

## The job

| Job | Component | What a run does |
|---|---|---|
| `exchange-outage-alerts` | Agents | Tells the users whose agents run where Binance has been out for long enough, and tells them when it answers again. It runs every minute, asks the gateway what it keeps in memory, and reads which agents run only when there is something to tell. It calls no exchange. |

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

| Event | Level |
|---|---|
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
