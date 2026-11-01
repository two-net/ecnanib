# Ecnanib

AI cryptocurrency trading agents. Ecnanib is a planned free, multi-user web application where users create agents that trade on Binance Spot and Binance USDⓈ-M Futures. Each agent is driven by a council of AI models from Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok), using the user's own API keys.

> **Status: early scaffold.** The repository currently contains an empty Blazor Server app and the requirements. None of the product features described below are implemented yet.

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

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From the repository root:

```sh
dotnet run --project src/Ecnanib
```

Then open <http://localhost:5057>. To serve over HTTPS as well, on <https://localhost:7272>:

```sh
dotnet run --project src/Ecnanib --launch-profile https
```

If the browser warns about the certificate, trust the local development certificate with `dotnet dev-certs https --trust`.

## Technology

| Concern | Choice | State |
|---|---|---|
| Web app | ASP.NET Core Blazor Server on .NET 10, interactive across the whole app | In place |
| Database | PostgreSQL | Required, not added yet |
| Persistence | Code-first ORM, with migrations applied at startup and no table partitioning | Required, not chosen yet |
| Logging | Structured logging library comparable to Serilog | Required, not chosen yet |

The architecture is a single deployable modular monolith, with no microservices, message brokers, distributed caches or separate job servers.

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
├── SRS.md               Software Requirements Specification (v1.0 draft)
└── src/Ecnanib/         Blazor Server application
    ├── Components/      Root component, routes, layout and pages
    ├── Properties/      Local launch profiles
    ├── wwwroot/         Static assets
    └── Program.cs       Application startup
```

## Documentation

[SRS.md](SRS.md) is the source of truth for requirements, including the risk rules, data model, acceptance criteria and open questions.
