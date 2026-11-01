# Ecnanib

AI cryptocurrency trading agents. Ecnanib is a planned free, multi-user web application where users create agents that trade on Binance Spot and Binance USDⓈ-M Futures. Each agent is driven by a council of AI models from Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok), using the user's own API keys.

> **Status: early scaffold.** The repository currently contains the requirements and a Blazor Server app with mostly empty modules, PostgreSQL persistence, tests and a CI pipeline. None of the product features described below are implemented yet.

> **Disclaimer.** Ecnanib is not affiliated with or endorsed by Binance. Nothing it produces is financial advice. Automated cryptocurrency trading can lose money, including through AI decisions that look reasonable.

## Planned features

- **Trading agents:** each agent has one trading pair, a market (Spot or Futures), a timeframe (15m, 1H, 4H or 1D) and an environment (Demo or Live).
- **AI council:** models collaborate in distinct roles, with prompts that vary by role, market and timeframe.
- **Bring your own keys:** AI provider and Binance credentials are encrypted at rest. There is no billing and there are no platform-provided keys.
- **Risk engine:** deterministic rules validate and size every order.
- **Model catalog:** available models are synced from each provider.
- **Trading experience:** performance is tracked per provider, model and role, and lessons from closed trades feed back into prompts.
- **Web UI:** live, animated and responsive, with Dark, Light and System themes.

## How an agent decides

A decision cycle runs each time a candle of the agent's timeframe closes:

```text
Reconcile with Binance
  -> Collect market data and compute indicators
  -> Technical Analyst + Sentiment Analyst (in parallel)
  -> Strategist proposes an action, or HOLD
  -> Risk Critic approves, modifies or rejects
  -> Risk Engine sizes the order and enforces hard limits
  -> Execute (Autonomous mode) or wait for approval (Confirm mode)
```

| Role | Responsibility | Default provider |
|---|---|---|
| Technical Analyst | Analyzes price action, indicators and higher-timeframe structure | Google Gemini |
| Sentiment Analyst | Assesses current news and social sentiment | xAI Grok |
| Strategist | Combines the analyses into a trade proposal | OpenAI GPT |
| Risk Critic | Challenges the proposal | Anthropic Claude |
| Reviewer | Writes a lesson after each trade closes | Google Gemini |

Users can change the provider and model per role, per agent. A single provider can fill every role.

## Design principles

1. **AI proposes, rules decide.** No order reaches the exchange without passing the deterministic Risk Engine.
2. **Protection lives on the exchange.** Every open position has a stop resting on Binance, so an Ecnanib outage never leaves it unprotected.
3. **Demo first.** New agents start in Demo, and Live requires explicit opt-in.
4. **Full transparency.** Every decision is traceable through its inputs, prompts, responses, risk checks and orders.
5. **Least privilege.** Binance keys with withdrawal permission are rejected.
6. **Simplicity.** One process, one database, one configuration file.
7. **Provider neutrality.** Default role assignments are hypotheses for Ecnanib's own trading data to confirm or overturn.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version 10.0.402 or later, and a PostgreSQL server on a currently supported major version.

`appsettings.json` holds the default connection string, without a password because git tracks that file. Put your full connection string in `src/Ecnanib/appsettings.Development.json`, which git ignores and which overrides the default:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=ecnanib;Username=postgres;Password=<password>"
  }
}
```

The app creates the database, schema and seed data on first start, and applies new migrations on every later start. If the database doesn't exist yet, the PostgreSQL role needs permission to create it. If a migration fails, the app logs the error and exits.

From the repository root:

```sh
dotnet run --project src/Ecnanib
```

Then open <http://localhost:5057>. To serve over HTTPS as well, on <https://localhost:7272>:

```sh
dotnet run --project src/Ecnanib --launch-profile https
```

If the browser warns about the certificate, trust the local development certificate with `dotnet dev-certs https --trust`.

To run the tests:

```sh
dotnet test
```

They need Docker, because they start their own PostgreSQL in a container. They never use the database in your settings files. They include the architecture tests that enforce the module boundaries in [docs/architecture.md](docs/architecture.md).

To add a migration, run `dotnet tool restore` once, then follow [Adding a migration](docs/architecture.md#adding-a-migration).

## Technology

| Concern | Choice | State |
|---|---|---|
| Web app | ASP.NET Core Blazor Server on .NET 10, interactive across the whole app | In place |
| Database | PostgreSQL | In place |
| Persistence | Entity Framework Core 10 with Npgsql, code-first, with migrations applied at startup and no table partitioning | In place |
| Logging | Structured logging library comparable to Serilog | Required, not chosen yet |
| Tests | xUnit v3 on Microsoft.Testing.Platform, with ArchUnitNET for module boundaries and Testcontainers for PostgreSQL | In place |
| Build pipeline | GitHub Actions: build, test and dependency vulnerability scan | In place |

The architecture is a single deployable modular monolith, with no microservices, message brokers, distributed caches or separate job servers. Modules live in one project and talk through defined interfaces. See [docs/architecture.md](docs/architecture.md) for the boundaries and rules.

Every `dotnet restore` audits direct and transitive packages for known vulnerabilities. CI fails on an advisory of any severity, and on any compiler warning. It also re-scans weekly, because new advisories appear without code changes.

## Configuration

The target design reads all configuration from one `appsettings.json`, which will hold secrets such as encryption keys. It uses no environment variables and no external vault. See [Section 7 of the SRS](SRS.md#7-configuration-file) for the sections and a placeholder example.

The scaffold doesn't meet this yet:

- `.gitignore` doesn't exclude `appsettings.json`. **Add that rule before putting any secrets in the file.**
- There is no `appsettings.example.json` yet.
- `appsettings.Development.json` is a second configuration file.
- `launchSettings.json` sets `ASPNETCORE_ENVIRONMENT`, and the default host builder in `Program.cs` reads environment variables.

## Repository layout

```text
.
├── SRS.md                  Software Requirements Specification (v1.0 draft)
├── docs/architecture.md    Module boundaries, persistence and rules
├── Directory.Build.props   Shared build settings and vulnerability scan rules
├── .config/                Pinned .NET tools, including dotnet-ef
├── .github/workflows/      CI pipeline
├── src/Ecnanib/            Blazor Server application
│   ├── Components/         Root component, routes, layout and pages
│   ├── Modules/            One folder per module, each with a public Contracts namespace
│   ├── Platform/           Cross-cutting services, including persistence and migrations
│   ├── Properties/         Local launch profiles
│   ├── wwwroot/            Static assets
│   └── Program.cs          Application startup and module registration
└── tests/Ecnanib.Tests/    Architecture, persistence and smoke tests
```

## Documentation

[SRS.md](SRS.md) is the source of truth for requirements, including the risk rules, data model, acceptance criteria and open questions. [docs/architecture.md](docs/architecture.md) maps its logical architecture onto the code.
