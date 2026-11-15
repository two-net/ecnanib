# Model catalog

Ecnanib keeps a catalog of the models that Claude, Gemini, GPT, and Grok offer. It is filled only by syncing from each provider's own list of its models: no model is hard-coded, and none is seeded (FR-CAT-1). A sync runs on a schedule, as the application starts when the catalog is empty, and on demand (FR-CAT-2). It asks with the catalog key that the configuration file holds for the provider. Where the file holds none, the provider's catalog is refreshed whenever a user's key for it is validated (FR-CAT-3). Each model is stored once, with what its provider says about it and with when it was first and last seen (FR-CAT-4). A model that is absent from a set number of syncs in a row is marked unavailable, and is never deleted (FR-CAT-5). A sync that fails leaves the catalog as it was, and every sync is recorded (FR-CAT-7).

Only an eligible model can be selected for an agent: one that the configuration file allows, with patterns for each provider (FR-CAT-6). Whatever depends on a model is told when the model is marked unavailable, and nothing is put in its place (FR-CAT-8). And each time a provider took a user's key, the Catalog says which models the provider lists to that key, for Credentials to keep with the key (FR-CAT-9).

The code is in `src/Ecnanib/Catalog`. The providers' lists are read by the gateway, in `src/Ecnanib/AiProviderGateway/Adapters`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Catalog`, `tests/Ecnanib.Tests/AiProviderGateway/AiModelListTests.cs`, and `tests/Ecnanib.Tests/BackgroundWork/BackgroundJobDueAtStartTests.cs`.

How a provider's list is read is in [ai-providers.md](ai-providers.md). How a job is scheduled is in [background-work.md](background-work.md). How a user's key is validated is in [credentials.md](credentials.md).

## The tables

| Table | A row is | What it holds |
|---|---|---|
| `ai_models` | A model of a provider. | The provider and the provider's identifier of the model. What the provider says about it: `display_name`, `context_tokens`, `output_tokens`, `released_at`, `deprecated_at`, `retires_at`. Its `status`, `Active` or `Unavailable`, with `missed_syncs`. And `first_seen_at` and `last_seen_at`. |
| `catalog_sync_runs` | One sync of one provider's catalog. | The provider, the `cause`, `started_at` and `finished_at`, the `result`, `Succeeded` or `Failed`, with the `failure` in one word, and the counts `added`, `changed`, and `unavailable`. |

- **A model is stored once for its provider and identifier.** A unique index holds that. The identifier is what a call to the model names it by: see "The call" in [ai-providers.md](ai-providers.md).
- **What a provider does not say is empty.** Empty is unknown. Nothing is guessed, and nothing is filled in from another model. Which provider says what is in "The list of models" in [ai-providers.md](ai-providers.md).
- **The rows belong to the service.** The catalog is the same for every user, so both tables are listed in `OwnershipRules.cs`: see [ownership.md](ownership.md). A row of a sync names no user and no key, also when a user's key was used.
- **No row of `ai_models` is ever deleted,** so what is recorded about a model later, such as its experience, still points at it.

## When the catalog is synced

| When | Which providers | With which key | Cause |
|---|---|---|---|
| On the schedule: every `CatalogSync:IntervalHours`, 24 in the tracked file. | Every provider that the file holds a `CatalogKey` for. | That key. | `Scheduled` |
| As the application starts, when the catalog is empty. | The same. | The same. | `Scheduled` |
| On demand: `ICatalogSync.SyncAsync`. | The same. | The same. | `OnDemand` |
| When a provider took a user's key: `ICatalogSync.KeyValidatedAsync`. | That provider, when the file holds no `CatalogKey` for it. | The user's key. | `KeyValidated` |

