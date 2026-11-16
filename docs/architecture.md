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
- The Decision Engine sits above every component except the Web UI. It cannot use Accounts, so personal data cannot reach a prompt.
- The Web UI and the API are on top, and nothing depends on them.
- Only Catalog, Credentials, and the Decision Engine may use the AI Provider Gateway.

Every type in an `Ecnanib` namespace must sit in a registered area, so new code cannot fall outside the rules.

### When a lower component must trigger a higher one

Sometimes a component must cause something to happen in a component above it. For example, when the Catalog marks a model as unavailable, the agents that use it must change state. The lower component declares an interface, and the higher component implements it and registers the implementation. The reference still points downward. This happens in process; there is no message broker.

Accounts sends its email this way. It declares `IAccountMailer`, and Notifications implements it on top of the email adapter. It asks about a user's agents this way too, before it deletes an account: it declares `IAgentActivity`, which Agents implements. And it takes the parts of the export of a user's data that other components keep this way: it declares `IExportedPart`, which Credentials implements for the keys and Agents for the agents. See [accounts.md](accounts.md).

Credentials tells whatever depends on a key this way, when the key is deleted or turns out to be invalid: it declares `IKeyDependents`, which Agents implements. See [credentials.md](credentials.md).

The Catalog tells whatever depends on a model this way, when a sync marks the model unavailable: it declares `IModelDependents`, which Agents implements. See [catalog.md](catalog.md).

Agents asks what an agent holds on the exchange this way, and has its position closed: it declares `IAgentExposure`, for whatever keeps positions and orders to implement. Agents places no order itself. See [agents.md](agents.md).

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

The adapters of the AI providers make the calls to a model, the calls that validate a key, which ask for the list of a provider's models, and the calls that read that list for the catalog: see [ai-providers.md](ai-providers.md). The adapter of Binance makes the calls that read: the clock of a market, what a key may do, what an account holds, and the market data, each on the real exchange and in Binance's test environment: see [exchange.md](exchange.md). The orders are not built. A gateway makes all its calls with one named client, `AiProviderGatewayModule.HttpClientName` or `ExchangeGatewayModule.HttpClientName`, so a test puts a handler of its own behind that name in place of the network.

### Adding an AI provider

1. Add a class that implements `IAiProviderAdapter` under `AiProviderGateway/Adapters/<Provider>`. `ModelCall` makes the exchange, which is the same for every provider. The adapter says what is the provider's own, as an `IModelCallFormat`: the path, the header of the key, the body with the schema where the provider takes it, how an answer reads, and which of its errors is the one to a wrong key or to a model it does not have.
2. Add a class that implements `IAiKeyChecker` beside it: the call that says whether the provider takes a key.
3. Add a class that implements `IAiModelLister` beside them: the list of the provider's models. `ModelListCall` makes the exchange. The class says what is the provider's own, as an `IModelListFormat`: the path of a page, how a page reads and names the next one, and what the provider says about a model.
4. Register all three in `AddAiProviderGateway()`.
5. Add its entry under `AiProviders` in the configuration file, with its address and its time limit, as [configuration.md](configuration.md) describes.
6. Add its row to the seed data in `AiProviderGateway/AiProvider.cs` and generate a migration, as [persistence.md](persistence.md) describes.
7. Add its code to the tests of the gateway, and run them against the provider itself once: see "Checking against the real providers" in [ai-providers.md](ai-providers.md).

Nothing else changes. Other components call `IAiProviderGateway`, `IAiKeyChecks`, and `IAiModelLists`, and name the provider by its `AiProviderCode`. Providers are an open set of codes, not an enum, so no caller can branch over them.

### Placing orders

Operations that place orders belong in the namespace `Ecnanib.ExchangeGateway.Trading`. Only the Decision Engine may use it, which keeps one path from a model's output to an order: schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6). The Web UI and the other components may use the rest of the Exchange Gateway, which only reads.

## Configuration

Settings come from one file and reach a component as typed settings. Two rules in `ComponentRules.cs` keep it that way: only `Platform` may use `Microsoft.Extensions.Configuration`, and no code may read the process environment or the command line directly. See [configuration.md](configuration.md).

