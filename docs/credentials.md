# Credentials

A user brings their own keys: one for each AI provider, and one Binance credential for each environment, Demo and Live (FR-KEY-1, FR-KEY-2). A key is stored encrypted and is write-only: after saving, only its last four characters are shown (FR-KEY-8). A user can replace or delete any key at any time, and deletion is permanent (FR-KEY-10). Adding or replacing a key takes a fresh proof of who the user is (FR-ACC-4), and every change to a key is recorded in the audit trail (NFR-LOG-7).

A key is validated with a read-only call to its provider: when it is saved, again daily, and when a call with it fails authentication (FR-KEY-3). Only a key that its provider takes is stored. A Binance key that can withdraw is never held: Ecnanib takes no custody of funds (FR-KEY-4, NFR-LEG-2). A Binance key that lacks the permission of an agent's market keeps that agent from starting (FR-KEY-5).

The code is in `src/Ecnanib/Credentials`, and the pages in `src/Ecnanib/WebUi/Pages/Account`. The calls to the providers are in the gateways: `src/Ecnanib/AiProviderGateway/Adapters` and `src/Ecnanib/ExchangeGateway/Adapters/Binance`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Credentials`, `tests/Ecnanib.Tests/AiProviderGateway`, and `tests/Ecnanib.Tests/ExchangeGateway`.

How a stored secret is encrypted is in [security.md](security.md). The proof before a sensitive action, the audit trail, and the export of a user's data are in [accounts.md](accounts.md). Whose rows a request reaches is in [ownership.md](ownership.md).

## The keys

| Key | A user has one for each | Table |
|---|---|---|
| The key of an AI provider | Provider: a row of `ai_providers`, which are Claude, Gemini, GPT, and Grok | `ai_credentials` |
| A Binance credential: an API key and its secret key | Environment: Demo, which is Binance's test environment, and Live | `exchange_credentials` |

- **The providers are the rows of their table,** and not a list in this component. A provider that is added there has a place on the page. See "Adding an AI provider" in [architecture.md](architecture.md).
- **A second key takes the place of the first.** The database holds one row for each user and provider, and one for each user and environment, with a unique index on each pair.
- **Ecnanib has no key of its own for a decision or an order** (FR-KEY-1). The configuration file has no place for a key of Binance. The one key it may hold for an AI provider is `CatalogKey`, for syncing the model catalog (FR-CAT-3). `NoSharedKeyTests` pins the secrets that the file can hold, and fails when anything but `Platform` and the catalog reads that key. The gateways take a key with each call and never fetch one.

## The pages

All are for signed-in users, and rendered on the server for each request.

| Path | What it does |
|---|---|
| `/account/keys` | Lists every provider and both environments, each with the key that is stored or with none, and leads to the pages below. A stored key is shown with what its last check found and when, and a Binance credential with what it may trade on and, where it works from any address, the warning about that. Each stored key has a button that checks it again. The account page leads here. |
| `/account/keys/ai/<provider>` | Stores the key for a provider, as a new one or in place of the stored one. The provider is its code, such as `claude`. It says so when the provider did not take the key. |
| `/account/keys/binance/<environment>` | Stores the API key and the secret key for an environment: `demo` or `live`. It says that no key may withdraw, shows the addresses that the service calls Binance from, and says so when Binance did not take the keys. |
| `/account/keys/ai/<provider>/delete`, `/account/keys/binance/<environment>/delete` | Says which key goes, and deletes it when the button is pressed. |

A path that names no provider or no environment is answered with "not found". Whose keys a page shows is never in the path: it is the user of the session.

## Storing a key

`IStoredKeys.StoreAiKeyAsync` and `StoreExchangeKeyAsync` store a key for the user who is signed in on the request.

| Rule | Detail |
|---|---|
| **It takes a proof** (FR-ACC-4). | The action asks `IReauthentication.IsRecentAsync` with `SensitiveAction.AddOrReplaceKey` first, on the server, in the request that brings the key. Without a proof of the last ten minutes on that browser, nothing is stored. It asks inside its transaction, under the lock on the account's row, so the answer holds until the key is stored. The pages ask too, before they show the form, and send the browser to `/confirm-identity`, which leads back. See "Re-authentication" in [accounts.md](accounts.md). |
| **It takes a verified address.** | An address that is not verified may have been registered by someone it does not belong to. Its owner can still take the account over by registering it again, and would take over a key that is stored in it: one that someone planted there, or the key of someone who mistyped their address. So nothing is stored until the address is verified. The pages say so in place of the form, and the list offers no way to add a key. See "An address that was registered again" in [accounts.md](accounts.md). |
| What a key is. | What was entered, without the spaces and line breaks around it: 16 to 512 characters of the ASCII set, with no space among them (`KeyRules`). Sixteen, because a key is shown by its last four characters, which must not be most of it. What passes is shown to the provider next. |
| **Only a key that its provider takes is stored** (FR-KEY-3). | The provider is asked about the key with a call that only reads: see "Validating a key". A key that it refuses is not stored, and neither is one that it gave no answer about: the page says which, and the user tries again. A key that was stored before stays as it was. So a stored key is one that passed a check, and is stored as `Valid` with the time of that check. |
| **A Binance key that can withdraw is not stored** (FR-KEY-4, NFR-LEG-2). | Binance took it, and it is refused all the same. See "Binance permissions". |
| The provider is asked between two transactions. | A provider may take seconds to answer, so no transaction and no connection is held meanwhile. The first transaction asks whether the user may store a key at all, the proof and the verified address, so that no call goes out for a request that would be refused. The second asks that again under the lock, and stores the key: the proof may have ended while the provider was asked. |
| A Binance credential is both keys. | The API key and the secret key are stored together or not at all, as one encrypted value: the API key, a line feed, and the secret key. |
| A key is replaced in place. | The row keeps its id, so that what points at the key still does, and the audit trail names the same key. The key that was stored is gone with that statement, encrypted or not. |
| Changes happen one after the other. | Each starts by locking the row of the account, as the changes to an account do. So a form that is sent twice stores one key, and the second time replaces it: `KeyPageTests` sends six at once. The deletion of the account takes the stronger lock of a deletion: see "Deleting the account" in [accounts.md](accounts.md). |
| It is recorded. | `KeyAdded` or `KeyReplaced`, in the same transaction, with the key as `ai-key:<id>` or `exchange-key:<id>`. |

## Write-only

A key comes in with a form, is shown to its provider, is encrypted, and is stored. No page and no export reads it back. One place decrypts a stored key: `KeyValidator`, for the call that asks the provider about it again (FR-KEY-8, FR-KEY-9).

| Where | How a key stays out of it |
|---|---|
| The database | It holds the key only encrypted: AES-256-GCM under the active master key, with the id of that key beside it, bound to the row and to its owner, as `ai-key:<user id>:<id>` and `exchange-key:<user id>:<id>`. A value that is copied to another row, or a row that is handed to another user, does not decrypt there. Both columns are declared with `AddStoredSecret`, so the values follow the active master key (FR-KEY-6, FR-KEY-7). See "Stored secrets" in [security.md](security.md). |
| A page | What a page shows is read from the columns beside the key: `masked_suffix`, which is its last four characters, `saved_at`, and what the last check found. The encrypted key is not read for a page at all. A key is shown as `••••` and those four characters. Of a Binance credential they are the last four of the API key, and nothing of the secret key is stored readable or shown. |
| A form | A field that takes a key is never written back into the page, also when what was entered is refused or the provider did not take it, and the message that says why does not repeat it. The fields ask the browser not to keep what is typed. |
| A provider | A key is sent to its own provider and to nothing else, in a header of the call: never in an address, which is logged. The secret key of a Binance credential is not sent at all: it signs the call. |
| The log | No event of this component takes a key or its last characters. An event names the user, and the provider or the environment: `User {UserId} added a key of the AI provider {Provider}`. The log of the call to the provider leaves out the headers and the query, which hold the key and the signature. The tests read the log at the `Debug` level and find no key in it, in any form that bytes are written in. See "Masking" in [logging.md](logging.md) for what protects a key that is logged by mistake, and what does not. |
| The audit trail | An event names the key by the id of its row, and nothing that makes an event takes text. See "The audit trail" in [accounts.md](accounts.md). |
| The export | The sections `aiKeys` and `exchangeKeys` say which keys are stored: the id, the provider or the environment, the last four characters, when it was saved, and what the last check found. See "Exporting your data" in [accounts.md](accounts.md). |
| An error | No exception of this component carries what was entered, and an unhandled error shows the user a reference id and nothing else. See "Unhandled errors" in [logging.md](logging.md). |
| A prompt | Nothing that composes a prompt can read a key. What decrypts one is internal to this component, and hands the key to the gateway and to nothing else. |

## Validating a key

A key is validated by asking its provider about it, with a call that only reads and changes nothing (FR-KEY-3). The gateways make the call, and say what the answer means as a `KeyVerdict`.

| Provider | The call | A wrong key is answered with |
|---|---|---|
| Claude | `GET /v1/models`, the key in `x-api-key` | 401 |
| GPT | `GET /v1/models`, the key as a bearer token | 401 |
| Grok | `GET /v1/models`, the key as a bearer token | 400, or 401 |
| Gemini | `GET /v1beta/models`, the key in `x-goog-api-key` | 400 with the reason `API_KEY_INVALID`, or 401 |
| Binance, Live | `GET /sapi/v1/account/apiRestrictions`, signed, at the address of the Spot market | 401, or one of the errors `-1002`, `-1022`, `-2008`, `-2014`, `-2015` |
| Binance, Demo | `GET /api/v3/account` and `GET /fapi/v1/accountConfig`, signed, each at the address of its market | The same |

| Verdict | When | What follows |
|---|---|---|
| `Accepted` | The provider took the key. | The key is `Valid`. |
| `Refused` | The provider answered as it answers a wrong key. For Binance that is also the answer to a key that works only from other addresses than the service's. | A key that is being saved is not stored. A stored key becomes `Invalid`. |
| `Unreachable` | Anything else: no answer within 15 seconds, no connection, a limit on requests, a block of the service's address, a fault of the provider, or an answer that is not what the provider sends. | Nothing is known about the key, and nothing is written. A key that is being saved is not stored. A stored key is asked about again later. |

- **Only the provider's answer to a wrong key is a refusal.** Each provider's check names its own, in `AiProviderGateway/Adapters/<Provider>`. A status that could also mean something else, such as 403, is no verdict: a block of the service's region must not turn every stored key invalid.
- **Binance is asked differently in its two environments,** because its test environment has no call that reports the permissions of a key. On the real exchange one call says everything. In the test environment the account of each market is read: a market that takes the credential says whether it may trade there, and a market that refuses a credential which the other one takes is one it may not trade on. Both refusing is a refusal. A market that gives no answer leaves no verdict.
- **The addresses are those of the configuration file:** `AiProviders:<Provider>:BaseUrl`, and `Binance:<Market>:<Environment>:BaseUrl`. A check has a time limit of its own, 15 seconds, and not the provider's `TimeoutSeconds`: a user waits for it with a form.

### When a key is validated

| When | How |
|---|---|
| When it is saved. | In the request that brings it, before it is stored: see "Storing a key". |
| Daily. | The job `key-validation` runs every hour, on the hour, and checks every key whose provider last answered a check a day ago or longer, and every key that was never checked. So each key is checked about a day after its last check, and not all keys at one time of day. See [background-work.md](background-work.md). |
| When a call with it fails authentication. | Whatever made the call asks `IKeyValidation.RevalidateAiKeyAsync` or `RevalidateExchangeKeyAsync`, which checks the key at once. Nothing calls them yet: see "Not built yet". |
| When its user asks. | The button "Check again" on `/account/keys`, for a user who changed something about the key at the provider. Binance shows a secret key only once, so a user who turns a permission on cannot enter the key again. It takes no proof: it shows nothing of the key and stores none. |

All but the first come to `KeyValidator`, which reads the stored key, asks the provider, and records what it said.

| Result | What is written |
|---|---|
| The provider took the key. | `Valid`, the time, and for Binance what the key may do. |
| The provider refused it. | `Invalid`, and the time. What depends on the key is told: see "What depends on a key". The key stays stored, and is asked about again every day: it is valid again when the provider takes it. |
| The provider gave no answer. | Nothing. The key is as it was, and still due. |
| Binance reports that the key can withdraw. | The key is deleted. See "Binance permissions". |
| The stored value does not decrypt. | Nothing, and nobody is asked. The log has an `Error`. See "Changing the master key" in [security.md](security.md). |

| Rule | Detail |
|---|---|
| **A stored key is decrypted in one place, for one call** (FR-KEY-9). | `KeyValidator` reads the key, hands it to the gateway, and clears the bytes it was decrypted into. |
| **A key that is replaced meanwhile keeps what was found about itself.** | The provider is asked with no transaction open. What it said is written only where the row still holds the encrypted value that was read. A key that took its place was checked when it was stored, and is not given the old key's result. |
| **Every change to a user's keys takes one lock first.** | A write that tells what depends on the key locks the row of the account, then tells, then changes the row of the key: the order in which a user's own deletion does it. |
| A key is asked about once a minute at most. | A provider that gives no answer leaves no time in the row, so the attempts are counted in memory. A button that is pressed again, or a call that fails again and again, does not have the provider asked each time. |
| One key does not keep the others from being checked. | The job logs what goes wrong with a key as an `Error`, and goes on. |
| A provider that is down is asked once in a run. | After the first key that it gives no answer about, the job leaves that provider, or that environment of Binance, alone until the next hour. |

The log has an event for a key that turned out invalid, was deleted, or could not be checked, under the area `Ecnanib.Credentials.KeyValidator`: `The key of user {UserId} for the AI provider {Provider} was refused by the provider, and is invalid`. A key that is still valid is not logged: the log of outbound calls has the call.

## Binance permissions

What Binance says a credential may do is recorded when the credential is checked, in the columns beside it.

| What | Where Binance reports it | What follows |
|---|---|---|
| Whether it can withdraw. | On the real exchange. The test environment holds no real funds, and reports nothing about the key. | A key that can withdraw is refused: see below. |
| Whether it works only from listed IP addresses. | On the real exchange. | A key that works from any address is stored, with a warning on `/account/keys`. |
| Whether it may trade on the Spot market, and on the Futures market. | In both environments. | An agent needs the permission of its market to start: see "What an agent needs". |

- **A key that can withdraw is never held** (FR-KEY-4, NFR-LEG-2). One that is being saved is not stored, and the page says to turn withdrawals off at Binance. One that is stored and turns out to be able to withdraw when it is checked again, because its user turned withdrawals on since, is deleted at once: what depends on it is told as when its user deletes it, and the audit trail records `KeyRejected`, with the owner of the key and no origin, because no request did it. An answer of Binance that does not say whether the key can withdraw is no verdict, so a key is never taken for one that cannot because the answer left it out.
- **The warning comes with the addresses to allow** (FR-KEY-4). `Binance:OutboundIps` in the configuration file lists the fixed addresses that the service calls Binance from. The page that takes the keys shows them before a key is entered, and the warning names them. With none listed, the warning stands alone. The addresses are shown on pages only, and are in no log event.
- **A refusal may be about the addresses.** Binance answers a key that works only from other addresses as it answers a wrong key. So the page that says Binance did not take the keys says that too, beside the addresses.

### What an agent needs

A Binance key that lacks the permission an agent's market needs keeps that agent from starting, with a message that names the permission (FR-KEY-5). Credentials may not use Agents, so it answers the question, and Agents asks it before an agent starts.

```csharp
public interface IKeyValidation
{
    Task<KeyCheckOutcome> RevalidateAiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    Task<KeyCheckOutcome> RevalidateExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken);

    Task<StartBlock?> BlockForAsync(Guid userId, TradingMode environment, Market market, CancellationToken cancellationToken);
}
```

| `BlockForAsync` answers | When |
|---|---|
| Nothing | The user's credential for the environment is valid and may trade on the market. |
| `NoKey` | The user has stored no credential for the environment. |
| `NotValid` | Binance refused the credential when it was last checked, or it was never checked. |
| `SpotTradingMissing`, `FuturesTradingMissing` | The credential lacks the permission of the market. |

- It is answered from what the last check recorded, and asks Binance nothing.
- `KeyPages.Says` makes the message of a block, which names the permission and says what to do: turn it on at Binance, and check the key again on `/account/keys`.
- **Nothing asks it yet,** because agents are not built. `AgentStartBlockTests` has a test that fails as soon as Agents has a table of its own and takes no `IKeyValidation`.

## Deleting a key

`IStoredKeys.DeleteAiKeyAsync` and `DeleteExchangeKeyAsync` delete a key of the user who is signed in on the request.

| Rule | Detail |
|---|---|
| It takes no proof. | Only adding and replacing a key are sensitive actions (FR-ACC-4). A key that is deleted exposes nothing. The page asks once more instead, and offers to keep the key. |
| **It is for good** (FR-KEY-10). | The row is deleted, and with it the encrypted key. Nothing is marked now and removed later, and nothing brings the key back: the user adds one anew, with a proof. |
| It is recorded. | `KeyDeleted`, in the same transaction. |
| A form that is sent twice is answered as the first one was. | Where nothing is stored, nothing happens, and nothing is recorded. |
| The keys of an account go with it. | Both tables hang on `users` with a foreign key that deletes the rows with the account, at once (FR-ACC-9). |
| The service deletes one kind of key itself. | A stored Binance key that turns out to be able to withdraw, when it is checked again (FR-KEY-4). It is deleted in the same way, for good, and recorded as `KeyRejected`. See "Binance permissions". |

### What depends on a key

An agent that depends on a missing or invalid key moves to Attention required (FR-KEY-10). Credentials may not use Agents, so it declares `IKeyDependents`, and Agents implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IKeyDependents
{
    Task AiKeyDeletedAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    Task ExchangeKeyDeletedAsync(EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken);

    Task AiKeyInvalidAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    Task ExchangeKeyInvalidAsync(EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken);
}
```