- **The schedule is the job `catalog-sync`,** on `BackgroundJobSchedule.Every`. It is due one interval after the start of its previous run, and at once on a database that it has never run on. See [background-work.md](background-work.md).
- **An empty catalog does not wait for the interval.** The job implements `IDueAtStart`, and says that it is due when a provider that the service has a key for has no model in the catalog. So the application syncs as it starts after the first sync failed, and after a key was added to the file. The run is a run of the job like any other: it syncs every provider that the service has a key for, and the next one is due an interval after it. A start with a catalog that is filled for each of them asks no provider: a restart then costs nothing.
- **A sync on demand answers with the sync of each provider, as it was recorded.** It does not move the schedule. Nothing calls it yet: see "Not built yet".
- **Each provider is synced on its own, and all at the same time.** One that is slow or fails neither delays nor fails another (FR-CAT-7).
- With no `CatalogKey` in the file at all, the job and a sync on demand ask nobody and write nothing. The catalog of a provider is then empty until a key of a user for it is next validated, which is within a day for a key that is stored.

## The key

| Rule | Detail |
|---|---|
| **The service's key is `AiProviders:<Provider>:CatalogKey`.** | Only the Catalog reads it, for each sync, and hands it to the gateway for the one list. `NoSharedKeyTests` fails when anything else reads it. See [configuration.md](configuration.md). |
| **Without one, the catalog is refreshed with the keys of users** (FR-CAT-3). | Credentials tells the Catalog each time a provider took a user's key: when the key is saved, when it is checked daily, and when its user has it checked. The Catalog then asks the provider for its models with that key. Every such check refreshes: none is skipped because another was a moment ago. |
| **With one, no user's key syncs that provider.** | The Catalog decides, and not Credentials, which may not read the file's key. The user's key is still asked for the provider's list, which says what that key can use: see "What a key can use". Nothing of that list is stored in the catalog, and no sync is recorded for it. |
| **A user's key is kept nowhere.** | It is handed to the gateway for the one list. It is not stored, it is in no log event, and no row of the catalog names the user or the key. |
| **What the list gives says nothing about the key.** | The key is stored, and valid, before the Catalog is told. A provider that gives no list, and a catalog that cannot be written, are recorded and logged by the Catalog. `KeyValidatedAsync` fails only when its caller cancels. |
| A list that is asked with a user's key may take 15 seconds. | That is what the check of a key may take: a user waits for both with one form, and for the moment that storing the list takes. A list that is asked with the service's key may take 45 seconds. |
| A provider whose list ran out of that time is left alone for a minute. | The daily check takes the keys of many users one after the other. A provider that does not answer is waited for once in that minute, and not once for every key. This is kept in memory, and holds whichever key the catalog of the provider is synced with. |
| **A list that failed at once keeps no other key from listing.** | A provider that refuses a key the list, or lists no model to it, may do so for that key alone. The keys are checked in the same order every day, so a pause after such a key would keep the same keys after it from ever listing theirs. |

### What a key can use

`KeyValidatedAsync` answers with the identifiers of the models that the provider listed to the key, as the provider writes them and in its order (FR-CAT-9). A key may be listed fewer models than another: Grok lists the models that the key may use, and GPT those of the key's project.

| Rule | Detail |
|---|---|
| **The Catalog is not told whose key it is, and keeps the answer nowhere.** | Credentials keeps it with the key: see "The models a key can use" in [credentials.md](credentials.md). |
| **No list is no answer.** | A provider that gave no list, a list that failed, and a provider that is left alone for its minute all answer with nothing: it is not known what the key can use. A list is never empty, so no answer is ever "none". |
| **The answer is what the provider listed, whatever the catalog holds.** | With a `CatalogKey`, a model that the user's key is listed and the service's key is not is in the answer, and not in the catalog. Without one, a list that could not be stored was listed to the key all the same, and is answered. |
| A list that is asked only for this, with a `CatalogKey` in the file, is not a sync. | Nothing is written. A provider that gives no list is a `Warning` in the log, and an error of the application an `Error`: see "The log". |

## What a sync does

1. It notes the time, and asks the gateway for the provider's list, with no transaction open.
2. Without a list, it records a failed sync, and nothing else is written.
3. With a list, it stores the list and the sync in one transaction. The transaction begins by locking the provider's row of `ai_providers`, so two syncs of one provider store one after the other.

