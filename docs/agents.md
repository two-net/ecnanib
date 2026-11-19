# Agents

A user creates, changes, copies, and deletes agents, and no user has two agents of one name (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, a team, allocated capital, and risk settings within the hard bounds of the configuration file (FR-AGT-2, FR-RSK-2). The symbol is one of the configuration file that the chosen market has (FR-AGT-6). The team is four roles, and its user gives each role a provider and a specific model of the catalog (FR-AGT-5, FR-TEAM-1). A user whose email address is not verified creates no agent, and no user has more agents than the fair-use limit allows (FR-ACC-1, FR-ACC-7).

A user starts, pauses, resumes, and stops an agent, and the agent follows the states of the specification (FR-AGT-1, SRS 3.3). Every change of its state is recorded with its cause and its time, and shown (FR-AGT-10). An agent that reaches a loss limit is halted: until the next UTC day for its daily loss limit, and until its user resumes it for its drawdown limit (FR-RSK-8). A user who stops an agent that has an open position chooses whether the position is closed at market or kept with its protective orders (FR-AGT-11), and can close the position of an agent at any time (FR-AGT-12).

A new agent is in Demo. Its user switches it to Live with a valid Live Binance key, a confirmation of the risk, and a fresh proof of who they are, and the switch is recorded in the audit trail (FR-AGT-7). Every page of an agent shows a Demo or Live badge, and every action that affects Live trading asks for confirmation (FR-UI-3). What an agent did in Demo stays labeled as Demo, and is never combined with what it does in Live (FR-BIN-11).

Agents keeps the settings, the state, and the mode of agents, and the models of their roles. It runs no cycle and places no order. The Decision Engine starts a cycle for each agent that runs when a candle of its timeframe closes, and asks Agents which agents those are: see [cycles.md](cycles.md). A cycle that starts builds its data package and runs the team of its agent: each role is called with its model, on the key of the agent's user, and the cycle ends with the decision of the team: see [team.md](team.md). What goes on of its plan is checked by the Risk Guard, which holds it to the risk settings of the agent: see [risk-guard.md](risk-guard.md). The Decision Engine places the orders of what the Risk Guard accepted, and keeps the positions: see [orders.md](orders.md). What an agent holds on the exchange is asked of it, and so is what in the account of its user at Binance stands in the way of an agent of Futures (FR-BIN-F2): see "What an agent holds".

The code is in `src/Ecnanib/Agents`, and the pages in `src/Ecnanib/WebUi/Pages/Agents`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Agents`.

Whose rows a request reaches is in [ownership.md](ownership.md). The fair-use limit, the audit trail, the export of a user's data, and the deletion of an account are in [accounts.md](accounts.md). The symbols of a market are in [exchange.md](exchange.md), and the bounds of the risk settings in [configuration.md](configuration.md). Which models can be selected is in [catalog.md](catalog.md), and which of them a user's key can use in [credentials.md](credentials.md).

## The settings

| Setting | What it is | Rule |
|---|---|---|
| Name | What its user calls the agent. | 1 to 60 characters, in one line. Spaces around it are dropped. **A user has no two agents of one name** (FR-AGT-1). Names are compared as they are written: `Swing` and `swing` are two names. Another user may use the same name. |
| Mode | Demo, which is Binance's test environment, or Live, which is the real Binance account of the user. | **A new agent is in Demo** (FR-AGT-7). The form shows the mode, as its badge, and offers no choice of it: the mode is switched on a page of its own. See "The mode". |
| Market | Spot or Futures (FR-AGT-4). | One of the two. It sets the exchange interface, the allowed actions, and the prompt variant: see "What goes by the timeframe and the market". |
| Symbol | The one symbol the agent trades. | One of `Binance:Symbols` that the chosen market has in the agent's environment (FR-AGT-6). See "The symbols". |
| Timeframe | 15m, 1H, 4H, or 1D (FR-AGT-3). | One of the four, by its code. The form has no default: its user chooses. |
| Team | The four roles that run the agent, each with a provider and one of its models (FR-AGT-5). | A model that can be selected, and that the user's own key can use. A role may wait for its model while the agent does not run. See "The team". |
| Allocated capital | How much the agent may trade with, in the asset that its symbol is priced in. | A number above 0, with 8 decimal places at most. It is not held to the balance of the account: an entry that the balance does not cover, or on Futures the margin of the wallet, is skipped when it is due (FR-BIN-S3, FR-BIN-F6). |
| Risk settings | Risk per trade, minimum reward-to-risk ratio, leverage cap, liquidation buffer, daily loss limit, drawdown limit, and maximum entry distance. | Each within the hard bounds of `Trading:Aggressive`. See "The risk settings". |

- **The settings are checked on the server,** by `IStoredAgents`, whatever a form offered or a browser checked. A form brings text, and `AgentRules.Read` says what is wrong with it, field by field. Everything that is wrong is said at once, each at its field, and what was entered stays in the form.
- A number is digits with at most one decimal point: no sign, no thousands separator, and no exponent, the same in every country.
- **The row names no Binance credential.** A user has one credential for each environment (FR-KEY-2), so the credential of an agent is the one of its user for its mode, and is found that way when it is needed. A foreign key to the credential's row would delete the agent with the key, or keep the key from being deleted.
- **The row names no model either.** The model of each role is a row of its own, in `agent_roles`: see "The team".

### The risk settings

The product has one profile, Aggressive. Its defaults and hard bounds are in the configuration file, and a user adjusts the settings of an agent only within the bounds (FR-RSK-1, FR-RSK-2). The settings are those of Appendix B of the specification, in its order, and the form names the profile.

| Setting | Key of the file | In the tracked file |
|---|---|---|
| Risk per trade, as a share of the agent's equity | `Trading:Aggressive:RiskPerTradePercent` | 0.5 to 5, default 2 |
| Minimum reward-to-risk ratio at entry | `Trading:Aggressive:MinRewardToRisk` | 1 or higher, default 1.5 |
| Leverage cap on Futures | `Trading:Aggressive:LeverageCap` | 1 to 10, default 5 |
| Liquidation buffer on Futures | `Trading:Aggressive:LiquidationBuffer` | 1.5 or higher, default 2 |
| Daily loss limit, as a share of the agent's equity | `Trading:Aggressive:DailyLossPercent` | 2 to 15, default 6 |
| Drawdown limit from the agent's peak equity | `Trading:Aggressive:DrawdownPercent` | 10 to 50, default 25 |
| Maximum distance of an entry from the current price | `Trading:Aggressive:MaxEntryDistancePercent` | 0.1 to 3, default 1 |

- **A new agent starts from the defaults.** The form shows them, and says the bounds and the default under each field, with what the setting does in plain language (NFR-USA-2).
- **A value outside the bounds is refused on the server,** on either side, and the bounds themselves are taken. A percentage and a factor have 2 decimal places at most, and the leverage cap is a whole number.
- **The reward-to-risk ratio and the liquidation buffer have a floor, and no highest value of their own.** A user raises either from its floor. Both are held to `AggressiveSettings.HighestFactor`, which is 100: more than any plan reaches, and still a number that a form can take. The file is held to it as well.
- The leverage cap and the liquidation buffer are stored for every agent and count on Futures only: an agent on Spot keeps them for when its market is changed.
- **Two limits are fixed, and are not an agent's** (SRS Appendix B): a position on Spot is worth at most 100 percent of the agent's equity, and an agent has one open position at a time. `FixedLimits` in `RiskGuard` has them. The file has no key for them, and the form says them and has no field for them. A form that brings them all the same stores nothing of them.
- **A change is recorded in the audit trail** (NFR-LOG-7): see "The audit trail".
- A change applies from the agent's next cycle (FR-AGT-8), also for an agent that runs. A cycle is handed the agent as it is stored when the turn of the cycle comes: `IRunningAgents.FindAsync` reads it then.

### The team

Every agent has the four roles of the specification, which work one after the other in each cycle (FR-TEAM-1, SRS 4.5): the Market Analyst, the Sentiment Analyst, the Strategist, and the Risk Manager. `Role` in `Shared` names them, and the form says what each does.

```csharp
Task<RoleChoices> RoleChoicesAsync(HttpContext request);
```

**For each role the user selects the provider and the specific model** (FR-AGT-5). The form has two choices for each role, one of the provider and one of the model, and `RoleChoicesAsync` says what they offer:

| Rule | Detail |
|---|---|
| **A new agent starts from four different providers** (FR-TEAM-2). | The provider of each role is the one that `Trading:DefaultRoles` of the configuration file gives it (FR-TEAM-3): in the tracked file GPT for the Market Analyst, Grok for the Sentiment Analyst, Gemini for the Strategist, and Claude for the Risk Manager. The model is for the user to choose: the form starts with none. |
| **Only a model that can be selected, and that the user's own key can use, is offered** (FR-CAT-6, FR-CAT-9). | `IKeyModels.SelectableAsync` of Credentials says which, for every provider, from what was recorded when the provider last took the user's key. No provider is asked. A model says the name its provider gives it, with its identifier, and whether it can search live. |
| **The form says that live search may add provider charges** (FR-CYC-3). | Above the roles, with the choices that mark the models: what "searches live" means, that only the Sentiment Analyst searches, in every cycle, and that searching may add charges of the provider to the user's own key, beside the tokens of the call. `AgentPages.LiveSearchExplained` is the text. |
| **A Sentiment Analyst whose model cannot search live is noted, and saved like any other.** | The role still runs. The form says under it that its report is marked "no live data" and that the Strategist gives it no weight: `AgentPages.SentimentWithoutLiveSearch`. It is a note, and no warning, and it is left out for a model that cannot be selected now, which has a note of its own. What the mark is, and who is told of it, is in "Live search" in [cycles.md](cycles.md). |
| **The list of models follows the provider.** | A page is not interactive. The button "Show the models of the chosen providers" stores nothing: the form comes back with the models of the provider that is chosen for each role, and with everything else as it was entered. It is the button of the symbols, for the roles. |
| **A choice is held to the same on the server, whatever the form offered.** | `IKeyModels.BlockForAsync` is asked when a role is given a model that it does not have: before the transaction, so that everything that is wrong with a form is said at once, and again under the lock, which is the lock that a change to a key takes. A model that only another user's key can use is refused like any other that the user cannot select. |
| **The model says the provider.** | A model is of one provider, so `agent_roles` stores the model alone. A model that is not of the provider chosen beside it is refused: the user chose a provider, and no model of it yet. The form comes back with the models of that provider. |
| **The choice is of the agent** (FR-AGT-5). | Two agents of one user can use different models of the same provider, and a copy of an agent has the models of its roles, in rows of its own. |
| **A role may wait for its model while the agent does not run.** | A user who has no key yet creates an agent all the same, and gives its roles their models later. **An agent is started only with its whole team:** see "The controls". |
| **A role that has a model keeps one.** | It is given another in any state, and the change applies from the agent's next cycle (FR-AGT-8). A form that says that such a role has no model is refused. A form that brings nothing of a role says nothing about it, and the role is as it was. |
| **A role that keeps its model is not asked about again.** | As an agent keeps the symbol it has. So the name, the capital, and the risk settings of an agent are saved while the key of a provider of its team is gone. The form shows the model that the role has, and says that it cannot be selected now. The team is asked about, whole, when the agent is started or resumed. |

**One provider may hold more than one role** (FR-TEAM-5). A user who lacks a key for a provider gives its role another provider: the form says under such a role that no key is stored for its provider, and that another provider can take the role. Nothing keeps a user from a team of fewer than four providers. The form warns of it:

| Warning | When |
|---|---|
| The team is reduced. | A provider holds more than one role. The warning names the provider. |
| The Strategist and the Risk Manager use the same provider. | The two have one provider. It is said of its own, because the Risk Manager reviews the plan of the Strategist. |

- `AgentRoleRules.WarningsOf` says what to warn of, from the provider of each role, and `AgentPages` has the texts.
- **The form warns of the providers that it holds now.** A provider is given a second role only by choosing it and pressing the button that shows its models, so the warning is on the form before such a team is saved, and on the form of the agent whenever it is shown afterwards.
- **Giving a role a model is no event of the audit trail** (NFR-LOG-7 does not name it), and changes no state. For an agent in Live it is asked about once more, as a change to a risk setting is: see "Asking once more in Live".
- **The default mapping is a setting of the file, and no data:** a role that has no model shows the provider that the file names now.

### The symbols

```csharp
Task<IReadOnlyList<MarketSymbols>> SymbolsAsync(TradingMode mode, CancellationToken cancellationToken);
```

- **The exchange says which symbols a market has,** through `IExchangeMarketData.GetSymbolsAsync`, which answers with the symbols of the file that the market has in an environment, each with the asset that it is priced in. Its answer is shared and kept for an hour: see "Symbols" in [exchange.md](exchange.md).
- **It is asked about the agent's own environment.** A new agent is in Demo, so its symbol is one that the market has in Binance's test environment. An agent in Live is offered the symbols that its market has in Live, and an agent is switched only to an environment whose market has its symbol: see "The mode".
- **The list follows the market.** A page is not interactive. The form has a second button, "Show the symbols of this market", which stores nothing: the form comes back with the symbols of the market that is chosen, and with everything else as it was entered.
- **A symbol that the chosen market lacks is refused on the server,** whatever the form offered, and so is one that the file does not list.
- **Nothing is saved while the exchange gives no answer.** The form says that no symbol can be chosen now, and a form that is posted all the same stores nothing: a symbol is not taken on trust.
- **An agent keeps the symbol it has.** The exchange is asked only about a market and a symbol that the agent does not have already. So its name, its capital, and its risk settings are changed while the exchange gives no answer.
- The asset that the symbol is priced in is stored with the agent, as `quote_asset`, so that a page writes `1000 USDT` without a call.

### What goes by the timeframe and the market

The timeframe sets the cycle schedule, the data window, and the prompt variant (FR-AGT-3). The market sets the exchange interface, the allowed actions, and the prompt variant (FR-AGT-4). What is built is the one stored choice, under the names that what goes by it uses, and `AgentRuleTests` holds the names together. The cycle schedule is built as well: a cycle of an agent starts at the close of each candle of its timeframe. So is the data window: the data package of a cycle holds the candles of the agent's timeframe and of the higher timeframes that are its context. See [cycles.md](cycles.md). The prompts are built as well: a module for each timeframe and each market, what composes the prompt of a role call from the modules of an agent's own market and timeframe, and the page of an agent's prompts, which shows them. See [prompts.md](prompts.md). So are the allowed actions and the stale limit: a plan is held to the actions of its agent's market, and a decision to the stale limit of its agent's timeframe. See [team.md](team.md).

| What goes by it | How it names the choice |
|---|---|
| The cycle schedule | `Timeframes.Length` says how long a candle of each `Timeframe` lasts, and `Timeframes.ClosingAt` which of them close at a time. Each length is a multiple of the shortest, so one job starts the cycles of all four, at every quarter hour. |
| The candles that the gateway reads, which a cycle starts with | `CandleInterval` has a member of the same name as each `Timeframe`, which `CandleIntervals.Of` gives, and the week besides, for context alone. |
| The data window: the candles of the data package of a cycle | Those of the agent's own timeframe, and those of the higher timeframes that are its context, which `TimeframeContext.Of` gives: the ones that the prompt for the timeframe names. `TimeframeContext.Code` writes each as its code, and the week as `1W`. See "The data package" in [cycles.md](cycles.md). |
| The stale limit after a candle closes | `Trading:StaleSeconds` has a key for each code, and `StaleSecondsSettings` a property of each name. `StaleSecondsSettings.Of` gives the limit of a timeframe, and a decision that completes later than it opens no position (FR-CYC-9): see "The stale limit" in [team.md](team.md). |
| The prompt for the timeframe | A template is seeded under each code: `15m`, `1H`, `4H`, `1D`. `AgentContext.Of` takes the timeframe and the market of a stored agent, and the modules of its prompts follow from them (FR-PRM-4). |
| The exchange interface | The gateway takes a `Market` with every call, and names nothing of Binance. |
| The prompt for the market, which says which actions it allows | A template is seeded under each name: `Spot`, `Futures`. |
| The allowed actions | `PlanActions.AllowedOn` gives the actions of each `Market`, and the plan of the Strategist is held to those of its agent's market (FR-CYC-4): see "What a plan may do" in [team.md](team.md). |

`Timeframe` is in `Shared`, with the codes in `Timeframes`. A member is stored by its name, `FourHours`, and shown and entered by its code, `4H`.

## The states

An agent has one of the six states of the specification (SRS 3.3). It is created in `Draft`. `AgentStates` says what each state means, and `AgentStateTests` holds it to the table of the specification:

| State | Entered when | Cycles run | New entries | Open position | Active |
|---|---|---|---|---|---|
| `Draft` | The agent is created. | No | No | None | No |
| `Running` | Its user starts or resumes it, or the service resumes it when the UTC day in which its daily loss limit halted it is over (FR-RSK-8). | Yes | Yes | Managed by the team | Yes |
| `Paused` | Its user pauses it. | No | No | Stays open with its protective orders | Yes |
| `Halted` | A loss limit of the Risk Guard is reached while it runs: its daily loss limit, or its drawdown limit (FR-RSK-8). See "Halting an agent". | No | No | Closed by the system | Yes |
| `AttentionRequired` | The Binance key that it runs with is deleted or refused, the key of an AI provider that a role of its team uses is, the provider of a model of its team no longer offers the model, its position is open with no stop-loss and could not be closed (FR-RSK-7), or the account of its user at Binance conflicts with how an agent of Futures trades (FR-BIN-F2). The other causes of the specification are not built. | No | No | Stays open with its protective orders, unless the cause says that it has none | Yes |
| `Stopped` | Its user stops it. | No | No | Closed or kept, as its user chose | No |

- **Only a running agent runs cycles and opens positions.** `AgentStates.RunsCycles` and `AgentStates.TakesNewEntries` say so, for what runs a cycle and what places an entry to ask. What starts the cycles asks `IRunningAgents`, which goes by `AgentStates.RunsCycles`: when a candle closes, and again as the turn of a cycle comes. See "What other components ask of Agents".
- **Every state is explained in plain language wherever it is shown** (NFR-USA-2): on the page of the agent's state, on the page of its settings, and under the list. The texts are `AgentPages.Explain`.

| Rule | Detail |
|---|---|
| **An agent is active in every state but `Draft` and `Stopped`.** | `AgentStates.IsActive`. A state that is added later is active until it is said not to be. |
| **A user has at most one active agent for each Binance credential, market, and symbol** (FR-AGT-9). | The credential is the user and the mode. What starts an agent asks first, under the lock, and says which rule stands in the way. The database holds to it as well, with a unique index on `(user_id, mode, market, symbol)` over the rows that are active: so it holds whatever changes a state. A user has any number of drafts and stopped agents for the same three. |
| **The market, the symbol, and the timeframe change only while the agent is in `Draft`, or is `Stopped` with no open position or orders** (FR-AGT-8). | `AgentStates.StructureCanChange`, asked under the lock, of the state that the agent has then. The page shows the three as they are, with why, and offers no choice of them. A form that brings another all the same is refused, and nothing of it is stored. |
| The name, the capital, the risk settings, and the models of the roles change in any state (FR-AGT-8). | A change applies from the next cycle. |
| **An agent is deleted while it is in `Draft`, or is `Stopped` with no open position or orders.** | An active agent is stopped first. |
| **The mode goes by the same rule as the market** (FR-AGT-8). | `IAgentModes` asks `AgentStates.StructureCanChange` under the lock, in either direction. See "The mode". |

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
- **An account at Binance that conflicts with how an agent of Futures trades blocks a start and a resume** (FR-BIN-F2). See "The account of Futures".
- **An agent runs with its whole team, so a role that cannot work blocks a start and a resume** (FR-TEAM-1): a role that has no model, and a role whose model its user cannot select now, because the key of its provider is missing or not valid, the key cannot use the model, or the model can no longer be selected. `IKeyModels.BlockForAsync` of Credentials says so for the model of each role, under the same lock, and asks no provider. The page names each such role with what to do about it, and leads to the settings of the agent and to the keys. Pausing and stopping ask nothing of the team.
- **A draft is not stopped:** it was never started. It is deleted.
- **A stopped agent is started again.** Its market, symbol, and timeframe can be changed before, while it holds nothing.
- **A halted agent is resumed by its user, and its cause says from when.** One that its drawdown limit halted is resumed at any time. One that its daily loss limit halted is halted until the next UTC day (FR-RSK-8): a resume before it changes nothing, and neither does a start once its user has stopped it, and the page says until when. The service resumes it then. See "Halting an agent".
- **A control of an agent in Live asks once more** (FR-UI-3). Starting, pausing, and resuming an agent in Demo are one press, and none takes a proof of who the user is in either mode: the switch to Live took it. See "Asking once more in Live".

### The changes of a state

**The state of an agent is written in one place,** `AgentStateChanges.MoveAsync`, inside the transaction of whatever changes it. It writes the state, its cause, and the time to the row of the agent, and records a change with the same three in `agent_state_changes`, with the mode that the agent is in. So the row of an agent and the latest of its changes say the same. It refuses a context that holds no transaction.

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
| `AiKeyDeleted`, `AiKeyInvalid` | The key of an AI provider that a role of its team uses was deleted, or refused by the provider, while it ran. |
| `ModelUnavailable` | The provider of a model that a role of its team was given no longer offers the model, while it ran. |
| `PositionUnprotected` | Its position was open with no stop-loss on the exchange, and could not be closed at market either, while it ran (FR-RSK-7). |
| `ExchangeSettingConflict` | The account of its user at Binance conflicted with how it trades when it was about to enter, while it ran: on Futures the account was in hedge mode or in Multi-Assets mode, or held a position in its symbol that the agent did not open (FR-BIN-F2). |
| `DailyLossLimitReached` | Its loss in the UTC day reached its daily loss limit, while it ran: the service closes its position, and halts it until the next UTC day (FR-RSK-8). |
| `DrawdownLimitReached` | Its equity fell from its peak by its drawdown limit, while it ran: the service closes its position, and halts it until its user resumes it (FR-RSK-8). |
| `DailyLossHaltEnded` | The UTC day in which its daily loss limit halted it was over, and the service resumed it (FR-RSK-8). |

- What changes the state of an agent adds the members of its causes when it is built.
- **A change says the mode that the agent was in when it happened,** and keeps saying it when the agent is switched (FR-BIN-11). A switch of the mode is no change of the state, and has no cause: the audit trail has it. See "Demo is labeled as Demo".
- **The changes are shown on the page of the agent's state,** latest first, each with its time, its mode, its state, and its cause in words. A page shows the latest 50, and says so when there are more. `IAgentControls.HistoryAsync` reads them.
- The id of a change starts with a time in milliseconds, and each one that is handed out is of a later millisecond than the one before. So the changes of one agent are in order also when the clock gives two of them the same time.
- The changes of an agent are deleted with it, and those of an account with the account. They are in the export of a user's data, as the section `agentStateChanges`.

### What an agent holds

Agents may not use the Decision Engine, and places no order, so it declares `IAgentExposure`, and whatever keeps positions and orders implements and registers it: the Decision Engine does, with `AgentExposure`. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md), and "What an agent holds" in [orders.md](orders.md).

```csharp
public interface IAgentExposure
{
    Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    Task<bool> HasOpenPositionAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);

    Task<PositionCloseOutcome> ClosePositionAtMarketAsync(Guid agentId, CancellationToken cancellationToken);

    Task<AccountConflict?> AccountConflictAsync(Agent agent, CancellationToken cancellationToken);
}
```

| Member | When Agents asks |
|---|---|
| `HasOpenPositionOrOrdersAsync` | Of a stopped agent, before its market, symbol, or timeframe is changed and before it is deleted (FR-AGT-8). A draft never held anything, and an active agent is refused whatever it holds. |
| `HasOpenPositionAsync` | Of every agent that is not a draft, for the pages, and under the lock when an agent is stopped or its position closed (FR-AGT-11, FR-AGT-12). |
| `ClosePositionAtMarketAsync` | When its user closes the position of an agent by hand, or stops the agent and chooses to close it. It answers `Closed`, `NothingOpen`, or `Failed`. |
| `AccountConflictAsync` | Before an agent of Futures is started or resumed: what in the account of its user at Binance stands in its way. See "The account of Futures". |

- A question is asked with the context of whoever asks, inside its transaction where it has one. An implementation asks for the rows of the agent it is given, and commits nothing.
- **A close is asked for after the transaction, and never inside it.** It waits for the exchange, and would hold the lock of the account for as long. Agents has recorded the request by then, and does not give it up when the browser goes away. What an implementation must not lose once it has taken the request is its own to keep.
- **An implementation answers with an outcome, and not with an error,** when the exchange refuses or gives no answer.
- **An agent has a position from the moment that its entry is written.** An entry that is on its way, or whose outcome is not known, may open one, so a user who stops the agent then is asked what to do with the position, and its market cannot be changed under it. So may an entry at a price that rests on the exchange, on Spot: for as long as it rests, "Close position now" is there, and takes it off.
- **An entry that rests is taken off when its agent no longer runs** (FR-BIN-S2). An agent that its user paused or stopped, or that waits for its user, opens no position, so what places the orders takes its entry off the exchange within a minute, whatever its user chose to do with a position. What the entry bought by then is a position, with its protective orders. See "An entry at a price" in [orders.md](orders.md).
- **A close settles the orders of the agent first,** and then takes the stop-loss off the exchange and closes the position at the market. When the position cannot be closed, the stop-loss is placed again, and the answer is `Failed`. See "Closing a position" in [orders.md](orders.md).
- Where nothing answers, no agent holds anything: nothing stands in the way of a change or a deletion, no user is asked what to do with a position, and no control that closes one is shown. The tests of Agents use a stand-in, `StandInExposure`.

### The account of Futures

Futures agents use isolated margin and one-way position mode. The system does not change account-wide settings: if the account is in a conflicting mode or already holds a position in the symbol, the agent does not start and the user is told what to change (FR-BIN-F2). `AgentAccountTests` holds it to the rules below.

| `AccountConflict` | What stands in the way | What the page says to do |
|---|---|---|
| `HedgeMode` | The account keeps a long and a short position of a symbol apart, and an agent trades in one-way mode. | Switch the position mode to one-way at Binance. |
| `MultiAssetsMode` | The account margins with more than one asset, in which Binance takes no isolated margin. | Switch the asset mode to single-asset at Binance. |
| `PositionInSymbol` | The account holds a position in the symbol of the agent that the agent did not open. An agent trades its symbol alone. | Close the position at Binance. |
| `NotKnown` | Binance did not say how the account is set. | Try again in a moment. |

- **Agents asks whatever keeps positions and orders,** through `IAgentExposure.AccountConflictAsync`, with the agent as it is stored. Agents is handed no key of an exchange: the Decision Engine reads the account, with the key of the agent's user. See "The account of Futures" in [orders.md](orders.md).
- **It is asked before the transaction that starts the agent, and never inside it.** It waits for Binance, and would hold the lock of the account for as long. The agent is read again under the lock: one whose mode, market, or symbol was changed meanwhile was asked about as another, and is answered as `NotKnown`.
- **Binance is asked only where the control would have the agent run.** A control that is not offered in the state of the agent, a pause, and a start of an agent in Live that was not asked about once more ask it nothing. An agent on Spot is not asked about.
- **Everything else that stands in the way is said first:** the key, another active agent, and the team. They are known without a call.
- **A position in the symbol is the agent's own when its records hold one.** So an agent that was paused, or that waits for its user, with its position open is resumed. A stopped agent that kept its position stands in the way of another agent of the user in the same symbol.
- **An account that could not be asked about starts nothing.** A failure never lets an agent run in an account that it was not checked against (NFR-REL-2).
- **The service changes none of it.** The page says what to change at Binance, with `AgentPages.Says`, and the same form starts the agent once it is changed.
- **An agent that runs meets the same question before each entry.** There it is what places the orders that asks: the entry is skipped, the user is told with a notification, and the agent moves to Attention required, with the cause `ExchangeSettingConflict`. Its user resumes it once the account is changed, and the resume asks again.
- Nothing is recorded for a start that was refused, and the log names what stood in the way.

### What places the orders asks

The Decision Engine places the orders of an agent, and asks Agents two things for it, through `IAgentTrading`:

```csharp
public interface IAgentTrading
{
    Task<AgentState?> StateUnderLockAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken);

    Task<bool> RequireAttentionAsync(Guid userId, Guid agentId, AgentStateCause cause, CancellationToken cancellationToken);
}
```

| Member | What it does |
|---|---|
| `StateUnderLockAsync` | Says the state that the agent is in now, read under the lock of its account, inside the transaction that writes an order of the agent. The lock is the one that the controls of its user take. So an entry and a stop of the agent's user cannot cross: either the entry is written first, and the stop finds it and asks for the choice, or the agent is stopped first, and no entry is written (`AgentStates.TakesNewEntries`). It changes nothing. |
| `RequireAttentionAsync` | Moves an agent that runs to Attention required, with the cause that it is given. `PositionUnprotected`: its position is open with no stop-loss on the exchange, and could not be closed at market either (FR-RSK-7). `ExchangeSettingConflict`: the account of its user at Binance conflicts with how it trades (FR-BIN-F2). It takes no other cause. An agent in any other state stays as it is: one that its user paused or stopped is not made to look as if it could be resumed for this. |

- Both take the user and the agent by their ids, which are those of the agent's own row: they are asked by background work, where nobody is signed in. See [ownership.md](ownership.md).
- **The move is the service's doing, and no action of a user.** The change of the state records it, and the audit trail does not. The user is told with a notification, which what places the orders raises: see [notifications.md](notifications.md).
- The page of the state says of such an agent that its position is open with no stop-loss, that the service keeps trying to protect or close it, and how to close it by hand.

### Halting an agent

When an agent's loss for the UTC day reaches the daily loss limit, the system closes its position and halts it until the next UTC day. When its drawdown from peak equity reaches the drawdown limit, the system closes its position and halts it until the user resumes it (FR-RSK-8). The Decision Engine works out the equity of an agent, says when a limit is reached with the rule of the Risk Guard, and closes the position: see "Halting an agent" in [risk-guard.md](risk-guard.md). It asks Agents three things for it, through `IAgentHalts`. `AgentHaltTests` holds Agents to the rules below.

```csharp
public interface IAgentHalts
{
    Task<Agent?> FindAsync(Guid agentId, CancellationToken cancellationToken);

