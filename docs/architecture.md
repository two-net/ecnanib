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

Accounts sends its email this way. It declares `IAccountMailer`, and Notifications implements it on top of the email adapter. It asks about a user's agents this way too, before it deletes an account: it declares `IAgentActivity`, for Agents to implement. And it takes the parts of the export of a user's data that other components keep this way: it declares `IExportedPart`, which Credentials implements for the keys. See [accounts.md](accounts.md).

Credentials tells whatever depends on a key this way, when the key is deleted or turns out to be invalid: it declares `IKeyDependents`, for Agents to implement. See [credentials.md](credentials.md).

### Changing a rule

Edit `ComponentRules.cs`. The allow-lists are deliberately tight, so a new dependency is a decision made in one visible place. The tests reject a change that creates a cycle.

## Adapters

Each external system sits behind an adapter with a common interface.

| External system | Common interface | Adapters live in |
|---|---|---|
| Each AI provider | `IAiProviderAdapter` for calls to a model, and `IAiKeyChecker` for the check of a key | `AiProviderGateway/Adapters/<Provider>` |
| Binance | `IExchangeAdapter` | `ExchangeGateway/Adapters/Binance` |
| Email delivery | `IEmailAdapter` | `Notifications/Adapters/<Service>`. SMTP is in `Notifications/Adapters/Smtp`. |

Three rules keep every call to an external system inside its adapter, and in the log:

- A component's `Adapters` namespace is private to that component. Other code uses the common interface.
- Network client types (`System.Net.Http`, `System.Net.Mail`, `System.Net.WebSockets`, `System.Net.Sockets`, and the SMTP library `MailKit`) may be used only inside an `Adapters` namespace.
- No code may create an `HttpClient`. An adapter takes one from the host's factory (a typed or named client, or `IHttpClientFactory`), and every client from the factory logs its calls (NFR-LOG-5). See [logging.md](logging.md).

What an adapter learns from the network leaves it as a type of its own component, because nothing outside an `Adapters` namespace may name a network type, an error of the network included. The checks of a key answer with a `KeyVerdict`, and a provider that does not answer is one of its values. A call to a model answers with an `AiCallOutcome` in the same way: a provider that refuses, fails, or takes too long is an outcome of the call, and not an error.

The adapters of the AI providers make the calls to a model, and the calls that validate a key, which ask for the list of a provider's models: see [ai-providers.md](ai-providers.md). The adapter of Binance so far makes the calls that validate a key: the permissions of a key, with the clock of each market. The orders are not built. A gateway makes all its calls with one named client, `AiProviderGatewayModule.HttpClientName` or `ExchangeGatewayModule.HttpClientName`, so a test puts a handler of its own behind that name in place of the network.

### Adding an AI provider

1. Add a class that implements `IAiProviderAdapter` under `AiProviderGateway/Adapters/<Provider>`. `ModelCall` makes the exchange, which is the same for every provider. The adapter says what is the provider's own, as an `IModelCallFormat`: the path, the header of the key, the body with the schema where the provider takes it, how an answer reads, and which of its errors is the one to a wrong key or to a model it does not have.
2. Add a class that implements `IAiKeyChecker` beside it: the call that says whether the provider takes a key.
3. Register both in `AddAiProviderGateway()`.
4. Add its entry under `AiProviders` in the configuration file, with its address and its time limit, as [configuration.md](configuration.md) describes.
5. Add its row to the seed data in `AiProviderGateway/AiProvider.cs` and generate a migration, as [persistence.md](persistence.md) describes.
6. Add its code to the tests of the gateway, and run them against the provider itself once: see "Checking against the real providers" in [ai-providers.md](ai-providers.md).

Nothing else changes. Other components call `IAiProviderGateway` and `IAiKeyChecks`, and name the provider by its `AiProviderCode`. Providers are an open set of codes, not an enum, so no caller can branch over them.

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

Adding or replacing a key is the second of the sensitive actions to be built, and asks Accounts for the proof first. Every change to a key is recorded in the audit trail, by the id of the key. Deleting a key tells whatever depends on it inside the same transaction, which is how an agent will move to Attention required.