| A model that | Becomes |
|---|---|
| The list names, and the catalog does not hold. | A new row: `Active`, first seen and last seen at the start of the sync. It is counted as added. |
| The list names, and the catalog holds. | Seen at the start of the sync, with no missed sync, and `Active`. It is counted as changed when the provider says something else about it than the row holds, and when it was `Unavailable`. |
| The catalog holds, and the list does not name. | Missed once more. It is marked `Unavailable` by the rule below, and is then counted as unavailable. |

A sync that finds the catalog as the list has it counts nothing.

## Unavailable models

A model is marked `Unavailable` when as many syncs in a row lacked it as `CatalogSync:MissedSyncsBeforeUnavailable` says, which is 2 in the tracked file (FR-CAT-5). A sync that failed is none of them.

| Synced with | A model is marked when | Why |
|---|---|---|
| The service's key | That many syncs in a row lacked it. | Every list is the view of the same key, so a model that it lacks that often is gone. On the schedule that is two days with the tracked file. Syncs on demand count like any other, so two of them confirm that a model was retired. |
| The keys of users | That many syncs in a row lacked it, **and no sync has listed it for that many days and half a day**: 60 hours with the tracked file. | See below. |

With the keys of users, a provider is synced as often as keys are validated, and a key may list fewer models than another: Grok lists the models that the key may use, and GPT those of the key's project. The number of syncs then says nothing: two checks of a key that sees less would take a model away from every user.

What counts instead is how often a key that does see the model could have listed it. Every stored key is checked daily, an hour later each day (FR-KEY-3). So the days that no key has listed a model stand for the syncs that it missed:

- A model that one user's key alone lists is seen once a day. It stays through a check of that key that gave no list, as a model stays through one sync that lacks it on the schedule.
- It is marked when that key has not listed it at two checks in a row, which is after 50 hours, and before its third, at 75.
- The time does not come from `CatalogSync:IntervalHours`: the keys are checked daily whatever the interval of the schedule is.

| Rule | Detail |
|---|---|
| **The row stays** (FR-CAT-5). | It keeps what was known about the model, and when it was last seen. |
| **A model that reappears is `Active` again.** | The same row, counted as changed. |

The log has an event for each model that is marked. Whatever depends on the model is told in the same transaction: see "What depends on a model".

## Which models can be selected

Only an eligible model can be selected for an agent (FR-CAT-6). Which models are eligible is set in the configuration file, because the providers do not report what their models can do in one way: a provider's list holds the models that write no text beside those that do, and only some say whether a model returns a structured result.

| Key | What it says |
|---|---|
| `AiProviders:<Provider>:Allow` | Patterns of the models that may be selected. |
| `AiProviders:<Provider>:Deny` | Patterns of the models that may not, whatever `Allow` says. |
| `AiProviders:<Provider>:LiveSearch` | Patterns of the models that can search live, which a selector shows (FR-CYC-3). |

- **A model is eligible when an `Allow` pattern of its provider names it, and no `Deny` pattern does.** A provider with no `Allow` pattern has no eligible model: a model is selected because someone allowed it, and never because nobody thought of it. The tracked file allows the models that each provider names after itself, such as `claude-*`. A deployment lists what it has tried.
- **A pattern is the identifier of a model, in which `*` stands for any run of characters,** an empty one included. Nothing else in it is special, and letter case is ignored. `gpt-5*` names `gpt-5` and `gpt-5.5-mini`, and `*-preview` every identifier that ends so.
- **To allow a model is to say that it can do the work of a role.** Every role returns a structured result (FR-TEAM-6), so a model that cannot return one is not allowed. There is no list for that capability beside `Allow`: it would say the same again.
- **`LiveSearch` says something of a model, and allows none.** A model that cannot search live is selected like any other: its role runs without live data.
- **A model that can be selected is `Active` too.** One that its provider no longer offers cannot be selected until the provider lists it again.
- **Nothing of this is stored.** The file is read once, as the application starts, and the patterns are held against the active models when someone asks. A pattern that was changed holds for every model of the catalog from the next start, with no sync in between. A model that the file no longer allows tells nothing to an agent that was given it before.

