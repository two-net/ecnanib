# Model-sync module

The per-provider model catalog (SRS §3.2 FR-SYNC-01..06, A-03; NFR-OBS-01). Each provider's list of available models
is fetched through its API with a stored key and kept current: when a user adds or validates a provider key (the keys
module hands over the list it already fetched), on the `Providers:ModelSyncIntervalMinutes` schedule, and on demand
from the providers page. No model identifier is written in code (FR-SYNC-05): the catalog is the only source of model
ids and of a provider's suggested default, its most recently added available model. Other modules depend on
`Ecnanib.ModelSync.Abstractions` and `AddModelSync(...)`. `AddModelSync` requires `AddDatabase` and `AddAiProviders`,
adds one hosted service and connects to nothing at registration.

| Need | Use |
|---|---|
| Record the models a key validation just listed (FR-SYNC-02) | `IProviderCatalogSync.SyncAsync(provider, models, SyncTrigger.KeyValidation, ct)` → `CatalogSyncResult` (added, updated, listed, missed, deprecated) |
| Sync a provider now with a stored key (FR-SYNC-02) | `IModelSyncService.SyncNowAsync(provider, userId, ct)` → `ModelSyncOutcome`: `Synced` with the result, `NoValidKey`, or `Failed` with the detail; an exception means the database failed |
| Show the last sync per provider (FR-SYNC-06, NFR-OBS-01) | `IModelCatalog.GetSyncStatesAsync(ct)` → one `ProviderSyncView` per provider in enum order (time, result and error, trigger, models listed, available and deprecated counts), `Never` for a provider never synced; the health status (#23) reads the same |
| Offer the catalog for choosing council models (FR-AI-03, FR-SYNC-05) | `IModelCatalog.ListAsync(ct)` → `CatalogModel` per provider, available first, the most recently added first |

Rules the module enforces:

- Every model must belong to the provider being synced (FR-KEY-05 in spirit: one provider's answer never lands under
  another). Duplicate ids collapse; an id longer than the column (200) is skipped with a warning; a blank display name
  becomes the id and a long one is cut to 200.
- A listed model is upserted under every trigger: typed columns and `capabilities` refreshed, `last_seen_at` set,
  status back to Available and the miss counter reset. That is also how a deprecated model recovers.
- Misses and deprecation (FR-SYNC-03): a sync the module fetched itself (trigger `Scheduled` or `Manual`) counts one
  miss on every catalog row the list did not contain and marks a row Deprecated once its consecutive misses reach
  `Providers:DeprecationMissThreshold`. A key validation (`KeyValidation`) leaves absent rows untouched: it is a
  user's action, not the cadence FR-SYNC-03 counts, so validating a key never deprecates anything. A deprecated model
  stays in the catalog, marked in the council picker; the agents pages warn about agents still using one and prompt
  for a replacement (agents module).
- Key choice for a fetch: the provider's stored keys with status Valid, the requester's own first for a manual sync,
  then the most recently validated; at most three are tried per sync. A key the provider rejects is skipped with its id
  logged and is never marked: FR-KEY-07 belongs to the keys module's re-validation. A malformed stored value is
  skipped the same way. Any other provider failure, every tried key rejected, or an empty list ends the sync as
  `Failed`: the reason is recorded as the provider's last result and the catalog is left untouched, so no miss is
  counted. An empty answer is refused on purpose: one restricted key must not count every model missing. No valid key
  at all is `NoValidKey`: nothing is called and nothing is recorded.
- `ModelSyncScheduler` syncs every provider three minutes after start and then every
  `Providers:ModelSyncIntervalMinutes`, one provider at a time, each in its own try/catch: a provider without a valid
  key is skipped at Debug, a provider-side failure is logged at Warning, an exception (the database) is logged at
  Warning and retried next run. The hosted service never stops the host.
- Syncs of one provider never interleave: the runner holds one gate per provider, so a manual sync overlapping the
  scheduled one waits and then runs in full. Key-validation syncs go straight to the upsert and race only on the
  unique index, where the loser rereads and retries once; any other failure is recorded as the provider's last sync
  result and rethrown (the keys module logs it and keeps the key: a catalog failure is never the user's problem).
- The catalog is shared by all users and a key never leaves the module: logs carry provider, trigger, key id and the
  AI module's scrubbed reason, never a key value.

Configuration (the `Providers` section is the AI module's and is validated there; these two keys are read here):

```json
{ "Providers": { "ModelSyncIntervalMinutes": 360, "DeprecationMissThreshold": 3 } }
```

Pages (static server rendering, plain forms): `/providers` (per provider the last sync time, result with the error,
trigger, models listed and deprecated count, and a "Sync now" button; below it the catalog per provider, collapsed,
with each model's status and first and last seen). The keys page links to it.

Known gap: a provider answers each key with what that key may use, so organisation-restricted, preview or fine-tuned
models appear for one user and not another. With one shared catalog a model only one key sees is counted missing
whenever another key is chosen and comes back as Available when that key is used again; the threshold and the refusal
of empty lists soften this, a per-key catalog would remove it. A manual sync counts like a scheduled one, so repeated
"Sync now" clicks shorten the time to deprecation for a model the provider really dropped.

Tests: `dotnet test` runs offline (registration and the options binding, the runner's guards, the scheduler's schedule
and failure handling, the page texts). Live checks run against PostgreSQL through the git-ignored
`tests/.database.json`, with the providers faked: the upsert and the miss counting, key choice and recorded failures,
one pass of the scheduler, the catalog reads: `dotnet test -- --filter-trait "Category=Live"`.
