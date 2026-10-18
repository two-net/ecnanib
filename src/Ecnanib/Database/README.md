# Database module

The one PostgreSQL database, reached only through EF Core (SRS §3.1.4, DB-01..03; NFR-DATA-01..03; NFR-ARCH-03;
C-09). Other modules depend on `EcnanibDbContext`, the entities in `Ecnanib.Database.Entities` and
`AddDatabase(...)`; there is no repository layer because the ORM is the boundary. The folder is named `Entities`
rather than `Abstractions` because the shared types here are the persisted model itself.

| Need | Use |
|---|---|
| Read or write rows from a service, job or Blazor component | `IDbContextFactory<EcnanibDbContext>` → `CreateDbContextAsync` (dispose per unit of work); the scoped `EcnanibDbContext` is for per-request work only |
| Bring the schema up to date before the host runs (DB-02, NFR-ARCH-03) | `await app.MigrateDatabaseAsync()` in `Program.cs`, right after `builder.Build()` |
| Add or change a table | edit the entity and its `IEntityTypeConfiguration<T>`, then `dotnet tool restore` once and `dotnet ef migrations add <Name> --project src/Ecnanib --output-dir Database/Migrations`; replace the generated `Down` with the forward-only throw (the tests enforce both) |
| Retention periods (NFR-DATA-03, FR-LOG-06, FR-DATA-05) | `Retention:LogDays`, `Retention:CandleDays`, `Retention:DeliberationTranscriptDays` in the configuration file |
| The system's own keys (FR-CFG-05, A-01) | the Data Protection key ring is persisted in `data_protection_keys`; other internal keys go in `SystemKey` by purpose |
| Idempotent evaluation per candle (FR-AGT-09) | the unique index on `evaluations (agent_id, candle_open_time)`; catch `DbUpdateException` on the duplicate |
| Never duplicate an order (FR-EXE-07, NFR-REL-02) | the unique index on `orders (client_order_id)` |
| Concurrent edits of an agent, order or trade | `Version` (PostgreSQL `xmin`) raises `DbUpdateConcurrencyException` on a stale update |

Rules the module enforces:

- Migrations are versioned, forward-only and applied at startup (DB-02). The migrator refuses to start when the
  database knows a migration the build does not, when the model has changes no migration covers, when the server is
  older than PostgreSQL 15, or when a migration fails; the cause is logged at Critical and the process exits
  (NFR-ARCH-03, FR-LOG-04). Every migration's `Down` throws.
- No table is partitioned (DB-03). Retention is scheduled deletion: `RetentionSweeper` runs every
  `Retention:SweepIntervalMinutes`, deleting expired log entries, candles and deliberation transcripts in batches of
  `Retention:DeleteBatchSize` through the ORM (NFR-DATA-02). Decisions, their compact role outputs, trades and
  experience statistics are never swept (NFR-DATA-03). A failing sweep is logged at Warning and retried next time.
- All three retention periods live under `Retention` because NFR-DATA-03 defines them together; Appendix D lists
  the log and candle periods under `logging` and `market data`, so issues #12 and #15 read them from here.
- Identifiers are snake_case, enums are stored as `text`, `DateTimeOffset` is normalized to UTC on write and read
  back with a zero offset, `decimal` is unbounded `numeric`, open-ended structures are `jsonb` (`JsonElement`: store
  a `Clone()` of an element whose document outlives the entity, or `JsonValues.EmptyObject`/`EmptyArray`).
  Guid keys are UUID v7, generated client-side by Npgsql.
- Every user-owned row cascades from `users`, so account deletion is one statement (FR-ACC-04). `log_entries` has
  no foreign keys so the log sink can never fail on a race (FR-LOG-08): account deletion must delete the user's log
  rows explicitly. Deleting an exchange key sets `agents.exchange_key_id` to null (FR-KEY-06).
- Keys and secrets are stored plain per A-02 and masked everywhere else (FR-KEY-04). EF and Npgsql log categories are
  capped at Warning in `appsettings.json` and the connection string never appears in a validation message.
- `Database:RetryOnFailureCount` enables Npgsql's retrying execution strategy; code that opens its own transaction
  must run it through `Database.CreateExecutionStrategy().ExecuteAsync(...)`.
- Paper equity (FR-AGT-11, FR-EXE-04) is derived from the configured starting balance and the account's trades;
  the daily loss (FR-EXE-09) is derived from `trades.realized_pnl` per UTC day. Neither has a column.
- Enums are text except the ordinal ones compared by range: `log_entries.level` and the entry-window days are
  integers, so "Warning and above" and day ranges translate correctly.
- A trade references its opening decision (`entry_decision_id`); only managing decisions set `decisions.trade_id`.
  Save the decision, then the trade, inside `Database.CreateExecutionStrategy().ExecuteAsync(...)` with a transaction;
  setting both links in one `SaveChanges` is a cycle EF refuses. Trades and evaluations snapshot the agent's
  symbol, market and timeframe so history survives a re-pointed agent (FR-AGT-07).
- `Ecnanib.Database.Entities.Candle` shares its name with the exchange module's `Candle`; alias one of them where
  both are in scope.

Configuration (`Database` is the only section of the file that holds a credential, FR-CFG-03):

```json
{ "Database": { "ConnectionString": "Host=localhost;Database=ecnanib;Username=ecnanib;Password=...", "CommandTimeoutSeconds": 30, "RetryOnFailureCount": 3 },
  "Retention": { "LogDays": 30, "CandleDays": 365, "DeliberationTranscriptDays": 90, "SweepIntervalMinutes": 60, "DeleteBatchSize": 5000 } }
```

An empty connection string refuses to start (FR-CFG-06). PostgreSQL 15 or newer is required.

Tests: `dotnet test` runs offline (model snapshot, generated SQL, forward-only and convention checks). Live checks
are opt-in through `tests/.database.json` (git-ignored):

```json
{ "connectionString": "Host=localhost;Port=5432;Database=postgres;Username=ecnanib;Password=..." }
```

The role must be allowed to `CREATE DATABASE`: each live test class migrates into its own `ecnanib_test_*` database
and drops it afterwards. Run them with `dotnet test -- --filter-trait "Category=Live"`; every live test skips when
the file is missing. Use `-- --filter-not-trait "Category=Live"` to exclude them explicitly.