`ISelectableModels` answers, the same for every user:

```csharp
public interface ISelectableModels
{
    Task<IReadOnlyList<SelectableModel>> ListAsync(CancellationToken cancellationToken);

    Task<SelectableModel?> FindAsync(Guid modelId, CancellationToken cancellationToken);
}
```

A `SelectableModel` is the id of the model in the catalog, its provider, its identifier, its name, and whether it can search live. Which of these a user's own key can use is for Credentials to say, and that is what a selector offers: see "The models a key can use" in [credentials.md](credentials.md).

### What depends on a model

When a model assigned to an agent becomes unavailable, the agent moves to Attention required, and the system does not substitute another model on its own (FR-CAT-8). The Catalog may not use Agents, so it declares `IModelDependents`, and Agents implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IModelDependents
{
    Task ModelsUnavailableAsync(
        EcnanibDbContext context, AiProviderCode provider, IReadOnlyList<Guid> modelIds, CancellationToken cancellationToken);
}
```

- **It is told inside the transaction that stores the sync,** with the context that holds it, before the rows change: while the models are still `Active`. What an implementation changes is stored when the models are marked, and not when they are not.
- **It is told once:** when models that were active become unavailable, with all of them that one sync marks. A model that stays unavailable tells nothing again, and neither does one that is active again: its user decides what the agent does then.
- **When it fails, the sync fails.** The transaction is undone, and the sync is recorded as one that failed, with `Error`. The catalog of that provider is as it was, for every user, a new model included, until a later sync can tell it. An agent is not left running on a model that is taken for gone.
- **Nothing is put in the place of a model.** The Catalog names the models that go, and offers no other. A model that is unavailable is not among the models that can be selected, and the gateway calls the model that it is given: see "The call" in [ai-providers.md](ai-providers.md).
- **An implementation commits nothing, and takes the owner of an agent from the agent's row.** With the keys of users the sync runs in the request of the user whose key listed the models, and the agents are those of every user.
- **It minds the locks.** The transaction holds the row of the provider and the rows of its models, and two providers are synced at the same time. An implementation locks its rows in one order, and takes no lock on a model while it holds one on an account. It takes nothing of Credentials, which itself takes the Catalog.
- A model that an agent was given while the sync was storing is not among what an implementation finds. The call that then reports that the model does not exist moves that agent: see "Not built yet".
- **Nothing implements it yet,** because agents are not built. Until then nothing depends on a model. `ModelDependentsTests` has a test that fails as soon as Agents has a table of its own and no implementation.

## A sync that fails

| `failure` | When |
|---|---|
| `KeyRefused` | The provider answered as it answers a wrong key. |
| `TimedOut` | The list took longer than it may, or the sync as a whole did: see "How long a sync takes". |
| `NoList` | Anything else that gave no list: no connection, a limit on requests, a fault of the provider, or an answer that is not its list, an empty one included. |
| `Error` | Something went wrong in the application itself: storing the list, most likely, or telling what depends on a model that the sync would mark. The log has the error. |

- **The catalog of that provider is as it was** (FR-CAT-7). No model is changed, and none is counted as missed: a sync that failed says nothing about what the provider offers.
- **The other providers are synced all the same.**
- **A list is whole or it is none.** The gateway hands on no part of one, because the models of the rest would look gone. See "The list of models" in [ai-providers.md](ai-providers.md).
- **The row says that it failed, and why in one word of the application's own.** It holds no text of an error and nothing that a provider wrote. Those are in the log (NFR-DAT-7).
- A failed sync is not tried again before the next one is due, or before the application starts with that provider's catalog still empty. A failure that repeats does not become a loop.
- A failed sync whose row cannot be written either is in the log, as an `Error`, and in the answer of a sync on demand all the same.

## How long a sync takes

A sync of one provider finishes within a minute (NFR-PRF-4). It has 55 seconds in all, by `CatalogSync.Limit`, which leaves it the time to record how it ended. The list has 45 of them for all of its pages, by `ModelListCall.Limit`, and what is left is for the one transaction that stores it. A sync that runs out of its time while it waits to store, behind another sync of the same provider, gives up: nothing of the list is stored, and it is recorded as `TimedOut`. The providers are synced at the same time, so the whole run takes no longer than its slowest provider.

## The log

The Catalog logs under the area `Ecnanib.Catalog.CatalogSync`. Each event carries the provider as `Provider`.

| Level | Event |
|---|---|
| `Information` | `The catalog of the AI provider claude was synced with the 12 models that the provider lists: 1 added, 0 changed, and 0 marked unavailable` |
| `Information` | `The model claude-haiku-4-5 of the AI provider claude is marked unavailable: the provider has not listed it in the last 2 syncs or more` |
| `Warning` | `The catalog of the AI provider claude was not synced: TimedOut. The catalog stays as it is`, with the `failure` of the sync |
| `Warning` | `The configuration file holds a catalog key for mistral, which is not an AI provider whose models the application can list, so nothing is synced for it` |
| `Error` | `The catalog of the AI provider claude was not synced, because of an error. The catalog stays as it is`, with the error. |
| `Error` | `The failed sync of the catalog of the AI provider claude could not be recorded`, with the error. |
| `Warning` | `The AI provider claude gave a user's key no list of its models: TimedOut. Which models the key can use is not known`, with the word that a failed sync is recorded with. Only where the file holds a `CatalogKey` for the provider: without one, such a list is a sync that was not synced. |
| `Error` | `The AI provider claude could not be asked for the models that it lists to a user's key, because of an error. Which models the key can use is not known`, with the error. |

