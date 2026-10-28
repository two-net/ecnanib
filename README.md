# Ecnanib

AI cryptocurrency trading agents: a free, self-hosted web application on which users create and run AI-assisted trading
agents against Binance Spot and USDⓈ-M Futures, with Claude, Gemini, GPT and Grok working as a council on the users' own
keys. The requirements are in [SRS.md](SRS.md); this file is the map of the code. Third-party code is listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Running it

One process and one PostgreSQL 15+ database, nothing else (SRS NFR-ARCH-01, C-10).

1. Create `src/Ecnanib/appsettings.Development.json` (git-ignored, never built or published) with
   `Database:ConnectionString` and `Database:EncryptionKey`, the base64 of 32 random bytes (`openssl rand -base64 32`)
   under which the users' exchange and provider keys are encrypted at rest; everything else comes from
   `src/Ecnanib/appsettings.json`, the reference file with every default. Those two are the only credentials the files
   hold (FR-CFG-03). Keep the encryption key as carefully as the database: without it every stored key is unreadable and
   the users enter them again. To rotate it, set the old key as `Database:PreviousEncryptionKey` beside the new one for
   one start, then remove it. Configuration is the ASP.NET Core
   standard (FR-CFG-01/02, C-11): `appsettings.json` and `appsettings.{Environment}.json` in the working directory, then
   environment variables (`Database__ConnectionString=…`), then the command line (`--Database:ConnectionString=…`).
2. `dotnet run --project src/Ecnanib`; the launch profile selects the Development environment. A published build runs as
   `ecnanib` from the directory that holds its `appsettings.json`, with `ASPNETCORE_ENVIRONMENT` naming an overlay when
   one is wanted.
3. The process validates every section of the file, creates the database when it is missing, applies the pending
   migrations, takes the database's instance lock, makes sure its own keys exist, proves `Database:EncryptionKey` against
   the database (encrypting any key an earlier version stored plain, or re-encrypting everything after a rotation), and
   only then starts its jobs and
   listens where the `Kestrel` section says (`http://127.0.0.1:5207` by default), plain HTTP behind a reverse proxy that
   terminates TLS at `Server:PublicUrl`.
   Anything that fails on that path, from a misspelt section to a migration that does not apply or an encryption key that
   does not open the stored keys, ends the process with
   exit code 1 and the reason on the console, with no port bound and no job started (FR-CFG-06, NFR-ARCH-03). A second
   process on the same database waits `Database:InstanceLockWaitSeconds` for the first to let go, then exits 1 the same way
   (NFR-REL-04). A service manager stops it with SIGTERM; the stop is clean and the exit code 0.

## Architecture

### One process, one database

Everything the system does runs inside the application process: the web UI (Blazor Server), the agent runtime, the
schedulers, the exchange streams and every background job. State lives in PostgreSQL, reached through EF Core only
(DB-01), or in process memory; there is no message broker, queue, cache, file store or second service (C-10), and the
process talks to nothing but its database, Binance and the four providers (NFR-SEC-04). The background jobs are the hosted
services below, in registration order; the database log writer goes first so it stops last and still writes the other
jobs' shutdown logs (FR-LOG-04). `tests/Ecnanib.Tests/Configuration/ArchitectureTests.cs` pins this list.

| Job | Module | What it does |
|---|---|---|
| `DatabaseLogWriter` | Logging | drains the database log sink into `log_entries` in batches (FR-LOG-01) |
| `MarketStreamHub`, `UserDataStreamHub` | Exchange | own the Binance market and account streams, reference-counted, reconnecting in-process (EX-03) |
| `BinanceRateLimitMonitor` | Exchange | watches the exchange rate limiter (EX-05) |
| `InstanceLock` | Database | holds the database's advisory lock and stops the host if another process takes it (NFR-REL-04) |
| `RetentionSweeper` | Database | deletes expired logs, candles and deliberation transcripts in batches (NFR-DATA-02) |
| `ModelSyncScheduler` | ModelSync | syncs each provider's model catalog on its schedule (FR-SYNC) |
| `ExperienceRefresher` | Experience | rebuilds the provider and model experience statistics (FR-EXP) |
| `SymbolCatalogRefresher` | MarketData | refreshes the symbol listings per environment and market (FR-DATA-01) |
| `ExchangeReconciler`, `LiveFillTracker` | Execution | compare the local orders and trades with the exchange and follow the fills (FR-EXE-08) |
| `PaperFillSimulator`, `AgentScheduler` | Agents | simulate paper fills against live prices; evaluate every Running agent at each candle close (FR-AGT) |
| `KeyRevalidator` | Keys | re-checks the stored exchange and provider keys (FR-KEY-07) |

### Modules

The application is one project, `src/Ecnanib`, organised into the modules NFR-ARCH-02 names and five that support them.
Each module is a folder with its own `README.md` (what it does, what other modules may use, the rules it enforces, its
configuration section, its tests) and an `Add<Module>(...)` registration; `src/Ecnanib/Configuration/EcnanibModules.cs`
registers them in the order their defaults and replacements require, and `Program.cs` is the composition root.

