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
- `Platform` holds the plumbing every component builds on. That is configuration, logging, persistence, background work, and how requests travel: it reads the one configuration file into typed settings, as [configuration.md](configuration.md) describes, it sets up structured logging from that file, as [logging.md](logging.md) describes, it holds the one database context and its migrations, as [persistence.md](persistence.md) describes, it runs the components' scheduled jobs, as [background-work.md](background-work.md) describes, and it sets the rules for TLS and cookies and encrypts the secrets that a component stores, as [security.md](security.md) describes. It may depend only on `Shared`.
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

Accounts sends its email this way. It declares `IAccountMailer`, and Notifications implements it on top of the email adapter. See [accounts.md](accounts.md).

### Changing a rule

Edit `ComponentRules.cs`. The allow-lists are deliberately tight, so a new dependency is a decision made in one visible place. The tests reject a change that creates a cycle.

## Adapters

Each external system sits behind an adapter with a common interface.

| External system | Common interface | Adapters live in |
|---|---|---|
| Each AI provider | `IAiProviderAdapter` | `AiProviderGateway/Adapters/<Provider>` |
| Binance | `IExchangeAdapter` | `ExchangeGateway/Adapters/Binance` |
| Email delivery | `IEmailAdapter` | `Notifications/Adapters/<Service>`. SMTP is in `Notifications/Adapters/Smtp`. |

Three rules keep every call to an external system inside its adapter, and in the log:

- A component's `Adapters` namespace is private to that component. Other code uses the common interface.
- Network client types (`System.Net.Http`, `System.Net.Mail`, `System.Net.WebSockets`, `System.Net.Sockets`, and the SMTP library `MailKit`) may be used only inside an `Adapters` namespace.
- No code may create an `HttpClient`. An adapter takes one from the host's factory (a typed or named client, or `IHttpClientFactory`), and every client from the factory logs its calls (NFR-LOG-5). See [logging.md](logging.md).

### Adding an AI provider

1. Add a class that implements `IAiProviderAdapter` under `AiProviderGateway/Adapters/<Provider>`.
2. Register it in `AddAiProviderGateway()`.
3. Add its row to the seed data in `AiProviderGateway/AiProvider.cs` and generate a migration, as [persistence.md](persistence.md) describes.

Nothing else changes. Other components call `IAiProviderGateway` and name the provider by its `AiProviderCode`. Providers are an open set of codes, not an enum, so no caller can branch over them.

### Placing orders

Operations that place orders belong in the namespace `Ecnanib.ExchangeGateway.Trading`. Only the Decision Engine may use it, which keeps one path from a model's output to an order: schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6). The Web UI and the other components may use the rest of the Exchange Gateway, which only reads.

## Configuration

Settings come from one file and reach a component as typed settings. Two rules in `ComponentRules.cs` keep it that way: only `Platform` may use `Microsoft.Extensions.Configuration`, and no code may read the process environment or the command line directly. See [configuration.md](configuration.md).

## Persistence

The application has one database, PostgreSQL, and reaches it through one context, `EcnanibDbContext` in `Platform`. An entity and its mapping live in the component that owns them, and the context finds every mapping by itself, so `Platform` never references a component. One rule in `ComponentRules.cs` keeps the context the only way in: only `Platform` may use the database driver, Npgsql (NFR-DAT-1, -2). See [persistence.md](persistence.md).

## Accounts

A visitor registers with an email address and a password, accepts the legal documents, and verifies the address by email. Passwords are stored only as Argon2id hashes. Whatever creates an agent asks Accounts first whether the user may, which is where the verified address and the fair-use limit are checked (FR-ACC-1, FR-ACC-7).

A user signs in with the address and the password, signs out, and resets a forgotten password by email (FR-ACC-2). A browser that is signed in has a session in the database, named by a cookie, which ends after a period of inactivity. Accounts registers the application's one authentication scheme, so the Web UI and the API both know the signed-in user, and a page says that it needs one with `[Authorize]`.

A user can turn on two-factor authentication with an authenticator app. Signing in then takes a code from the app after the password, and no session starts without one (FR-ACC-3). The key that the account shares with the app is stored encrypted.

Being signed in is not enough for an action that exposes keys or money: adding or replacing a key, switching an agent to Live, and deleting the account. Whatever does one asks Accounts first whether the user has just proved again who they are, with their password and, when they have it on, a code (FR-ACC-4).

Accounts also holds the audit trail: the record in the database, apart from the logs, of security- and money-relevant actions (NFR-LOG-7). An event is written in the transaction of the action it records. The first events are the changes to two-factor authentication. See [accounts.md](accounts.md).

## Ownership

The only account type is the individual user, and every resource belongs to exactly one (FR-ACC-5). A row that belongs to a user carries its owner. Whatever serves a request asks for the rows of the user who is signed in, so a row of another user is not found (NFR-SEC-3, NFR-DAT-9). The vocabulary for it, `IUserOwned` and `OwnedBy`, is in `Platform`, so that a component which may not use Accounts can still say whose its rows are.

Operator functions require a policy of their own, which Accounts registers: an operator is a user whose verified address the configuration file lists. Being an operator opens nothing that belongs to another user. There are no tenants, and the service is free, so there is no billing (FR-ACC-6). See [ownership.md](ownership.md).

## Security

All traffic between a browser and the application uses TLS: a request over plain HTTP is redirected and not served. Every cookie is sent only over TLS and is hidden from scripts (NFR-SEC-1).

A secret that the application must read again is stored encrypted, with authenticated encryption under a master key of the configuration file, and with the id of that key beside it (NFR-SEC-5). The encryption is in `Platform`, because Accounts needs it and may not use Credentials, which will need it too. See [security.md](security.md).

## Background work

Scheduled work runs on one scheduler in `Platform`. A component implements `IBackgroundJob` and registers it; the scheduler keeps each job's latest run in the database, so the schedule goes on after a restart, and it runs jobs only while it holds a database lock, so they never run in two instances at once (NFR-ARC-4). `Platform` never references a component: it finds the jobs among the registered services. See [background-work.md](background-work.md).

## Logging

Components write log events through the host's `ILogger<T>` and nothing else. One rule in `ComponentRules.cs` keeps it that way: only `Platform` may use the logging library, Serilog. So every event passes the one place that masks secrets before it reaches an output (NFR-LOG-4). See [logging.md](logging.md).

## Deployment guard

`DeploymentRuleTests` reads the application's package graph and fails if it contains a message broker client, a cache server client, a search server client, a proprietary cloud SDK, a client for a database other than PostgreSQL (NFR-DAT-1), or the SDK of a payment service, because the service is free (FR-ACC-6). The list is in [`ForbiddenInfrastructure.cs`](../tests/Ecnanib.Tests/Architecture/ForbiddenInfrastructure.cs). It also fails if the graph contains a log output other than the console and the file, so no log can be written to a database (NFR-DAT-7).
