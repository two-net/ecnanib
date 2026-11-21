# Architecture

Ecnanib is one deployable application. A single process serves the UI, exposes the API, and runs the scheduler and background work. The only other thing to deploy is one PostgreSQL database. There are no microservices, message brokers, separate cache servers, or search servers, and nothing depends on a proprietary cloud service (NFR-ARC-1, -2, -5).

Version 1 targets a single running instance of that process. What must not run twice, the migrations and the scheduler, takes a database lock, so a second instance would be safe (NFR-ARC-4). See [background-work.md](background-work.md).

The rules below are enforced by tests in `tests/Ecnanib.Tests/Architecture`. Run them with:

```
dotnet test --solution Ecnanib.slnx
```

The tests start the application on a PostgreSQL container of their own, so Docker must be running. See [persistence.md](persistence.md).

## Components

The code is the project `src/Ecnanib`. Each logical component of SRS section 3.1 is a top-level folder and the namespace `Ecnanib.<Folder>`.

| SRS component | Folder |
|---|---|
| Web UI | `WebUi` |
| Accounts | `Accounts` |
| Credentials | `Credentials` |
| Catalog | `Catalog` |
| Agents | `Agents` |
| Decision Engine | `DecisionEngine` |
| Risk Guard | `RiskGuard` |
| AI Provider Gateway | `AiProviderGateway` |
| Exchange Gateway | `ExchangeGateway` |
| Experience | `Experience` |
| Notifications | `Notifications` |

Three more areas are not SRS components:

- `Shared` holds vocabulary that several components use, such as `Market` and `TradingMode`. It depends on nothing.
- `Platform` holds the plumbing every component builds on. That is configuration, logging, persistence, background work, and how requests travel: it reads the one configuration file into typed settings, as [configuration.md](configuration.md) describes, it sets up structured logging from that file, as [logging.md](logging.md) describes, it holds the one database context and its migrations, as [persistence.md](persistence.md) describes, it runs the components' scheduled jobs, as [background-work.md](background-work.md) describes, and it sets the rules for TLS and cookies, says what a browser is told to refuse, and encrypts the secrets that a component stores, as [security.md](security.md) describes. It may depend only on `Shared`.
- `Api` holds the HTTP endpoints under `/api`. Like the Web UI, it is an entry point on top of the components.

Each component has a `<Name>Module.cs` with an `Add<Name>()` method that registers its services. `Program.cs` is the composition root: it creates the host with `PlatformModule.CreateBuilder`, calls every `Add<Name>()`, and maps the API and the UI. A component that has scheduled work registers a job with `AddBackgroundJob<T>()` in its `Add<Name>()`, and the platform's scheduler runs it in the same process. A component does not start a hosted service of its own for such work: that would run on every instance, without the lock that makes a second instance safe.

## Dependency rules

Dependencies between components point one way. Each component may reference only the components on its allow-list, and the allow-lists contain no cycle.

The allow-lists are in [`ComponentRules.cs`](../tests/Ecnanib.Tests/Architecture/ComponentRules.cs). That file is the source of truth; this document does not repeat it. In outline:

- The gateways, the Risk Guard, and Accounts are leaves. They depend on no other component.
- The Decision Engine sits above every component except the Web UI. It cannot use Accounts, so personal data cannot reach a prompt (FR-PRM-9). It is the one component that calls a model, so what a provider is sent is a prompt that it composed (NFR-SEC-8).
- The Web UI and the API are on top, and nothing depends on them.
- Only Catalog, Credentials, and the Decision Engine may use the AI Provider Gateway.

Every type in an `Ecnanib` namespace must sit in a registered area, so new code cannot fall outside the rules.

### When a lower component must trigger a higher one

Sometimes a component must cause something to happen in a component above it. For example, when the Catalog marks a model as unavailable, the agents that use it must change state. The lower component declares an interface, and the higher component implements it and registers the implementation. The reference still points downward. This happens in process; there is no message broker.

Accounts sends its email this way. It declares `IAccountMailer`, and Notifications implements it on top of the email adapter. It asks about a user's agents this way too, before it deletes an account: it declares `IAgentActivity`, which Agents implements. And it takes the parts of the export of a user's data that other components keep this way: it declares `IExportedPart`, which Credentials implements for the keys, Agents for the agents, and Notifications for the notifications. See [accounts.md](accounts.md).

Credentials tells whatever depends on a key this way, when the key is deleted or turns out to be invalid: it declares `IKeyDependents`, which Agents implements. See [credentials.md](credentials.md).

The Catalog tells whatever depends on a model this way, when a sync marks the model unavailable: it declares `IModelDependents`, which Agents implements. See [catalog.md](catalog.md).

Agents asks what an agent holds on the exchange this way, has its position closed, and asks what in the account of a user at the exchange stands in the way of an agent (FR-BIN-F2): it declares `IAgentExposure`, which the Decision Engine implements, because it keeps the positions and the orders, and is the one component that is handed the Binance key of a user for a call. Agents places no order itself. See [agents.md](agents.md) and [orders.md](orders.md).

The Risk Guard asks whether the global halt is on this way: it declares `IGlobalHalt`, for whatever keeps the halt to implement. See [risk-guard.md](risk-guard.md).

Experience asks for the closed trades of a user this way, which a track record is worked out from: it declares `IClosedTrades`, which the Decision Engine implements, because it keeps the positions, their results, and the models that held their roles. So Agents, which may use Experience and not the Decision Engine, asks Experience alone about a track record. See [experience.md](experience.md).

