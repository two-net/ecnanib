# Architecture

Ecnanib is one deployable application. A single process serves the UI, exposes the API, and runs the scheduler and background work. The only other thing to deploy is one PostgreSQL database. There are no microservices, message brokers, separate cache servers, or search servers, and nothing depends on a proprietary cloud service (NFR-ARC-1, -2, -5).

The rules below are enforced by tests in `tests/Ecnanib.Tests/Architecture`. Run them with:

```
dotnet test --solution Ecnanib.slnx
```

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
- `Platform` holds the plumbing every component builds on. Today that is configuration: it reads the one configuration file into typed settings, as [configuration.md](configuration.md) describes. Persistence, logging, and background work are reserved for it. It may depend only on `Shared`.
- `Api` holds the HTTP endpoints under `/api`. Like the Web UI, it is an entry point on top of the components.

Each component has a `<Name>Module.cs` with an `Add<Name>()` method that registers its services. `Program.cs` is the composition root: it creates the host with `PlatformModule.CreateBuilder`, calls every `Add<Name>()`, and maps the API and the UI. A component that runs scheduled or background work registers a hosted service in its `Add<Name>()`, so the work runs in the same process.

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

### Changing a rule

Edit `ComponentRules.cs`. The allow-lists are deliberately tight, so a new dependency is a decision made in one visible place. The tests reject a change that creates a cycle.

## Adapters

Each external system sits behind an adapter with a common interface.

| External system | Common interface | Adapters live in |
|---|---|---|
| Each AI provider | `IAiProviderAdapter` | `AiProviderGateway/Adapters/<Provider>` |
| Binance | `IExchangeAdapter` | `ExchangeGateway/Adapters/Binance` |
| Email delivery | `IEmailAdapter` | `Notifications/Adapters/<Service>` |

Two rules keep every call to an external system inside its adapter:

- A component's `Adapters` namespace is private to that component. Other code uses the common interface.
- Network client types (`System.Net.Http`, `System.Net.Mail`, `System.Net.WebSockets`, `System.Net.Sockets`) may be used only inside an `Adapters` namespace.

### Adding an AI provider

1. Add a class that implements `IAiProviderAdapter` under `AiProviderGateway/Adapters/<Provider>`.
2. Register it in `AddAiProviderGateway()`.

Nothing else changes. Other components call `IAiProviderGateway` and name the provider by its `AiProviderCode`. Providers are an open set of codes, not an enum, so no caller can branch over them.

### Placing orders

Operations that place orders belong in the namespace `Ecnanib.ExchangeGateway.Trading`. Only the Decision Engine may use it, which keeps one path from a model's output to an order: schema validation, then the Risk Guard, then the Exchange Gateway (NFR-SEC-6). The Web UI and the other components may use the rest of the Exchange Gateway, which only reads.

## Configuration

Settings come from one file and reach a component as typed settings. Two rules in `ComponentRules.cs` keep it that way: only `Platform` may use `Microsoft.Extensions.Configuration`, and no code may read the process environment or the command line directly. See [configuration.md](configuration.md).

## Deployment guard

`DeploymentRuleTests` reads the application's package graph and fails if it contains a message broker client, a cache server client, a search server client, or a proprietary cloud SDK. The list is in [`ForbiddenInfrastructure.cs`](../tests/Ecnanib.Tests/Architecture/ForbiddenInfrastructure.cs).