## Persistence

The application has one database, PostgreSQL, and reaches it through one context, `EcnanibDbContext` in `Platform`. An entity and its mapping live in the component that owns them, and the context finds every mapping by itself, so `Platform` never references a component. Two rules in `ComponentRules.cs` keep the context the only way in: only `Platform` may use the database driver, Npgsql, and only `Platform` may write a database command of its own (NFR-DAT-1, -2). See [persistence.md](persistence.md).

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

A key is validated with a read-only call to its provider, through the gateways: before it is stored, daily by a job, and when a call with it fails authentication (FR-KEY-3). Only a key that its provider takes is stored, and a stored key that is refused later becomes invalid, which whatever depends on it is told as well. A Binance key that can withdraw is not stored, and one that turns out to be able to is deleted (FR-KEY-4, NFR-LEG-2). What a Binance key may trade on is recorded, and Credentials says whether it lets an agent start on a market, for Agents to ask (FR-KEY-5). Which models an AI key can use is recorded each time its provider takes it, and Credentials says which models a user can select with their keys, for Agents to ask (FR-CAT-9). Agents asks before an agent starts, and moves an agent that runs with a Binance key to Attention required when it is told that the key goes. Not built yet: handing a key to the call of an agent that needs it, and what Agents does about the key of an AI provider, which comes with the models of an agent's team. See [credentials.md](credentials.md).

## AI providers

Every call to a model is made through the AI Provider Gateway, with one adapter each for Claude, Gemini, GPT, and Grok. A call takes prompt text and a result schema, and returns the structured result, its token usage, and its latency. The prompt is the same for every provider, and only how the structured result is asked for is a provider's own (FR-PRM-10). Where a call goes and how long it may take come from the configuration file.

The gateway takes the user's key with each call and keeps it nowhere (FR-KEY-9). A call that gives no result ends as an outcome, and not as an error: a timeout, an answer that is no result, a failed authentication, a model that the provider does not have, or a failure. What follows from each is for the caller. Not built yet: what calls the gateway for an agent. See [ai-providers.md](ai-providers.md).

## Exchange

Every call to Binance is made through the Exchange Gateway, with one adapter behind `IExchangeAdapter`. Binance is the only exchange. The gateway reaches its Spot market and its USDⓈ-margined perpetual Futures market, each on the real exchange and in Binance's test environment, at the four addresses of the configuration file (FR-BIN-1, FR-BIN-2). A caller names a market and an environment, and nothing of Binance.

The gateway takes the user's credential with each call that needs one, signs the call with it, and keeps it nowhere. A call that gives no answer ends as an outcome, and not as an error. Everything the gateway can send to Binance is a short list of calls that read, and each passes one place: it has no operation that withdraws, deposits, or transfers funds, and a test holds it to that (FR-BIN-9).

Binance counts its limits on requests against the address of the service, so all users spend one budget. One limiter in the gateway governs every call, for all users: it keeps the service under the part of each limit that the configuration file allows it, sends nothing while Binance signals that it has had too many, and gives the users whose calls wait their turns. A call that it holds back ends as an outcome of its own. It says what is used through `IExchangeLimits`, for the operator status screen (FR-BIN-4).

Binance takes a signed call only when the time that it carries is near Binance's own. A job of the gateway reads Binance's clocks on a schedule, and the gateway holds the clock of the service against them: new entries pause on a market in an environment while the clock is further from Binance's than the configuration file allows, while it could not be read, and after Binance refused a call for its time. It says how the clock stands through `IExchangeClock`, for what places an entry to ask and for the operator status screen (FR-BIN-5).

Market data is behind `IExchangeMarketData`: candles, prices, and the rules of the symbols, and on Futures also the mark price, the funding rate, and the open interest. It is read once and shared by all who need it, because every read costs of the one budget: whoever asks first causes the read, and whoever asks while it is under way, or while what it gave still counts, gets the same. Only closed candles are given, and which candle is closed is Binance's word. What was read is held in memory for a short time, candles for a minute at most, and nothing of it is written to the database (FR-BIN-3, NFR-DAT-6). Not built yet: what asks for market data, the orders, and reconciliation. See [exchange.md](exchange.md).