| SRS module | Folder | Holds |
|---|---|---|
| accounts | [`Accounts`](src/Ecnanib/Accounts/README.md) | registration, the cookie session, password change, deletion, fair-use limits, the risk disclaimer |
| keys | [`Keys`](src/Ecnanib/Keys/README.md) | bring-your-own-key for Binance and the providers: validation, rotation, re-validation |
| configuration | [`Configuration`](src/Ecnanib/Configuration/README.md) | the one configuration file, the host, the request pipeline, the module registration order |
| market data | [`MarketData`](src/Ecnanib/MarketData/README.md) | the candle series, backfill and repair, the symbol catalog, futures metrics |
| strategy engine | [`Strategies`](src/Ecnanib/Strategies/README.md) | the Appendix A catalog and its deterministic engines |
| council | [`Council`](src/Ecnanib/Council/README.md) | the sequential multi-model council, its roles, prompts, schemas and hard limits |
| model sync | [`ModelSync`](src/Ecnanib/ModelSync/README.md) | the per-provider model catalog and its sync |
| experience | [`Experience`](src/Ecnanib/Experience/README.md) | decisions and outcomes per provider, model and role, fed back into prompts and the UI |
| execution | [`Execution`](src/Ecnanib/Execution/README.md) | sizing, order placement, protection, position management, reconciliation |
| logging | [`Logging`](src/Ecnanib/Logging/README.md) | the console and database sinks, the log viewer |
| UI | [`Ui`](src/Ecnanib/Ui/README.md) and `Components` | the theme and motion contract and the stylesheets; the pages live in `Components` next to the module they belong to |
| — | [`Agents`](src/Ecnanib/Agents/README.md) | the agents, their lifecycle, the scheduler and the evaluation pipeline the other modules plug into |
| — | [`Ai`](src/Ecnanib/Ai/README.md) | the four providers' REST APIs |
| — | [`Database`](src/Ecnanib/Database/README.md) | the context, the entities, the migrations, retention, the instance lock |
| — | [`Exchange`](src/Ecnanib/Exchange/README.md) | Binance Spot and Futures: REST, streams, rate limits |
| — | [`Health`](src/Ecnanib/Health/README.md) | the health report, the anonymous `/health` document and the status page (NFR-OBS-01) |

### Module boundaries

A module's contract is its root namespace (`Ecnanib.<Module>`, the registration), `Ecnanib.<Module>.Abstractions` (the
interfaces and the types they exchange), `Ecnanib.<Module>.Configuration` (its options, which other modules may read
where the module's README says so) and, for the database module, `Ecnanib.Database.Entities` (the persisted model; the
ORM is the boundary, there is no repository layer). Everything else in a module (`Services`, `Providers`, `Binance`,
`Streaming`, `Authentication`, `Migrations`, …) is `internal` and referenced from inside the module only. A module's pages
live under `Components/<Module>` and are part of it; the shared components (`Layout`, `Live`, `Pages`) and `Program.cs` see
contracts only, and `Program.cs` sees module roots only. A module replaces another's default by registering the consumer's
abstraction first (the order in `EcnanibModules.cs`), never by reaching into its services. `ArchitectureTests` enforces the
rule, the visibility split, the folder-derived namespaces, the module set and the one-deployable project.

### Database and migrations

PostgreSQL 15 or newer through EF Core and Npgsql, snake_case names, no partitioning (DB-03). Migrations are versioned and
forward-only (every `Down` throws) and are applied by the process itself at start (DB-02, NFR-ARCH-03): a database that
knows a migration the build does not, a model with changes no migration covers, an older server or a migration that fails
refuses the start. To change the schema, edit the entity and its configuration, then `dotnet tool restore` once and
`dotnet ef migrations add <Name> --project src/Ecnanib --output-dir Database/Migrations`, and replace the generated `Down`
with the forward-only throw; the database README has the details.

## Tests

`tests/Ecnanib.Tests` (xunit v3 on Microsoft.Testing.Platform) mirrors the module folders.

- `dotnet test` runs offline: every module's unit tests, the architecture rules above, the accessibility, design and
  culture contracts (the structural checker over the anonymous pages, the contrast of every colour pair, the motion
  rules, the invariant culture; NFR-USE-01..03), and the start-up contract on the built application as a child process
  (`Category=Process`: a malformed file, an unknown section, a missing file, connection string or encryption key, an
  override from the environment and from the command line, an unparseable listen URL and an unreachable database each
  end with exit code 1 and the reason on the console). `dotnet test -- --filter-not-trait "Category=Process"` leaves the
  child processes out.
- `dotnet test -- --filter-trait "Category=Live"` runs against PostgreSQL through the git-ignored `tests/.database.json`
  (a role allowed to `CREATE DATABASE`; every test class gets its own `ecnanib_test_*` database and drops it): the schema,
  the instance lock, the request pipeline, the modules against real tables, a tour of every signed-in page with seeded
  content through the accessibility checker, and the application process itself creating and migrating a database, taking
  the lock, serving `/`, refusing a second process and stopping cleanly on SIGTERM. The exchange and provider live checks
  need `tests/.exchange-keys.json` and `tests/.ai-keys.json` as the exchange and AI READMEs describe.

## Disclaimer and licence

Ecnanib is software, not financial advice: nothing it shows, the AI council's output included, is a recommendation to
buy, sell or hold anything. Trading cryptocurrencies can lose the entire balance, and leveraged futures can lose it in a
very short time. Using Binance and the AI providers must be legal where you are, and the taxes on your trading are yours.
The service is provided free of charge, as is, without warranty of any kind (SRS NFR-LEGAL-01/02). The full text is
`Ecnanib.Accounts.Abstractions.RiskDisclaimer`: it is shown on the landing page, during agent creation and on every
agent page, every page's footer states its summary, and a user accepts it before their first live agent starts
(FR-ACC-08). The code is released under the MIT licence, see [LICENSE](LICENSE); third-party code is listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
