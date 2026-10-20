# Configuration module

The one system configuration file and the host built from it (SRS §3.2 FR-CFG-01..08; Appendix D; C-11; A-01;
NFR-ARCH-02). `Program.cs` depends on `ConfigurationFile`, `EcnanibHost`, `AddServer(...)` and `EnsureSystemKeys()`;
other modules depend on `ServerOptions` only. The module references no other module: every module owns its own
section, binds it in its own `Add*` method and is listed here by name.

| Need | Use |
|---|---|
| Where the file is (FR-CFG-01) | `ecnanib.json` in the current directory, or `ecnanib --config <path>` (`--config=<path>` works too); nothing else is accepted on the command line |
| What is read (FR-CFG-02) | that file and nothing else: no environment variables, no `appsettings*.json`, no user secrets, no command-line settings. `EcnanibHost.CreateBuilder` starts from `WebApplication.CreateEmptyBuilder` and adds back only Kestrel, routing, static web assets and the console logger |
| Add a section for a new module | an `XOptions` with `SectionName` and an `IValidateOptions<XOptions>`, registered as `AddOptions<XOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` in `AddX`; the name in `ConfigurationFile.Sections`; the defaults in `ecnanib.json`; a row in the table below |
| Where the application listens | `Server:BindAddress` and `Server:Port`, plain HTTP. `Server:PublicUrl` is the origin users reach, normally a reverse proxy that terminates TLS (NFR-SEC-02) |
| Cookies and https | session cookies carry the Secure flag when `Server:PublicUrl` is https; with a plain-http public URL (local use) they do not |
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
  are logged at Critical as `Section:Key must …` and the process exits 1 without a stack trace. An endpoint this host
  cannot bind (`Server` names a port in use or an address that is not local) ends the same way.
- The file holds no credential except `Database:ConnectionString` (FR-CFG-03, A-01) and the system talks to no secret
  manager or vault (FR-CFG-04). The committed file ships with an empty connection string and refuses to start until
  the operator fills it in; a test checks the file for credential-like names.
- Kestrel serves plain HTTP only: a certificate's private key would be a credential held outside the database.
  Terminate TLS at a reverse proxy, keep `BindAddress` at `127.0.0.1` when the proxy is on the same host, and set
  `PublicUrl` to the https origin. Forwarded headers (client address and scheme behind the proxy) are not configured
  yet, so `UseHsts` and `UseHttpsRedirection` are not used either; both belong to the security issue.
- There is no Development environment: the error page, the container checks (`ValidateScopes`, `ValidateOnBuild`)
  and the cookie policy are the same everywhere. `Properties/launchSettings.json` only opens the browser and passes
  `--config ecnanib.local.json`; its `applicationUrl` does not decide where Kestrel listens.
- `Logging` is the framework's logger configuration (console sink, minimum level per category) until the logging
  issue adds the database sink and per-sink levels. Data Protection logs one warning on the first start that the key
  is stored unencrypted; that is A-02 by design, because no key may live outside the database.

Appendix D coverage (FR-CFG-08), with the issue that reads each section:

| Appendix D | Section | Settings | Owner |
|---|---|---|---|
| server | `Server` | `BindAddress`, `Port`, `PublicUrl` | this module |
| database | `Database` | connection string (the only credential), command timeout, retries | database (#3) |
| logging | `Logging` | minimum level per category for the console; per-sink levels arrive with logging (#15); log retention is `Retention:LogDays` | logging (#15) |
| exchange | `Exchange` | endpoints, request timeout, reconciliation interval, streams, retry | exchange (#1) |
| providers | `Providers` | endpoints per provider, model sync interval, miss threshold, call timeout, max output tokens | AI (#2) |
| agents | `Agents` | `MaxAgentsPerUser`, `MaxRunningAgentsPerUser` (accounts); `EvaluationDeadlineSeconds` per timeframe, `MaxConcurrentEvaluations`, `DefaultEntryTimeoutCandles` (agents) | accounts (#4), agents (#7) |
| risk caps | `RiskCaps` | `MaxRiskPerTradePercent`, `MaxLeverage`, `MaxDailyLossPercent`, `MaxOpenPositions`; agents validate against them, execution (#13) enforces them at order time | agents (#7), execution (#13) |
| market data | — | backfill arrives with market data (#12); candle retention is `Retention:CandleDays` | market data (#12) |
| paper | `Paper` | `StartingBalance`, maker and taker fee percents per market, `SlippagePercent` | agents (#7) |
| ui | — | default theme and animation defaults arrive with the UI (#14) | UI (#14) |
| retention | `Retention` | log, candle and deliberation-transcript days, sweep interval, batch size | database (#3) |
| beyond the appendix | `Accounts`, `Keys`, `Council` | sessions, throttling, password length; key re-validation; the council's role budgets, prompt history and text bounds | accounts (#4), keys (#5), council (#9) |

Configuration (`Server` is this module's section; `src/Ecnanib/ecnanib.json` is the complete reference file with
every default):

```json
{ "Server": { "BindAddress": "127.0.0.1", "Port": 5207, "PublicUrl": "http://localhost:5207" } }
```

Development: copy `ecnanib.json` to `ecnanib.local.json` (git-ignored, never built or published), set the connection
string and `dotnet run`; the launch profile passes `--config ecnanib.local.json`. `dotnet run --no-launch-profile`
uses the committed file and refuses to start on the empty connection string, which is FR-CFG-06 at work.

Tests: `dotnet test` runs offline (argument parsing, file errors with line numbers, duplicate keys, unknown sections,
the single configuration source, environment variables ignored, `Server` validation, Kestrel registration, key-ring
creation against an in-memory repository, and the reference file: known sections, no credential, every module binds
and validates it). The start-up path end to end is checked by running the application.