## Model catalog

The Catalog keeps the models that the four providers offer. It is filled only from each provider's own list of its models, which the gateway reads: no model is hard-coded (FR-CAT-1). The catalog is synced on a schedule, as the application starts when it is empty, and on demand, with the key that the configuration file holds for the provider for this alone (FR-CAT-2, FR-CAT-3). Credentials hands the Catalog each key of a user that its provider took. The Catalog lists the provider's models with it, keeps it nowhere, and answers with what the provider listed to it, which is what that key can use (FR-CAT-9). Where the file holds no key for the provider, that list also fills the provider's catalog.

A model is stored once, with what its provider says about it, and what the provider does not say is unknown (FR-CAT-4). A model that enough syncs in a row lack is marked unavailable, and is never deleted (FR-CAT-5). With the keys of users it must also have gone unseen for long enough, because one key may see fewer models than another. A sync that fails for one provider leaves that provider's catalog as it was and the others alone, and every sync is recorded (FR-CAT-7).

Only an eligible model can be selected for an agent: one that its provider still offers and that the configuration file allows, with allow and deny patterns for each provider. The file also says which models can search live (FR-CAT-6). Whatever depends on a model is told inside the transaction that marks it unavailable, and no other model is ever put in its place (FR-CAT-8). A screen lists the catalog by provider for every signed-in user: each model with its status and what its provider says about it, with unknown shown as unknown, and each provider with when it was last synced. It links each model to the page of its experience record (FR-CAT-10). Agents is told, and moves the agents that run with the model to Attention required. Not built yet: what follows a call that reports that the model does not exist, and the experience record itself. See [catalog.md](catalog.md).

## Agents

A user creates, changes, copies, and deletes agents, and no user has two agents of one name (FR-AGT-1). An agent has a name, a mode, a market, a symbol, a timeframe, a team, allocated capital, and risk settings (FR-AGT-2). The settings are checked on the server: the risk settings against the hard bounds of the one profile in the configuration file, and the symbol against the symbols of the file that the chosen market has, which the Exchange Gateway says (FR-AGT-6, FR-RSK-2).

The team of an agent is four roles, and its user gives each a provider and a specific model, stored with the agent (FR-AGT-5, FR-TEAM-1). A new agent starts from the provider that the configuration file gives each role, four different ones (FR-TEAM-2, FR-TEAM-3). Only a model that can be selected, and that the user's own key can use, is offered and stored: Credentials says which (FR-CAT-6, FR-CAT-9). One provider may hold more than one role, and the form warns that the team is reduced, and of its own when the Strategist and the Risk Manager use the same provider (FR-TEAM-5). Whatever creates an agent asks Accounts first, so a user whose address is not verified creates none, and no user has more than the fair-use limit allows.

A user starts, pauses, resumes, and stops an agent, and the agent follows the states of the specification: only a running agent runs cycles and opens positions (FR-AGT-1, SRS 3.3). Every change of its state is recorded with its cause and its time, in one place, and shown to its user (FR-AGT-10). A start asks Credentials first whether the user's Binance key lets the agent run on its market, and whether its user can select the model of every role of its team: an agent runs only with its whole team. An agent that runs moves to Attention required when that key is deleted or refused, when the key of a provider of its team is, and when a model of its team is no longer offered, and no other model is put in its place. The mode, the market, the symbol, and the timeframe change only while an agent is a draft, or is stopped with no open position or orders (FR-AGT-8). A user has at most one active agent for each Binance credential, market, and symbol, which a start asks about and the database holds to with an index (FR-AGT-9).

A new agent is in Demo. Its user switches it to Live with a valid Live Binance key, a confirmation of the risk, and a fresh proof of who they are, and back to Demo without them (FR-AGT-7). Every page of an agent shows a Demo or Live badge, and every action that affects Live trading asks for confirmation, which the server decides of the mode that the agent is in then (FR-UI-3). What an agent did is stored with the mode it did it in, row by row, so that Demo is never read as Live (FR-BIN-11).

