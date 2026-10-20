# Model-sync module

The per-provider model catalog (SRS §3.2 FR-SYNC-01..06, A-03). This is the seed laid by the keys issue: the one
operation the keys module needs, writing a model list that was already fetched into `provider_models` and recording
the sync in `provider_sync_states` (FR-SYNC-01, FR-SYNC-06). Other modules depend on `Ecnanib.ModelSync.Abstractions`
and `AddModelSync(...)`. `AddModelSync` requires `AddDatabase`, adds no hosted service and connects to nothing; it also registers the catalog
read the council's model picker uses.

| Need | Use |
|---|---|
| Record the models a key validation just listed (FR-SYNC-02) | `IProviderCatalogSync.SyncAsync(provider, models, SyncTrigger.KeyValidation, ct)` → `CatalogSyncResult` (added, updated, listed) |
| Show the last sync per provider (FR-SYNC-06, NFR-OBS-01) | read `ProviderSyncState` |
| Offer the catalog for choosing council models (FR-AI-03, FR-SYNC-05) | `IModelCatalog.ListAsync(ct)` → `CatalogModel` per provider, available first, the most recently added first |

Rules the module enforces:

- Every model must belong to the provider being synced (FR-KEY-05 in spirit: one provider's answer never lands under
  another). Duplicate ids collapse; an id longer than the column (200) is skipped with a warning; a blank display name
  becomes the id and a long one is cut to 200.
- A listed model is upserted: typed columns and `capabilities` refreshed, `last_seen_at` set, status back to Available
  and the miss counter reset. A model absent from the list is left untouched: counting misses and deprecating belongs to
  the scheduled sync (FR-SYNC-03), which does not exist yet.
- The catalog is shared by all users. Two users validating the same provider at once race on the unique index; the
  loser rereads and retries once. Any other failure is recorded as the provider's last sync result and rethrown; the
  keys module logs it and keeps the key (a catalog failure is never the user's problem).

Still to come with the model-sync issue: fetching on the `Providers:ModelSyncIntervalMinutes` schedule with any valid
stored key, the manual sync, deprecation after `Providers:DeprecationMissThreshold` misses, replacement prompts and the
providers page. `AddModelSync` already takes the configuration so those options bind without touching `Program.cs`.

Tests: `dotnet test` runs offline (registration, provider guard). Live checks run against PostgreSQL through the
git-ignored `tests/.database.json`: `dotnet test -- --filter-trait "Category=Live"`.