An interface that waits for its implementation has a test that fails once the implementation is due. For the ones that wait for Agents, that is once an agent can run or uses a model, and not once Agents has a table. An agent can run now, and uses the models that the roles of its team were given, so what waited for either is built, and its tests hold Agents to it. [`AgentsRules.cs`](../tests/Ecnanib.Tests/Agents/AgentsRules.cs) says what Agents can do, and those tests ask it.

### Changing a rule

Edit `ComponentRules.cs`. The allow-lists are deliberately tight, so a new dependency is a decision made in one visible place. The tests reject a change that creates a cycle.

## Adapters

Each external system sits behind an adapter with a common interface.

| External system | Common interface | Adapters live in |
|---|---|---|
| Each AI provider | `IAiProviderAdapter` for calls to a model, `IAiKeyChecker` for the check of a key, and `IAiModelLister` for the list of its models | `AiProviderGateway/Adapters/<Provider>` |
| Binance | `IExchangeAdapter` | `ExchangeGateway/Adapters/Binance` |
| Email delivery | `IEmailAdapter` | `Notifications/Adapters/<Service>`. SMTP is in `Notifications/Adapters/Smtp`. |

Three rules keep every call to an external system inside its adapter, and in the log:

- A component's `Adapters` namespace is private to that component. Other code uses the common interface.
- Network client types (`System.Net.Http`, `System.Net.Mail`, `System.Net.WebSockets`, `System.Net.Sockets`, and the SMTP library `MailKit`) may be used only inside an `Adapters` namespace.
- No code may create an `HttpClient`. An adapter takes one from the host's factory (a typed or named client, or `IHttpClientFactory`), and every client from the factory logs its calls (NFR-LOG-5). See [logging.md](logging.md).

What an adapter learns from the network leaves it as a type of its own component, because nothing outside an `Adapters` namespace may name a network type, an error of the network included. The checks of a key answer with a `KeyVerdict`, and a provider that does not answer is one of its values. A call to a model answers with an `AiCallOutcome` in the same way: a provider that refuses, fails, or takes too long is an outcome of the call, and not an error. So does the list of a provider's models, with an `AiModelListOutcome`, and a call to Binance, with an `ExchangeCallOutcome`.

The adapters of the AI providers make the calls to a model, the calls that validate a key, which ask for the list of a provider's models, and the calls that read that list for the catalog: see [ai-providers.md](ai-providers.md). The adapter of Binance makes the calls that read: the clock of a market, what a key may do, what an account holds, and the market data, each on the real exchange and in Binance's test environment. It makes the calls of the orders as well: it places an order, asks what became of it, takes it off the exchange, and reads its executions. And it follows the account event feed, on a WebSocket, which one type of the adapter opens. See [exchange.md](exchange.md). A gateway makes all its calls with one named client, `AiProviderGatewayModule.HttpClientName` or `ExchangeGatewayModule.HttpClientName`, so a test puts a handler of its own behind that name in place of the network.

### Adding an AI provider

1. Add a class that implements `IAiProviderAdapter` under `AiProviderGateway/Adapters/<Provider>`. `ModelCall` makes the exchange, which is the same for every provider. The adapter says what is the provider's own, as an `IModelCallFormat`: the path, the header of the key, the body with the schema where the provider takes it and with the provider's search as a tool when the call asks for live search, how an answer reads and how many searches it shows, and which of its errors is the one to a wrong key or to a model it does not have.
2. Add a class that implements `IAiKeyChecker` beside it: the call that says whether the provider takes a key.
3. Add a class that implements `IAiModelLister` beside them: the list of the provider's models. `ModelListCall` makes the exchange. The class says what is the provider's own, as an `IModelListFormat`: the path of a page, how a page reads and names the next one, and what the provider says about a model.
4. Register all three in `AddAiProviderGateway()`.
5. Add its entry under `AiProviders` in the configuration file, with its address and its time limit, as [configuration.md](configuration.md) describes.
6. Add its row to the seed data in `AiProviderGateway/AiProvider.cs` and generate a migration, as [persistence.md](persistence.md) describes.
7. Add its code to the tests of the gateway, and run them against the provider itself once: see "Checking against the real providers" in [ai-providers.md](ai-providers.md).

Nothing else changes. Other components call `IAiProviderGateway`, `IAiKeyChecks`, and `IAiModelLists`, and name the provider by its `AiProviderCode`. Providers are an open set of codes, not an enum, so no caller can branch over them.

### Placing orders

Operations that place orders are in the namespace `Ecnanib.ExchangeGateway.Trading`: `IExchangeOrders`. Only the Decision Engine may use it, which keeps one path from a model's output to an order: schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6). In the Decision Engine one type uses it, `AgentOrders`, which makes an entry from what the Risk Guard accepted, and a test pins that. The Web UI and the other components may use the rest of the Exchange Gateway, which only reads. See [orders.md](orders.md).

## Configuration

Settings come from one file and reach a component as typed settings. Two rules in `ComponentRules.cs` keep it that way: only `Platform` may use `Microsoft.Extensions.Configuration`, and no code may read the process environment or the command line directly. See [configuration.md](configuration.md).

## Persistence

The application has one database, PostgreSQL, and reaches it through one context, `EcnanibDbContext` in `Platform`. An entity and its mapping live in the component that owns them, and the context finds every mapping by itself, so `Platform` never references a component. A key between two components is mapped in the one that may reference the other: the rows of Experience hang on a cycle of the Decision Engine, which declares that key. Two rules in `ComponentRules.cs` keep the context the only way in: only `Platform` may use the database driver, Npgsql, and only `Platform` may write a database command of its own (NFR-DAT-1, -2). See [persistence.md](persistence.md).

## Accounts

A visitor registers with an email address and a password, accepts the legal documents, and verifies the address by email. Passwords are stored only as Argon2id hashes. Whatever creates an agent asks Accounts first whether the user may, which is where the verified address and the fair-use limit are checked (FR-ACC-1, FR-ACC-7).