- **It is told inside the transaction that deletes the key or marks it invalid,** with the context that holds it, before the row changes: while the key is still stored, and still marked as it was. What an implementation changes is stored when the key is deleted or marked, and not when it is not.
- **When it fails, the key stays as it was.** The transaction is undone. A request ends in an error, and the job logs one and tries again with its next run. An agent is not left running on a key that is gone or refused.
- **A key that the service deletes is told like one that its user deletes:** the Binance key that turned out to be able to withdraw.
- **It is told once that a key is invalid:** when a key that was not invalid becomes so. A key that is still refused the next day tells nothing again.
- It is not told when a key is added or replaced, nor when an invalid key is valid again: a key is then where an agent looks for it, and its user starts the agent.
- **Nothing implements it yet,** because agents are not built. Until then nothing depends on a key. `KeyDependentsTests` has a test that fails as soon as Agents has a table of its own and no implementation.

## The data

| Table | Columns |
|---|---|
| `ai_credentials` | `id`, `user_id`, `provider`, `encrypted_value`, `master_key_id`, `masked_suffix`, `saved_at`, `status`, `validated_at`. One row for each `(user_id, provider)`. `provider` points at `ai_providers.code`. |
| `exchange_credentials` | `id`, `user_id`, `environment`, `encrypted_value`, `master_key_id`, `masked_suffix`, `saved_at`, `status`, `validated_at`, `spot_trading`, `futures_trading`, `ip_restricted`. One row for each `(user_id, environment)`. `environment` is `Demo` or `Live`. |

