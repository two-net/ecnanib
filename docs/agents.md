# Agents

A user creates, changes, copies, and deletes agents, and no user has two agents of one name (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, allocated capital, and risk settings within the hard bounds of the configuration file (FR-AGT-2, FR-RSK-2). The symbol is one of the configuration file that the chosen market has (FR-AGT-6). A user whose email address is not verified creates no agent, and no user has more agents than the fair-use limit allows (FR-ACC-1, FR-ACC-7).

A user starts, pauses, resumes, and stops an agent, and the agent follows the states of the specification (FR-AGT-1, SRS 3.3). Every change of its state is recorded with its cause and its time, and shown (FR-AGT-10). A user who stops an agent that has an open position chooses whether the position is closed at market or kept with its protective orders (FR-AGT-11), and can close the position of an agent at any time (FR-AGT-12).

Agents keeps the settings and the state of agents. It runs no cycle and places no order: nothing does yet. Nothing switches an agent to Live, and no role of its team is given a model. So every agent is in Demo, and an agent that runs does nothing more than be in that state. What an agent holds on the exchange is asked of whatever keeps positions and orders, which is not built: see "What an agent holds".

The code is in `src/Ecnanib/Agents`, and the pages in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Agents`.

Whose rows a request reaches is in [ownership.md](ownership.md). The fair-use limit, the audit trail, the export of a user's data, and the deletion of an account are in [accounts.md](accounts.md). The symbols of a market are in [exchange.md](exchange.md), and the bounds of the risk settings in [configuration.md](configuration.md).

## The settings

| Setting | What it is | Rule |
|---|---|---|
| Name | What its user calls the agent. | 1 to 60 characters, in one line. Spaces around it are dropped. **A user has no two agents of one name** (FR-AGT-1). Names are compared as they are written: `Swing` and `swing` are two names. Another user may use the same name. |
| Mode | Demo, which is Binance's test environment, or Live. | **A new agent is in Demo.** The page shows the mode and offers no choice of it: the switch to Live is not built. |
| Market | Spot or Futures (FR-AGT-4). | One of the two. It sets the exchange interface, the allowed actions, and the prompt variant: see "What goes by the timeframe and the market". |
| Symbol | The one symbol the agent trades. | One of `Binance:Symbols` that the chosen market has in the agent's environment (FR-AGT-6). See "The symbols". |
| Timeframe | 15m, 1H, 4H, or 1D (FR-AGT-3). | One of the four, by its code. The form has no default: its user chooses. |
| Allocated capital | How much the agent may trade with, in the asset that its symbol is priced in. | A number above 0, with 8 decimal places at most. It is not held to the balance of the account: an entry that the balance does not cover is skipped when it is due (FR-BIN-S3, FR-BIN-F6). |
| Risk settings | Risk per trade, leverage cap, daily loss limit, drawdown limit, and maximum entry distance. | Each within the hard bounds of `Trading:Aggressive`. See "The risk settings". |

- **The settings are checked on the server,** by `IStoredAgents`, whatever a form offered or a browser checked. A form brings text, and `AgentRules.Read` says what is wrong with it, field by field. Everything that is wrong is said at once, each at its field, and what was entered stays in the form.
- A number is digits with at most one decimal point: no sign, no thousands separator, and no exponent, the same in every country.
- **The row names no Binance credential.** A user has one credential for each environment (FR-KEY-2), so the credential of an agent is the one of its user for its mode, and is found that way when it is needed. A foreign key to the credential's row would delete the agent with the key, or keep the key from being deleted.
- **The team is shown, and not stored.** Every agent has the four roles, and the page names them with what each does. Giving a role its provider and model is not built: see "Not built yet".

### The risk settings

The product has one profile, Aggressive. Its defaults and hard bounds are in the configuration file, and a user adjusts the settings of an agent only within the bounds (FR-RSK-1, FR-RSK-2).

| Setting | Key of the file | In the tracked file |
|---|---|---|
| Risk per trade, as a share of the agent's equity | `Trading:Aggressive:RiskPerTradePercent` | 0.5 to 5, default 2 |
| Leverage cap on Futures | `Trading:Aggressive:LeverageCap` | 1 to 10, default 5 |
| Daily loss limit, as a share of the agent's equity | `Trading:Aggressive:DailyLossPercent` | 2 to 15, default 6 |
| Drawdown limit from the agent's peak equity | `Trading:Aggressive:DrawdownPercent` | 10 to 50, default 25 |
| Maximum distance of an entry from the current price | `Trading:Aggressive:MaxEntryDistancePercent` | 0.1 to 3, default 1 |

- **A new agent starts from the defaults.** The form shows them, and says the bounds and the default under each field, with what the setting does in plain language (NFR-USA-2).
- **A value outside the bounds is refused on the server,** on either side, and the bounds themselves are taken. A percentage has 2 decimal places at most, and the leverage cap is a whole number.
- The leverage cap is stored for every agent and counts on Futures only: an agent on Spot keeps it for when its market is changed.
- **The settings that the file fixes are not an agent's.** The minimum reward-to-risk ratio and the liquidation buffer are single values of the file, the same for every agent, and so are the two that the specification fixes: a position on Spot is at most the agent's equity, and an agent has one position. No page offers them.
- **A change is recorded in the audit trail** (NFR-LOG-7): see "The audit trail".
- A change applies from the agent's next cycle (FR-AGT-8), also for an agent that runs. Nothing runs a cycle yet: what does reads the settings as they are when it starts.

### The symbols

```csharp
Task<IReadOnlyList<MarketSymbols>> SymbolsAsync(TradingMode mode, CancellationToken cancellationToken);
```

- **The exchange says which symbols a market has,** through `IExchangeMarketData.GetSymbolsAsync`, which answers with the symbols of the file that the market has in an environment, each with the asset that it is priced in. Its answer is shared and kept for an hour: see "Symbols" in [exchange.md](exchange.md).
- **It is asked about the agent's own environment.** A new agent is in Demo, so its symbol is one that the market has in Binance's test environment.
- **The list follows the market.** A page is not interactive. The form has a second button, "Show the symbols of this market", which stores nothing: the form comes back with the symbols of the market that is chosen, and with everything else as it was entered.
- **A symbol that the chosen market lacks is refused on the server,** whatever the form offered, and so is one that the file does not list.
- **Nothing is saved while the exchange gives no answer.** The form says that no symbol can be chosen now, and a form that is posted all the same stores nothing: a symbol is not taken on trust.
- **An agent keeps the symbol it has.** The exchange is asked only about a market and a symbol that the agent does not have already. So its name, its capital, and its risk settings are changed while the exchange gives no answer.
- The asset that the symbol is priced in is stored with the agent, as `quote_asset`, so that a page writes `1000 USDT` without a call.

### What goes by the timeframe and the market

The timeframe sets the cycle schedule, the data window, and the prompt variant (FR-AGT-3). The market sets the exchange interface, the allowed actions, and the prompt variant (FR-AGT-4). Nothing runs a cycle or composes a prompt yet. What is built is the one stored choice, under the names that what goes by it uses, and `AgentRuleTests` holds the names together:

| What goes by it | How it names the choice |
|---|---|
| The candles that the gateway reads, which a cycle starts with | `CandleInterval` has a member of the same name as each `Timeframe`, and the week besides, for context alone. |
| The stale limit after a candle closes | `Trading:StaleSeconds` has a key for each code, and `StaleSecondsSettings` a property of each name. |
| The prompt for the timeframe | A template is seeded under each code: `15m`, `1H`, `4H`, `1D`. |
| The exchange interface | The gateway takes a `Market` with every call, and names nothing of Binance. |
| The prompt for the market, which says which actions it allows | A template is seeded under each name: `Spot`, `Futures`. |

`Timeframe` is in `Shared`, with the codes in `Timeframes`. A member is stored by its name, `FourHours`, and shown and entered by its code, `4H`.

## The states

An agent has one of the six states of the specification (SRS 3.3). It is created in `Draft`. `AgentStates` says what each state means, and `AgentStateTests` holds it to the table of the specification:

| State | Entered when | Cycles run | New entries | Open position | Active |
|---|---|---|---|---|---|
| `Draft` | The agent is created. | No | No | None | No |
| `Running` | Its user starts or resumes it. | Yes | Yes | Managed by the team | Yes |
| `Paused` | Its user pauses it. | No | No | Stays open with its protective orders | Yes |
| `Halted` | A loss limit of the Risk Guard is reached (FR-RSK-8). Nothing does this yet. | No | No | Closed by the system | Yes |
| `AttentionRequired` | The Binance key that it runs with is deleted or refused. The other causes of the specification are not built. | No | No | Stays open with its protective orders | Yes |
| `Stopped` | Its user stops it. | No | No | Closed or kept, as its user chose | No |

- **Only a running agent runs cycles and opens positions.** `AgentStates.RunsCycles` and `AgentStates.TakesNewEntries` say so, for what runs a cycle and what places an entry to ask.
- **Every state is explained in plain language wherever it is shown** (NFR-USA-2): on the page of the agent's state, on the page of its settings, and under the list. The texts are `AgentPages.Explain`.

| Rule | Detail |
|---|---|
| **An agent is active in every state but `Draft` and `Stopped`.** | `AgentStates.IsActive`. A state that is added later is active until it is said not to be. |
| **A user has at most one active agent for each Binance credential, market, and symbol** (FR-AGT-9). | The credential is the user and the mode. What starts an agent asks first, under the lock, and says which rule stands in the way. The database holds to it as well, with a unique index on `(user_id, mode, market, symbol)` over the rows that are active: so it holds whatever changes a state. A user has any number of drafts and stopped agents for the same three. |
| **The market, the symbol, and the timeframe change only while the agent is in `Draft`, or is `Stopped` with no open position or orders** (FR-AGT-8). | `AgentStates.StructureCanChange`, asked under the lock, of the state that the agent has then. The page shows the three as they are, with why, and offers no choice of them. A form that brings another all the same is refused, and nothing of it is stored. |
| The name, the capital, and the risk settings change in any state. | A change applies from the next cycle. |
| **An agent is deleted while it is in `Draft`, or is `Stopped` with no open position or orders.** | An active agent is stopped first. |
| The mode goes by the same rule as the market. | What switches an agent to Live asks `AgentStates.StructureCanChange` when it is built. |

### The controls

`IAgentControls` starts, pauses, resumes, and stops an agent, for the user who is signed in on the request. `AgentStates.After` is the one table of which control leads from which state to which:

| Control | From | To |
|---|---|---|
| Start | `Draft`, `Stopped` | `Running` |
| Pause | `Running` | `Paused` |
| Resume | `Paused`, `Halted`, `AttentionRequired` | `Running` |
| Stop | `Running`, `Paused`, `Halted`, `AttentionRequired` | `Stopped` |

- **A control is decided on the server, under the lock,** of the state that the agent has then, whatever the page offered. A control that is not offered in that state changes nothing, and the page says why.
- **A form that is sent twice is answered as the first one was.** It finds the agent in the state that the control leads to, which is what was asked for, and records nothing again.
- **A Binance key that is missing, is not valid, or lacks the permission of the agent's market blocks a start and a resume** (FR-KEY-5). `IKeyValidation.BlockForAsync` of Credentials says so, from what the last check of the key recorded, and the page shows the message of `KeyPages.Says`, which names the missing permission, with a link to the keys. It is asked under the lock that a change to a key takes, so the answer holds until the agent runs. Pausing and stopping ask nothing of the key.
- **Another active agent for the same credential, market, and symbol blocks a start** (FR-AGT-9). An agent that is resumed is active already.
- **A draft is not stopped:** it was never started. It is deleted.
- **A stopped agent is started again.** Its market, symbol, and timeframe can be changed before, while it holds nothing.
- A halted agent is resumed by its user. What halts an agent says with its cause when the agent may be resumed, when it is built: a daily loss limit halts until the next UTC day (FR-RSK-8).
- Starting an agent in Demo takes no proof of who the user is, and no page asks once more: no agent is in Live.

### The changes of a state

**The state of an agent is written in one place,** `AgentStateChanges.MoveAsync`, inside the transaction of whatever changes it. It writes the state, its cause, and the time to the row of the agent, and records a change with the same three in `agent_state_changes`. So the row of an agent and the latest of its changes say the same. It refuses a context that holds no transaction.

- **An agent has a history from when it is created.** Creating an agent and copying one store its first change, with the cause `Created`. A copy starts a history of its own.
- **The cause is a member of `AgentStateCause`,** stored by its name, and shown in words by `AgentPages.Says`:

| Cause | When |
|---|---|
| `Created` | The user created the agent, as a new one or as a copy. |
| `UserStarted`, `UserPaused`, `UserResumed` | The user started, paused, or resumed it. |
| `UserStopped` | The user stopped it, and it had no open position. |
| `UserStoppedClosingPosition` | The user stopped it, and chose to close its position at market. |
| `UserStoppedKeepingPosition` | The user stopped it, and chose to keep its position with its protective orders. |
| `ExchangeKeyDeleted`, `ExchangeKeyInvalid` | The Binance key of its mode was deleted, or refused by Binance, while it ran. |

- What changes the state of an agent adds the members of its causes when it is built.
- **The changes are shown on the page of the agent's state,** latest first, each with its time, its state, and its cause in words. A page shows the latest 50, and says so when there are more. `IAgentControls.HistoryAsync` reads them.
- The id of a change starts with a time in milliseconds, and each one that is handed out is of a later millisecond than the one before. So the changes of one agent are in order also when the clock gives two of them the same time.
- The changes of an agent are deleted with it, and those of an account with the account. They are in the export of a user's data, as the section `agentStateChanges`.

### What an agent holds

Positions and orders are not built. Agents may not use the Decision Engine, and places no order, so it declares `IAgentExposure`, and whatever keeps positions and orders implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IAgentExposure
{
    Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    Task<bool> HasOpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    Task<PositionCloseOutcome> ClosePositionAtMarketAsync(Guid agentId, CancellationToken cancellationToken);
}
```

| Member | When Agents asks |
|---|---|
| `HasOpenPositionOrOrdersAsync` | Of a stopped agent, before its market, symbol, or timeframe is changed and before it is deleted (FR-AGT-8). A draft never held anything, and an active agent is refused whatever it holds. |
| `HasOpenPositionAsync` | Of every agent that is not a draft, for the pages, and under the lock when an agent is stopped or its position closed (FR-AGT-11, FR-AGT-12). |
| `ClosePositionAtMarketAsync` | When its user closes the position of an agent by hand, or stops the agent and chooses to close it. It answers `Closed`, `NothingOpen`, or `Failed`. |

- A question is asked with the context of whoever asks, inside its transaction where it has one. An implementation asks for the rows of the agent it is given, and commits nothing.
- **A close is asked for after the transaction, and never inside it.** It waits for the exchange, and would hold the lock of the account for as long. Agents has recorded the request by then, and does not give it up when the browser goes away. What an implementation must not lose once it has taken the request is its own to keep.
- **An implementation answers with an outcome, and not with an error,** when the exchange refuses or gives no answer.
- **Nothing implements it yet.** Until then no agent holds anything: nothing stands in the way of a change or a deletion, no user is asked what to do with a position, and no control that closes one is shown. `AgentsRulesTests` has a test that fails as soon as a table of positions or of orders is in the model and nothing answers. The tests of Agents use a stand-in, `StandInExposure`.

### Stopping an agent that has an open position

**The user chooses** (FR-AGT-11). The page that stops an agent names both choices, each with what it means, and has made neither:

| Choice | What is done |
|---|---|
| Close the position at market | The agent is stopped, with the cause `UserStoppedClosingPosition`, and whatever keeps positions is asked to close the position. |
| Keep the position with its protective orders | The agent is stopped, with the cause `UserStoppedKeepingPosition`, and nothing is asked: the position stays open at the exchange with its stop-loss and take-profit orders. The agent no longer manages it. |

- **An agent that has a position is not stopped without the choice.** What it holds is asked again under the lock, whatever the page showed: a form that brings no choice for a position that opened meanwhile stops nothing, and comes back with the choice.
- An agent that has no position is stopped with the cause `UserStopped`. A choice that is posted all the same is no choice of anything.
- **The agent is stopped first, and its position is closed after.** So a position that could not be closed is that of a stopped agent, which opens nothing more, with its protective orders. The page says that the agent is stopped and that its position is open still, and leads to the control that closes it.
- A stopped agent that kept its position is neither given another market nor deleted until the position is closed (FR-AGT-8).
- **Pausing asks nothing about a position,** and nothing to be done with it: it stays open with its protective orders.

### Close position now

**The control is there at all times for an agent that has an open position** (FR-AGT-12): in whatever state the agent is, on the list and on the page of its state. It is called "Close position now" wherever it is, and leads to a page that asks once more, because a close cannot be undone.

- `IAgentControls.ClosePositionAsync` asks nothing of the state of the agent, and nothing of its key: closing a position reduces risk, and nothing of Agents stands in its way.
- **The state of the agent stays as it is.** A running agent runs on, and its team may open a position again.
- The request is recorded in the audit trail in a transaction of its own, and whatever keeps positions is asked after it: see "The audit trail".
- A form that is sent twice finds nothing open the second time. Nothing is asked for, and nothing is recorded.
- Where the position could not be closed, the page says that it is open as it was, and the button is there to try again.

### When a key goes

Credentials tells whatever depends on a key when the key is deleted or turns out to be invalid, through `IKeyDependents`, inside the transaction that deletes the key or marks it. Agents implements it, as `AgentKeyDependents` (FR-KEY-10).

- **The agents that run with a Binance credential move to `AttentionRequired`** when it is deleted or refused, with the cause `ExchangeKeyDeleted` or `ExchangeKeyInvalid`. The credential of an agent is the one of its user for its mode, so they are the running agents of the user in the environment of the key.
- **An agent that does not run stays as its user left it:** `AgentStates.NeedsItsKey`. A paused agent stays paused, and a halted one halted with its cause. Each is asked about its key when it is resumed.
- The change is stored when the change to the key is, and not when it is not: both are of one transaction, which has locked the row of the account.
- **The service does it, and no user:** the change is recorded as a change of the state, and not in the audit trail, which has the deletion of the key.
- The user resumes the agent once a valid key is stored. Until then the resume is blocked, with the message that says what is wrong with the key.
- **No agent depends on a key of an AI provider yet,** because no agent is given a model. `AgentKeyDependentsTests` has a test that fails as soon as one is.

## Creating, changing, copying, and deleting

`IStoredAgents` does all four, for the user who is signed in on the request.

| Operation | What it does |
|---|---|
| `CreateAsync` | Stores a new agent, in Demo and in `Draft`, with the first change of its state. |
| `SaveAsync` | Changes the settings of an agent, and neither its state nor its mode. |
| `CloneAsync` | Stores a copy of an agent, with its market, symbol, timeframe, capital, and risk settings. **A copy is a new agent:** in Demo and in `Draft`, whatever the agent it was made from is, and created when it was copied. Its name is "Copy of" and the name of the agent, with a number after it when that name is taken, cut to the length of a name. The answer is the page of the copy, where it is given a name of its own. |
| `DeleteAsync` | Deletes an agent, at once and for good. It takes no proof of who the user is: it exposes no key and moves no money. The page asks once more instead, and offers to keep the agent. |

- **A user who may create no agent is told that first,** before anything that was entered is looked at. `IFairUse.MayCreateAgentAsync` of Accounts says whether the address is verified and whether the user has `Limits:MaxAgentsPerUser` agents already. The list and the page of the form say why, and have no form. A copy is one more agent, and counts as one.
- **Every change is one transaction, which starts by locking the row of the account,** as what changes a key does, and as the controls of an agent do. So two changes to the agents of one user happen one after the other, and what one read under the lock holds until it ends: how many agents the user has, which names are taken, and what state an agent is in. The deletion of the account takes the stronger lock of a deletion, so no agent is created or started in an account that is being deleted.
- **The exchange is asked before the transaction, and never inside it.** A call that waits for the exchange would hold the lock for as long. What may have changed meanwhile is asked again under the lock.
- **A form that is sent twice is answered as the first one was.** Deleting where no agent is deletes nothing, and leads to the list.
- **An agent of another user is not found:** its pages answer "not found", as for an agent that does not exist, and a form that is posted at them changes nothing. See [ownership.md](ownership.md).

## The pages

The pages are rendered on the server for each request, and none is interactive. Each is for a signed-in user: a visitor is sent to sign in. The bar at the top of every page leads to the list.

| Page | What it is |
|---|---|
| `/agents` | The agents of the user, oldest first: name, mode, market, symbol, timeframe, capital, and state. It says how many agents the user has of how many an account can have, and leads to the form unless the user may create none. Each agent has a link to the page of its state, a button that copies it, and a link to the page that deletes it, and an agent that has an open position has "Close position now". Under the table it says what each state means. |
| `/agents/new` | The form that creates an agent. |
| `/agents/<id>` | The same form, with the settings of the agent. It says the state, and leads to the page of the state. |
| `/agents/<id>/state` | The state of the agent, since when, what it means, and why the agent is in it. The buttons that start, pause, and resume the agent, each in the states that it is offered in, a link to the page that stops it, and "Close position now" when it has an open position. The changes of its state, latest first. |
| `/agents/<id>/stop` | The page that stops the agent, with the choice of what happens to its open position when it has one. |
| `/agents/<id>/close-position` | The page that asks once more, with the button "Close position now". |
| `/agents/<id>/delete` | The page that asks once more, with the button that deletes the agent. |

- **A control that changes nothing says why on the page it was pressed on,** and one that did what was asked leads to the page of the state by a request that changes nothing, so a reload sends no form again. A position that was closed is said on the page that closed it: no other page would say it.
- When a form of the page of the state is posted, all three of its forms are on the page: a form that is not there cannot be answered, and one that is sent twice finds the agent in another state the second time.
- What a user named an agent is written as text, like everything else on a page, and is in no log event.
- The texts of the pages are in `AgentPages`, in one place.

## What other components ask of Agents

| Who asks | Through | What Agents answers |
|---|---|---|
| Accounts, before it deletes an account | `IAgentActivity` | Whether an agent of the user is active (FR-ACC-9). A draft was never started, and stands in the way no more than a stopped agent. It locks the rows of the user's agents for the rest of the transaction, in the order of their ids. |
| Accounts, for the export of a user's data | `IExportedPart` | The section `agents`: each agent with its settings and its state (NFR-SEC-9). |

The rows of an account go with it: `agents.user_id` points at `users` with a foreign key that deletes the rows with the account.

### What Agents asks, and is told

| Of whom | Through | What for |
|---|---|---|
| Credentials | `IKeyValidation.BlockForAsync` | Whether the Binance key of the user lets an agent run on its market, before a start and a resume (FR-KEY-5). |
| Credentials tells Agents | `IKeyDependents` | That a key is being deleted or marked invalid: the agents that run with it move to `AttentionRequired` (FR-KEY-10). See "When a key goes". |
| Accounts | `IFairUse.MayCreateAgentAsync` | Whether the user may create one more agent (FR-ACC-1, FR-ACC-7). |
| Accounts | `AuditTrail.RecordAsync` | Recording what a user does to an agent (NFR-LOG-7). See "The audit trail". |
| Whatever keeps positions and orders | `IAgentExposure` | What an agent holds, and closing its position. See "What an agent holds". |

### What other components hold Agents to

Other components declared interfaces for Agents before it could do what they are about, each with a test that waited for it. [`AgentsRules.cs`](../tests/Ecnanib.Tests/Agents/AgentsRules.cs) is the source of truth for what Agents can do, and those tests ask it. Every method of a public interface of `Ecnanib.Agents` is listed there, in one of two lists:

| List | What is in it |
|---|---|
| `SettingsOperations` | What keeps the settings of agents, or only reads or answers. |
| `ControlOperations` | What does more with an agent: starting, pausing, resuming, and stopping it, and closing its position. |

A method that is added is in neither list, and fails `AgentsRulesTests` until someone lists it, and so decides what it is.

| What tells that Agents can do more | How |
|---|---|
| **An agent can run** | An operation that is not in `SettingsOperations`. There are some now: the controls. |
| **A model choice** | A foreign key from a table of Agents to `ai_models`. There is none. |

| What is held to, or waits | For | Test |
|---|---|---|
| Asking `IKeyValidation.BlockForAsync` before an agent starts (FR-KEY-5) | An agent that can run. **Built.** | `AgentStartBlockTests` |
| Implementing `IKeyDependents` (FR-KEY-10) | An agent that can run. **Built** for the Binance key. | `KeyDependentsTests` |
| Recording starts, pauses, stops, and manual closes in the audit trail (NFR-LOG-7) | An agent that can run. **Built.** | `AuditRulesTests` |
| Recording mode switches in the audit trail (NFR-LOG-7) | What switches an agent to Live: a type of Agents that takes `IReauthentication`, which the switch needs (FR-AGT-7). Waits. | `AuditRulesTests` |
| Moving an agent when the key of its model's provider goes (FR-KEY-10) | A model choice. Waits. | `AgentKeyDependentsTests` |
| Implementing `IModelDependents`, which moves an agent to Attention required when its model goes (FR-CAT-8) | A model choice. Waits. | `ModelDependentsTests` |
| Asking `IKeyModels` which models a user can select (FR-CAT-9) | A model choice. Waits. | `KeyModelsTests` |

**To add an operation to Agents,** list it in `AgentsRules`, as one that keeps settings or as a control. A control asks what blocks a start where it starts an agent, and records what the user did in the audit trail.

## The audit trail

What a user does to an agent that is money-relevant is recorded, in the transaction that does it, with the agent as its target: `agent:<id>` (NFR-LOG-7).

| Action | Recorded when |
|---|---|
| `AgentRiskSettingsChanged` | A form that changed a risk setting of the agent was saved. |
| `AgentStarted` | The user started the agent. |
| `AgentPaused` | The user paused it. |
| `AgentResumed` | The user resumed it, which is a start. |
| `AgentStopped` | The user stopped it. |
| `AgentPositionCloseRequested` | The user asked for its position to be closed at market: with "Close position now", or as they stopped the agent. |

- **One event for each form that changed a risk setting,** whichever of the five and however many. A form that brings the settings as they are records nothing, however the numbers are written: `1.50` is `1.5`.
- **Setting them for the first time changes none.** Creating an agent and copying one record nothing, and neither does deleting one.
- **A control that changes nothing records nothing:** one that is refused, one that is not offered in the state of the agent, and a form that is sent twice.
- **A manual close is recorded when it is asked for, before the order.** What places the order may not use Accounts, and records nothing. So the request stands in the trail whatever becomes of the order. Stopping an agent and choosing to close its position records both, `AgentStopped` and `AgentPositionCloseRequested`.
- **What the service does to the state of an agent is no action of a user,** and is not in the audit trail: an agent that moves to Attention required when its key goes. The changes of the state record it, with its cause.
- An event holds no number and no name. What the settings and the state are now is on the pages of the agent and in the export.
- The events of an agent stay when the agent is deleted: they are the account's, and go with the account.

See "The audit trail" in [accounts.md](accounts.md).

## The data

| Table | Columns |
|---|---|
| `agents` | `id`, `user_id`, `name`, `mode`, `market`, `symbol`, `quote_asset`, `timeframe`, `allocated_capital`, `risk_per_trade_percent`, `leverage_cap`, `daily_loss_percent`, `drawdown_percent`, `max_entry_distance_percent`, `state`, `state_cause`, `state_changed_at`, `created_at`. One row for each `(user_id, name)`. At most one active row for each `(user_id, mode, market, symbol)`. |
| `agent_state_changes` | `id`, `agent_id`, `user_id`, `state`, `cause`, `changed_at`. One row for each change of the state of an agent, the first of them its creation. Indexed by `(agent_id, changed_at)`. |

The tables hold rows of users: `Agent` and `AgentStateChange` implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. The rows of an account are deleted with it, and the changes of an agent with the agent.

| Column | What it holds |
|---|---|
| `mode`, `market`, `timeframe`, `state`, `state_cause` | The name of a member of `TradingMode`, `Market`, `Timeframe`, `AgentState`, and `AgentStateCause`. |
| `quote_asset` | The asset that the symbol is priced in, as Binance named it when the symbol was stored. |
| `allocated_capital` and the percentages | Exact decimals. |
| `state_changed_at` | Since when the agent is in its state. For a draft, when it was created. |
| `agent_state_changes.state`, `cause`, `changed_at` | The state the agent came to, as the name of a member of `AgentState`, why, as the name of a member of `AgentStateCause`, and when. The latest row of an agent says what its row in `agents` says. |

An agent's id is a version 7 UUID that the application assigns, and so is the id of a change. `name` is what its user typed: it is listed in `PersonalDataRules` with what it holds, is in the export, and is in no log event. A change holds nothing that a user typed.

The migration that added `agent_state_changes` gave every agent that was stored then its first change, from what its row said: its state, the cause, and since when.

## The log

```
User {UserId} created agent {AgentId}
User {UserId} created agent {AgentId} as a copy of agent {SourceAgentId}
User {UserId} changed the settings of agent {AgentId}
User {UserId} changed the risk settings of agent {AgentId}
User {UserId} deleted agent {AgentId}
User {UserId} was refused creating an agent: their email address is not verified
User {UserId} was refused creating an agent: they have {AgentCount} of {MaxAgentsPerUser} already
User {UserId} was refused changing the market, symbol, or timeframe of agent {AgentId}, which is {AgentState}
User {UserId} was refused deleting agent {AgentId}, which is {AgentState}
User {UserId} could not create an agent: the exchange did not say which symbols {Market} has in {TradingMode}
User {UserId} moved agent {AgentId} from {PreviousAgentState} to {AgentState}: {AgentStateCause}
User {UserId} stopped agent {AgentId}, which was {AgentState}: {AgentStateCause}
User {UserId} was refused the control {AgentControl} of agent {AgentId}, which is {AgentState}
User {UserId} was refused the control {AgentControl} of agent {AgentId}: the Binance key for {TradingMode} blocks it ({StartBlock})
User {UserId} was refused starting agent {AgentId}: another agent of theirs is active for the same market and symbol
User {UserId} was refused stopping agent {AgentId}, which is {AgentState}
User {UserId} asked to close the position of agent {AgentId} at market: {PositionCloseOutcome}
The position of agent {AgentId} could not be closed at market, as user {UserId} asked. It is open as it was
Agent {AgentId} of user {UserId} requires attention with the change to the Binance key for {TradingMode}: {AgentStateCause}
```

An event names a user and an agent by their ids, and never holds a name or a number of the settings. A position that could not be closed is a warning. The event of an agent that requires attention is written inside the transaction that changes the key, and stands for a change that is stored with it.

## Not built yet

- What keeps positions and orders, which answers `IAgentExposure` and closes a position. Until it is built, no agent has a position: stopping an agent asks for no choice, and no "Close position now" is shown. With it comes cancelling an entry that has not filled when its agent stops running (FR-BIN-S2).
- The cycles, the data package, and the prompts that go by the timeframe and the market of an agent. An agent that runs is in that state, and nothing more happens. What runs a cycle asks `AgentStates.RunsCycles`.
- What halts an agent when a loss limit is reached, and what resumes it after a daily loss limit, on the next UTC day (FR-RSK-8). A halted agent is resumed and stopped by its user already.
- The other causes of Attention required: a model that is unavailable (FR-CAT-8), an exchange setting that conflicts (FR-BIN-F2), and calls that fail repeatedly (NFR-REL-4). With the second comes refusing a start while the account is in a conflicting mode or holds a position in the symbol.
- Telling the user that an agent was halted or needs attention (FR-NTF-1), and showing a change of state without a reload (FR-NTF-2). In-app notifications are not built.
- Switching an agent to Live, with a valid Live key, a risk confirmation, and a fresh proof of who the user is (FR-AGT-7). `SensitiveAction.SwitchAgentToLive` of Accounts is there for it. A copy of a Live agent is in Demo, where its market may lack its symbol: what switches an agent decides what a copy does then.
- Asking once more before a control acts on an agent in Live (FR-UI-3). Stopping an agent and closing a position ask once more already. Starting, pausing, and resuming are one press: no agent is in Live. It comes with the switch to Live.
- Giving each role of the team its provider and model (FR-AGT-5, FR-TEAM-1 to FR-TEAM-5). The page names the four roles, and stores nothing of them. With it come moving an agent when the key of its model's provider goes, and when its model does.
- The Demo or Live badge on every agent screen (FR-UI-3). The pages say the mode in words.
- The agent detail screen (FR-RPT-2), and the times of an agent in the local time of its user. The page of the state of an agent is where its position, its trades, and its decisions will be shown.
