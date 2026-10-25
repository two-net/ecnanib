# Health module

The health status of NFR-OBS-01 and the system status page of FR-UI-01: database connectivity, exchange stream status,
scheduler status and the last model sync per provider, in one report. Other modules depend on `Ecnanib.Health.Abstractions`
and `AddHealth()`; the probe never leaves this folder. `AddHealth` requires `AddDatabase`, `AddExchange`, `AddAgents` and
`AddModelSync` (all read when a report is asked for) and has no configuration section.

| Need | Use |
|---|---|
| The whole picture (NFR-OBS-01) | `ISystemHealth.GetAsync(ct)` → `SystemHealthReport` with `Overall`, `Database`, `MarketStreams`, `UserDataStreams`, `Scheduler`, `ModelSync` (+ `ModelSyncError`) and `RateLimits` |
| One state for a badge | `SystemHealthReport.Overall`: `Healthy`, `Degraded` or `Unhealthy` |
| What each part comes from | the database probe is this module's; the streams and rate limits are the exchange module's `IExchangeHealth`; the scheduler is the agents module's `ISchedulerStatus`; the sync states are the model-sync module's `IModelCatalog.GetSyncStatesAsync` |

Rules the module enforces:

- The database probe opens one connection and asks whether it answers, under a 5 s timeout; a timeout, a refused
  connection or any other failure is reported as Unhealthy with the reason, never thrown. Only the caller's own
  cancellation propagates. The probe runs on demand and is not cached, so a status page that is left open polls the
  database once per refresh.
- The roll-up, the worst wins: the database unreachable or the scheduler NotStarted or Stopped is **Unhealthy**
  (nothing is being evaluated); the scheduler still Starting (the database refused its start-up sweep and it retries),
  a Running scheduler without a completed minute tick for three minutes, any stream Down (socket and polling both
  failing), a provider whose last sync Failed, sync states that could not be read, or an exchange rate limit in force
  is **Degraded**; everything else is **Healthy**. Polling is the designed stream fallback (FR-DATA-03) and shows as
  such without degrading; a provider never synced is not a problem.
- The report names market streams (`candles:production:spot:btcusdt:1h`), which carry the symbols running agents
  trade: public market data, shown behind sign-in only. User-data stream names carry exchange-key ids; the status
  page shows their counts and the last error, never their names.

Configuration: none.

Pages (static server rendering): `/status` (signed-in users): the overall state, the database, the agent scheduler,
the market streams, the account streams in aggregate, the model sync per provider and the exchange rate limits, with a
refresh every minute.

Known gaps: no JSON endpoint yet; the observability issue (#23) maps one over the same `ISystemHealth`. Only the agent
scheduler reports a status; the model sync, retention and symbol refresh jobs log their failures and retry.

Tests: `dotnet test` runs offline (the roll-up rules, a down database and a failing catalog reported without an
exception, the probe timeout under a fake clock, registration). Live checks run against PostgreSQL through the same
git-ignored `tests/.database.json` as the database module: `dotnet test -- --filter-trait "Category=Live"`.
