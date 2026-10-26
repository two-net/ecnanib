# Configuration module

The one system configuration file, the host built from it and the request pipeline (SRS §3.2 FR-CFG-01..08; Appendix D;
C-11; A-01; NFR-ARCH-02; NFR-SEC-02/04). `Program.cs` depends on `ConfigurationFile`, `EcnanibHost`, `AddEcnanibModules(...)`,
`EnsureSystemKeys()` and `UseEcnanibPipeline()`; other modules depend on `ServerOptions` and `HostNames` only. The module
references the others in two places only: `AddEcnanibModules`, which registers every module in the order their defaults
and replacements require, and the pipeline, which maps the accounts module's endpoints and the root component. Every
module owns its own section, binds it in its own `Add*` method and is listed here by name.

| Need | Use |
|---|---|
| Where the file is (FR-CFG-01) | `ecnanib.json` in the current directory, or `ecnanib --config <path>` (`--config=<path>` works too); nothing else is accepted on the command line |
| What is read (FR-CFG-02) | that file and nothing else: no environment variables, no `appsettings*.json`, no user secrets, no command-line settings. `EcnanibHost.CreateBuilder` starts from `WebApplication.CreateEmptyBuilder` and adds back only Kestrel, routing and static web assets; both log sinks are the logging module's `AddLogSinks` |
| Add a section for a new module | an `XOptions` with `SectionName` and an `IValidateOptions<XOptions>`, registered as `AddOptions<XOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` in `AddX`; the name in `ConfigurationFile.Sections`; the defaults in `ecnanib.json`; a row in the table below |
| Where the application listens | `Server:BindAddress` and `Server:Port`, plain HTTP. `Server:PublicUrl` is the origin users reach, normally a reverse proxy that terminates TLS; it must be https unless its host is localhost or a loopback address (NFR-SEC-02) |
| Behind a reverse proxy | `Server:TrustedProxies`: the proxies (IP addresses or CIDR networks) whose `X-Forwarded-For` and `X-Forwarded-Proto` are believed; loopback always is, so a same-host proxy needs no entry |
| Cookies, HSTS and redirection | with an https `Server:PublicUrl` the session and antiforgery cookies carry the Secure flag, every request Kestrel sees as plain http is redirected to the same path on the public origin and `Strict-Transport-Security` (one year) is sent; with a loopback http public URL none of that happens |
| Register every module | `services.AddEcnanibModules(configuration)`, in `Program.cs` after `AddLogSinks` (C-08) and in the tests' in-memory server |
| The request pipeline | `app.UseEcnanibPipeline()` in `Program.cs`: forwarded headers, error pages, redirection and HSTS, the security headers, authentication, authorization, antiforgery and the endpoints, in that order; the tests run the same method on an in-memory server |
| The system's own keys (FR-CFG-05) | `EnsureSystemKeys()` after `MigrateDatabaseAsync()`: the Data Protection key ring that protects session tickets and antiforgery tokens is created on the first start and persisted in `data_protection_keys`. `SystemKey` stays reserved for other internal keys by purpose |
| Apply a change (FR-CFG-07) | restart; the file is read once and not watched |
| What a bad file does (FR-CFG-06) | the process writes the reason to the console and exits with code 1 before anything else runs |

Rules the module enforces:

- `--config <path>` is the only launch argument. A missing path, a repeated option or anything else prints the usage
  on standard error and exits 1. A relative path is taken from the current directory, which is also the content root.
- The file must exist, be readable and be one JSON object. A syntax error is reported with its line and column, a
  duplicate key by its path, an array at the root as such. The reader accepts `//` comments and trailing commas.
