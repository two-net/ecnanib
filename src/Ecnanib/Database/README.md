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
| Run exactly one instance per database (NFR-REL-04) | `await app.AcquireInstanceLockAsync()` in `Program.cs`, right after `MigrateDatabaseAsync()`; `IInstanceLockStatus.GetStatus()` for the status page |
| Concurrent edits of an agent, order or trade | `Version` (PostgreSQL `xmin`) raises `DbUpdateConcurrencyException` on a stale update |
| A save that lost a unique-key race | `DbUpdateExceptions.IsUniqueViolation(ex)`, then read the row that won |

Rules the module enforces:

- Migrations are versioned, forward-only and applied at startup (DB-02). The migrator refuses to start when the
  database knows a migration the build does not, when the model has changes no migration covers, when the server is
  older than PostgreSQL 15, or when a migration fails; the cause is logged at Critical and the process exits
  (NFR-ARCH-03, FR-LOG-04). Every migration's `Down` throws.
- One process per database (NFR-REL-04): `InstanceLock` takes a PostgreSQL session advisory lock on the key
  `0x65636E616E6962` (the ASCII bytes of "ecnanib", 28538298379954530) through one context whose connection stays open
  for the process's lifetime, so the lock goes when the process does, cleanly or not. It is taken after the migrations
  and before any hosted service runs; a second instance waits `Database:InstanceLockWaitSeconds` for the previous one to
  release it (a restart), then logs the holding backend at Critical and exits 1. The lock's hosted service asks
  `pg_locks` every 30 s whether its own backend still holds the key (a connection the retrying strategy reopened answers
  a plain query and holds nothing), takes the lock again on a new connection when it was lost, and stops the host when
  another instance holds it by then. `SELECT pid FROM pg_locks WHERE locktype = 'advisory' AND classid = 6644590 AND objid =
  1634625890 AND granted` names the holder; a backend left behind by a dead process can be ended with
  `pg_terminate_backend(pid)`.
- No table is partitioned (DB-03). Retention is scheduled deletion: `RetentionSweeper` runs every
  `Retention:SweepIntervalMinutes`, deleting expired log entries, candles and deliberation transcripts in batches of
  `Retention:DeleteBatchSize` through the ORM (NFR-DATA-02). Decisions, their compact role outputs, trades and
  experience statistics are never swept (NFR-DATA-03). A failing sweep is logged at Warning and retried next time.
- All three retention periods live under `Retention` because NFR-DATA-03 defines them together; Appendix D lists
  the log and candle periods under `logging` and `market data`, so the market-data module (#12) and the logging
  module read them from here. `candles` is written by the market-data module's feed: the backfill, repairs and
  streamed closes of every followed series whose open time is inside the candle retention period.
- Identifiers are snake_case, enums are stored as `text`, `DateTimeOffset` is normalized to UTC on write and read
  back with a zero offset, `decimal` is unbounded `numeric`, open-ended structures are `jsonb` (`JsonElement`: store
  a `Clone()` of an element whose document outlives the entity, or `JsonValues.EmptyObject`/`EmptyArray`).
  Guid keys are UUID v7, generated client-side by Npgsql.
- Every user-owned row cascades from `users`, so account deletion is one statement (FR-ACC-04). `log_entries` is
  written in batches by the logging module's sink and has no foreign keys so that sink can never fail on a race
  (FR-LOG-08): account deletion must delete the user's log rows explicitly, the agents' rows without a user id among them.
  `agents (exchange_key_id, user_id)` references `exchange_keys (id, user_id)`, so an agent can carry only a key of its
  own user (NFR-SEC-03); the constraint is NO ACTION (a composite SET NULL would null `user_id` too), so the keys module
  clears the reference before it deletes a key (FR-KEY-06), and deleting a user still cascades to keys and agents in one
  statement because the check runs at its end.
- Keys and secrets are stored plain per A-02 and masked everywhere else (FR-KEY-04). EF and Npgsql log categories are
  capped at Warning in the configuration file (`ecnanib.json`) and the connection string never appears in a validation message.
- `Database:RetryOnFailureCount` enables Npgsql's retrying execution strategy; code that opens its own transaction
  must run it through `Database.CreateExecutionStrategy().ExecuteAsync(...)`.
- Paper equity (FR-AGT-11, FR-EXE-04) is derived from the configured starting balance (`Paper:StartingBalance`) and the agent's own paper trades' realized P&L;
  the daily loss (FR-EXE-09) is derived from `orders.realized_pnl` of the exit fills and `orders.fee` of the entry fills per UTC day. Neither has a column.
- A trade's `size` is what was opened and never changes; `remaining_size` is what partial exits left (0 once closed,
  checked by `ck_trades_remaining_size`). Exit orders carry their fill's `fee` and `realized_pnl`; the trade accumulates
  them and is net of fees and funding at the close. A futures stop or take-profit placed through the conditional (algo)
  endpoint keeps its algo id in `orders.exchange_algo_id`; `exchange_order_id` is the actual order it became once
  triggered. `decisions.execution_check` holds the execution module's checks beside the council's `limits_check`.
- Enums are text except the ordinal ones compared by range: `log_entries.level` and the entry-window days are
  integers, so "Warning and above" and day ranges translate correctly.
- `experience_stats` is written by the experience module's refresher alone, which replaces every row each run; the
  `metrics` JSON holds the counts and sums the FR-EXP-04 ratios derive from beside the ratios (`ExperienceMetrics`),
  and the `ExtendExperienceMetrics` migration deleted the rows written in the earlier shape. `shadow_outcomes.detail`
  holds the setup a shadow follows and the walk's progress.
- A trade references its opening decision (`entry_decision_id`); only managing decisions set `decisions.trade_id`.
  Save the decision, then the trade, inside `Database.CreateExecutionStrategy().ExecuteAsync(...)` with a transaction;
  setting both links in one `SaveChanges` is a cycle EF refuses. Trades and evaluations snapshot the agent's
  symbol, market and timeframe so history survives a re-pointed agent (FR-AGT-07).
- `Ecnanib.Database.Entities.Candle` shares its name with the exchange module's `Candle`; alias one of them where
  both are in scope.

Configuration (`Database` is the only section of the file that holds a credential, FR-CFG-03):

```json
{ "Database": { "ConnectionString": "Host=localhost;Database=ecnanib;Username=ecnanib;Password=...", "CommandTimeoutSeconds": 30, "RetryOnFailureCount": 3, "InstanceLockWaitSeconds": 30 },
  "Retention": { "LogDays": 30, "CandleDays": 365, "DeliberationTranscriptDays": 90, "SweepIntervalMinutes": 60, "DeleteBatchSize": 5000 } }
```

An empty connection string refuses to start (FR-CFG-06). PostgreSQL 15 or newer is required. The start-up sequence is
validate the configuration → migrate → take the instance lock → ensure the system keys → run.

Tests: `dotnet test` runs offline (model snapshot, generated SQL, forward-only and convention checks, the lock's
registration). Live checks (the schema, the sweeper, a second instance waiting then refusing and acquiring once the first
stops, the lock taken again after a lost connection and the host stopped when another instance holds it)
are opt-in through `tests/.database.json` (git-ignored):

```json
{ "connectionString": "Host=localhost;Port=5432;Database=postgres;Username=ecnanib;Password=..." }
```

The role must be allowed to `CREATE DATABASE`: each live test class migrates into its own `ecnanib_test_*` database
and drops it afterwards. Run them with `dotnet test -- --filter-trait "Category=Live"`; every live test skips when
the file is missing. Use `-- --filter-not-trait "Category=Live"` to exclude them explicitly.