A user signs in with the address and the password, signs out, and resets a forgotten password by email (FR-ACC-2). Signing in, registering, and asking for a reset can each be tried only so often, from one client and for one address (NFR-SEC-4). A browser that is signed in has a session in the database, named by a cookie, which ends after a period of inactivity. Accounts registers the application's one authentication scheme, so the Web UI and the API both know the signed-in user, and a page says that it needs one with `[Authorize]`.

A user can turn on two-factor authentication with an authenticator app. Signing in then takes a code from the app after the password, and no session starts without one (FR-ACC-3). The key that the account shares with the app is stored encrypted.

Being signed in is not enough for an action that exposes keys or money: adding or replacing a key, switching an agent to Live, and deleting the account. Whatever does one asks Accounts first whether the user has just proved again who they are, with their password and, when they have it on, a code (FR-ACC-4).

A user can delete their account, which is the first of those actions to be built. Every row that belongs to a user hangs on the account and goes with it, in one transaction, stored keys included. It is done only while every agent of the user is stopped, which Agents says. A user can also download what is stored about them, without the secrets. Personal data is limited to the email address, the password hash, and preferences, and a test holds the columns of the model to that (FR-ACC-9, NFR-SEC-9).

Accounts also holds the audit trail: the record in the database, apart from the logs, of security- and money-relevant actions (NFR-LOG-7). An event is written in the transaction of the action it records, and is made from values that take no text, so it cannot hold a secret. Accounts records sign-ins, re-authentications, new passwords, and the changes to two-factor authentication. A component above it records its own actions through `AuditTrail`, and a test fails for an action of the requirement that is still not recorded once what does it is built. See [accounts.md](accounts.md).

## Credentials

A user brings their own keys: one for each AI provider, and one Binance credential for each environment, Demo and Live. Ecnanib has no key of its own for a decision or an order (FR-KEY-1, FR-KEY-2). A key is stored encrypted and is write-only: no page, export, log, or audit event holds it, and after saving only its last four characters are shown (FR-KEY-8). A user can replace or delete any key at any time (FR-KEY-10).

Adding or replacing a key is the second of the sensitive actions to be built, and asks Accounts for the proof first. Every change to a key is recorded in the audit trail, by the id of the key. Deleting a key tells whatever depends on it inside the same transaction, which is how an agent that runs with it moves to Attention required.

A key is validated with a read-only call to its provider, through the gateways: before it is stored, daily by a job, and when a call with it fails authentication (FR-KEY-3). A stored key is handed to the call of a model through `IKeyHandOver`, decrypted for that call alone, and so is a Binance credential to a call about an order, to a read of the position that an order opened, and to the gateway as it begins to follow the events of an account. The Decision Engine alone asks for either (FR-KEY-9). Only a key that its provider takes is stored, and a stored key that is refused later becomes invalid, which whatever depends on it is told as well. A Binance key that can withdraw is not stored, and one that turns out to be able to is deleted (FR-KEY-4, NFR-LEG-2). What a Binance key may trade on is recorded, and Credentials says whether it lets an agent start on a market, for Agents to ask (FR-KEY-5). Which models an AI key can use is recorded each time its provider takes it, and Credentials says which models a user can select with their keys, for Agents to ask (FR-CAT-9). Agents asks before an agent starts, and moves an agent that runs with a Binance key to Attention required when it is told that the key goes. A Binance credential that Binance refuses for a call about an order is checked again at once. See [credentials.md](credentials.md).

## AI providers

Every call to a model is made through the AI Provider Gateway, with one adapter each for Claude, Gemini, GPT, and Grok. A call takes prompt text and a result schema, and returns the structured result, its token usage, and its latency. The prompt is the same for every provider, and only how the structured result is asked for is a provider's own (FR-PRM-10). A call can let its model search live, with the search of its own provider as a tool of the request, and its result says how many searches the provider ran (FR-CYC-3). Where a call goes and how long it may take come from the configuration file.

The gateway takes the user's key with each call and keeps it nowhere (FR-KEY-9). A call that gives no result ends as an outcome, and not as an error: a timeout, an answer that is no result, a failed authentication, a model that the provider does not have, or a failure. What follows from each is for the caller. Not built yet: what calls the gateway for an agent. See [ai-providers.md](ai-providers.md).

## Exchange

Every call to Binance is made through the Exchange Gateway, with one adapter behind `IExchangeAdapter`. Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2). A caller names a market and an environment, and nothing of Binance.

The gateway takes the user's credential with each call that needs one, signs the call with it, and keeps it nowhere. A call that gives no answer ends as an outcome, and not as an error. Everything the gateway can send to Binance is a short list of calls that read and of calls about orders, and each passes one place: it has no operation that withdraws, deposits, or transfers funds, and a test holds it to that (FR-BIN-9).

The orders are behind `IExchangeOrders`, in a namespace that only the Decision Engine may use: an order at the market and a stop, on both markets. Every order carries the identifier that its caller gave it, and is asked about and taken off the exchange by that identifier. A call is made once, and how it ended says whether there is an order: Binance took it, refused it, the call was not sent, or what became of it is unknown, which is when the order is asked about before anything else is done with it (FR-BIN-6). A call that places an order is not given up by its caller once it is sent.

Binance counts its limits on requests against the address of the service, so all users spend one budget. One limiter in the gateway governs every call, for all users: it keeps the service under the part of each limit that the configuration file allows it, sends nothing while Binance signals that it has had too many, and gives the users whose calls wait their turns. A call that it holds back ends as an outcome of its own. It says what is used through `IExchangeLimits`, for the operator status screen (FR-BIN-4).