    Task<bool> HaltAsync(
        EcnanibDbContext context, Guid userId, Guid agentId, AgentStateCause cause, DateTimeOffset at, CancellationToken cancellationToken);

    Task<int> EndDailyHaltsAsync(CancellationToken cancellationToken);
}
```

| Member | What it does |
|---|---|
| `FindAsync` | Says one agent as it is stored, in whatever state it is: a position is reconciled whether its agent runs or not, and what watches the loss limits is handed the agent of each. It only reads, across users, as `IRunningAgents` does. |
| `HaltAsync` | Moves an agent that runs to `Halted`, with the cause `DailyLossLimitReached` or `DrawdownLimitReached`, and with the moment that the limit was found reached at as the time. It is asked inside the transaction of what watches the loss limits, which writes what the limit is counted from with it, and locks the row of the account for the rest of that transaction. It takes no other cause, and an agent in any other state stays as it is. |
| `EndDailyHaltsAsync` | Resumes every agent, of all users, that its daily loss limit halted on an earlier UTC day: it moves to `Running`, with the cause `DailyLossHaltEnded`. |

| Rule | Detail |
|---|---|
| **Only an agent that runs is halted.** | One that its user paused or stopped, or that waits for its user, keeps its position with its protective orders (SRS 3.3), and is not made to look as if a loss limit had closed it. Its loss limits are found reached once it runs again, before its team is called. |
| **A halt is recorded with its cause and its time, and shown** (FR-AGT-10). | As every change of a state is: in the row of the agent and in `agent_state_changes`, with the mode that the agent is in. The page of its state says the state, since when, why, and for a daily loss limit until when. |
| **A daily halt ends when the next UTC day begins.** | `AgentStates.HaltEnds` says when: midnight, UTC, after the moment that the agent was halted at. No other halt ends by itself. |
| **Its user does not have it run before.** | `ResumeAsync` answers `HaltedForTheDay`, with when the day begins, and nothing changes: neither AI output nor a user action bypasses the Risk Guard (FR-RSK-3). It is said before anything else that a resume asks, and the exchange is asked nothing for it. The agent is stopped at any time. |
| **Stopping it and starting it again is no way around the halt.** | `StartAsync` answers the same for an agent that its daily loss limit halted in this UTC day, whatever state it is in by now and whatever its user changed of its settings meanwhile. It goes by the changes of the state of the agent, in the mode that the agent is in: the latest with the cause `DailyLossLimitReached` says the day. |
| **The service resumes it with the first candle that closes on the next UTC day.** | What starts the cycles asks `EndDailyHaltsAsync` before it reads the agents that run, at every close. So the agent has its cycle of the close of midnight, which evaluates its loss limits before its team is called: see "When a cycle starts" in [cycles.md](cycles.md). |
| **It is resumed when what a resume asks without the exchange allows it.** | A valid Binance key that may trade on its market (FR-KEY-5), and a team that can work (FR-TEAM-1), each asked under the lock of its account, of the agent as it is then. Otherwise it stays halted, the log says what keeps it, and it is asked about again with the next close. Its user resumes it from that day on. |
| **Nobody is asked once more for an agent in Live.** | The halt ends as the requirement says it does: it is the service that resumes the agent, and no user. |
| **The account of its user at Binance is not asked about.** | That waits for Binance. An agent that runs meets the question before each entry, where an account that conflicts moves it to Attention required: see "The account of Futures". |
| **An agent that its drawdown limit halted is resumed by its user, at any time.** | Its drawdown is counted anew from then on. Resumed on the day of the loss, it may be halted again by its daily loss limit: see "Halting an agent" in [risk-guard.md](risk-guard.md). |
| **What the service does is no action of a user.** | A halt, and the resume after a daily halt, are recorded as changes of the state, and not in the audit trail. Its user is told of a halt with a notification, which what watches the loss limits raises: see [notifications.md](notifications.md). |

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
- **The agents that run with a model of an AI provider move to `AttentionRequired`** when the user's key for that provider is deleted or refused, with the cause `AiKeyDeleted` or `AiKeyInvalid`. A user has one key for each AI provider, whatever the environment, so they are the running agents of the user, in Demo and in Live, that have a role whose model is of the provider. An agent whose team uses no model of that provider runs on.
- **No role is given another model.** The roles of the agent point at the models that its user chose, and its user resumes it once a valid key is stored that can use them, or gives the roles other models.

### When a model goes

The Catalog tells whatever depends on a model when a sync marks the model unavailable, through `IModelDependents`, inside the transaction that marks it. Agents implements it, as `AgentModelDependents` (FR-CAT-8). See "When a model becomes unavailable" in [catalog.md](catalog.md).

- **The agents that run with the model move to `AttentionRequired`,** with the cause `ModelUnavailable`: the running agents that have a role on it, whoever they belong to. An agent that does not run stays as its user left it, and is asked about its team when it is resumed.
- **The system does not substitute another model on its own** (FR-CAT-8). The role keeps the model that its user gave it. The form of the agent shows it, says that it cannot be selected now, and its user gives the role another.
- **The accounts are locked one after the other, in the order of their ids,** as what changes an agent or a key locks its account, and the agents of each are read and moved under its lock, with the time as it is then: a change that waited for the lock is later than the one that held it. Two providers are synced at the same time, and lock in the same order. No lock is taken on a model: the transaction of the sync holds the rows of the models already, and what changes an agent takes no lock on a model that the sync would have to wait for.
- The change is stored when the model is marked, and not when it is not. A change that cannot be stored fails the sync, and the model is as it was.
- **The service does it, and no user:** the change is recorded as a change of the state, and not in the audit trail.
- A model that is active again resumes no agent: its user does.
- **A model that the configuration file no longer allows moves no agent.** It stops being one that can be selected, so an agent that has it is not started or resumed with it. See "Not built yet".

## The mode

An agent is in Demo, which is Binance's test environment with no real funds, or in Live, which is the real Binance account of its user. `IAgentModes` switches it, for the user who is signed in on the request:

| Operation | What it does |
|---|---|
| `SwitchToLiveAsync` | Switches an agent from Demo to Live, with a valid Live key, the confirmation of the risk, and a fresh proof of who the user is (FR-AGT-7). |
| `SwitchToDemoAsync` | Switches an agent from Live back to Demo. It takes neither a key, nor a confirmation, nor a proof: no money is at stake in Demo. |
| `BlockOfAsync` | Says what stands in the way of a switch now, apart from the confirmation and the proof, for a page to say before it asks for either. It switches nothing. |

- **A new agent is in Demo, and so is a copy** (FR-AGT-7). `AgentEntry` has no mode: a form that brings one all the same is not read.
- **The mode is switched only while the agent is in `Draft`, or is `Stopped` with no open position or orders** (FR-AGT-8): the rule of the market, `AgentStates.StructureCanChange`, asked under the lock. So no agent changes its environment while it runs or holds anything, and a switch never meets the index of FR-AGT-9, which is over the active rows.
- **The state of the agent stays as it is.** A switch starts nothing: its user starts the agent, in the mode that it is in then.
- **A switch is one transaction, which starts by locking the row of the account,** as the controls do. Everything that a switch takes is asked under the lock, whatever a page showed or asked.
- **The symbol is held to the market of the new environment** (FR-AGT-6). A market has symbols of its own in each environment. The exchange is asked which before the transaction, about both markets, so that the answer covers the agent as it is found under the lock. An agent whose symbol the market lacks there is not switched, and neither is one while the exchange gives no answer. The asset that the symbol is priced in there is stored with the switch.
- **A form that is sent twice is answered as the first one was.** It finds the agent in the mode that was asked for, whatever became of the agent since, and records nothing again.
- A switch is recorded in the audit trail: see "The audit trail".

### The switch to Live

In Live an agent trades with real money, so `SwitchToLiveAsync` asks for more. It asks in this order, the first that fails is the answer, and nothing is switched then:

| What it asks | The answer when it fails |
|---|---|
| The agent is a draft, or is stopped with nothing open. | `StructureLocked` |
| **A valid Live key that may trade on the market of the agent.** `IKeyValidation.BlockForAsync` of Credentials says so for the Live environment, from what the last check of the key recorded: it is what a start asks. A key that lacks the permission of the agent's market stands in the way as well, because the agent could not start with it. | `KeyBlocks`, with the block |
| The market of the agent has its symbol in Live. | `SymbolsUnknown`, `SymbolNotOnMarket` |
| **A fresh proof of who the user is** (FR-ACC-4). `IReauthentication.IsRecentAsync` of Accounts says so for `SensitiveAction.SwitchAgentToLive`, inside the transaction. | `ProofNeeded` |
| **The confirmation of the risk.** The form has a box for it, which is never ticked for its user. | `RiskNotConfirmed` |

- **The page says first what stands in the way, and asks for nothing then:** an agent that is not a draft and not stopped with nothing open, what is wrong with the Live key, with a link to the keys, and a symbol that the market lacks in Live, with a link to the settings. `AgentPages.Says` has the texts. There is nothing to do with a proof while one of them holds.
- **Otherwise the page is shown to a user who has just proved who they are, and to nobody else.** It sends anyone else to `/confirm-identity`, and from there back, as the pages of the keys do. A form that is posted without the proof switches nothing, and leads there as well. See "Re-authentication" in [accounts.md](accounts.md).
- The page says what is at stake in plain language, with `AgentPages.LiveRisks`, and leads to the risk disclosure (NFR-LEG-1).
- **The confirmation is not stored on its own.** No agent is switched without it, so the event of the switch in the audit trail stands for it.

### The badge

**Every page of an agent shows the badge of its mode under its heading** (FR-UI-3). `ModeBadge` says `Demo` or `Live` as a word, and does not rely on its colour: its look is in `app.css`. The list has the badge in the row of each agent, and the form that creates an agent has the one of Demo. `AgentModeBadgeTests` asks every route of `Ecnanib.WebUi.Pages.Agents`, so a page that is added fails it until it shows the badge.

### Asking once more in Live

**Every action that affects Live trading asks for confirmation** (FR-UI-3):

| Action on an agent in Live | How it asks |
|---|---|
| Start, pause, resume | The page of the state has no button for them. Each leads to `/agents/<id>/confirm/<control>`, which says what the control does and that it acts on the real account, and has the button. |
| Stop, "Close position now" | Each has a page that asks once more, in either mode. For an agent in Live it says that it acts on the real account. |
| A change to the capital, to a risk setting, or to the model of a role | Nothing is stored at first. The form comes back as it was entered, says that the agent is in Live, and has the button "Save for Live". |
| The switch to Live | The box that confirms the risk. |

- **What asks is decided on the server, under the lock, of the mode that the agent is in then.** `StartAsync`, `PauseAsync`, `ResumeAsync`, and `SaveAsync` take `confirmedForLive`, and answer `NotConfirmedForLive` for an agent in Live without it. So the one-press form of a page that showed the agent in Demo starts nothing in Live: it leads to the page that asks.
- A form that is sent twice is answered before that: the agent is where the control leads already.
- A new name is not asked about, and neither is a form that brings the settings as they are.
- **Stopping an agent and closing its position take no such answer.** Their pages ask in either mode, and neither meets an agent whose mode changed under it: a switch takes an agent that is stopped and holds nothing.
- An agent in Demo is started, paused, and resumed with one press. The page that asks once more is there for it all the same.
- Deleting an agent asks once more in either mode, and affects no trading: an agent is deleted only while it is a draft, or is stopped with nothing open.
- Deleting and replacing the Live key have pages of their own, in Credentials. Checking a key again is one press: what the check finds is the service's doing, and no action of the user on Live trading.

### Demo is labeled as Demo

**What an agent did is stored with the mode it did it in, row by row, and is never read through the mode that the agent is in now** (FR-BIN-11). So what an agent did in Demo stays Demo after it is switched to Live, and is not combined with what it does there.

- **A change of the state says its mode:** `agent_state_changes.mode`, written by `AgentStateChanges.MoveAsync` with the mode that the agent is in under the lock. The page of the state shows it for each change, as a badge, and the export has it.
- **An order and a position say their mode:** `orders.mode` and `positions.mode`, written with the mode that the agent had when the order was placed, and kept when the agent is switched. See "The record of an order" in [orders.md](orders.md). So does what the loss limits of an agent are counted from: `equity_marks.mode`, and the equity of an agent is that of its positions in the mode that it is in, and of no other. The equity of an agent over time says its mode as well, `equity_snapshots.mode`, and the result of a closed position is that of its position, which says the mode: see "The results of a position" in [orders.md](orders.md). `AgentsRulesTests` fails for a table that has rows of an agent and no column `mode`, unless `AgentsRules.AgentRowsWithoutMode` lists it with why nothing in it is a result.
- **Nothing is added up over both modes.** What counts the agents of a user counts them whatever their mode: the fair-use limit, and whether an account has an active agent. Neither is a result.

## Creating, changing, copying, and deleting

`IStoredAgents` does all four, for the user who is signed in on the request.

| Operation | What it does |
|---|---|
| `CreateAsync` | Stores a new agent, in Demo and in `Draft`, with the first change of its state, and with the models that were chosen for its roles. |
| `SaveAsync` | Changes the settings of an agent, the models of its roles among them, and neither its state nor its mode. A change to the capital, to a risk setting, or to the model of a role of an agent in Live is stored only once its user was asked once more: see "Asking once more in Live". |
| `CloneAsync` | Stores a copy of an agent, with its market, symbol, timeframe, capital, and risk settings, and with the models of its roles. **A copy is a new agent:** in Demo and in `Draft`, whatever the agent it was made from is, and created when it was copied. Its name is "Copy of" and the name of the agent, with a number after it when that name is taken, cut to the length of a name. The answer is the page of the copy, where it is given a name of its own. **An agent in Live is copied only where its market has its symbol in Demo as well:** the exchange is asked before the transaction, the copy is priced in the asset of the symbol there, and the list says why nothing was copied when the market lacks it or the exchange gives no answer. An agent in Demo is copied without a question to the exchange. |
| `DeleteAsync` | Deletes an agent, at once and for good. It takes no proof of who the user is: it exposes no key and moves no money. The page asks once more instead, and offers to keep the agent. |

- **A user who may create no agent is told that first,** before anything that was entered is looked at. `IFairUse.MayCreateAgentAsync` of Accounts says whether the address is verified and whether the user has `Limits:MaxAgentsPerUser` agents already. The list and the page of the form say why, and have no form. A copy is one more agent, and counts as one.
- **Every change is one transaction, which starts by locking the row of the account,** as what changes a key does, and as the controls of an agent do. So two changes to the agents of one user happen one after the other, and what one read under the lock holds until it ends: how many agents the user has, which names are taken, and what state an agent is in. The deletion of the account takes the stronger lock of a deletion, so no agent is created or started in an account that is being deleted.
- **The exchange is asked before the transaction, and never inside it.** A call that waits for the exchange would hold the lock for as long. What may have changed meanwhile is asked again under the lock. An agent whose mode was switched while the exchange was asked is not saved: what was asked about its symbol was asked of the other environment. The page says to try again.
- **A form that is sent twice is answered as the first one was.** Deleting where no agent is deletes nothing, and leads to the list.
- **An agent of another user is not found:** its pages answer "not found", as for an agent that does not exist, and a form that is posted at them changes nothing. See [ownership.md](ownership.md).

## The pages

The pages are rendered on the server for each request, and none is interactive. Each is for a signed-in user: a visitor is sent to sign in. The bar at the top of every page leads to the list.

| Page | What it is |
|---|---|
| `/agents` | The agents of the user, oldest first: name, mode as its badge, market, symbol, timeframe, capital, and state. It says how many agents the user has of how many an account can have, and leads to the form unless the user may create none. Each agent has a link to the page of its state, a button that copies it, and a link to the page that deletes it, and an agent that has an open position has "Close position now". Under the table it says what each state means. |
| `/agents/new` | The form that creates an agent. Each role has a choice of its provider, which starts from the default mapping, and a choice of its model. It says that a model which searches live may add provider charges. See "The team". |
| `/agents/<id>` | The same form, with the settings of the agent and the models of its roles. It says the state, and leads to the page of the state, to the page of the prompts, and to the page that switches the mode. For an agent in Live it asks once more before a change to the capital, to a risk setting, or to the model of a role is stored. |
| `/agents/<id>/state` | What the service has to tell the user about the agent and they have not read, when there is something: see [notifications.md](notifications.md). The state of the agent, since when, what it means, and why the agent is in it, and for an agent that its daily loss limit halted until when it is halted. The buttons that start, pause, and resume the agent, each in the states that it is offered in, a link to the page that stops it, and "Close position now" when it has an open position. An agent that was not started or resumed because of its team is told which roles stand in the way. For an agent in Live, links to the page that asks once more stand in place of the three buttons. A link to the page that switches the mode, or when the mode can be switched. The changes of its state, latest first, each with its mode. A link to the page of the prompts, one to the page of the cycles, and one to the page of the risk checks. |
| `/agents/<id>/cycles` | The cycles that were recorded for the agent, latest first: each with the close of its candle, the mode it ran in, and how it ended, also one that made no decision or placed no order (FR-CYC-10). It has no form. See "The timeline" in [team.md](team.md). |
| `/agents/<id>/cycles/<cycle>` | One cycle as a timeline: the summary of its data package, every role's output, the Risk Guard results, its orders, and its outcome, in the order they happened and in plain language. It has no form. |
| `/agents/<id>/risk-checks` | What the Risk Guard made of the plans of the agent: each check with its verdict, and every rule with its outcome and why, in plain language (FR-RSK-9). It has no form. See "The page" in [risk-guard.md](risk-guard.md). |
| `/agents/<id>/prompts` | The prompt templates that the agent uses now: the instructions of each role, and the modules of its market, its timeframe, and the profile, each with its version. It has no form: a user reads the prompts, and cannot edit them (FR-PRM-7). It says what an AI provider is sent with them, under whose key, and under whose terms (NFR-SEC-8). See [prompts.md](prompts.md). |
| `/agents/<id>/confirm/<control>` | The page that asks once more before the agent is started, paused, or resumed: `start`, `pause`, or `resume`. Another name is no page. |
| `/agents/<id>/live` | The page that switches the agent to Live: what stands in the way, or what is at stake, the box that confirms the risk, and the button. It is shown to a user who has just proved who they are. |
| `/agents/<id>/demo` | The page that asks once more, with the button that switches the agent back to Demo. |
| `/agents/<id>/stop` | The page that stops the agent, with the choice of what happens to its open position when it has one. |
| `/agents/<id>/close-position` | The page that asks once more, with the button "Close position now". |
| `/agents/<id>/delete` | The page that asks once more, with the button that deletes the agent. |

- **Every page of an agent has the badge of its mode under its heading** (FR-UI-3). See "The badge".
- **A control that changes nothing says why on the page it was pressed on,** and one that did what was asked leads to the page of the state by a request that changes nothing, so a reload sends no form again. A position that was closed is said on the page that closed it: no other page would say it.
- When a form of the page of the state is posted, all three of its forms are on the page: a form that is not there cannot be answered, and one that is sent twice finds the agent in another state the second time.
- What a user named an agent is written as text, like everything else on a page, and is in no log event.
- The texts of the pages are in `AgentPages`, in one place, those of the page of the risk checks in `RiskCheckPages` beside it, and those of the pages of the cycles in `CyclePages`.

## What other components ask of Agents

| Who asks | Through | What Agents answers |
|---|---|---|
| Accounts, before it deletes an account | `IAgentActivity` | Whether an agent of the user is active (FR-ACC-9). A draft was never started, and stands in the way no more than a stopped agent. It locks the rows of the user's agents for the rest of the transaction, in the order of their ids. |
| Accounts, for the export of a user's data | `IExportedPart` | The section `agents`: each agent with its settings and its state (NFR-SEC-9). The section `agentRoles`: each role that has a model, with the provider and the identifier of the model. The section `agentStateChanges`: each change of a state. |
| The Decision Engine, when a candle closes | `IRunningAgents.OfTimeframesAsync` | The agents that run cycles and work on a timeframe of which a candle closed, of every user, in the order of their ids, each with the roles of its team that have a model (FR-CYC-1). |
| The Decision Engine, as the turn of a cycle comes | `IRunningAgents.FindAsync` | The agent as it is stored then, with its roles, when it still runs cycles, and nothing otherwise: an agent that was paused, stopped, or deleted while its cycle waited gets none. |
| The Decision Engine, which keeps the record of the cycles | The foreign key `(agent_id, user_id)` of `decision_cycles` | Nothing is asked. The record of the cycles of an agent hangs on its row, and goes with it when the agent is deleted: see "The record of a cycle" in [team.md](team.md). |
| The Decision Engine, which watches the loss limits | `IAgentHalts` | An agent in whatever state it is, that an agent which reached a loss limit is halted, and that the agents which their daily loss limit halted on an earlier UTC day run again (FR-RSK-8). See "Halting an agent". |

**`IRunningAgents` is for background work, and reads the agents of every user.** A job has no user who is signed in: each agent says whose it is, and what is done for it is done for that user. No page and no endpoint asks it. Which states run cycles is `AgentStates.RunsCycles`, and `RunningAgents` names no state of its own. It only reads. See "What is not asked for by owner" in [ownership.md](ownership.md).

The rows of an account go with it: `agents.user_id`, `agent_roles.user_id`, and `agent_state_changes.user_id` point at `users` with a foreign key that deletes the rows with the account. **Deleting an agent deletes the record of its cycles with it,** as it deletes the changes of its state: an agent is deleted only while it is a draft, or is stopped with nothing open.

### What Agents asks, and is told

| Of whom | Through | What for |
|---|---|---|
| Credentials | `IKeyValidation.BlockForAsync` | Whether the Binance key of the user lets an agent run on its market, before a start and a resume (FR-KEY-5), and whether their Live key does, before an agent is switched to Live (FR-AGT-7). |
| Accounts | `IReauthentication.IsRecentAsync` | Whether the user has just proved who they are, before an agent is switched to Live (FR-ACC-4). |
| Credentials | `IKeyModels.SelectableAsync`, `BlockForAsync` | Which models the user can select for a role, and whether a model that a role is given, or has, is one of them (FR-CAT-6, FR-CAT-9). See "The team". |
| The Catalog | `ICatalogModels` | The names of the providers as users read them, and the name of a model that a role has, also once the model is gone. |
| Credentials tells Agents | `IKeyDependents` | That a key is being deleted or marked invalid: the agents that run with it move to `AttentionRequired` (FR-KEY-10). See "When a key goes". |
| The Catalog tells Agents | `IModelDependents` | That models are being marked unavailable: the agents that run with one move to `AttentionRequired` (FR-CAT-8). See "When a model goes". |
| Accounts | `IFairUse.MayCreateAgentAsync` | Whether the user may create one more agent (FR-ACC-1, FR-ACC-7). |
| Accounts | `AuditTrail.RecordAsync` | Recording what a user does to an agent (NFR-LOG-7). See "The audit trail". |
| Whatever keeps positions and orders | `IAgentExposure` | What an agent holds, closing its position, and what in the account of its user at Binance stands in the way of an agent of Futures. See "What an agent holds" and "The account of Futures". |

### What other components hold Agents to

Other components declared interfaces for Agents before it could do what they are about, each with a test that waited for it. [`AgentsRules.cs`](../tests/Ecnanib.Tests/Agents/AgentsRules.cs) is the source of truth for what Agents can do, and those tests ask it. Every method of a public interface of `Ecnanib.Agents` is listed there, in one of three lists:

| List | What is in it |
|---|---|
| `SettingsOperations` | What keeps the settings of agents, or only reads or answers. |
| `ControlOperations` | What does more with an agent: starting, pausing, resuming, and stopping it, and closing its position. And what the service does to its state: it has the agent wait for its user, halts it for a loss limit, and resumes it after a daily halt. |
| `ModeOperations` | What switches the mode of an agent: to Live, and back to Demo. |

A method that is added is in none of them, and fails `AgentsRulesTests` until someone lists it, and so decides what it is.

| What tells that Agents can do more | How |
|---|---|
| **An agent can run** | An operation that is not in `SettingsOperations`. There are some now: the controls. |
| **A model choice** | A foreign key from a table of Agents to `ai_models`. There is one, `agent_roles.model_id`, and `AgentsRulesTests` pins it: another way for an agent to use a model shows there. |

| What is held to, or waits | For | Test |
|---|---|---|
| Asking `IKeyValidation.BlockForAsync` before an agent starts (FR-KEY-5) | An agent that can run. **Built.** | `AgentStartBlockTests` |
| Implementing `IKeyDependents` (FR-KEY-10) | An agent that can run. **Built** for the Binance key. | `KeyDependentsTests` |
| Recording starts, pauses, stops, and manual closes in the audit trail (NFR-LOG-7) | An agent that can run. **Built.** | `AuditRulesTests` |
| Recording mode switches in the audit trail (NFR-LOG-7) | An agent that can be switched. **Built.** | `AuditRulesTests` |
| Storing what an agent did with the mode it did it in (FR-BIN-11) | A table with rows of an agent. **Built** for the changes of a state. The models of the roles are settings, and are listed in `AgentsRules.AgentRowsWithoutMode` as that. | `AgentsRulesTests` |
| Moving an agent when the key of its model's provider goes (FR-KEY-10) | A model choice. **Built.** | `AgentKeyDependentsTests` |
| Implementing `IModelDependents`, which moves an agent to Attention required when its model goes (FR-CAT-8) | A model choice. **Built.** | `ModelDependentsTests`, `AgentModelDependentsTests` |
| Asking `IKeyModels` which models a user can select (FR-CAT-9) | A model choice. **Built.** | `KeyModelsTests`, `AgentRoleTests` |

**To add an operation to Agents,** list it in `AgentsRules`, as one that keeps settings, as a control, or as a switch of the mode. A control asks what blocks a start where it starts an agent, asks once more where it acts on an agent in Live, and records what the user did in the audit trail.

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
| `AgentSwitchedToLive` | The user switched it from Demo to Live. |
| `AgentSwitchedToDemo` | The user switched it from Live back to Demo. |

- **One event for each form that changed a risk setting,** whichever of the seven and however many. A form that brings the settings as they are records nothing, however the numbers are written: `1.50` is `1.5`.
- **Setting them for the first time changes none.** Creating an agent and copying one record nothing, and neither does deleting one.
- **A control that changes nothing records nothing:** one that is refused, one that is not offered in the state of the agent, one that was not asked about for an agent in Live, and a form that is sent twice.
- **A switch that is refused records nothing,** whatever stood in its way, and neither does a form that is sent twice. The proof that the switch to Live takes is an event of its own, `Reauthenticated`, recorded when it was given.
- **A manual close is recorded when it is asked for, before the order.** What places the order may not use Accounts, and records nothing. So the request stands in the trail whatever becomes of the order. Stopping an agent and choosing to close its position records both, `AgentStopped` and `AgentPositionCloseRequested`.
- **What the service does to the state of an agent is no action of a user,** and is not in the audit trail: an agent that moves to Attention required when a key or a model of its team goes. The changes of the state record it, with its cause.
- **Giving a role a model records nothing.** The requirement names the risk settings, and not the team. What the roles have now is on the form of the agent and in the export.
- An event holds no number and no name. What the settings and the state are now is on the pages of the agent and in the export.
- The events of an agent stay when the agent is deleted: they are the account's, and go with the account.

See "The audit trail" in [accounts.md](accounts.md).

## The data

| Table | Columns |
|---|---|
| `agents` | `id`, `user_id`, `name`, `mode`, `market`, `symbol`, `quote_asset`, `timeframe`, `allocated_capital`, `risk_per_trade_percent`, `min_reward_to_risk`, `leverage_cap`, `liquidation_buffer`, `daily_loss_percent`, `drawdown_percent`, `max_entry_distance_percent`, `state`, `state_cause`, `state_changed_at`, `created_at`. One row for each `(user_id, name)`. At most one active row for each `(user_id, mode, market, symbol)`. `(id, user_id)` is a key as well, for the rows of a component that may not use Accounts and carry their owner along to their agent: see [ownership.md](ownership.md). |
| `agent_roles` | `agent_id`, `role`, `user_id`, `model_id`. One row for each role of an agent that has a model: `(agent_id, role)` is its key. `role` points at `roles`, and `model_id` at `ai_models`. |
| `agent_state_changes` | `id`, `agent_id`, `user_id`, `mode`, `state`, `cause`, `changed_at`. One row for each change of the state of an agent, the first of them its creation. Indexed by `(agent_id, changed_at)`. |

The tables hold rows of users: `Agent`, `AgentRole`, and `AgentStateChange` implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. The rows of an account are deleted with it, and the roles and the changes of an agent with the agent.

| Column | What it holds |
|---|---|
| `mode`, `market`, `timeframe`, `state`, `state_cause` | The name of a member of `TradingMode`, `Market`, `Timeframe`, `AgentState`, and `AgentStateCause`. |
| `quote_asset` | The asset that the symbol is priced in, as Binance named it when the symbol was stored. |
| `allocated_capital`, the percentages, `min_reward_to_risk`, and `liquidation_buffer` | Exact decimals. |
| `state_changed_at` | Since when the agent is in its state. For a draft, when it was created. |
| `agent_roles.role` | The name of a member of `Role`, which is the key of its row in `roles`. |
| `agent_roles.model_id` | The model of the catalog that the role was given. A model is never deleted, so the row finds it for good, also once its provider no longer offers it. The provider of the role is the one of the model. The row says no mode: it is a setting, and no result (FR-BIN-11). |
| `agent_state_changes.state`, `cause`, `changed_at` | The state the agent came to, as the name of a member of `AgentState`, why, as the name of a member of `AgentStateCause`, and when. The latest row of an agent says what its row in `agents` says. |
| `agent_state_changes.mode` | The mode the agent was in when its state changed, as the name of a member of `TradingMode`. It stays as it was written when the agent is switched (FR-BIN-11). |

An agent's id is a version 7 UUID that the application assigns, and so is the id of a change. `name` is what its user typed: it is listed in `PersonalDataRules` with what it holds, is in the export, and is in no log event. A change holds nothing that a user typed.

The migration that added `agent_roles` gave no agent a model: an agent that was stored then has a team of roles that wait for theirs, and is started once its user has given each one. An agent that ran then was not moved: nothing called a model then, and it is asked about its team when it is next started or resumed.

The migration that added `agent_state_changes` gave every agent that was stored then its first change, from what its row said: its state, the cause, and since when. The migration that added `agent_state_changes.mode` gave every change that was stored then the mode of its agent: nothing switched the mode of an agent before it.

The migration that added `min_reward_to_risk` and `liquidation_buffer` gave every agent that was stored then 1.5 and 2, the defaults of the specification, which the tracked file carries. Until then both were single values of the file, the same for every agent. A migration reads no file, so a deployment whose file has other values finds these two in its rows, and the user of an agent changes them on its form.

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
User {UserId} was refused the control {AgentControl} of agent {AgentId}: their Binance account for {TradingMode} stands in its way ({AccountConflict})
User {UserId} was refused starting agent {AgentId}: another agent of theirs is active for the same market and symbol
User {UserId} was refused the control {AgentControl} of agent {AgentId}: {RoleCount} of the roles of its team cannot work ({Roles})
User {UserId} was refused stopping agent {AgentId}, which is {AgentState}
User {UserId} asked to close the position of agent {AgentId} at market: {PositionCloseOutcome}
The position of agent {AgentId} could not be closed at market, as user {UserId} asked. It is open as it was
Agent {AgentId} of user {UserId} requires attention with the change to the Binance key for {TradingMode}: {AgentStateCause}
Agent {AgentId} of user {UserId} requires attention with the change to the key for the AI provider {Provider}: {AgentStateCause}
Agent {AgentId} of user {UserId} requires attention: the AI provider {Provider} no longer offers a model of its team
Agent {AgentId} of user {UserId} is halted: {AgentStateCause}
Agent {AgentId} of user {UserId} runs again: the UTC day in which its daily loss limit halted it is over
Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: the Binance key for {TradingMode} blocks it ({StartBlock})
Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: {RoleCount} of the roles of its team cannot work ({Roles})
The halt of agent {AgentId} of user {UserId} for its daily loss limit could not be ended
User {UserId} was refused the control {AgentControl} of agent {AgentId}: its daily loss limit halted it until the next UTC day
User {UserId} switched agent {AgentId} from {PreviousTradingMode} to {TradingMode}
User {UserId} was refused switching agent {AgentId} to {TradingMode}: {ModeSwitchOutcome}
User {UserId} could not change agent {AgentId}: it was switched to {TradingMode} meanwhile
User {UserId} could not copy agent {AgentId}: the exchange did not say which symbols {Market} has in {TradingMode}
```