The calls to the provider are in the log of outbound calls, and an answer that is no list in the gateway's own event: see [logging.md](logging.md) and [ai-providers.md](ai-providers.md). A sync that fails because of the provider is a warning, and not an error of the application.

## Testing

No test reaches a provider. `StandInModelLists` stands for the providers' lists: it keeps what it was asked, with which key, and lists what the test says. `CatalogApplication` runs the application with it, on a database of the test's own. `AccountsApplication` has it too, because a key that a test stores is handed to the Catalog. A test that is about what depends on a model registers a stand-in of its own for `IModelDependents`, with the `services` of `CatalogApplication`.

The configuration file of the tests holds no `CatalogKey`, as the tracked file holds none, so an application of a test syncs nothing by itself. A test that is about the key adds it with `SettingsJson.WithCatalogKey()`. Its application then syncs as it starts, and `CatalogApplication.StartedAsync` waits for that.

The file of the tests allows the models of Claude that `claude-*` names, but for those that `*-preview` names, and no model of another provider. A test that needs other patterns writes them into its own file, with `SettingsJson.WithJson`. What is eligible is asked through `ISelectableModels`, as a component asks.

## Not built yet

- What Agents does when a model becomes unavailable (FR-CAT-8). It implements `IModelDependents` when agents and their states are built, and moves the agent to Attention required.
- What follows a call that reports that the model does not exist (FR-CAT-8). The gateway answers such a call with the outcome `ModelNotFound`: see "Outcomes" in [ai-providers.md](ai-providers.md). What calls a model for an agent moves the agent then. The Catalog is not told, and marks nothing for it: a model is marked by the provider's list alone.
- Telling the user that a model is unavailable (FR-NTF-1). In-app notifications are not built.
- The model selector (FR-AGT-5). It offers what `IKeyModels` of Credentials answers, and shows which models can search live.
- Telling an agent that its model is no longer eligible, after the patterns of the file were changed. The model cannot be selected anew, and an agent that has it keeps it.
- The catalog screen (FR-CAT-10).
- The operator's button that triggers a sync, and its event in the audit trail (FR-OPS-4, FR-OPS-5). `ICatalogSync.SyncAsync` is what it calls.
- Deleting old rows of `catalog_sync_runs`. Every sync adds a row. With the keys of users that is a row for each check of a key, which is about one a day for each stored key.
- Prices (FR-CAT-11). Grok lists them, and they are not kept.
