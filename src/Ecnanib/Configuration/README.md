# Configuration module

The configuration files, the host built from them and the request pipeline (SRS §3.2 FR-CFG-01..08; Appendix D; C-11;
A-01; NFR-ARCH-02; NFR-SEC-02/04). `Program.cs` depends on `EcnanibHost`, `AddEcnanibModules(...)`, `EnsureSystemKeys()`
and `UseEcnanibPipeline()`; other modules depend on `ServerOptions` and `HostNames` only. The module references the
others in two places only: `AddEcnanibModules`, which registers every module in the order their defaults and replacements
require, and the pipeline, which maps the root component, the accounts module's sign-out and the health module's `/health`
(the section names in `ConfigurationFile.Sections` are literals, pinned to the modules' constants by a test). Every module
owns its own section, binds it in its own `Add*` method and is listed here by name. The root `README.md` is the
architecture overview: the SRS modules mapped to the folders, the dependency rule and every job the process runs.

| Need | Use |
|---|---|
| Where the settings are (FR-CFG-01) | the ASP.NET Core standard: `appsettings.json` in the working directory (the content root), `appsettings.{Environment}.json` over it (`ASPNETCORE_ENVIRONMENT`; `Development` under the launch profile), the framework's per-application pair `Ecnanib.settings.json` and `Ecnanib.settings.{Environment}.json` over those, then environment variables (`Database__ConnectionString=…`), then the command line (`--Database:ConnectionString=…`); a later source wins |
| What is read (FR-CFG-02) | those sources and nothing else: `EcnanibHost.CreateBuilder` is `WebApplication.CreateBuilder(args)` plus the checks below; it adds no source of its own and there is no `--config`. Both log sinks are the logging module's `AddLogSinks`, which removes the console, debug and event-source providers the default builder registers |
| Add a section for a new module | an `XOptions` with `SectionName` and an `IValidateOptions<XOptions>`, registered as `AddOptions<XOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` in `AddX`; the name in `ConfigurationFile.Sections`; the defaults in `appsettings.json`; a row in the table below |
| Where the application listens | the framework's `Kestrel` section: `Kestrel:Endpoints:Http:Url`, `http://127.0.0.1:5207` in the reference file (`http://0.0.0.0:5207` or `http://[::]:5207` for every interface), plain HTTP. Without a `Kestrel` section `ASPNETCORE_URLS` or `--urls` decide, and with neither Kestrel's own default applies; when both are set the section wins and Kestrel logs that it overrode the urls |
| The public origin | `Server:PublicUrl` is the origin users reach, normally a reverse proxy that terminates TLS; it must be https unless its host is localhost or a loopback address (NFR-SEC-02) |
| Behind a reverse proxy | `Server:TrustedProxies`: the proxies (IP addresses or CIDR networks) whose `X-Forwarded-For` and `X-Forwarded-Proto` are believed; loopback always is, so a same-host proxy needs no entry |
| Cookies, HSTS and redirection | with an https `Server:PublicUrl` the session and antiforgery cookies carry the Secure flag, every request Kestrel sees as plain http is redirected to the same path on the public origin (except `/health`, the health module's anonymous document, which a proxy's upstream check reaches over plain http) and `Strict-Transport-Security` (one year) is sent; with a loopback http public URL none of that happens |
| Register every module | `services.AddEcnanibModules(configuration)`, in `Program.cs` after `AddLogSinks` (C-08) and in the tests' in-memory server |
| The request pipeline | `app.UseEcnanibPipeline()` in `Program.cs`: forwarded headers, error pages, redirection and HSTS, the security headers, authentication, authorization, antiforgery and the endpoints, in that order; the tests run the same method on an in-memory server |
| The system's own keys (FR-CFG-05) | `EnsureSystemKeys()` after `MigrateDatabaseAsync()`: the Data Protection key ring that protects session tickets and antiforgery tokens is created on the first start and persisted in `data_protection_keys`; then the database module's `ProtectStoredKeysAsync()` proves `Database:EncryptionKey` against its check value in `system_keys` and encrypts or re-encrypts the users' stored keys as needed (FR-KEY-04) |
| Apply a change (FR-CFG-07) | restart; the framework watches the files, but every setting is bound once into `IOptions<T>` when the process starts |
| What a bad file does (FR-CFG-06) | the process writes the reason to the console and exits with code 1 before anything else runs |

Rules the module enforces:

- The command line is the framework's: `--Section:Key=value` (or `--Section:Key value`) overrides a setting, `--urls`,
  `--environment` and `--contentRoot` are the host's own, anything else is ignored. There is no `--config`: a published
  build is started from the directory that holds its `appsettings.json`.
- The files are optional, as the framework has them: without any, every section keeps its defaults and the start is
  refused on the empty connection string. A file that exists must be one JSON object. A syntax error is reported with
  its line and column, a duplicate key by its path, an array at the root as such, each with the file's path; the reader
  accepts `//` comments and trailing commas.
- Every top-level section of every JSON file must be a module's (`ConfigurationFile.Sections`) or the framework's
  (`ConfigurationFile.FrameworkSections`: `Kestrel`, `AllowedHosts`); a misspelt or stale section (`Databse`, the
  template's `ConnectionStrings`) refuses to start and the message names the file and lists the known sections. Section
  and key names are case-insensitive; a `$schema` key is allowed. Unknown keys inside a section are still ignored by the
  binder. Environment variables and the command line are not checked: they carry the host's own keys and whatever else
  the machine has.
- Every bound section is validated before the database is migrated (`MigrateDatabaseAsync`, FR-CFG-06), `Server`
  included. Failures are logged at Critical as `Section:Key must …` and the process exits 1 without a stack trace. The
  `Kestrel` section is loaded just before that (`ValidateListenEndpoints`): an endpoint URL Kestrel cannot parse is
  refused the same way instead of failing at bind time. An endpoint this host cannot bind (a port in use, an address that
  is not local) ends the same way when the server starts.
- The files hold no credential except `Database:ConnectionString` and the stored-key encryption keys
  `Database:EncryptionKey` and `Database:PreviousEncryptionKey` (FR-CFG-03, A-01), and the system talks to no secret
  manager or vault (FR-CFG-04). The committed `appsettings.json` ships with those three empty and refuses to start until
  the connection string and the key are supplied; a test checks the file for credential-like names.
- Kestrel serves plain HTTP: TLS terminates at a reverse proxy, so keep the endpoint on `127.0.0.1` when the proxy is on
  the same host and set `PublicUrl` to the https origin. The `Kestrel` section is the framework's and could name a
  certificate, but a private key on disk would be a credential held outside the database (FR-CFG-03, C-11), so that is
  not a supported deployment. "Production" is read from the public origin (NFR-SEC-02): a plain-http `PublicUrl` whose
  host is not localhost or a loopback address refuses to start, like any other invalid setting.
- Behind the proxy the pipeline reads `X-Forwarded-For` and `X-Forwarded-Proto` (one hop, either header alone is
  enough) from loopback and from `Server:TrustedProxies`, and from nobody else: a client that sends them itself is not
  believed. The sign-in throttle, the Secure cookie policy, HSTS and the redirection all see the forwarded address and
  scheme. With an https `PublicUrl`, every request Kestrel sees as plain http is redirected (307) to the same path on
  the public origin, built from `PublicUrl` rather than a Host header the proxy may have rewritten to the upstream
  address: a request the trusted proxy received over http (`X-Forwarded-Proto: http`) and a direct hit on Kestrel
  alike, with one exemption: `/health`, the health module's anonymous document, is answered over plain http too, so a
  reverse proxy's upstream check and a local monitor can reach Kestrel directly (it carries nothing a transport
  protects; `/healthz` or `/health/x` is redirected like any other path). A proxy that is not trusted or forwards no
  scheme therefore loops on its first request, which is the intended
  visible failure (the Transport card on `/status`, reached over the proxy once it is fixed, shows the scheme and
  client address the application sees); serving such requests instead would fail on every form, because the
  antiforgery cookie is Secure. An https response carries
  `Strict-Transport-Security: max-age=31536000` (this host only, no preload). The two REST clients follow no redirect,
  so a 3xx from a provider or the exchange cannot carry a key to another host. Every response
  carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`,
  `Referrer-Policy: same-origin` and a `Permissions-Policy` that grants nothing; a full Content-Security-Policy
  (`script-src`) is a known gap, because the Blazor script, the import map and the reconnect UI would need nonces.
- Outbound traffic goes to Binance and the four providers and nowhere else (NFR-SEC-04): the exchange and provider
  endpoints in the configuration must be hosts under `binance.com` or `binance.vision` and under `anthropic.com`,
  `googleapis.com`, `openai.com` or `x.ai` respectively, which startup validation enforces; the only other socket is
  the PostgreSQL connection. The system sends no telemetry: no exporter, crash reporter, usage ping or update check, and
  the browser loads nothing from another origin. A test pins the package list, scans the source for other network
  clients and checks every endpoint in the reference file. An egress proxy set in the process environment
  (`HTTPS_PROXY` and the system proxy) is honoured by the runtime for the REST clients and the Binance websockets alike:
  it is the operator's network plumbing, like DNS and routing, and the destinations stay the hosts above. A
  `Server:OutboundProxy` setting is a possible follow-up.
- Environments are the framework's: `Development` (the launch profile) adds the developer exception page before the
  application's own `/Error` handler, as every ASP.NET Core application has it; everything else (the container checks
  `ValidateScopes` and `ValidateOnBuild`, the static web assets manifest, the cookie policy) is the same in every
  environment. `Properties/launchSettings.json` only sets `ASPNETCORE_ENVIRONMENT=Development` and opens the browser; it
  names no `applicationUrl`, because the `Kestrel` section decides where the application listens.
- `Logging` is the logging module's section (#15): `Console:MinimumLevel`, `Database:MinimumLevel` and the `LogLevel`
  category levels both sinks share, validated like every other section (`LogLevel:Default`, a per-sink `LogLevel`
  table or an exchange category below Information refuses to start). Data Protection logs one warning on the first
  start that its key ring is stored unencrypted: the users' keys are encrypted under `Database:EncryptionKey` (A-02), the
  ring that signs sessions is not yet, a known gap.

Appendix D coverage (FR-CFG-08), with the issue that reads each section:

| Appendix D | Section | Settings | Owner |
|---|---|---|---|
| server | `Server` | `PublicUrl`, `TrustedProxies` (beyond the appendix: the proxies whose forwarded headers are believed); the listen endpoint is the framework's `Kestrel` section | this module |
| database | `Database` | connection string (the only credential), command timeout, retries, `InstanceLockWaitSeconds` (how long a start waits for the previous instance to release the database, NFR-REL-04) | database (#3) |
| logging | `Logging` | `Console:MinimumLevel`, `Database:MinimumLevel`, `LogLevel` (levels per category prefix, shared by both sinks); log retention is `Retention:LogDays` | logging (#15) |
| exchange | `Exchange` | endpoints, request timeout, reconciliation interval, streams, retry | exchange (#1) |
| providers | `Providers` | endpoints per provider, model sync interval, miss threshold, call timeout, max output tokens (the cap of every call, NFR-COST-01) | AI (#2); the sync interval and miss threshold are read by model sync (#10), the cap by the council (#9), whose own `Council:MaxOutputTokens` may only lower it |
| agents | `Agents` | `MaxAgentsPerUser`, `MaxRunningAgentsPerUser` (accounts); `EvaluationDeadlineSeconds` per timeframe, `MaxConcurrentEvaluations`, `DefaultEntryTimeoutCandles` (agents) | accounts (#4), agents (#7) |
| risk caps | `RiskCaps` | `MaxRiskPerTradePercent`, `MaxLeverage`, `MaxDailyLossPercent`, `MaxOpenPositions`; agents validate against them, execution enforces them at order time | agents (#7), execution (#13) |
| market data | `MarketData` | `BackfillCandles` per interval (the backfill depth and the window the engine sees), `SymbolRefreshIntervalMinutes` (beyond the appendix, a day at most); candle retention is `Retention:CandleDays` | market data (#12) |
| paper | `Paper` | `StartingBalance`, maker and taker fee percents per market, `SlippagePercent` | agents (#7) |
| ui | `Ui` | `DefaultTheme` (System, Dark or Light; System by default, FR-UI-02), `ReducedMotion` (the animation default for visitors and new accounts), `TransitionMilliseconds` (the length of UI transitions, at most 400, FR-UI-03) | UI (#14) |
| retention | `Retention` | log, candle and deliberation-transcript days, sweep interval, batch size | database (#3) |
| beyond the appendix | `Accounts`, `Keys`, `Council`, `Experience`, `Execution` | sessions, throttling, password length; key re-validation; the council's role budgets and execution reserve, prompt history, text bounds and optional output-token cap (at most `Providers:MaxOutputTokens`); the experience refresh interval, shadow horizon, recent outcomes and suggestion sample; the execution module's protective-order retries, spot stop-limit offset, management budget, reconciliation age and fill-tracker start delay | accounts (#4), keys (#5), council (#9), experience (#11), execution (#13) |

Configuration (`Server` is this module's section, `Kestrel` the framework's; `src/Ecnanib/appsettings.json` is the
complete reference file with every default):

```json
{ "Kestrel": { "Endpoints": { "Http": { "Url": "http://127.0.0.1:5207" } } },
  "Server": { "PublicUrl": "http://localhost:5207", "TrustedProxies": [] } }
```

Development: create `appsettings.Development.json` next to `appsettings.json` (git-ignored, never built or published)
holding what differs from the reference file, which is the connection string and the encryption key, and `dotnet run`;
the launch profile selects the Development environment, so the overlay is read. `dotnet run --no-launch-profile` runs as Production, reads
the committed file alone and refuses to start on the empty connection string, which is FR-CFG-06 at work.

One culture (NFR-USE-03): the project sets `InvariantGlobalization`, so the process formats and parses numbers and dates
the same way on every host, needs no ICU library, and cannot be switched to another culture; the UI is English and writes
every instant in UTC. `CultureTests` pins the mode on both projects and the built application's runtime configuration.

Running one instance (NFR-REL-04, NFR-ARCH-01): exactly one process serves a database. On start the process migrates
the schema, takes the database's instance lock and only then starts its jobs; on a restart it records the evaluations
the stop cut short as failed, resumes every Running and Paused agent and verifies each live agent against the exchange
before doing anything to it (NFR-REL-01). A second process pointed at the same database waits
`Database:InstanceLockWaitSeconds` (30 by default) for the first to let go, which covers a restart whose old process is
still shutting down, then prints `Another Ecnanib instance holds database … (backend N); this instance will not start`
at Critical and exits with code 1 without binding its port. The status page shows the lock, the restart sweep and the
exchange reconciliation. High availability is out of scope. `StartupProcessLiveTests` drives this on the built
application: a fresh database created, migrated and locked, `/` served, a second process refused with exit code 1, a
clean stop with exit code 0 on SIGTERM.

Tests: `dotnet test` runs offline (the standard sources in their order, the overlay, environment and command-line
overrides, file errors with line numbers, duplicate keys, unknown sections in either file, `Server` validation including
the https rule and the trusted proxies, forwarded-header, redirection and HSTS registration, key-ring creation against an
in-memory repository, the reference file: known sections, the listen endpoint, no credential, every module binds and
validates it, every endpoint an allowed host; the outbound allowlist, package list and source scan; and the pipeline
itself on an in-memory server: security headers, redirection and HSTS behind an https origin, forwarded headers believed
from trusted proxies only, antiforgery on every POST, the challenge of anonymous requests, and the health module's
`/health` answered over plain http while every other path is redirected). `ArchitectureTests` pin NFR-ARCH-01/02 and
C-10: one deployable project, one context over Npgsql, no broker, queue, cache or file state in the source, the fourteen
hosted services in registration order and connecting to nothing when constructed, the sixteen module folders with their
registrations and READMEs, the contract-only dependency rule, the visibility split, the folder-derived namespaces and the
root README naming every module and job. `StartupProcessTests` (in the default run under `Category=Process`; exclude them
with `-- --filter-not-trait "Category=Process"`) start the built application as a child process from a directory holding
its `appsettings.json`: a malformed file and an unknown section are reported on standard error; a missing file, connection
string or encryption key, an override from the environment and from the command line, an unparseable listen URL and an
unreachable database at Critical; each with exit code 1 and no port bound. Live checks sign up, sign in, sign out and read
another user's agent through the same pipeline against PostgreSQL, and `StartupProcessLiveTests` (both `Live` and
`Process`) run the application process itself (above).
