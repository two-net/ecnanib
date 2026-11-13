# Credentials

A user brings their own keys: one for each AI provider, and one Binance credential for each environment, Demo and Live (FR-KEY-1, FR-KEY-2). A key is stored encrypted and is write-only: after saving, only its last four characters are shown (FR-KEY-8). A user can replace or delete any key at any time, and deletion is permanent (FR-KEY-10). Adding or replacing a key takes a fresh proof of who the user is (FR-ACC-4), and every change to a key is recorded in the audit trail (NFR-LOG-7).

The code is in `src/Ecnanib/Credentials`, and the pages in `src/Ecnanib/WebUi/Pages/Account`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Credentials`.

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
| `/account/keys` | Lists every provider and both environments, each with the key that is stored or with none, and leads to the pages below. The account page leads here. |
| `/account/keys/ai/<provider>` | Stores the key for a provider, as a new one or in place of the stored one. The provider is its code, such as `claude`. |
| `/account/keys/binance/<environment>` | Stores the API key and the secret key for an environment: `demo` or `live`. |
| `/account/keys/ai/<provider>/delete`, `/account/keys/binance/<environment>/delete` | Says which key goes, and deletes it when the button is pressed. |

A path that names no provider or no environment is answered with "not found". Whose keys a page shows is never in the path: it is the user of the session.

## Storing a key

`IStoredKeys.StoreAiKeyAsync` and `StoreExchangeKeyAsync` store a key for the user who is signed in on the request.

| Rule | Detail |
|---|---|
| **It takes a proof** (FR-ACC-4). | The action asks `IReauthentication.IsRecentAsync` with `SensitiveAction.AddOrReplaceKey` first, on the server, in the request that brings the key. Without a proof of the last ten minutes on that browser, nothing is stored. It asks inside its transaction, under the lock on the account's row, so the answer holds until the key is stored. The pages ask too, before they show the form, and send the browser to `/confirm-identity`, which leads back. See "Re-authentication" in [accounts.md](accounts.md). |
| **It takes a verified address.** | An address that is not verified may have been registered by someone it does not belong to. Its owner can still take the account over by registering it again, and would take over a key that is stored in it: one that someone planted there, or the key of someone who mistyped their address. So nothing is stored until the address is verified. The pages say so in place of the form, and the list offers no way to add a key. See "An address that was registered again" in [accounts.md](accounts.md). |
| What a key is. | What was entered, without the spaces and line breaks around it: 16 to 512 characters of the ASCII set, with no space among them (`KeyRules`). This is not a check that the provider accepts the key: see "Not built yet". Sixteen, because a key is shown by its last four characters, which must not be most of it. |
| A Binance credential is both keys. | The API key and the secret key are stored together or not at all, as one encrypted value: the API key, a line feed, and the secret key. |
| A key is replaced in place. | The row keeps its id, so that what points at the key still does, and the audit trail names the same key. The key that was stored is gone with that statement, encrypted or not. |
| Changes happen one after the other. | Each starts by locking the row of the account, as the changes to an account do. So a form that is sent twice stores one key, and the second time replaces it: `KeyPageTests` sends six at once. The deletion of the account takes the stronger lock of a deletion: see "Deleting the account" in [accounts.md](accounts.md). |
| It is recorded. | `KeyAdded` or `KeyReplaced`, in the same transaction, with the key as `ai-key:<id>` or `exchange-key:<id>`. |

## Write-only

A key comes in with a form, is encrypted, and is stored. Nothing reads it back: no page, no export, and no other code yet (FR-KEY-8).

| Where | How a key stays out of it |
|---|---|
| The database | It holds the key only encrypted: AES-256-GCM under the active master key, with the id of that key beside it, bound to the row and to its owner, as `ai-key:<user id>:<id>` and `exchange-key:<user id>:<id>`. A value that is copied to another row, or a row that is handed to another user, does not decrypt there. Both columns are declared with `AddStoredSecret`, so the values follow the active master key (FR-KEY-6, FR-KEY-7). See "Stored secrets" in [security.md](security.md). |
| A page | What a page shows is read from the columns beside the key: `masked_suffix`, which is its last four characters, and `saved_at`. The encrypted key is not read for a page at all. A key is shown as `••••` and those four characters. Of a Binance credential they are the last four of the API key, and nothing of the secret key is stored readable or shown. |
| A form | A field that takes a key is never written back into the page, also when what was entered is refused, and the message that says why does not repeat it. The fields ask the browser not to keep what is typed. |
| The log | No event of this component takes a key or its last characters. An event names the user, and the provider or the environment: `User {UserId} added a key of the AI provider {Provider}`. The tests read the log at the `Debug` level and find no key in it, in any form that bytes are written in. See "Masking" in [logging.md](logging.md) for what protects a key that is logged by mistake, and what does not. |
| The audit trail | An event names the key by the id of its row, and nothing that makes an event takes text. See "The audit trail" in [accounts.md](accounts.md). |
| The export | The sections `aiKeys` and `exchangeKeys` say which keys are stored: the id, the provider or the environment, the last four characters, and when it was saved. See "Exporting your data" in [accounts.md](accounts.md). |
| An error | No exception of this component carries what was entered, and an unhandled error shows the user a reference id and nothing else. See "Unhandled errors" in [logging.md](logging.md). |
| A prompt | Nothing that composes a prompt can read a key: no code decrypts one yet. See "Not built yet". |

## Deleting a key

`IStoredKeys.DeleteAiKeyAsync` and `DeleteExchangeKeyAsync` delete a key of the user who is signed in on the request.

| Rule | Detail |
|---|---|
| It takes no proof. | Only adding and replacing a key are sensitive actions (FR-ACC-4). A key that is deleted exposes nothing. The page asks once more instead, and offers to keep the key. |
| **It is for good** (FR-KEY-10). | The row is deleted, and with it the encrypted key. Nothing is marked now and removed later, and nothing brings the key back: the user adds one anew, with a proof. |
| It is recorded. | `KeyDeleted`, in the same transaction. |
| A form that is sent twice is answered as the first one was. | Where nothing is stored, nothing happens, and nothing is recorded. |
| The keys of an account go with it. | Both tables hang on `users` with a foreign key that deletes the rows with the account, at once (FR-ACC-9). |

### What depends on a key

An agent that depends on a missing key moves to Attention required (FR-KEY-10). Credentials may not use Agents, so it declares `IKeyDependents`, and Agents implements and registers it. See "When a lower component must trigger a higher one" in [architecture.md](architecture.md).

```csharp
public interface IKeyDependents
{
    Task AiKeyDeletedAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    Task ExchangeKeyDeletedAsync(EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken);
}
```

- **It is told inside the transaction that deletes the key,** with the context that holds it, while the key is still stored. What an implementation changes is stored when the key is deleted, and not when it is not.
- **When it fails, the key stays.** The transaction is undone, and the request ends in an error. An agent is not left running on a key that is gone.
- It is not told when a key is added or replaced: a key is then where an agent looks for it.
- **Nothing implements it yet,** because agents are not built. Until then nothing depends on a key. `KeyDependentsTests` has a test that fails as soon as Agents has a table of its own and no implementation.

## The data

| Table | Columns |
|---|---|
| `ai_credentials` | `id`, `user_id`, `provider`, `encrypted_value`, `master_key_id`, `masked_suffix`, `saved_at`. One row for each `(user_id, provider)`. `provider` points at `ai_providers.code`. |
| `exchange_credentials` | `id`, `user_id`, `environment`, `encrypted_value`, `master_key_id`, `masked_suffix`, `saved_at`. One row for each `(user_id, environment)`. `environment` is `Demo` or `Live`. |

Both hold rows of users: their entities implement `IUserOwned`, and what is asked for a user is asked with `OwnedBy`. The rows of an account are deleted with it. `saved_at` is when the key that is stored now was stored.

## Not built yet

- Validating a key with a read-only call when it is saved, daily, and when a call fails authentication, with its status, its permissions, and the time (FR-KEY-3). With it come the refusal of a Binance key that can withdraw, the warning about a key without an IP restriction, and the check of the permission that an agent's market needs (FR-KEY-4, FR-KEY-5).
- Handing a key to the call that needs it, decrypted only in memory and only for that call (FR-KEY-9). No code decrypts a stored key yet. Whatever does clears it there: see "Adding a stored secret" in [security.md](security.md).
- What Agents does when a key goes. It implements `IKeyDependents` when agents are built. An agent whose key turns out to be invalid moves to Attention required as well, which comes with the validation.
- The usage of each AI key: calls, tokens, and estimated cost (FR-KEY-11).
- Which catalog models a key can use (FR-CAT-9).