- Every top-level section must be one of `ConfigurationFile.Sections`; a misspelt or stale section (such as the
  template's `AllowedHosts`) refuses to start and the message lists the known names. Section and key names are
  case-insensitive; a `$schema` key is allowed. Unknown keys inside a section are still ignored by the binder.
- Every bound section is validated before the database is migrated (`MigrateDatabaseAsync`, FR-CFG-06). Failures
  are logged at Critical as `Section:Key must …` and the process exits 1 without a stack trace. The `Server` section
  is read by Kestrel while the host is built, before any logger exists, so its failures (an http `PublicUrl` that is
  not loopback, an unparseable `TrustedProxies` entry) go to standard error instead, with the same exit code. An
  endpoint this host cannot bind (`Server` names a port in use or an address that is not local) ends the same way.
- The file holds no credential except `Database:ConnectionString` (FR-CFG-03, A-01) and the system talks to no secret
  manager or vault (FR-CFG-04). The committed file ships with an empty connection string and refuses to start until
  the operator fills it in; a test checks the file for credential-like names.
- Kestrel serves plain HTTP only: a certificate's private key would be a credential held outside the database
  (FR-CFG-03, C-11). Terminate TLS at a reverse proxy, keep `BindAddress` at `127.0.0.1` when the proxy is on the same
  host, and set `PublicUrl` to the https origin. "Production" is read from that origin (NFR-SEC-02): a plain-http
  `PublicUrl` whose host is not localhost or a loopback address refuses to start, like any other invalid setting.
- Behind the proxy the pipeline reads `X-Forwarded-For` and `X-Forwarded-Proto` (one hop, either header alone is
  enough) from loopback and from `Server:TrustedProxies`, and from nobody else: a client that sends them itself is not
  believed. The sign-in throttle, the Secure cookie policy, HSTS and the redirection all see the forwarded address and
  scheme. With an https `PublicUrl`, every request Kestrel sees as plain http is redirected (307) to the same path on
  the public origin, built from `PublicUrl` rather than a Host header the proxy may have rewritten to the upstream
  address: a request the trusted proxy received over http (`X-Forwarded-Proto: http`) and a direct hit on Kestrel
  alike. A proxy that is not trusted or forwards no scheme therefore loops on its first request, which is the intended
  visible failure (the Transport card on `/status`, reached over the proxy once it is fixed, shows the scheme and
  client address the application sees); serving such requests instead would fail on every form, because the
  antiforgery cookie is Secure. An https response carries
  `Strict-Transport-Security: max-age=31536000` (this host only, no preload). The two REST clients follow no redirect,
  so a 3xx from a provider or the exchange cannot carry a key to another host. Every response
  carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`,
  `Referrer-Policy: same-origin` and a `Permissions-Policy` that grants nothing; a full Content-Security-Policy
  (`script-src`) is a known gap, because the Blazor script, the import map and the reconnect UI would need nonces.
- Outbound traffic goes to Binance and the four providers and nowhere else (NFR-SEC-04): the exchange and provider
  endpoints in the file must be hosts under `binance.com` or `binance.vision` and under `anthropic.com`,
  `googleapis.com`, `openai.com` or `x.ai` respectively, which startup validation enforces; the only other socket is
  the PostgreSQL connection. The system sends no telemetry: no exporter, crash reporter, usage ping or update check, and
  the browser loads nothing from another origin. A test pins the package list, scans the source for other network
  clients and checks every endpoint in the reference file. An egress proxy set in the process environment
  (`HTTPS_PROXY` and the system proxy) is honoured by the runtime for the REST clients and the Binance websockets alike:
  it is the operator's network plumbing, like DNS and routing, not configuration this file reads (FR-CFG-02), and the
  destinations stay the hosts above. A `Server:OutboundProxy` setting is a possible follow-up.
- There is no Development environment: the error page, the container checks (`ValidateScopes`, `ValidateOnBuild`)
  and the cookie policy are the same everywhere. `Properties/launchSettings.json` only opens the browser and passes
  `--config ecnanib.local.json`; its `applicationUrl` does not decide where Kestrel listens.
- `Logging` is the logging module's section (#15): `Console:MinimumLevel`, `Database:MinimumLevel` and the `LogLevel`
  category levels both sinks share, validated like every other section (`LogLevel:Default`, a per-sink `LogLevel`
  table or an exchange category below Information refuses to start). Data Protection logs one warning on the first
  start that the key is stored unencrypted; that is A-02 by design, because no key may live outside the database.

Appendix D coverage (FR-CFG-08), with the issue that reads each section:

| Appendix D | Section | Settings | Owner |
|---|---|---|---|
| server | `Server` | `BindAddress`, `Port`, `PublicUrl`, `TrustedProxies` (beyond the appendix: the proxies whose forwarded headers are believed) | this module |
| database | `Database` | connection string (the only credential), command timeout, retries, `InstanceLockWaitSeconds` (how long a start waits for the previous instance to release the database, NFR-REL-04) | database (#3) |
| logging | `Logging` | `Console:MinimumLevel`, `Database:MinimumLevel`, `LogLevel` (levels per category prefix, shared by both sinks); log retention is `Retention:LogDays` | logging (#15) |
| exchange | `Exchange` | endpoints, request timeout, reconciliation interval, streams, retry | exchange (#1) |
| providers | `Providers` | endpoints per provider, model sync interval, miss threshold, call timeout, max output tokens | AI (#2); the sync interval and miss threshold are read by model sync (#10) |
| agents | `Agents` | `MaxAgentsPerUser`, `MaxRunningAgentsPerUser` (accounts); `EvaluationDeadlineSeconds` per timeframe, `MaxConcurrentEvaluations`, `DefaultEntryTimeoutCandles` (agents) | accounts (#4), agents (#7) |
| risk caps | `RiskCaps` | `MaxRiskPerTradePercent`, `MaxLeverage`, `MaxDailyLossPercent`, `MaxOpenPositions`; agents validate against them, execution enforces them at order time | agents (#7), execution (#13) |
| market data | `MarketData` | `BackfillCandles` per interval (the backfill depth and the window the engine sees), `SymbolRefreshIntervalMinutes` (beyond the appendix, a day at most); candle retention is `Retention:CandleDays` | market data (#12) |
| paper | `Paper` | `StartingBalance`, maker and taker fee percents per market, `SlippagePercent` | agents (#7) |
| ui | `Ui` | `DefaultTheme` (System, Dark or Light; System by default, FR-UI-02), `ReducedMotion` (the animation default for visitors and new accounts), `TransitionMilliseconds` (the length of UI transitions, at most 400, FR-UI-03) | UI (#14) |
| retention | `Retention` | log, candle and deliberation-transcript days, sweep interval, batch size | database (#3) |
| beyond the appendix | `Accounts`, `Keys`, `Council`, `Experience`, `Execution` | sessions, throttling, password length; key re-validation; the council's role budgets and execution reserve, prompt history and text bounds; the experience refresh interval, shadow horizon, recent outcomes and suggestion sample; the execution module's protective-order retries, spot stop-limit offset, management budget, reconciliation age and fill-tracker start delay | accounts (#4), keys (#5), council (#9), experience (#11), execution (#13) |

Configuration (`Server` is this module's section; `src/Ecnanib/ecnanib.json` is the complete reference file with
every default):

```json
{ "Server": { "BindAddress": "127.0.0.1", "Port": 5207, "PublicUrl": "http://localhost:5207", "TrustedProxies": [] } }
```

Development: copy `ecnanib.json` to `ecnanib.local.json` (git-ignored, never built or published), set the connection
string and `dotnet run`; the launch profile passes `--config ecnanib.local.json`. `dotnet run --no-launch-profile`
uses the committed file and refuses to start on the empty connection string, which is FR-CFG-06 at work.

Running one instance (NFR-REL-04, NFR-ARCH-01): exactly one process serves a database. On start the process migrates
the schema, takes the database's instance lock and only then starts its jobs; on a restart it records the evaluations
the stop cut short as failed, resumes every Running and Paused agent and verifies each live agent against the exchange
before doing anything to it (NFR-REL-01). A second process pointed at the same database waits
`Database:InstanceLockWaitSeconds` (30 by default) for the first to let go, which covers a restart whose old process is
still shutting down, then prints `Another Ecnanib instance holds database … (backend N); this instance will not start`
at Critical and exits with code 1 without binding its port. The status page shows the lock, the restart sweep and the
exchange reconciliation. High availability is out of scope.

Tests: `dotnet test` runs offline (argument parsing, file errors with line numbers, duplicate keys, unknown sections,
the single configuration source, environment variables ignored, `Server` validation including the https rule and the
trusted proxies, Kestrel, forwarded-header, redirection and HSTS registration, key-ring creation against an in-memory
repository, the reference file: known sections, no credential, every module binds and validates it, every endpoint an
allowed host; the outbound allowlist, package list and source scan; and the pipeline itself on an in-memory server:
security headers, redirection and HSTS behind an https origin, forwarded headers believed from trusted proxies only,
antiforgery on every POST and the challenge of anonymous requests). Live checks sign up, sign in, sign out and read
another user's agent through the same pipeline against PostgreSQL. The start-up path end to end is checked by running
the application.