Binance takes a signed call only when the time that it carries is near Binance's own. A job of the gateway reads Binance's clocks on a schedule, and the gateway holds the clock of the service against them: new entries pause on a market in an environment while the clock is further from Binance's than the configuration file allows, while it could not be read, and after Binance refused a call for its time. It says how the clock stands through `IExchangeClock`, for what places an entry to ask and for the operator status screen (FR-BIN-5).

Market data is behind `IExchangeMarketData`: candles, prices, and the rules of the symbols, and on Futures also the mark price, the funding rate, and the open interest. It is read once and shared by all who need it, because every read costs of the one budget: whoever asks first causes the read, and whoever asks while it is under way, or while what it gave still counts, gets the same. Only closed candles are given, and which candle is closed is Binance's word. What was read is held in memory for a short time, candles for a minute at most, and nothing of it is written to the database (FR-BIN-3, NFR-DAT-6). What starts the cycles asks for the candles of each close, and each cycle for the candles of the higher timeframes of its data package, and on Futures for the mark price, the funding rate, and the open interest. What has the Risk Guard check a plan asks for the price and for the rules of the symbol. Experience asks for the candles that closed after a call of an analyst and after a vetoed plan, which it scores them against.

Where Binance has an account event feed, and the configuration file names its address, the gateway follows the account of a credential on it, behind `IExchangeAccountFeed`: a connection that stays open, on which Binance says what changes in the account as it happens (FR-BIN-7). The gateway says of every event which symbol it is about, and nothing else of it: an event is a signal, and what changed is asked with the calls that read. It keeps no key for it, sends nothing on a feed but the one request that subscribes, and closes a feed that nobody has asked for in a while. See [exchange.md](exchange.md).

## Model catalog

The Catalog keeps the models that the four providers offer. It is filled only from each provider's own list of its models, which the gateway reads: no model is hard-coded (FR-CAT-1). The catalog is synced on a schedule, as the application starts when it is empty, and on demand, with the key that the configuration file holds for the provider for this alone (FR-CAT-2, FR-CAT-3). Credentials hands the Catalog each key of a user that its provider took. The Catalog lists the provider's models with it, keeps it nowhere, and answers with what the provider listed to it, which is what that key can use (FR-CAT-9). Where the file holds no key for the provider, that list also fills the provider's catalog.

A model is stored once, with what its provider says about it, and what the provider does not say is unknown (FR-CAT-4). A model that enough syncs in a row lack is marked unavailable, and is never deleted (FR-CAT-5). With the keys of users it must also have gone unseen for long enough, because one key may see fewer models than another. A sync that fails for one provider leaves that provider's catalog as it was and the others alone, and every sync is recorded (FR-CAT-7).

Only an eligible model can be selected for an agent: one that its provider still offers and that the configuration file allows, with allow and deny patterns for each provider. The file also says which models can search live (FR-CAT-6), which a selector shows and `IModelCapabilities` answers for what calls a model (FR-CYC-3). Whatever depends on a model is told inside the transaction that marks it unavailable, and no other model is ever put in its place (FR-CAT-8). A screen lists the catalog by provider for every signed-in user: each model with its status and what its provider says about it, with unknown shown as unknown, and each provider with when it was last synced. It links each model to the page of its experience record (FR-CAT-10). Agents is told, and moves the agents that run with the model to Attention required. Not built yet: what follows a call that reports that the model does not exist. See [catalog.md](catalog.md).

## Agents

