# Health module

The health status of NFR-OBS-01 and the system status page of FR-UI-01: database connectivity and the instance lock,
exchange stream status, scheduler status, the exchange reconciliation and the last model sync per provider with the
sync job's heartbeat, in one report with one state per part, served as the signed-in `/status` page and the anonymous
`/health` document. Other modules depend on `Ecnanib.Health.Abstractions`, `AddHealth()` and `MapHealthEndpoints()`;
the probe never leaves this folder. `AddHealth` requires `AddDatabase`, `AddExchange`, `AddAgents` and `AddModelSync`
(all read when a report is asked for) and has no configuration section.

| Need | Use |
|---|---|
| The whole picture (NFR-OBS-01) | `ISystemHealth.GetAsync(ct)` → `SystemHealthReport` with `Overall`, `States` (one `HealthState` per part), `Database`, `InstanceLock`, `MarketStreams`, `UserDataStreams`, `Scheduler`, `Reconciliation`, `ModelSync` (+ `ModelSyncError`), `ModelSyncJob` and `RateLimits` |
| One state for a badge | `SystemHealthReport.Overall`: `Healthy`, `Degraded` or `Unhealthy`, the worst of `States` |
| The machine-readable status | `GET` or `HEAD /health`, no session: `HealthDocument` as JSON (camelCase names, lowerCamel enum values, ISO 8601 instants), 200 for Healthy and Degraded, 503 for Unhealthy, `Cache-Control: no-store`; any other method is 405 with `Allow: GET, HEAD` |
| What each part comes from | the database probe is this module's; the instance lock is the database module's `IInstanceLockStatus`; the streams and rate limits are the exchange module's `IExchangeHealth`; the scheduler is the agents module's `ISchedulerStatus`; the reconciliation is the execution module's `IReconciliationStatus` (unavailable without it); the sync states are the model-sync module's `IModelCatalog.GetSyncStatesAsync` and the sync job's heartbeat its `IModelSyncStatus` |

Rules the module enforces:

- The database probe opens one connection and asks whether it answers, under a 5 s timeout; a timeout, a refused
  connection or any other failure is reported as Unhealthy with the failure's kind, never thrown. The read of the sync
  states has the same bound and the same treatment and runs beside the probe, so a database that hangs costs one
  timeout, not two. A read that starts failing is logged at Warning with the detail, one that keeps failing the same
  way at Debug and one that answers again at Information: an anonymous client can ask for a report as often as the
  previous one completes and must not be able to flood the log. Only the caller's own cancellation ends its wait. The
  probe runs on demand and is not cached, so a status page that is left open polls the database once per refresh;
  callers that overlap share the one report in flight, so a client hammering `/health` costs one probe at a time.
- The roll-up, one state per part and the worst wins: the database unreachable, the scheduler NotStarted or Stopped,
  or the instance lock lost to another process is **Unhealthy** (nothing is, or soon will be, evaluated); the scheduler
  still Starting (the database refused its start-up sweep and it retries), a Running scheduler without a completed
  minute tick for three minutes, any stream Down (socket and polling both failing), a provider whose last sync Failed,
  sync states that could not be read, the sync job's last pass hit by an exception (the database, say) or the job three
  minutes past its due time with no pass in progress (or with one in progress for an hour), an exchange rate limit in
  force, the instance lock being taken
  again after a lost connection, a reconciliation whose last round could not read a key or compare an agent (or whose
  last failure is newer than its last round), or live agents still awaiting their restart check three minutes after
  the scheduler started is **Degraded**; everything else is **Healthy**. Polling is the designed stream fallback
  (FR-DATA-03) and shows as such without degrading; a provider never synced is not a problem; a lock not taken and a
  sync job not started are how every test host runs and are not problems either; a pass that takes long (four
  providers, each with its call timeout and retries) is a pass in progress, not a stuck loop, until the hour by which
  the call timeout, the three attempts and the three keys per provider end even four dead providers.
- `/health` is anonymous and says states and times only: the overall state, each part's state, the probe's answer
  time (null when nothing answered: a monitor's graph must not take the bound for a measurement),
  the lock's state, the scheduler's phase with its start and last tick, how many streams are in each health, each
  provider's last sync time and result, the sync job's start, running-since, last and next run, whether a rate limit
  holds requests back and the reconciliation's availability and last round. Never a stream name (it carries a symbol),
  a key id, an error text (not even a kind), a count of agents or evaluations, or the transport: those are the signed-in
  page's. Behind an https public URL it is the one path exempt from the plain-http redirect, because a reverse proxy's
  upstream check and a local monitor hit Kestrel over plain http and the document carries nothing a transport
  protects; `/healthz` or `/health/anything` is redirected like every other path.
- The report names market streams (`candles:production:spot:btcusdt:1h`) for the hub and the logs; the status page
  groups them by kind, environment and market and shows counts, the worst health and the last error, never a symbol:
  which symbols are followed is the agents' owners' business (NFR-SEC-03). User-data stream names carry exchange-key
  ids; the page shows their counts and the last error, never their names. Every last error the page shows (the
  database probe and the sync-states read, the scheduler, the sync job, the reconciliation, the instance lock, the
  streams) is a kind (`ErrorSummary.Describe`: the exception type, or an exchange error's kind and code), never a
  message that could name a key, an agent, a symbol, a host, a database or a user; the log line beside each has the
  detail. The probe's own "No answer within 5 s." and "The database did not answer." name nothing.

Configuration: none.

Pages (static server rendering): `/status` (signed-in users): the overall state, the database with its instance lock
(NFR-REL-04), the transport (the public origin and what follows from its scheme, this request's scheme and client
address after the trusted proxy's forwarded headers, the trusted proxies; NFR-SEC-02), the agent scheduler (evaluations
in flight and queued, deadline misses since start, NFR-PERF-01, the evaluations the last stop cut short, NFR-REL-01),
the exchange reconciliation (last round, live agents verified since start and awaiting their check, adoptions, last
error), the market streams grouped by kind, environment and market, the account streams in aggregate, the model sync
per provider with the scheduled job's line (started, running for, last run with its counts, next run, last error as a
kind) and the exchange rate limits. Each card's heading carries its part's state badge (the Database card the worse of
the database's and the lock's; the account streams their own, under the market streams'), so the card that degrades
the whole is found at a glance and its accessible name says so. It reloads only when asked (WCAG 2.2.1, NFR-USE-01):
"Reload every minute" follows `/status?reload=1`, where a meta refresh reloads the page each minute and "Stop
reloading" returns to the still page; "Refresh now" reloads once.

Known gaps: the retention, symbol refresh, experience and key re-validation jobs log their failures and retry without
reporting a status; the logging module has no sink status; the model sync runner keeps the provider's own (scrubbed)
error text on the sync row, which the providers and status pages show as the provider's reason.

Tests: `dotnet test` runs offline (the roll-up rules part by part, the lock, reconciliation, restart-check and sync-job
rules among them; a down database and a failing catalog reported as kinds and logged on change only, never thrown; the probe and the
sync-states read timing out under a fake clock; overlapping callers sharing one probe and a later one probing again;
the document's keys, spelling and what it leaves out; the endpoint over the in-memory pipeline: JSON with `no-store`,
HEAD, 405, the redirect exemption; the page's texts; registration). Live checks run against PostgreSQL through the same
git-ignored `tests/.database.json` as the database module (the report and the document against real tables, the
started application process answering `/health` 200 and healthy): `dotnet test -- --filter-trait "Category=Live"`.
