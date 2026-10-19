# Keys module

Bring your own key (SRS §3.2 FR-KEY-01..09; FR-SYNC-02; FR-LOG-04/05; NFR-SEC-03/05; C-05, C-11, A-02). Users add
Binance API key pairs and one key per AI provider; the system holds no credentials of its own. Other modules depend on
`Ecnanib.Keys.Abstractions` and `AddKeys(...)`; the validators and the job never leave this folder. `AddKeys` requires
`AddDatabase`, `AddExchange`, `AddAiProviders` and `AddModelSync`, and adds one hosted service.

| Need | Use |
|---|---|
| List, add, replace, validate or delete the user's keys (FR-KEY-01..03, FR-KEY-06) | `IKeyService` → `KeyOverview`, `KeySaveResult`, `KeyValidationResult`, `KeyDeletionResult`; user mistakes and refused keys are results, exceptions mean infrastructure failed |
| A handle to trade or call a provider with the user's key (AI-03, FR-KEY-05) | `IKeyResolver` → `KeyResolution<ExchangeKeyHandle>` / `KeyResolution<AiKeyHandle>`; only a Valid key resolves, the others carry the reason |
| Say that a feature is unavailable because a key is missing (FR-KEY-08) | `ProviderKeySlot.Key == null`, `KeyResolutionStatus.Missing` / `Invalid` with `Reason` |
| Stop or fail agents that use a key (FR-KEY-06, FR-KEY-07) | `IKeyDependentAgents`; the default writes the agent rows, the agents runtime registers its own before `AddKeys` so that Stop also cancels orders |

Rules the module enforces:

- A key is proven with its own service before it is stored, and only then: every stored key is Valid or Invalid. A
  Binance key answers one authenticated account request per market it is allowed for, then its permissions are read.
  Production reports them (`/sapi/v1/account/apiRestrictions`); a key with withdrawal permission is refused and the
  user is told why (FR-KEY-02). The Spot and Futures testnets have no such endpoint: a testnet key is accepted on its
  requests alone and stored with `verified: false` in `permissions_snapshot`, which the UI says plainly. A provider key
  is proven by listing the provider's models (FR-KEY-03); the list feeds the catalog through `IProviderCatalogSync`
  with trigger `KeyValidation` (FR-SYNC-02), and a catalog failure never fails the key save.
- Refusals: the service rejected the key → `Rejected` with the service's own explanation; withdrawal permission →
  `WithdrawalEnabled`; outage, rate limit, timeout or an unexpected answer → `Failed`, nothing stored, try again. One
  validation is bounded by `Keys:ValidationTimeoutSeconds`, inside which the exchange and AI modules still retry.
- Validations run under a throwaway key id (`validate:…`) that is released afterwards, so they never touch the account
  clients the exchange module caches per stored key. Replacing a key keeps its id, environment and markets (agents keep
  their reference) and releases the stored id after the save, because the cached clients were built on the old secret.
  Deleting a key releases it after the row is gone; `agents.exchange_key_id` is cleared by the foreign key.
- An exchange key is "used" by Running or Paused live agents that reference it; paper agents never need one (FR-KEY-09).
  A provider key is used by the user's Running or Paused agents with an enabled council role on that provider, in both
  modes. Deletion is refused with `AgentsInUse` unless the caller asks to stop them, which moves them to Stopped with
  a reason first (FR-KEY-06). The agents runtime registers its own `IKeyDependentAgents`, so the stop also cancels the
  agents' orders and flags their positions unmanaged; a Start committed between the count and the delete leaves a live
  agent without a key, which its next evaluation turns into Error.
- `KeyRevalidator` re-checks every key whose last check is older than `Keys:RevalidationIntervalMinutes`, one at a
  time, two minutes after start and then every interval (FR-KEY-07). An invalid key is marked with the cause and the
  Running or Paused agents using it enter Error with `state_reason` set, so they open nothing new; a key that recovers
  goes back to Valid while those agents stay in Error until the user starts them (Appendix E: Error → Running). A check
  that could not complete changes nothing and is due again next time. Outcomes are written with an update guarded by
  the key value that was checked, so a key replaced during a slow check is never marked by the stale result. The job
  does not feed the catalog: the scheduled sync has its own cadence (FR-SYNC-03). "Validate now" on the pages runs the
  same check for one key, and does sync the catalog for a provider key.
- Appendix E also allows "key invalid" as a recoverable pause (`PauseCause.KeyInvalid`); FR-KEY-07 asks for Error, which
  this module does. The enum value stays unused.
- Masking and logging (FR-KEY-04, FR-UI-06, FR-LOG-04, FR-LOG-05): views carry the key masked to its last four
  characters and never the secret; handles are the only objects that leave the module with a value, and they print
  masked. Logs carry the user id, key id, environment or provider and the outcome; the reasons stored and shown come
  from the exchange and AI modules, which scrub the key, and a label (the user's own text) is never logged. The pages
  blank the key and secret fields before a failed form is rendered again, so a value never comes back in the page.
- Keys are stored plain (A-02, Appendix F #1): protection is database access control, transport security, masking,
  non-logging and the refusal of withdrawal permission.

Configuration (the `Keys` section goes beyond Appendix D's minimum, FR-CFG-08):

```json
{ "Keys": { "RevalidationIntervalMinutes": 60, "ValidationTimeoutSeconds": 60 } }
```

Pages (static server rendering, plain forms): `/keys` (both lists, the add-a-Binance-key form, "no key" notes per
provider), `/keys/exchange/{id}` (details and permissions, validate now, replace credentials, delete with the option to
stop the agents using it) and `/keys/providers/{claude|gemini|gpt|grok}` (current key, validate now, add or replace,
delete). Another user's key and a missing one both answer not found.

Known gap: account deletion (Accounts) cascades key rows away without releasing the exchange clients cached for them;
they linger in memory until restart. Deletion requires every agent Stopped or Draft, so no stream is open at the time.

Tests: `dotnet test` runs offline (options, registration, both validators with faked exchange and provider factories,
input rules, the job's schedule and failure handling). Live checks run against PostgreSQL through the same git-ignored
`tests/.database.json` as the database module, with the exchange and providers still faked:
`dotnet test -- --filter-trait "Category=Live"`.
