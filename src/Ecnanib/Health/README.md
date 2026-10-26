# Health module

The health status of NFR-OBS-01 and the system status page of FR-UI-01: database connectivity and the instance lock,
exchange stream status, scheduler status, the exchange reconciliation and the last model sync per provider, in one
report. Other modules depend on `Ecnanib.Health.Abstractions`
and `AddHealth()`; the probe never leaves this folder. `AddHealth` requires `AddDatabase`, `AddExchange`, `AddAgents` and
`AddModelSync` (all read when a report is asked for) and has no configuration section.

| Need | Use |
|---|---|
| The whole picture (NFR-OBS-01) | `ISystemHealth.GetAsync(ct)` → `SystemHealthReport` with `Overall`, `Database`, `InstanceLock`, `MarketStreams`, `UserDataStreams`, `Scheduler`, `Reconciliation`, `ModelSync` (+ `ModelSyncError`) and `RateLimits` |
| One state for a badge | `SystemHealthReport.Overall`: `Healthy`, `Degraded` or `Unhealthy` |
| What each part comes from | the database probe is this module's; the instance lock is the database module's `IInstanceLockStatus`; the streams and rate limits are the exchange module's `IExchangeHealth`; the scheduler is the agents module's `ISchedulerStatus`; the reconciliation is the execution module's `IReconciliationStatus` (unavailable without it); the sync states are the model-sync module's `IModelCatalog.GetSyncStatesAsync` |

Rules the module enforces:

- The database probe opens one connection and asks whether it answers, under a 5 s timeout; a timeout, a refused
  connection or any other failure is reported as Unhealthy with the reason, never thrown. Only the caller's own
  cancellation propagates. The probe runs on demand and is not cached, so a status page that is left open polls the
  database once per refresh.
- The roll-up, the worst wins: the database unreachable, the scheduler NotStarted or Stopped, or the instance lock
  lost to another process is **Unhealthy** (nothing is, or soon will be, evaluated); the scheduler still Starting (the
  database refused its start-up sweep and it retries), a Running scheduler without a completed minute tick for three
  minutes, any stream Down (socket and polling both failing), a provider whose last sync Failed, sync states that could
  not be read, an exchange rate limit in force, the instance lock being taken again after a lost connection, a
  reconciliation whose last round could not read a key or compare an agent (or whose last failure is newer than its
  last round), or live agents still awaiting their restart check
  three minutes after the scheduler started is **Degraded**; everything else is **Healthy**. Polling is the designed
  stream fallback (FR-DATA-03) and shows as such without degrading; a provider never synced is not a problem; a lock
  not taken is how every test host runs and is not a problem either.
- The report names market streams (`candles:production:spot:btcusdt:1h`) for the hub and the logs; the status page
  groups them by kind, environment and market and shows counts, the worst health and the last error, never a symbol:
  which symbols are followed is the agents' owners' business (NFR-SEC-03). User-data stream names carry exchange-key
  ids; the page shows their counts and the last error, never their names. Every last error the page shows (scheduler,
  reconciliation, streams) is a kind (`ErrorSummary.Describe`: the exception type, or an exchange error's kind and
  code), never a message that could name a key, an agent, a symbol or a host; the log line beside each has the detail.

Configuration: none.

Pages (static server rendering): `/status` (signed-in users): the overall state, the database with its instance lock
(NFR-REL-04), the transport (the public origin and what follows from its scheme, this request's scheme and client
address after the trusted proxy's forwarded headers, the trusted proxies; NFR-SEC-02), the agent scheduler (evaluations in flight and queued, deadline misses since start, NFR-PERF-01, the
evaluations the last stop cut short, NFR-REL-01), the exchange reconciliation (last round, live agents verified since
start and awaiting their check, adoptions, last error), the market streams grouped by kind, environment and market, the account streams in aggregate, the model
sync per provider and the exchange rate limits, with a refresh every minute.

Known gaps: no JSON endpoint yet; the observability issue (#23) maps one over the same `ISystemHealth`. The scheduler
and the reconciler report a status; the model sync, retention and symbol refresh jobs log their failures and retry.

Tests: `dotnet test` runs offline (the roll-up rules, the lock, reconciliation and restart-check rules among them, a
down database and a failing catalog reported without an exception, the probe timeout under a fake clock, the page's
texts, registration). Live checks run against PostgreSQL through the same
git-ignored `tests/.database.json` as the database module: `dotnet test -- --filter-trait "Category=Live"`.