A user creates, changes, copies, and deletes agents, and no user has two agents of one name (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, a team, allocated capital, and risk settings (FR-AGT-2). The settings are checked on the server: the risk settings against the hard bounds of the one profile in the configuration file, and the symbol against the symbols of the file that the chosen market has, which the Exchange Gateway says (FR-AGT-6, FR-RSK-2).

The team of an agent is four roles, and its user gives each a provider and a specific model, stored with the agent (FR-AGT-5, FR-TEAM-1). A new agent starts from the provider that the configuration file gives each role, four different ones (FR-TEAM-2, FR-TEAM-3). Only a model that can be selected, and that the user's own key can use, is offered and stored: Credentials says which (FR-CAT-6, FR-CAT-9). One provider may hold more than one role, and the form warns that the team is reduced, and of its own when the Strategist and the Risk Manager use the same provider (FR-TEAM-5). Whatever creates an agent asks Accounts first, so a user whose address is not verified creates none, and no user has more than the fair-use limit allows.

A user starts, pauses, resumes, and stops an agent, and the agent follows the states of the specification: only a running agent runs cycles and opens positions (FR-AGT-1, SRS 3.3). Every change of its state is recorded with its cause and its time, in one place, and shown to its user (FR-AGT-10). A start asks Credentials first whether the user's Binance key lets the agent run on its market, and whether its user can select the model of every role of its team: an agent runs only with its whole team. An agent that runs moves to Attention required when that key is deleted or refused, when the key of a provider of its team is, and when a model of its team is no longer offered, and no other model is put in its place. The mode, the market, the symbol, and the timeframe change only while an agent is a draft, or is stopped with no open position or orders (FR-AGT-8). A user has at most one active agent for each Binance credential, market, and symbol, which a start asks about and the database holds to with an index (FR-AGT-9).

A new agent is in Demo. Its user switches it to Live with a valid Live Binance key, a confirmation of the risk, and a fresh proof of who they are, and back to Demo without them (FR-AGT-7). Every page of an agent shows a Demo or Live badge, and every action that affects Live trading asks for confirmation, which the server decides of the mode that the agent is in then (FR-UI-3). What an agent did is stored with the mode it did it in, row by row, so that Demo is never read as Live (FR-BIN-11).

Agents places no order. A user who stops an agent that has an open position chooses whether it is closed at market or kept with its protective orders, and can close the position of an agent at any time, in whatever state the agent is (FR-AGT-11, FR-AGT-12). Agents records the request and asks whatever keeps positions, through `IAgentExposure`, which the Decision Engine answers. What places the orders asks Agents in turn, through `IAgentTrading`: for the state of an agent under the lock of its account, as an order of it is written, so that an entry and a stop of its user cannot cross, and to have an agent that runs wait for its user when its position is left with no stop-loss (FR-RSK-7), or when the account of its user at the exchange conflicts with how it trades (FR-BIN-F2).

Agents says whether an agent of a user is active, for Accounts to ask before it deletes an account, and hands over the agents, the models of their roles, and the changes of their states for the export of a user's data. What a user does to an agent is recorded in the audit trail: a change to a risk setting, a start, a pause, a resume, a stop, a manual close, and a switch of its mode. Agents says which agents run, of every user, for what starts their cycles. An agent that reaches a loss limit is halted (FR-RSK-8): what watches the loss limits asks Agents, through `IAgentHalts`, to halt an agent that runs, and to resume the agents that their daily loss limit halted once the UTC day of the halt is over, which Agents does when their key and their team allow it. Its user does not resume such an agent before that day. Agents recommends a team by measured strength (FR-TEAM-4): the form of an agent shows the strongest candidate of each role, from the strength scores that Experience works out for the agent's market and timeframe, among the models that the keys of its user can use, and one button saves the form with the recommended models. `RoleRecommendations` is the rule, and it reads nothing. An agent that opted in has its roles reassigned by the same rule by a job of Agents, at most once in `Trading:RoleReassignmentDays` days and only while it holds nothing, and its user is told of each change through Notifications (FR-TEAM-10). See [agents.md](agents.md).

## Cycles

The Decision Engine starts a cycle for each running agent when a candle of its timeframe closes, on UTC boundaries (FR-CYC-1). One job runs at every quarter hour, which is the close of the shortest candle, and has the cycles of every timeframe that closes then. Agents says which agents run, and is asked again as the turn of a cycle comes: only an agent that runs gets a cycle (SRS 3.3). A boundary that passed while the application was down is skipped by the scheduler and never made up for, and nothing of a queue is stored, so a restart waits for the next close (FR-CYC-8).

The cycles of a close start with the candle of that close, which the Exchange Gateway is asked for until Binance has closed it: a cycle is handed closed candles through its close, and never the one in progress (FR-CYC-1). The cycles that share a boundary wait in one queue, the shortest timeframe first, and a cycle starts when it finds a place with every AI provider that its team uses and with the key of its user for that provider, within the limits of the configuration file (FR-CYC-11). A cycle that finds none waits and reserves nothing, so a provider that is slow holds back the cycles that need it and no others. The cycles of a close are over before the next close: one that could not start in time is skipped, and one that still runs as the next close nears is stopped.

A cycle that starts builds its data package: the most recent closed candles of the agent's timeframe, the candles of the higher timeframes that are its context, the position, the orders, and the equity of the agent, its limits, and on Futures the mark price, the funding, and the open interest (FR-CYC-2). The indicators of the package, which are moving averages, relative strength, average true range, and volume averages, are computed by the system from the candles, and by no model. The reads of a package are shared by the cycles of a symbol, as all market data is, and a cycle whose market data the exchange does not give makes no decision. What an agent holds is said by what keeps the positions and the orders, through `IAgentHoldings`, which the Decision Engine declares and answers from its rows.

The Sentiment Analyst gathers news and social context with the live search of its model, on the key of the agent's user (FR-CYC-3). `IRoleCalls` says what the model of a role is called with: only the Sentiment Analyst searches, and only with a model that the configuration file says can. A Sentiment Analyst whose model has no live search still runs. Its report is marked "no live data" by the service, as is the report of a model that could search and did not, and the instructions of the Strategist say to give such a report no weight.

What a cycle does once it has started is `TeamCycle`: it builds the data package and runs the four roles of the agent's team, the Market Analyst and the Sentiment Analyst at the same time, then the Strategist, then the Risk Manager, which is asked about a plan that opens a position (FR-TEAM-6, FR-TEAM-7). Each role is given only its own inputs, and what its model answers is validated against the fixed schema of the role and the ranges of its values: a result outside either is rejected (NFR-SEC-7). A plan is held to what a plan may do as well: the actions of its agent's market, one position, protection that only tightens, and a stop-loss and a take-profit where it opens a position (FR-CYC-4 to FR-CYC-7). A decision that completes later than the stale limit of the agent's timeframe opens no position (FR-CYC-9). A call that times out or returns an invalid result is made once more. A cycle whose Sentiment Analyst still gives no result continues with sentiment marked unavailable, and a cycle whose other role gives none ends with no decision and no new entry (FR-TEAM-9). Every call is recorded with its cycle, in `decision_cycles` and `role_outputs`: the model and so its provider, the versions of the prompt templates, the latency, the token usage, the structured result, and the validation outcome (FR-TEAM-8). A cycle is stored as a complete record, however it ends: the row of the cycle keeps a summary of its data package, with no candles, and why no entry was placed where none was, and its checks and its orders are rows of their own. `IRecordedCycles` reads a record for the user of its agent, and the pages of the cycles show it as a timeline (FR-CYC-10). With its record, a cycle that a candle close started leaves Experience the call of each analyst that called a direction, and its plan where the Risk Manager vetoed it, as rows of Experience that are written in the same transaction. The validation is the first step of the one path from a model's output to an order (NFR-SEC-6), and the Risk Guard the second: what goes on of the plan is checked against its fixed rules, and the outcome of a cycle stays the decision of its team. What the Risk Guard accepted is handed to what places the orders, which is the third. See [team.md](team.md).

A user runs a test cycle of an agent on demand, through `ITestCycles`, which hands `TeamCycle` a cycle that says it is a test: it is recorded as every cycle is, with the kind that tells it apart, and what the Risk Guard accepted of it is handed to nothing, so it places no orders (FR-CYC-12). It runs inside the request that asked for it, outside the job and its queue. See "A test cycle" in [cycles.md](cycles.md).

What a cycle does once it has started is behind `IDecisionCycle`, and what reconciles an agent with the exchange answers through `IReconciliation`, which a cycle waits for after a restart. See [cycles.md](cycles.md).

## Orders

The orders are step 7 of a cycle: `IPlanOrders` places the entry and the stop-loss of a plan that the Risk Guard accepted, closes a position, or moves its stop-loss, or does nothing. `AgentOrders` is the one type of the Decision Engine that uses the orders of the Exchange Gateway, and it makes an entry from the result of the Risk Guard, with the quantity that the check computed (NFR-SEC-6, FR-RSK-5).

An order is never sent twice (FR-BIN-6). Every order carries an identifier of its own, which is the id of its row, and the row is written before the order is sent. An order of which Binance did not say what became of it is asked about by that identifier before anything else is done with it. An entry is never sent again. A stop-loss and an order that closes a position are sent once more, as the same order, once Binance does not have them and they could no longer arrive.

Every open position has a stop-loss that rests on the exchange (FR-RSK-7). It is placed when the entry is executed, for what the agent holds of it. When it cannot be placed, the position is closed at market at once, and its user is told with a notification. When it cannot be closed either, an agent that runs waits for its user, and a job that settles the orders tries again every minute. The job also asks about what a cycle left unknown, and an agent whose orders are not settled gets no cycle: that is what answers `IReconciliation`, and what keeps a restart from placing an order twice (NFR-REL-3).

The Decision Engine keeps the orders, their executions, and the positions, in `orders`, `fills`, and `positions`, each row with its owner and the mode of its agent, and what a position of Futures received and paid as funding in `fundings`. A position says the timeframe of its agent, and the models that held the roles of the agent when it was opened, in `position_roles`. From them it answers what an agent holds, for a cycle through `IAgentHoldings` and for Agents through `IAgentExposure`, and closes a position for the user of an agent. It reads how the account of a user stands on Futures as well, before an entry and for Agents before an agent runs.

The positions of Futures are watched while they are open, by a job that runs at the time between reconciliations: the margin of each is read, and its user is told with a notification when it approaches liquidation, and what it received and paid as funding is read after each funding time and stored (FR-BIN-F4, FR-BIN-F5). `PositionWatch` only reads the exchange: it uses none of the orders of the gateway, and the gateway has no call that adds margin to a position.

The exchange is the source of truth for what an agent holds (FR-BIN-7, FR-BIN-8). A job reconciles every agent that holds something, as the background work starts and every few minutes: it asks about each of its orders, and holds its open position against what the account of its user holds. A position that the account no longer holds is closed in the records, and one of which it holds less, or more, is written as the account holds it, so what a user does at Binance by hand is in the records. The records are corrected only once Binance has said the same twice, with the orders of the agent asked about in between, and every mismatch is said in the log and told to the user with a notification. Another job has the gateway follow the account event feed of the accounts in which an agent holds something, and reconciles an agent at once when its account says a change. After a start, an agent that holds something from before gets no cycle until it was reconciled (NFR-REL-3).

The equity of an agent is its allocated capital plus its net profit or loss to date (FR-RSK-5, FR-RSK-8): `AgentResults` works it out from the rows, as what the positions of the agent made by the orders that closed them, less the fees of its executions, with its funding, and with what its open position would make at the price now, which is the mark price on Futures. The rows are read in one snapshot, so that an order that is executed meanwhile is counted once.

Fees and funding are what the exchange said, and are in every profit and loss figure (FR-BIN-10). An execution says what its fee is worth in the asset that its symbol is priced in, whatever asset the exchange took it in: `FeeValuation` prices a fee in a third asset, such as BNB, by a symbol of `Binance:FeeSymbols`. What a user, or the exchange, closed of a position by no order of the agent has no execution, and is counted at the price when the reconciliation found it.

Every closed position is given its result, once all of it is stored (FR-EXP-1): a job records its net profit or loss, its result in R, its fees, its funding, and how long it was held, in `position_results`, by the sums that the equity of its agent is worked out with. And the equity of an agent is recorded over time, in `equity_snapshots`: what watches the loss limits writes the equity that it evaluated.

Not built yet: the pages that show the results and the equity of an agent, and the executions of what was done at the exchange by hand, with their prices and their fees. See [orders.md](orders.md).

## Experience

Experience works out a track record for each AI provider and for each model, from the closed trades of one user: closed trades, win rate, average R, total R, profit factor, largest drawdown in R, and average holding time, filterable by role, market, timeframe, mode, and period (FR-EXP-2). `TrackRecord` is the rule of the measures, and `ITrackRecords` answers with the records of a user. Nothing of a record is stored: it is worked out from the results of the closed positions each time it is asked for, which the Decision Engine hands over through `IClosedTrades`. So a record says what the positions say, and a trade that is deleted with its agent is in no record.

Demo and Live are scored separately, and never added up (FR-EXP-9, FR-BIN-11). What is not told a mode goes by `ExperienceBasis`: Live where it has at least `Experience:MinSamples` samples, otherwise Demo, and it says which. The experience screens go by it for the mode that they show first.

A role-specific measure is kept for each role (FR-EXP-3): for the two analysts the share of their calls that matched the price change over the next 4 closed candles, minus a half, for the Strategist the average R of its executed plans, and for the Risk Manager the average R of what it approved minus the average R that what it vetoed would have produced. A strength score per model, role, market, and timeframe is that measure damped toward zero for small samples, measure × n ÷ (n + damping), and a slice with too few samples has none (FR-EXP-4). `RoleMeasures` and `StrengthScore` are the rules, and `IRoleStrengths` answers with the slices of a user in one mode. No aggregate is stored.

What a measure needs of the future is stored, and nothing else: Experience keeps the calls of the analysts and the shadow trades of the vetoed plans, in `analyst_calls` and `shadow_trades`, which the Decision Engine writes as it records a cycle. A job of Experience completes them from the candles that closed afterwards, which it reads through the Exchange Gateway: a call is scored once its 4th later candle has closed, and a vetoed plan is followed as a shadow trade, with its own entry, stop-loss, and take-profit and with standard fees, and with the stop-loss taken to be hit first when both fall inside one candle (FR-EXP-5). A shadow trade never places an order: Experience may use the gateway, and not its orders, which are open to the Decision Engine alone.

A signed-in user reads the records on the experience screen, which the bar at the top of every page leads to, and the record of one model by role on the page that the catalog links the model to. Both show the strength scores under the records, with the formula. Every experience screen states that past results do not predict future results, that small samples are unreliable, and that Demo results differ from Live (FR-EXP-11), and a test asks every one of them for it.

Experience keeps the lessons as well: what one closed position taught, as the setup, what was expected, what happened, and one takeaway of at most 60 words (FR-EXP-6). The row of a lesson is written with the result of its position, by the Decision Engine, and a job of the Decision Engine has the model of the agent's Risk Manager write it, with the key of the agent's user: only the Decision Engine calls a model. Experience declares what that job asks and completes, `IDueLessons`, and implements it itself, so the reference points downward as it is. `ILessons` answers what a prompt recalls: up to `Experience:LessonsPerPrompt` lessons, those of the agent first and then those of its user's other agents on the same symbol, market, and timeframe, within a fixed size budget, and only what is written, enabled, and the same user's (FR-EXP-7). A user reads all of their lessons on a page of experience, disables or deletes any of them, and resets those of an agent (FR-EXP-8). Experience may not use Accounts, so that what feeds a prompt is out of reach of personal data: Agents hands the lessons to the export of a user's data.

What recommends a team from the scores is Agents, which may use Experience: see "Role by strength" in [agents.md](agents.md). Not built yet: the platform-wide board. See [experience.md](experience.md).

## Risk Guard

The Risk Guard is fixed rules that every plan is checked against after the Risk Manager and before any order (FR-RSK-3). It is rules only, and a leaf: `GuardCheck.Of` is one function of a plan and the facts, which calls no model, reads nothing, and stores nothing. The Decision Engine gathers the facts as the plan is checked: the price and the rules of the symbol, from the Exchange Gateway, what the agent holds, whether the Binance key of its user lets it trade, which Credentials says, whether a global halt is on, and how long ago the candle closed.

A plan that opens a position is rejected unless every rule holds: a stop-loss on the correct side of the entry, the reward-to-risk ratio, the distance of the entry from the current price, the exchange's rules for the price step, the quantity step, the least quantity, and the least value, the loss limits, the stale limit, a permitted mode, market, and symbol, no global halt, and on Futures the liquidation buffer (FR-RSK-4, FR-RSK-6). A plan that only closes a position or adjusts protection is held to the rules of the exchange and to protection that only tightens, so that risk can always be reduced. Of a reverse that is rejected, the close alone goes on.

The check computes the size of the order that a plan opens, and no model chooses it (FR-RSK-5): the quantity from the risk fraction that was approved, the agent's equity, and the distance to the stop-loss, rounded down to the quantity step. On Spot the position is worth at most the agent's equity. On Futures the leverage is the lowest that fits the margin of the position within the agent's equity, up to the leverage cap, and a quantity that would take more is reduced. Nothing hands a check a size: a case has no place for one, and the result says what was computed.

Every check stores the outcome of every rule, with its cycle, in `risk_checks` and `risk_check_rules`, and the quantity and the leverage that it computed, and a user reads the checks of an agent on a page of the agent, each rule with why it held or did not (FR-RSK-9). Neither AI output nor a user action bypasses the check: only the check makes a result, only the Decision Engine says its facts, and it has no switch.

When an agent's loss for the UTC day reaches its daily loss limit, or its drawdown from its peak equity reaches its drawdown limit, its position is closed and it is halted: until the next UTC day, or until its user resumes it (FR-RSK-8). `LossLimits` of the Risk Guard says when a limit is reached, for the rule that rejects an entry and for what halts an agent alike. `LossLimitWatch` of the Decision Engine evaluates the limits at every cycle, before the team is called, and at every reconciliation: in one transaction under the lock of the account it keeps what the limits are counted from, in `equity_marks`, records the equity that it found, in `equity_snapshots`, and has Agents halt an agent that runs. Then it tells its user with a notification, and has the position closed by what places the orders. Only an agent that runs is halted, and an equity that is not known halts nothing.

Not built yet: the global halt of an Operator. See [risk-guard.md](risk-guard.md).

## Prompts

The Decision Engine keeps the prompt templates: the instructions of each role, and a module each for a market, a timeframe, and the trading profile. There is a distinct module for each of the four timeframes and for each of the two markets, and one for the Aggressive profile (FR-PRM-2, FR-PRM-3, FR-PRM-5). They are managed by the system and delivered as seed data, through migrations (FR-PRM-7, NFR-DAT-10). A template is versioned, and a version is never changed: an edit is a new version, which takes the earlier one out of use. The database holds to that with a trigger, which refuses a change to a stored version whatever asks for it, a later migration among them (FR-PRM-6).

`IPromptTemplates` answers with the templates in use for the market and the timeframe of an agent, and only reads. A user reads the prompts that an agent of theirs uses on a page of the agent, and cannot edit them (FR-PRM-7).

`IPromptComposition` composes the prompt of a role call from seven parts: the instructions of the role, the modules of the market, the timeframe, and the profile, the context of the agent with its symbol, its equity, its position, and its limits, the data package, and recalled lessons (FR-PRM-1). It reads the templates itself, by the market and the timeframe that the context of the agent says once, and is handed none. So the modules are selected from the agent's settings, for each of the eight combinations, and a mismatched one cannot be chosen (FR-PRM-4). The prompt says which template versions it was composed from.

The instructions of every role tell its model to treat news, social posts, and web content as untrusted data, to ignore instructions found inside such content, and to return only the defined schema (FR-PRM-8). Those of the Sentiment Analyst say when to search live, and those of the Strategist to give a sentiment report that is marked "no live data" no weight (FR-CYC-3). A prompt never holds a secret, an email address, an account identifier, or other personal data: what a caller hands to `IPromptComposition` has no place for one, and a prompt that would hold one all the same is not composed (FR-PRM-9). What is handed over is what an AI provider is sent, and is limited to market data, agent settings, position state, and lessons, each value listed with its kind in the tests. The pages of the keys and the page of an agent's prompts tell users that it goes to the providers they configure, under their own keys and those providers' terms (NFR-SEC-8).

The instructions of each role say what the role is given and name each field of the structured result that it answers, with its values and ranges (FR-TEAM-6). Each call of a role is recorded with the template versions that its prompt was composed from (FR-PRM-6).

`IPromptComposition` composes one more prompt: that of the call that writes the lesson of a closed position, from the lesson instructions, which are a seeded template of a kind of their own, and from the closed position with the plan that opened it and the review of that plan (FR-EXP-6). Every part of it passes the same screen. And the prompts of the Strategist and of the Risk Manager recall the lessons that Experience answers with (FR-EXP-7). See [prompts.md](prompts.md).

## Ownership

The only account type is the individual user, and every resource belongs to exactly one (FR-ACC-5). A row that belongs to a user carries its owner. Whatever serves a request asks for the rows of the user who is signed in, so a row of another user is not found (NFR-SEC-3, NFR-DAT-9). The vocabulary for it, `IUserOwned` and `OwnedBy`, is in `Platform`, so that a component which may not use Accounts can still say whose its rows are.

Operator functions require a policy of their own, which Accounts registers: an operator is a user whose verified address the configuration file lists. Being an operator opens nothing that belongs to another user. There are no tenants, and the service is free, so there is no billing (FR-ACC-6). See [ownership.md](ownership.md).

## Security

All traffic between a browser and the application uses TLS: a request over plain HTTP is redirected and not served. Every cookie is sent only over TLS and is hidden from scripts (NFR-SEC-1).

The application defends against injection, cross-site scripting, request forgery, and clickjacking (NFR-SEC-4). Every answer tells the browser to run no script and apply no style that is not the application's own, and to show the application in no frame. A form is taken only with the token of a page that the application served. Two rules in `ComponentRules.cs` keep the rest in place: no code sends SQL that was put together as text, and no code writes text into a page as markup.

A secret that the application must read again is stored encrypted, with authenticated encryption under a master key of the configuration file, and with the id of that key beside it (NFR-SEC-5). The encryption is in `Platform`, because Accounts needs it and may not use Credentials, which needs it too, for the keys that users bring. The master key can be changed: a component declares each column that holds such a secret with `AddStoredSecret`, and when the application starts, the platform encrypts every value that an earlier key encrypted again with the active one (FR-KEY-7). `Platform` never references a component for it: it finds the declarations among the registered services. See [security.md](security.md).

## Background work

Scheduled work runs on one scheduler in `Platform`. A component implements `IBackgroundJob` and registers it; the scheduler keeps each job's latest run in the database, so the schedule goes on after a restart, and it runs jobs only while it holds a database lock, so they never run in two instances at once (NFR-ARC-4). `Platform` never references a component: it finds the jobs among the registered services. The jobs so far are that of Credentials, which checks the stored keys, that of Catalog, which syncs the model catalog, that of the Exchange Gateway, which reads Binance's clocks, those of the Decision Engine, which start the cycles, settle the orders, watch the positions of Futures, reconcile the agents with the exchange, and follow the account event feed, that of Experience, which scores the calls of the analysts and follows the shadow trades, and that of Agents, which reassigns the roles of the agents that opted in by strength. A job on an interval can also be due when the work starts, which is how the catalog is synced at startup when it is empty, and how the clocks are read at every start. See [background-work.md](background-work.md).

## Logging

Components write log events through the host's `ILogger<T>` and nothing else. One rule in `ComponentRules.cs` keeps it that way: only `Platform` may use the logging library, Serilog. So every event passes the one place that masks secrets before it reaches an output (NFR-LOG-4). See [logging.md](logging.md).

## Deployment guard

`DeploymentRuleTests` reads the application's package graph and fails if it contains a message broker client, a cache server client, a search server client, a proprietary cloud SDK, a client for a database other than PostgreSQL (NFR-DAT-1), or the SDK of a payment service, because the service is free (FR-ACC-6). The list is in [`ForbiddenInfrastructure.cs`](../tests/Ecnanib.Tests/Architecture/ForbiddenInfrastructure.cs). It also fails if the graph contains a log output other than the console and the file, so no log can be written to a database (NFR-DAT-7).