A key is validated with a read-only call to its provider, through the gateways: before it is stored, daily by a job, and when a call with it fails authentication (FR-KEY-3). Only a key that its provider takes is stored, and a stored key that is refused later becomes invalid, which whatever depends on it is told as well. A Binance key that can withdraw is not stored, and one that turns out to be able to is deleted (FR-KEY-4, NFR-LEG-2). What a Binance key may trade on is recorded, and Credentials says whether it lets an agent start on a market, for Agents to ask (FR-KEY-5). Not built yet: handing a key to the call of an agent that needs it. See [credentials.md](credentials.md).

## AI providers

Every call to a model is made through the AI Provider Gateway, with one adapter each for Claude, Gemini, GPT, and Grok. A call takes prompt text and a result schema, and returns the structured result, its token usage, and its latency. The prompt is the same for every provider, and only how the structured result is asked for is a provider's own (FR-PRM-10). Where a call goes and how long it may take come from the configuration file.

The gateway takes the user's key with each call and keeps it nowhere (FR-KEY-9). A call that gives no result ends as an outcome, and not as an error: a timeout, an answer that is no result, a failed authentication, a model that the provider does not have, or a failure. What follows from each is for the caller. Not built yet: what calls the gateway for an agent. See [ai-providers.md](ai-providers.md).

## Ownership

The only account type is the individual user, and every resource belongs to exactly one (FR-ACC-5). A row that belongs to a user carries its owner. Whatever serves a request asks for the rows of the user who is signed in, so a row of another user is not found (NFR-SEC-3, NFR-DAT-9). The vocabulary for it, `IUserOwned` and `OwnedBy`, is in `Platform`, so that a component which may not use Accounts can still say whose its rows are.

Operator functions require a policy of their own, which Accounts registers: an operator is a user whose verified address the configuration file lists. Being an operator opens nothing that belongs to another user. There are no tenants, and the service is free, so there is no billing (FR-ACC-6). See [ownership.md](ownership.md).

## Security

All traffic between a browser and the application uses TLS: a request over plain HTTP is redirected and not served. Every cookie is sent only over TLS and is hidden from scripts (NFR-SEC-1).

The application defends against injection, cross-site scripting, request forgery, and clickjacking (NFR-SEC-4). Every answer tells the browser to run no script and apply no style that is not the application's own, and to show the application in no frame. A form is taken only with the token of a page that the application served. Two rules in `ComponentRules.cs` keep the rest in place: no code sends SQL that was put together as text, and no code writes text into a page as markup.

A secret that the application must read again is stored encrypted, with authenticated encryption under a master key of the configuration file, and with the id of that key beside it (NFR-SEC-5). The encryption is in `Platform`, because Accounts needs it and may not use Credentials, which needs it too, for the keys that users bring. The master key can be changed: a component declares each column that holds such a secret with `AddStoredSecret`, and when the application starts, the platform encrypts every value that an earlier key encrypted again with the active one (FR-KEY-7). `Platform` never references a component for it: it finds the declarations among the registered services. See [security.md](security.md).

## Background work

Scheduled work runs on one scheduler in `Platform`. A component implements `IBackgroundJob` and registers it; the scheduler keeps each job's latest run in the database, so the schedule goes on after a restart, and it runs jobs only while it holds a database lock, so they never run in two instances at once (NFR-ARC-4). `Platform` never references a component: it finds the jobs among the registered services. The first job is that of Credentials, which checks the stored keys. See [background-work.md](background-work.md).

## Logging

Components write log events through the host's `ILogger<T>` and nothing else. One rule in `ComponentRules.cs` keeps it that way: only `Platform` may use the logging library, Serilog. So every event passes the one place that masks secrets before it reaches an output (NFR-LOG-4). See [logging.md](logging.md).

## Deployment guard

`DeploymentRuleTests` reads the application's package graph and fails if it contains a message broker client, a cache server client, a search server client, a proprietary cloud SDK, a client for a database other than PostgreSQL (NFR-DAT-1), or the SDK of a payment service, because the service is free (FR-ACC-6). The list is in [`ForbiddenInfrastructure.cs`](../tests/Ecnanib.Tests/Architecture/ForbiddenInfrastructure.cs). It also fails if the graph contains a log output other than the console and the file, so no log can be written to a database (NFR-DAT-7).
