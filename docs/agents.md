# Agents

A user creates, changes, copies, and deletes agents, and no user has two agents of one name (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, allocated capital, and risk settings within the hard bounds of the configuration file (FR-AGT-2, FR-RSK-2). The symbol is one of the configuration file that the chosen market has (FR-AGT-6). A user whose email address is not verified creates no agent, and no user has more agents than the fair-use limit allows (FR-ACC-1, FR-ACC-7).

Agents keeps the settings of agents, and does nothing else with an agent yet. Nothing starts, pauses, resumes, or stops one, nothing switches one to Live, and no role of its team is given a model. So every agent is a draft in Demo. The rules that are stated in other states are built all the same, and hold for a row in any state: when the market, symbol, and timeframe may change (FR-AGT-8), and that a user has at most one active agent for each Binance credential, market, and symbol (FR-AGT-9).

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
- A change applies from the agent's next cycle (FR-AGT-8). Nothing runs a cycle yet: what does reads the settings as they are when it starts.

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

An agent has one of the six states of the specification (SRS 3.3): `Draft`, `Running`, `Paused`, `Halted`, `AttentionRequired`, and `Stopped`. It is created in `Draft`, with the cause `Created`, and nothing moves it yet.

| Rule | Detail |
|---|---|
| **An agent is active in every state but `Draft` and `Stopped`.** | `AgentStates.IsActive`. A state that is added later is active until it is said not to be. |
| **A user has at most one active agent for each Binance credential, market, and symbol** (FR-AGT-9). | The credential is the user and the mode. The database holds to it, with a unique index on `(user_id, mode, market, symbol)` over the rows that are active. So it holds whatever starts an agent, and a second one that is made active is refused. A user has any number of drafts and stopped agents for the same three. |
| **The market, the symbol, and the timeframe change only while the agent is in `Draft`, or is `Stopped` with no open position or orders** (FR-AGT-8). | `AgentStates.StructureCanChange`, asked under the lock, of the state that the agent has then. The page shows the three as they are, with why, and offers no choice of them. A form that brings another all the same is refused, and nothing of it is stored. |
| The name, the capital, and the risk settings change in any state. | A change applies from the next cycle. |
| **An agent is deleted while it is in `Draft`, or is `Stopped` with no open position or orders.** | An active agent is stopped first. |
| The mode goes by the same rule as the market. | What switches an agent to Live asks `AgentStates.StructureCanChange` when it is built. |

### What an agent holds

Positions and orders are not built. Agents may not use the Decision Engine, so it declares `IAgentExposure`, and whatever keeps positions and orders implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IAgentExposure
{
    Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);
}
```

- **It is asked of a stopped agent only.** A draft never held anything, and an active agent is refused whatever it holds.
- It is asked with the context of whoever asks, inside its transaction where it has one. An implementation asks for the rows of the agent it is given, and commits nothing.
- **Nothing implements it yet.** Until then no agent holds anything, and nothing stands in the way. `AgentsRulesTests` has a test that fails as soon as a table of positions or of orders is in the model and nothing answers.

## Creating, changing, copying, and deleting

`IStoredAgents` does all four, for the user who is signed in on the request.

| Operation | What it does |
|---|---|
| `CreateAsync` | Stores a new agent, in Demo and in `Draft`. |
| `SaveAsync` | Changes the settings of an agent, and neither its state nor its mode. |
| `CloneAsync` | Stores a copy of an agent, with its market, symbol, timeframe, capital, and risk settings. **A copy is a new agent:** in Demo and in `Draft`, whatever the agent it was made from is, and created when it was copied. Its name is "Copy of" and the name of the agent, with a number after it when that name is taken, cut to the length of a name. The answer is the page of the copy, where it is given a name of its own. |
| `DeleteAsync` | Deletes an agent, at once and for good. It takes no proof of who the user is: it exposes no key and moves no money. The page asks once more instead, and offers to keep the agent. |

- **A user who may create no agent is told that first,** before anything that was entered is looked at. `IFairUse.MayCreateAgentAsync` of Accounts says whether the address is verified and whether the user has `Limits:MaxAgentsPerUser` agents already. The list and the page of the form say why, and have no form. A copy is one more agent, and counts as one.
- **Every change is one transaction, which starts by locking the row of the account,** as what changes a key does. So two changes to the agents of one user happen one after the other, and what one read under the lock holds until it ends: how many agents the user has, and which names are taken. The deletion of the account takes the stronger lock of a deletion, so no agent is created in an account that is being deleted.
- **The exchange is asked before the transaction, and never inside it.** A call that waits for the exchange would hold the lock for as long. What may have changed meanwhile is asked again under the lock.
- **A form that is sent twice is answered as the first one was.** Deleting where no agent is deletes nothing, and leads to the list.
- **An agent of another user is not found:** its pages answer "not found", as for an agent that does not exist, and a form that is posted at them changes nothing. See [ownership.md](ownership.md).

## The pages

The pages are rendered on the server for each request, and none is interactive. Each is for a signed-in user: a visitor is sent to sign in. The bar at the top of every page leads to the list.

| Page | What it is |
|---|---|
| `/agents` | The agents of the user, oldest first: name, mode, market, symbol, timeframe, capital, and state. It says how many agents the user has of how many an account can have, and leads to the form unless the user may create none. Each agent has a button that copies it and a link to the page that deletes it. |
| `/agents/new` | The form that creates an agent. |
| `/agents/<id>` | The same form, with the settings of the agent. |
| `/agents/<id>/delete` | The page that asks once more, with the button that deletes the agent. |

- What a user named an agent is written as text, like everything else on a page, and is in no log event.
- The texts of the pages are in `AgentPages`, in one place.

## What other components ask of Agents

| Who asks | Through | What Agents answers |
|---|---|---|
| Accounts, before it deletes an account | `IAgentActivity` | Whether an agent of the user is active (FR-ACC-9). A draft was never started, and stands in the way no more than a stopped agent. It locks the rows of the user's agents for the rest of the transaction, in the order of their ids. |
| Accounts, for the export of a user's data | `IExportedPart` | The section `agents`: each agent with its settings and its state (NFR-SEC-9). |

The rows of an account go with it: `agents.user_id` points at `users` with a foreign key that deletes the rows with the account.

### What waits for agents that can run

Other components declared interfaces for Agents before it was built, each with a test that fails once Agents can do what the interface is about. An agent that only has settings can do none of it: it cannot run, so it depends on no key, and it uses no model. [`AgentsRules.cs`](../tests/Ecnanib.Tests/Agents/AgentsRules.cs) is the source of truth for what Agents can do, and those tests ask it.

| What tells that Agents can do more | How |
|---|---|
| **An operation beyond settings** | Every method of a public interface of `Ecnanib.Agents` is listed in `AgentsRules.SettingsOperations`, with what it does. A method that is added is not listed, and is taken for one that starts, stops, or switches an agent until someone lists it. |
| **A model choice** | A foreign key from a table of Agents to `ai_models`. |

| What waits | For | Fails with |
|---|---|---|
| Asking `IKeyValidation.BlockForAsync` before an agent starts (FR-KEY-5) | An operation beyond settings | `AgentStartBlockTests` |
| Implementing `IKeyDependents`, which moves an agent to Attention required when its key goes (FR-KEY-10) | An operation beyond settings, or a model choice | `KeyDependentsTests` |
| Recording starts, pauses, and stops, mode switches, and manual closes in the audit trail (NFR-LOG-7) | An operation beyond settings | `AuditRulesTests` |
| Implementing `IModelDependents`, which moves an agent to Attention required when its model goes (FR-CAT-8) | A model choice | `ModelDependentsTests` |
| Asking `IKeyModels` which models a user can select (FR-CAT-9) | A model choice | `KeyModelsTests` |

**To add an operation to Agents,** build what waits for it, or say in the test that waits what it still waits for, and then list the operation in `AgentsRules.SettingsOperations`. `AgentsRulesTests` fails for an operation that is not listed, and for one that is listed and is not there.

## The audit trail

A change to a risk setting of an agent is recorded as `AgentRiskSettingsChanged`, in the transaction that stores it, with the agent as its target: `agent:<id>` (NFR-LOG-7).

- **One event for each form that changed a risk setting,** whichever of the five and however many.
- A form that brings the settings as they are records nothing, however the numbers are written: `1.50` is `1.5`.
- **Setting them for the first time changes none.** Creating an agent and copying one record nothing, and neither does deleting one.
- The event holds no number and no name. What the settings are now is on the page of the agent and in the export.

See "The audit trail" in [accounts.md](accounts.md).

## The data

| Table | Columns |
|---|---|
| `agents` | `id`, `user_id`, `name`, `mode`, `market`, `symbol`, `quote_asset`, `timeframe`, `allocated_capital`, `risk_per_trade_percent`, `leverage_cap`, `daily_loss_percent`, `drawdown_percent`, `max_entry_distance_percent`, `state`, `state_cause`, `state_changed_at`, `created_at`. One row for each `(user_id, name)`. At most one active row for each `(user_id, mode, market, symbol)`. |

The table holds rows of users: `Agent` implements `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. The rows of an account are deleted with it.