Agents places no order. A user who stops an agent that has an open position chooses whether it is closed at market or kept with its protective orders, and can close the position of an agent at any time, in whatever state the agent is (FR-AGT-11, FR-AGT-12). Agents records the request and asks whatever keeps positions, through `IAgentExposure`. Nothing keeps positions yet, so no agent has one.

Agents says whether an agent of a user is active, for Accounts to ask before it deletes an account, and hands over the agents, the models of their roles, and the changes of their states for the export of a user's data. What a user does to an agent is recorded in the audit trail: a change to a risk setting, a start, a pause, a resume, a stop, a manual close, and a switch of its mode. Not built yet: the cycles and what keeps positions, what halts an agent, and recommending a team by measured strength. See [agents.md](agents.md).

## Ownership

The only account type is the individual user, and every resource belongs to exactly one (FR-ACC-5). A row that belongs to a user carries its owner. Whatever serves a request asks for the rows of the user who is signed in, so a row of another user is not found (NFR-SEC-3, NFR-DAT-9). The vocabulary for it, `IUserOwned` and `OwnedBy`, is in `Platform`, so that a component which may not use Accounts can still say whose its rows are.

Operator functions require a policy of their own, which Accounts registers: an operator is a user whose verified address the configuration file lists. Being an operator opens nothing that belongs to another user. There are no tenants, and the service is free, so there is no billing (FR-ACC-6). See [ownership.md](ownership.md).

## Security

All traffic between a browser and the application uses TLS: a request over plain HTTP is redirected and not served. Every cookie is sent only over TLS and is hidden from scripts (NFR-SEC-1).

The application defends against injection, cross-site scripting, request forgery, and clickjacking (NFR-SEC-4). Every answer tells the browser to run no script and apply no style that is not the application's own, and to show the application in no frame. A form is taken only with the token of a page that the application served. Two rules in `ComponentRules.cs` keep the rest in place: no code sends SQL that was put together as text, and no code writes text into a page as markup.

A secret that the application must read again is stored encrypted, with authenticated encryption under a master key of the configuration file, and with the id of that key beside it (NFR-SEC-5). The encryption is in `Platform`, because Accounts needs it and may not use Credentials, which needs it too, for the keys that users bring. The master key can be changed: a component declares each column that holds such a secret with `AddStoredSecret`, and when the application starts, the platform encrypts every value that an earlier key encrypted again with the active one (FR-KEY-7). `Platform` never references a component for it: it finds the declarations among the registered services. See [security.md](security.md).

## Background work

Scheduled work runs on one scheduler in `Platform`. A component implements `IBackgroundJob` and registers it; the scheduler keeps each job's latest run in the database, so the schedule goes on after a restart, and it runs jobs only while it holds a database lock, so they never run in two instances at once (NFR-ARC-4). `Platform` never references a component: it finds the jobs among the registered services. The jobs so far are that of Credentials, which checks the stored keys, that of Catalog, which syncs the model catalog, and that of the Exchange Gateway, which reads Binance's clocks. A job on an interval can also be due when the work starts, which is how the catalog is synced at startup when it is empty, and how the clocks are read at every start. See [background-work.md](background-work.md).

## Logging

Components write log events through the host's `ILogger<T>` and nothing else. One rule in `ComponentRules.cs` keeps it that way: only `Platform` may use the logging library, Serilog. So every event passes the one place that masks secrets before it reaches an output (NFR-LOG-4). See [logging.md](logging.md).

## Deployment guard

`DeploymentRuleTests` reads the application's package graph and fails if it contains a message broker client, a cache server client, a search server client, a proprietary cloud SDK, a client for a database other than PostgreSQL (NFR-DAT-1), or the SDK of a payment service, because the service is free (FR-ACC-6). The list is in [`ForbiddenInfrastructure.cs`](../tests/Ecnanib.Tests/Architecture/ForbiddenInfrastructure.cs). It also fails if the graph contains a log output other than the console and the file, so no log can be written to a database (NFR-DAT-7).