An event names a user and an agent by their ids, and never holds a name or a number of the settings. What keeps a team from working is said by the names of the roles and of what is wrong with each. A position that could not be closed is a warning. The event of an agent that requires attention is written inside the transaction that changes the key, or that marks the model, and stands for a change that is stored with it. So is the event of an agent that is halted, which is a warning, inside the transaction of what watches the loss limits. An agent that runs again after a daily halt, and one that stays halted, are information, and a halt that could not be ended for an error of the application is an error, said once for the agent.

## Not built yet

- Stopping the cycle of an agent that its user pauses or stops while the cycle runs. Whether an agent runs is asked when its cycle starts, and again, under the lock of its account, as its entry is written: no entry is placed for an agent that no longer runs.
- Holding a stored risk setting to bounds that the file is given later. An agent whose value is outside bounds that were narrowed since keeps it, and so does a copy of it. Its form shows the value, and is saved only once the value is within the bounds.
- The other causes of Attention required: a call that reports that a model does not exist (FR-CAT-8), and calls that fail repeatedly (NFR-REL-4). The first takes an operation of Agents that a cycle can ask: what runs the team records such a call as `ModelNotFound`, and the cycle ends as its role says, but nothing moves the agent for it. See "Not built yet" in [team.md](team.md).
- Moving an agent that runs when its user's key for a provider can no longer use a model of its team. Which models a key can use is recorded when its provider lists them: when a key is stored or replaced, and at each check. A list that comes to lack the model, and a key that is replaced, tell Agents nothing, and the list is written apart from the lock of the account, so a start may go by the list as it was a moment before. Such an agent is refused the next time it is started or resumed. Until then its calls of that model fail, and nothing moves it for them: see the item above. See "The models a key can use" in [credentials.md](credentials.md).
- Moving an agent that runs when the configuration file stops allowing a model of its team. Such a model can no longer be selected, and the agent is not started or resumed with it, but nothing tells an agent that runs. See "Not built yet" in [catalog.md](catalog.md).
- Telling the user that an agent needs attention for every cause (FR-NTF-1), and showing a change of state without a reload (FR-NTF-2). A user is told with a notification that an agent was halted, that a position could not be protected, and that their account at Binance conflicts with how an agent trades, which is when an agent comes to wait for them for those causes: see [notifications.md](notifications.md). Nothing tells them of the other causes of Attention required, and nothing tells them that a daily halt has ended, or that an agent stays halted after it because its key or its team keeps it.
- Recommending a team by measured strength, and applying it in one action (FR-TEAM-4), and reassigning the roles of an agent by strength on its own (FR-TEAM-10). The default mapping is the starting point until there is something to measure.
- The results of an agent on its pages: its profit or loss, its equity, and the pages that show its positions and its orders (FR-RPT-2). The orders and the positions are stored, each with the mode it is of, the result of every closed position is recorded, and so is the equity of the agent over time: see "The results of a position" and "The equity of an agent over time" in [orders.md](orders.md). No page shows them yet. It adds nothing up over both modes (FR-BIN-11): see "Demo is labeled as Demo".
- A record of the risk confirmation of its own, with the version of the risk disclosure that was confirmed. The event of the switch in the audit trail stands for the confirmation.
- The agent detail screen (FR-RPT-2), and the times of an agent in the local time of its user. The page of the state of an agent is where its position, its trades, and its decisions will be shown. Its decisions are on the pages of its cycles until then: see "The timeline" in [team.md](team.md).
