# Ecnanib

AI cryptocurrency trading agents. Ecnanib is a planned free, multi-user web application where users create agents that trade on Binance Spot and Binance USDⓈ-M Futures. Each agent is driven by a council of AI models from Anthropic (Claude), Google (Gemini), OpenAI (GPT) and xAI (Grok), using the user's own API keys.

> **Status: early scaffold.** The repository currently contains the requirements and a Blazor Server app with a landing page, mostly empty modules, PostgreSQL persistence, tests and a CI pipeline. None of the product features described below are implemented yet.

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
| Interface | Hand-written CSS with no component library, self-hosted fonts, a System theme, and all text in a resource file | In place |
| Database | PostgreSQL | In place |
| Persistence | Entity Framework Core 10 with Npgsql, code-first, with migrations applied at startup and no table partitioning | In place |
| Logging | Serilog, with console, rolling file and PostgreSQL sinks, and masking in front of all of them | In place |
| Health | ASP.NET Core health checks, on `/health/live` and `/health/ready` | In place |
| Web security | HTTPS and HSTS, content security policy, secure cookies and rate limiting, in the application itself | In place |
| Background jobs | An in-process runner, with its history and schedules in PostgreSQL | In place |
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

### Logging

The `Logging` section follows [SRS 7.3](SRS.md#73-example-placeholders-only), not the framework's `Logging:LogLevel`. Changes to it apply without a restart. A value that isn't valid stops startup, and on a running application it's refused with an error in the log, which leaves the previous settings in place.

| Setting | Meaning |
|---|---|
| `MinimumLevel` | Required. `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal` |
| `Overrides` | Logger name prefix to level, such as `"Microsoft.AspNetCore": "Warning"`. `Http` is the request log |
| `Sinks:Console:Enabled` | Defaults to `true` |
| `Sinks:File:Enabled` | Defaults to `false` |
| `Sinks:File:Path` | Required when enabled. Relative paths start at the content root |
| `Sinks:File:RollingInterval` | `Day` by default, or `Size` |
| `Sinks:File:FileSizeLimitBytes` | The size a file rolls at, under either interval. Defaults to 1 GiB |
| `Sinks:File:RetainedFileCount` | Required when enabled |
| `Sinks:Database:Enabled` | Defaults to `false`. Writes to the `log_events` table |
| `Sinks:Database:MinimumLevel` | Defaults to `Warning` |

The file sink writes one JSON event per line, with every property. The console shows timestamp, level, category and message.

A few framework categories can't be set below a fixed level, because their debug events can carry form input or raw URLs. See [Masking](docs/architecture.md#masking).

Every response carries an `X-Correlation-ID` header, and each log event from that request holds the same value as `CorrelationId`. To trace a request, search the log files for it, or `log_events.correlation_id` in the database. A value sent in a request is ignored.

## Security

Outside Development the application won't start until it knows its public address. It must use `https`:

```json
{
  "App": {
    "PublicBaseUrl": "https://ecnanib.example.com"
  }
}
```

| Setting | Meaning |
|---|---|
| `App:PublicBaseUrl` | Required outside Development. Sets where plain HTTP redirects to, and the only origin allowed to open a WebSocket |
| `Security:TrustedProxies` | IP addresses or CIDR networks, such as `"10.0.0.0/8"`, whose `X-Forwarded-*` headers are believed. Loopback is always trusted. Defaults to none |
| `Security:RateLimits:RequestsPerMinute` | Limit per IP address, and separately per account. Defaults to 300 |
| `Security:RateLimits:AuthenticationRequestsPerMinute` | Limit per IP address on sign-in, on top of the above. Defaults to 10 |

A value that isn't valid stops startup. Set `AllowedHosts` to your public host too, plus any host a local probe uses, because the shipped `*` accepts any `Host` header.

### Behind a reverse proxy

TLS can end at a reverse proxy. The proxy has to pass WebSocket upgrades and say who the client is. With nginx:

```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection $connection_upgrade;
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
```

If the proxy isn't on the same host, add its address to `Security:TrustedProxies`. **A redirect loop means it's missing:** the application isn't believing the proxy, so it thinks every request is plain HTTP. If you've worked around that some other way, every user will share one rate limit.

### Rate limits

A request over the limit gets a 429 with a `Retry-After` header. A page load costs about 4 requests, and moving between pages costs none. Health endpoints and static files aren't limited. Limits are held in memory, so a restart clears them.

## Health

Neither endpoint needs a sign-in, so block them at your reverse proxy if they shouldn't be public.

| Endpoint | Meaning | Answers |
|---|---|---|
| `/health/live` | The process is running. It doesn't touch the database, so an outage can't cause a restart | Always 200 |
| `/health/ready` | The application can serve requests | 200 when ready, otherwise 503 |

```json
{"status":"Unhealthy","database":"connected","migrations":"pending"}
```

| Field | Values |
|---|---|
| `status` | `Healthy` or `Unhealthy` |
| `database` | `connected` or `unreachable` |
| `migrations` | `current`, `pending`, `ahead` (the database has a migration this build doesn't include) or `unknown` |

The reason for a failure is in the logs, not the response. A readiness probe answers within about 4 seconds, even if the database has stopped responding.

These endpoints answer on plain HTTP as well as HTTPS, so a probe can reach the application directly behind a reverse proxy. They're the only ones that do: anything else on plain HTTP gets a 307 redirect. They're also never rate limited.

While migrations run at startup the port isn't open yet, so a probe gets connection refused rather than a 503.

## Background jobs

Scheduled and background work runs inside the application, so there's no job server to deploy. Each run is a row in the `job_runs` table:

| Status | Meaning |
|---|---|
| `Running` | In progress |
| `Succeeded` | Finished |
| `Failed` | Threw an exception. `details` has its type |
| `TimedOut` | Passed its time limit |
| `Interrupted` | A shutdown or crash ended it before it finished |

`details` never holds the error text. To see that, search the logs for the row's `correlation_id`.

**Stopping takes up to 45 seconds.** A job finishing an exchange call gets 30 seconds, and the rest is for writing down what happened. Give your process supervisor a stop timeout of at least 60 seconds, which includes the final log flush. systemd's default of 90 is enough, but Docker's 10 and Kubernetes' 30 aren't. A process killed before then behaves as if it had crashed: the next start marks its unfinished runs `Interrupted`.

Schedules are read from `job_runs`, so restarting doesn't reset them. See [Jobs](docs/architecture.md#jobs).

## Repository layout

```text
.
├── SRS.md                  Software Requirements Specification (v1.0 draft)
├── docs/architecture.md    Module boundaries, persistence, logging, health, jobs, web security and rules
├── Directory.Build.props   Shared build settings and vulnerability scan rules
├── .config/                Pinned .NET tools, including dotnet-ef
├── .github/workflows/      CI pipeline
├── src/Ecnanib/            Blazor Server application
│   ├── Components/         Root component, routes, layout, notices, pages and interface text
│   ├── Endpoints/          HTTP that isn't a page, such as the health endpoints and the security pipeline
│   ├── Modules/            One folder per module, each with a public Contracts namespace
│   ├── Platform/           Cross-cutting services, including logging, correlation, jobs, persistence and migrations
│   ├── Properties/         Local launch profiles
│   ├── wwwroot/            Static assets
│   └── Program.cs          Application startup and module registration
└── tests/Ecnanib.Tests/    Architecture, persistence, logging, health, job, security, UI and smoke tests
```

## Documentation

[SRS.md](SRS.md) is the source of truth for requirements, including the risk rules, data model, acceptance criteria and open questions. [docs/architecture.md](docs/architecture.md) maps its logical architecture onto the code.