| Column | What it holds |
|---|---|
| `mode`, `market`, `timeframe`, `state`, `state_cause` | The name of a member of `TradingMode`, `Market`, `Timeframe`, `AgentState`, and `AgentStateCause`. |
| `quote_asset` | The asset that the symbol is priced in, as Binance named it when the symbol was stored. |
| `allocated_capital` and the percentages | Exact decimals. |
| `state_changed_at` | Since when the agent is in its state. For a draft, when it was created. |

An agent's id is a version 7 UUID that the application assigns. `name` is what its user typed: it is listed in `PersonalDataRules` with what it holds, is in the export, and is in no log event.

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
```

An event names a user and an agent by their ids, and never holds a name or a number of the settings.

## Not built yet

- Starting, pausing, resuming, and stopping an agent, the states that follow from them, and "Close position now" (FR-AGT-10 to FR-AGT-12). With them come what waits for an agent that can run: see "What waits for agents that can run".
- Switching an agent to Live, with a valid Live key, a risk confirmation, and a fresh proof of who the user is (FR-AGT-7). `SensitiveAction.SwitchAgentToLive` of Accounts is there for it. A copy of a Live agent is in Demo, where its market may lack its symbol: what switches an agent decides what a copy does then.
- Giving each role of the team its provider and model (FR-AGT-5, FR-TEAM-1 to FR-TEAM-5). The page names the four roles, and stores nothing of them.
- The Demo or Live badge on every agent screen (FR-UI-3). The list and the form say the mode in words.
- The cycles, the data package, and the prompts that go by the timeframe and the market of an agent.
- What keeps positions and orders, which answers `IAgentExposure`.
- The history of the changes of an agent's state (FR-AGT-10). The row holds the state, its cause, and since when.
- The agent detail screen, and the times of an agent in the local time of its user.
