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

`appsettings.json` holds the default connection string, without a password because git tracks that file. The app also needs a master key to encrypt secrets with, and won't start without one. Put your full connection string and a key in `src/Ecnanib/appsettings.Development.json`, which git ignores and which overrides the default. Generate the key with `openssl rand -base64 32`:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=ecnanib;Username=postgres;Password=<password>"
  },
  "Security": {
    "EncryptionKeys": [
      { "Id": "dev", "Key": "<output of openssl rand -base64 32>", "Active": true }
    ]
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

They need Docker, because they start their own PostgreSQL and SMTP server in containers. They never use the database or mail server in your settings files. They include the architecture tests that enforce the module boundaries in [docs/architecture.md](docs/architecture.md).

To add a migration, run `dotnet tool restore` once, then follow [Adding a migration](docs/architecture.md#adding-a-migration).

## Technology

| Concern | Choice | State |
|---|---|---|
| Web app | ASP.NET Core Blazor Server on .NET 10, interactive across the whole app | In place |
| Interface | Hand-written CSS with no component library, self-hosted fonts, Dark, Light and System themes, and all text in a resource file | In place. Users choose their theme, animation level and time zone |
| Database | PostgreSQL | In place |
| Persistence | Entity Framework Core 10 with Npgsql, code-first, with migrations applied at startup and no table partitioning | In place |
| Logging | Serilog, with console, rolling file and PostgreSQL sinks, and masking in front of all of them | In place |
| Health | ASP.NET Core health checks, on `/health/live` and `/health/ready` | In place |
| Web security | HTTPS and HSTS, content security policy, secure cookies and rate limiting, in the application itself | In place |
| Background jobs | An in-process runner, with its history and schedules in PostgreSQL | In place |
| Accounts | Email and password with Argon2id, optional two-factor with an authenticator app and recovery codes, sessions that users can list and revoke, password change and reset, lockout, recorded acceptance of the legal documents, verified addresses, and administrators from the configuration file | In place |
| Email | Optional. MailKit over SMTP with TLS required, sent in the background, for address verification, password reset links and notifications | In place |
| Notifications | In the app always, listed newest first with an unread count in the menu, and by email for the events each user chooses, batched each minute | In place. Each later feature raises its own |
| Secrets at rest | AES-256-GCM under master keys from the configuration file, re-encrypted in the background when the active key changes. ASP.NET Data Protection keys and two-factor keys live in PostgreSQL under the same keys | In place. Credentials will store their secrets through it |
| Audit trail | Security and trading actions in their own PostgreSQL table, which the database keeps append-only, with who, what, when, the client's address and device, and a correlation ID. Users see their own on their account | In place for accounts. Each later feature records its own actions |
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

Outside Development, the app refuses to start if anyone but the account it runs as can read or change a settings file, so restrict each one, for example with `chmod 600` (NFR-SEC-05).

### Encryption keys

`Security:EncryptionKeys` holds the master keys that encrypt users' secrets and the keys that protect cookies. It's required, and an invalid entry stops startup with a message that names it.

| Setting | Meaning |
|---|---|
| `Id` | Required. 1 to 32 letters, digits, dots, hyphens or underscores, such as `2026-11`. It's stored with every secret, so never reuse one for a different key |
| `Key` | Required. 32 random bytes in base64, from `openssl rand -base64 32`. The sample value from the SRS is refused |
| `Active` | `true` for exactly one key, which encrypts everything new. The others only decrypt. Defaults to `false` |

To rotate the master key:

1. Add a new entry with `"Active": true`, set the current key's `Active` to `false`, and restart.
2. The app re-encrypts every secret with the new key in the background, logging its progress, and keeps decrypting with the old key meanwhile.
3. When the log says every secret uses the new key, remove the old entry and restart. The app refuses to start without a key that secrets still use, and names it.

See [Secrets](docs/architecture.md#secrets) for how this works.

### Logging

The `Logging` section follows [SRS 7.3](SRS.md#73-example-placeholders-only), not the framework's `Logging:LogLevel`. Changes to it apply without a restart. A value that isn't valid stops startup, and on a running application it's refused with an error in the log, which leaves the previous settings in place.

| Setting | Meaning |
|---|---|
| `MinimumLevel` | Required. `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal` |
| `Overrides` | Logger name prefix to level, such as `"Microsoft.AspNetCore": "Warning"`. `Http` is the request log, `Exchange` logs each Binance call, and `AiCouncil` logs each AI call |
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

### Binance

The `Binance` section names the servers the app calls. Users can't supply their own (NFR-SEC-10). `appsettings.json` ships Binance's official addresses, and an invalid value stops startup with a message that names it.

| Setting | Meaning |
|---|---|
| `Spot:LiveBaseUrl`, `Spot:DemoBaseUrl` | Required. `https://api.binance.com` and `https://demo-api.binance.com` |
| `Futures:LiveBaseUrl`, `Futures:DemoBaseUrl` | Required. `https://fapi.binance.com` and `https://demo-fapi.binance.com` |
| `MarketDataBaseUrl` | Required. Public Spot Live data, `https://data-api.binance.vision` |
| `RecvWindowMs` | How long a signed request stays valid, 1 to 10000. Defaults to `5000` |
| `WeightHeadroomPercent` | The share of each server's request-weight limit kept for protective orders, 0 to 90. Defaults to `30` |
| `ExchangeInfoRefreshHours` | How long symbols and their filters are kept before they're fetched again, 1 to 168. Defaults to `24` |

Each URL is a server's address only, with no path, and must use https outside Development. Binance limits request weight per IP address, so every user shares the server's budget. If Binance bans the address from one of its servers, requests to that server stop until the ban ends, and administrators are notified. See [Exchange adapter](docs/architecture.md#exchange-adapter).

### AI providers

The `AiProviders` section has one entry per AI provider, under the provider's name, such as `AiProviders:OpenAI`. Users bring their own keys, but they can't choose the server (NFR-SEC-10). Each provider that has an adapter needs its entry, and an invalid value stops startup with a message that names it. An entry for a name with no adapter is ignored, with a warning.

| Setting | Meaning |
|---|---|
| `Enabled` | `true` or `false`. Defaults to `true`. When it's `false`, nothing else in the entry is read, and calls to that provider fail without being sent |
| `BaseUrl` | Required. The provider's API server, such as `https://api.openai.com` |
| `TimeoutSeconds` | How long one attempt may take, 1 to 600. Defaults to `120` |
| `MaxRetries` | Further attempts after a rate limit, overload, timeout, refusal or server error, 0 to 5. Defaults to `2` |
| `RetryBackoffMs` | The first wait before a retry, which doubles each time, 0 to 60000. Defaults to `1000`. A provider's own `Retry-After` takes its place |
| `MaxConcurrentPerCredential` | How many calls one user key may have in flight at once, 1 to 32. Defaults to `4` |

Each URL is a server's address only, with no path, and must use https outside Development. See [AI adapters](docs/architecture.md#ai-adapters).

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
| `Security:RateLimits:AuthenticationRequestsPerMinute` | Limit per IP address on sign-in, registration, the password, email and two-factor forms, and the administrator pages, on top of the above. It counts submissions, not page views. Defaults to 10 |

A value that isn't valid stops startup. Set `AllowedHosts` to your public host too, plus any host a local probe uses, because the shipped `*` accepts any `Host` header.

### Accounts

| Setting | Meaning |
|---|---|
| `Security:PasswordMinLength` | Shortest password allowed. It can't go below 12. Defaults to 12 |
| `Security:MaxFailedSignIns` | Failed attempts before an account locks, counting wrong two-factor codes at sign-in as well as wrong passwords. It also limits wrong codes for sensitive actions, which have a count of their own. Defaults to 5 |
| `Security:LockoutMinutes` | How long a lock lasts, and how long sensitive actions stop taking authenticator codes after too many wrong ones. Defaults to 15 |
| `Security:SessionIdleTimeoutMinutes` | How long a session can last without a request or a user action. A page that's only showing live data doesn't count. Defaults to 60 |
| `Security:SessionAbsoluteTimeoutHours` | The longest a session can last, however active it is. Defaults to 24 |
| `Admin:AdminEmails` | Addresses that get administrator rights once verified. Defaults to none |
| `Admin:BootstrapSecret` | At least 32 random characters. Required when an address is listed and `Email:Enabled` is false |
| `Email:Enabled` | Defaults to `false`. See [Email](#email) |
| `Legal:BlockedCountries` | ISO 3166-1 alpha-2 codes, such as `"GB"`, that can't register. Registration asks for a country only when this has entries. Defaults to none |
| `Ui:DefaultTheme` | `System`, `Light` or `Dark`, for users who haven't chosen. Visitors always get `System`. Defaults to `System` |
| `Ui:DefaultAnimation` | `System`, `Full`, `Reduced` or `Off`, for visitors and for users who haven't chosen. `System` follows the device's reduced motion setting. Defaults to `System` |

To make yourself an administrator with email off:

1. Add your address to `Admin:AdminEmails`, set `Admin:BootstrapSecret`, and restart.
2. Register that address as normal, and sign in.
3. On your account page, choose **Confirm you are an administrator**, and enter the secret.

Registering a listed address doesn't grant anything by itself, so a stranger who registers it first gets an ordinary account. If that happens, list a different address. Removing an address and restarting ends its rights at once.

With email on, the bootstrap secret isn't used. Register the listed address, sign in, and open the link that's emailed to it.

The Terms of Use and Risk Disclosure are placeholders until the [legal review](https://github.com/two-net/ecnanib/issues/58). Sign-in keeps its session key in ASP.NET Data Protection, so if that key store doesn't persist, each restart signs everyone out.

### Two-factor authentication

Users can turn on two-factor authentication from their account page, with any authenticator app that takes a QR code, such as 1Password, Google Authenticator or Microsoft Authenticator. Once it's on, signing in needs a code from the app, and so will adding or changing exchange keys, switching an agent to Live, the kill switch, and turning two-factor off. There's nothing to configure.

Turning it on gives the user 10 recovery codes, shown once. Each works once in place of a code from the app, which is how someone who loses their phone gets back in, and how they reach the kill switch if they've mistyped too many codes. There's no other way: an administrator can't turn it off for them, and a password reset leaves it on, so that a reset link alone can't take over an account. Someone who loses both their phone and their codes can't sign in. Tell users to keep the codes somewhere other than their phone.

The key is encrypted with the [master key](#encryption-keys), like any other secret, and rotating the master key re-encrypts it.

### Email

Email is optional (SRS 4.4). With it off, addresses aren't verified and nothing needs them to be, and users who forget their password ask an administrator for a link. With it on, users verify their address with an emailed link, and reset a forgotten password from the sign-in page.

```json
{
  "App": { "PublicBaseUrl": "https://ecnanib.example.com" },
  "Email": {
    "Enabled": true,
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "UseTls": true,
    "Username": "ecnanib",
    "Password": "<smtp password>",
    "FromAddress": "no-reply@example.com"
  }
}
```

| Setting | Meaning |
|---|---|
| `Email:Enabled` | Defaults to `false`. The rest of the section is only read when it's `true` |
| `Email:SmtpHost` | Required when enabled. A host name or IP address |
| `Email:SmtpPort` | Defaults to 587. On 465, TLS starts with the connection, and on any other port it starts with STARTTLS |
| `Email:UseTls` | Defaults to `true`, which refuses a server that doesn't offer TLS rather than sending in clear. `false` is only allowed in Development, or for a server on the same machine |
| `Email:Username`, `Email:Password` | Set both for a server that needs a sign-in, or neither |
| `Email:FromAddress` | Required when enabled. A bare address, such as `no-reply@example.com` |
| `App:PublicBaseUrl` | Required when enabled, in Development too, because every link in an email starts with it |

A value that isn't valid stops startup. The log says at startup whether email is on, and through which server. Each message is sent in the background and recorded in `job_runs` as `send-email`, so a failure shows there and in the log, which names neither the recipient nor the message. Nothing retries a failed message: the user asks again.

Links last 24 hours to verify an address, and an hour to reset a password. Each works once, and asking again replaces it. One account gets at most one email of each kind every 5 minutes, however often anyone asks.

#### Notification emails

Notifications always appear in the app. With email on, users choose on their preferences page which ones are also emailed. Until they choose, they get the ones that need them to act: a trade waiting for approval, a paused agent, a key that stopped working, a retired model and a used kill switch, plus model catalog sync failures and Binance rate-limit bans for administrators. Email only goes to verified addresses.

A job checks each minute and sends each user one email listing everything new, so a burst arrives as one message. It's recorded in `job_runs` as `notification-email`, and each message as `send-email`. A notification more than a day old isn't emailed, so restarting after an outage doesn't send yesterday's news. There's nothing to configure.

To try email locally, run [Mailpit](https://mailpit.axllent.org) with `docker run -p 1025:1025 -p 8025:8025 axllent/mailpit`, set `SmtpHost` to `localhost`, `SmtpPort` to `1025` and `UseTls` to `false`, and read the mail at `http://localhost:8025`.

#### Resetting a password with email off

An administrator issues the link:

1. On your account page, choose **Issue a password reset link for a user**.
2. Enter the user's address, and copy the link that appears. It works once, for 24 hours, and issuing another replaces it.
3. Give it to the user in a way you're sure reaches only them. Anyone with the link can take over the account until it's used.

Resetting signs the account out everywhere. A link from an administrator doesn't verify the address, so it can't make anyone an administrator.

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

If the proxy isn't on the same host, add its address to `Security:TrustedProxies`. **A redirect loop means it's missing:** the application isn't believing the proxy, so it thinks every request is plain HTTP. If you've worked around that some other way, every user will share one rate limit, and the audit trail will record the proxy's address for everyone.

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
