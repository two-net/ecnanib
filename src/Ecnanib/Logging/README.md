# Logging module

The log viewer (SRS §3.2 FR-LOG-07; NFR-SEC-03; NFR-ARCH-02 "logging"). Today the module holds the reader the pages use
over the `log_entries` table the database module defines; the database sink that fills the table, the minimum level
per sink and the console/database parity are the logging issue's (FR-LOG-01..05, FR-LOG-08, #15), and until it lands
the console (`Configuration/EcnanibHost.cs`) is the only sink and the viewer shows no rows. Other modules depend on
`Ecnanib.Logging.Abstractions` and `AddLogReader()`, which requires `AddDatabase` and has no configuration section.

| Need | Use |
|---|---|
| A page of one agent's log, filtered (FR-LOG-07) | `ILogReader.QueryAsync(userId, LogQuery)` → `LogPage` (newest first, `HasMore`) or null for another user's agent |
| The filter's component list | `ILogReader.ComponentsAsync(userId, agentId)` |
| Link a row to its deliberation | `LogEntryView.EvaluationId`, resolved from the row's decision id |

Rules the module enforces:

- Ownership comes from `agents`: the agent must be the user's, or the answer is null (NFR-SEC-03). A row counts when
  it names the agent and either the user or no user at all, because the scheduler and the fill loops log agent rows
  without a user id (FR-LOG-02 "where applicable").
- Every query is narrowed by the indexed `(agent_id, timestamp)` first; a text search is a case-insensitive `ILIKE`
  on the message after that, with `%`, `_` and the escape character made literal and the term cut at 200 characters.
  Pages hold at most 200 rows; one row more than the page is read to know whether an older page exists.
- Nothing here writes a log entry. Retention is the database module's `RetentionSweeper` (`Retention:LogDays`) and
  account deletion removes a user's rows (`AccountService.DeleteAccountAsync`).

Configuration: none in this module. The top-level `Logging` section is the framework's console configuration; per-sink
levels arrive with the logging issue.

Pages (static server rendering, plain forms): `/agents/{id}/log` (level, component, text and time filters, newer/older
paging, exception and context per row) and the Log section of `/agents/{id}` (the newest rows).

Known gaps: nothing writes `log_entries` yet (#15). A deleted agent's rows are reachable by nobody until swept. There
is no cross-agent view.

Tests: `dotnet test` runs offline (LIKE escaping, registration, a down database is an exception as in every module).
Live checks run against PostgreSQL through the same git-ignored `tests/.database.json` as the database module (scoping,
filters, paging, deliberation links): `dotnet test -- --filter-trait "Category=Live"`.