Both hold rows of users: their entities implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. The rows of an account are deleted with it. `saved_at` is when the key that is stored now was stored.

| Column | What it holds |
|---|---|
| `status` | What the last check found: `Valid`, `Invalid`, or `Unchecked`. `Unchecked` is a key that was stored before keys were checked: the migration leaves the rows that were there so, and the job takes them with its next run. A key that is stored now is never `Unchecked`. |
| `validated_at` | When the provider last answered a check of the key that is stored now. Empty for a key that was never checked. A check that got no answer leaves it as it is. |
| `spot_trading`, `futures_trading` | Whether the credential may trade on each market, as Binance last said. |
| `ip_restricted` | Whether the credential works only from listed addresses, or empty where Binance does not report it: its test environment does not. |

Whether a key can withdraw is not stored: a key that can is not.

## Not built yet

- Handing a key to the call of an agent that needs it, decrypted only in memory and only for that call (FR-KEY-9). So far a stored key is decrypted for the call that validates it. The calls to models and the orders come with the gateways.
- Checking a key again when a call with it fails authentication. `IKeyValidation` takes the request, and nothing makes such a call yet. The gateways say how a call tells that authentication failed when they are built.
- What Agents does when a key goes or turns out to be invalid, and before an agent starts. It implements `IKeyDependents` and asks `IKeyValidation.BlockForAsync` when agents are built.
- Telling the user that a key is invalid (FR-NTF-1). The list of keys says so. In-app notifications are not built.
- A share of Binance's request limit for the checks (FR-BIN-4). A check of a key on the real exchange weighs 1, and one in the test environment 25. The limiter is not built.
- The usage of each AI key: calls, tokens, and estimated cost (FR-KEY-11).
- Which catalog models a key can use (FR-CAT-9).
