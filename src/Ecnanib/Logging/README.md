# Logging module

Both log sinks and the log viewer (SRS §3.2 FR-LOG-01..08; C-08; NFR-SEC-03; NFR-ARCH-02 "logging"). The module owns
the Serilog pipeline behind `ILogger<T>`: the console sink and the database sink that fills the `log_entries` table the
database module defines, each with its own minimum level, and the reader the pages use over that table. Other modules
depend on `Ecnanib.Logging.Abstractions` (`LogScope` and `BeginScope(agentId: …)`), `AddLogSinks(configuration)` and
`AddLogReader()`; both registrations require `AddDatabase`, and `AddLogSinks` goes first in `Program.cs`.

| Need | Use |
|---|---|
| Write a log | `ILogger<T>` as everywhere. Ids named as template arguments (`{UserId}`, `{AgentId}`, `{DecisionId}`, `{OrderId}`) land in their columns; every other named argument lands in `context` |
| Give every log of a stretch of work its ids (FR-LOG-02) | `using var scope = _logger.BeginScope(agentId: …, userId: …, evaluationId: …, decisionId: …, orderId: …, tradeId: …)`. The scheduler opens one per candle arrival, the pipeline one per evaluation, the live venue one per order sent and per fill applied, so council, provider, execution and exchange logs reach the agent's viewer |
| Where an event goes | the console when its level reaches `Logging:Console:MinimumLevel`, `log_entries` when it reaches `Logging:Database:MinimumLevel`; a `Logging:LogLevel` entry replaces both minimums for its category prefix |
| A page of one agent's log, filtered (FR-LOG-07) | `ILogReader.QueryAsync(userId, LogQuery)` → `LogPage` (newest first, `HasMore`) or null for another user's agent |
| The filter's component list | `ILogReader.ComponentsAsync(userId, agentId)` |
| Link a row to its deliberation | `LogEntryView.EvaluationId`, resolved from the row's decision id |

Rules the module enforces:

- Serilog is the `ILogger<T>` implementation (`AddSerilog`); the framework's console logger and its `Logging:LogLevel`
  filtering are not used. The category becomes `SourceContext` and the `component` column; template arguments and the
  key-value pairs of every `BeginScope` become properties (the message's own argument wins over a scope, an inner
  scope over an outer one); text scopes become a `Scope` list in the context. ASP.NET's request scope adds `RequestId`
  and `RequestPath` to request-bound rows such as sign-ins.
- The database sink is the module's own and writes through EF Core (C-09, DB-01), not a raw-Npgsql package.
  `DatabaseLogSink` is a bounded queue of 10 000 events; `DatabaseLogWriter`, a hosted service, drains it in batches
  of 100, gathering a trickle for a second first. It starts with the host, after the migrations, so nothing is
  written to a schema that is not there yet; `Program.cs` registers it before every other module so it stops last and
  still writes their shutdown logs, with five seconds to drain what is queued.
- Failure never interrupts the application (FR-LOG-08). A batch the database refuses is kept and retried with a delay
  that doubles from two seconds to a minute, for ten minutes, then given up; while the queue is full the newest events
  are dropped and counted. The failure, the recovery and the dropped count are logged, at most one warning a minute,
  and because the writer's whole flow runs under `DatabaseLogSink.Suppress()`, those warnings and EF's own logs of the
  inserts reach the console only: the sink cannot feed itself, even with `Microsoft.EntityFrameworkCore` at Debug.
  With `Database:RetryOnFailureCount` on, one attempt against a dead database can take about a minute.
- A row holds the timestamp, the level (the `Microsoft.Extensions.Logging` level as an int, so "Warning and above"
  compares), the component (at most 200 characters), the message (4 000), the exception text (16 000), the four id
  columns and `context`: one flat JSON object of the remaining properties, string values cut at 500 characters. NUL
  characters are removed because PostgreSQL text and jsonb refuse them. An event the mapper cannot handle becomes a
  row with its template and the reason.
- Levels (FR-LOG-03): `Logging:Console:MinimumLevel` and `Logging:Database:MinimumLevel` are each sink's default;
  every `Logging:LogLevel` entry applies to both sinks and replaces the default for the categories under its prefix
  (the longest prefix wins; a prefix matches whole dotted segments, so `Binance` covers `Binance.RestClient` and not
  `BinanceX`). The root runs at the lower of the two minimums with the same overrides, so events neither sink wants
  are never created. The file refuses to start (FR-CFG-06) on an unknown level name, on `None`, on
  `Logging:LogLevel:Default`, on a per-sink `LogLevel` table and on a wildcard.
- No secrets (FR-LOG-05): keys, passwords and signatures are never logged, only key ids and masked keys. The exchange
  library's own loggers (`Binance.RestClient`, `Binance.SocketClient`, `Binance.TokenManager`, `CryptoExchange`) print
  the request with its API-key header and signature at Debug, so the file refuses to start when a sink would log
  `Binance` or `CryptoExchange` below Information. The Binance `HttpClient` redacts `X-MBX-APIKEY` from the factory's
  Trace logs, as the AI client redacts its headers.
- Ownership in the viewer comes from `agents`: the agent must be the user's, or the answer is null (NFR-SEC-03). A
  row counts when it names the agent and either the user or no user at all, because some loops log agent rows without
  a user id (FR-LOG-02 "where applicable").
- Every query is narrowed by the indexed `(agent_id, timestamp)` first; a text search is a case-insensitive `ILIKE`
  on the message after that, with `%`, `_` and the escape character made literal and the term cut at 200 characters.
  Pages hold at most 200 rows; one row more than the page is read to know whether an older page exists.
- Nothing here deletes a log entry. Retention is the database module's `RetentionSweeper` (`Retention:LogDays`) and
  account deletion removes a user's rows (`AccountService.DeleteAccountAsync`).

Configuration (`Logging`; `src/Ecnanib/appsettings.json` has the defaults):

```json
{ "Logging": { "Console": { "MinimumLevel": "Information" }, "Database": { "MinimumLevel": "Information" },
  "LogLevel": { "Microsoft.AspNetCore": "Warning", "Binance": "Warning", "CryptoExchange": "Warning", "System.Net.Http.HttpClient": "Warning", "Microsoft.EntityFrameworkCore": "Warning", "Npgsql": "Warning" } } }
```

Levels are `Trace`, `Debug`, `Information`, `Warning`, `Error` or `Critical`. Log retention is `Retention:LogDays`
(database module). A console line reads `2026-01-05 10:00:00.000 +00:00 [INF] Ecnanib.Agents.Services.AgentService: Agent started`,
in local time with its offset.

Pages (static server rendering, plain forms): `/agents/{id}/log` (level, component, text and time filters, newer/older
paging, exception and context per row) and the Log section of `/agents/{id}` (the newest rows).

Known gaps: rows queued for an account deleted a moment earlier are written after the delete and swept with the rest
after `Retention:LogDays`. A database outage longer than ten minutes loses the batches it refused and whatever the full
queue dropped; the console has them. A deleted agent's rows are reachable by nobody until swept. There is no
cross-agent view and no sink status on the health page.

Tests: `dotnet test` runs offline (the level filter and the pipeline through the `ILogger` bridge, event mapping, the
sink's queue and suppression, the writer's batching, retries, give-up and drain against a fake clock and store, section
validation, registration, LIKE escaping). Live checks run against PostgreSQL through the same git-ignored
`tests/.database.json` as the database module (rows with ids and context from scopes and no self-feeding with EF at
Debug; the reader's scoping, filters, paging and deliberation links): `dotnet test -- --filter-trait "Category=Live"`.
