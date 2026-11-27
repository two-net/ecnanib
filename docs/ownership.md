# Ownership

The only account type is the individual user. Every resource belongs to exactly one user, and a request reaches only what belongs to the user it is signed in as (FR-ACC-5, NFR-SEC-3, NFR-DAT-9). There are no tenants, organizations, workspaces, teams, or shared resources. The service is free: there are no plans, subscriptions, payments, invoices, trials, or paid tiers, and nothing behaves differently for one user than for another (FR-ACC-6). Operator functions are checked separately from ownership.

The vocabulary is in `src/Ecnanib/Platform/Persistence/Ownership.cs`. Who is signed in, and who is an operator, is in `src/Ecnanib/Accounts`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Persistence`, `tests/Ecnanib.Tests/Accounts`, and `tests/Ecnanib.Tests/Architecture`.

## The rules

| Rule | How it is enforced |
|---|---|
| Every table holds rows of users, which carry their owner, or rows of the service. | `OwnershipModelTests` reads the model. A table that says neither fails it. |
| The owner of a row is an account. | The owner has a foreign key that ends at `users.id`. The same test checks it. |
| Each kind of row that belongs to a user has a test that another user cannot reach it. | A test marked `[ProvesOwnershipOf("table")]`. `OwnershipModelTests` fails for a table without one. |
| The rows of a user go with the account when it is deleted (FR-ACC-9). | Each foreign key that carries the owner to `users` deletes the row with what it points at. `OwnershipModelTests` reads the model, and `AccountDeletionTests` deletes an account that has a row in every such table. |
| Nothing is about a tenant, a team, a shared resource, or money for the service. | `IndividualServiceTests` reads the name of every table, column, page, and endpoint. `DeploymentRuleTests` refuses the package of a payment service. |
| An operator function requires an operator, whoever owns what. | The `Operator` policy, which `OperatorAccessTests` covers. |

## Whose a row is

A row that belongs to a user implements `IUserOwned`, and its `user_id` points at the account:

```csharp
public sealed class RecoveryCode(Guid userId, byte[] codeHash, DateTimeOffset createdAt) : IUserOwned
{
    public Guid UserId { get; } = userId;

    public byte[] CodeHash { get; } = codeHash;

    public DateTimeOffset CreatedAt { get; } = createdAt;
}

internal sealed class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> builder)
    {
        builder.ToTable("two_factor_recovery_codes");
        builder.HasKey(code => new { code.UserId, code.CodeHash });
        builder.Property(code => code.UserId);
        builder.Property(code => code.CodeHash);
        builder.Property(code => code.CreatedAt);

        // The codes belong to the account and go with it when it is deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(code => code.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

- **Every row carries its owner itself,** also a row that hangs on another row of the same user. A query for it needs no join to know whose it is.
- **A component that may not use Accounts cannot point at `users`.** The Decision Engine is one. Its rows carry the owner along in the foreign key to the row they hang on: `(agent_id, user_id)` to `agents (id, user_id)`. The database then holds the two owners to being the same, and the row it hangs on points at the account. `decision_cycles` hangs on its agent this way, and `role_outputs` on its cycle, as `(cycle_id, user_id)` to `decision_cycles (id, user_id)`: see "The record of a cycle" in [team.md](team.md). `risk_checks` hangs on its cycle in the same way, and `risk_check_rules` on its check: see "The record of a check" in [risk-guard.md](risk-guard.md). `orders` and `positions` hang on their agent, and `fills` on its order, as `(order_id, user_id)` to `orders (id, user_id)`: see "The record of an order" in [orders.md](orders.md). `equity_marks`, which is what the loss limits of an agent are counted from, hangs on its agent as well, and so does `equity_snapshots`, which is its equity over time. `fundings`, `position_roles`, and `position_results` hang on their position, as `(position_id, user_id)` to `positions (id, user_id)`. `agents` has `(id, user_id)` as a key of its own for it.
- **A row of Notifications points at the account itself.** Notifications may use Accounts, so `notifications` and `email_opt_ins` each declare their own key to `users (id)`. A notification names its agent by the id alone, and stays when the agent is deleted: see [notifications.md](notifications.md).
- **One row of such a component hangs on nothing but the account:** who left the platform-wide board, `board_opt_outs`, which Experience keeps. A user who has no agent can leave the board, so there is no row of theirs for it to hang on. Its `user_id` points at `users (id)`, and the key is declared by Agents, which may use both Experience and Accounts: see "The data" in [experience.md](experience.md).
- **A row goes with its account.** The foreign key that carries the owner is declared with `OnDelete(DeleteBehavior.Cascade)`, at every row on the way to `users`. Deleting an account deletes one row, and the database deletes the rest, stored keys included. See "Deleting the account" in [accounts.md](accounts.md).
- **Another foreign key between two rows of one user must not stand in the way of that.** One that is declared with `Restrict` can refuse the deletion of the account, depending on the order the database deletes in. The test that deletes an account with a row in every table shows whether it does.
- The owner is an id. It tells nothing about the person, so it is not personal data, and a component that must not see personal data may hold it.

A row that belongs to no user belongs to the service: vocabulary, the catalog, the prompts, the state of the jobs, and the runtime flags, of which the global halt is the one there is. Such a table is listed, with what it holds, in [`OwnershipRules.cs`](../tests/Ecnanib.Tests/Persistence/OwnershipRules.cs). That file is the source of truth. To list a table there is to decide that no user owns its rows.

| Rows of users | Rows of the service |
|---|---|
| `sessions`, `pending_sign_ins`, `two_factor_recovery_codes`, `legal_acceptances`, `audit_events`, `ai_credentials`, `exchange_credentials`, `agents`, `agent_roles`, `agent_state_changes`, `decision_cycles`, `role_outputs`, `risk_checks`, `risk_check_rules`, `analyst_calls`, `shadow_trades`, `lessons`, `board_opt_outs`, `email_opt_ins`, `orders`, `fills`, `positions`, `fundings`, `position_roles`, `position_results`, `equity_marks`, `equity_snapshots`, `notifications` | `roles`, `ai_providers`, `ai_models`, `catalog_sync_runs`, `prompt_templates`, `background_jobs`, `runtime_flags`, `up_spans`, `maintenance_windows` |

`users` is the account itself. No table holds both kinds of row.

**A row of the service may say which account changed it last, and keeps nothing of that account when it is deleted.** `runtime_flags.changed_by` points at `users` and is declared with `OnDelete(DeleteBehavior.SetNull)`: the flag stays as it is, because a halt that is on is on for every user, and the id of the account goes. It is the one key to `users` that does not carry an owner. `GlobalHaltTests` deletes the account of whoever switched the halt, and finds the halt on and nothing of the account left.

## Asking for a user's data

Whatever serves a request takes the user from the session, and asks for the rows of that user. Asking for an agent looks like this:

```csharp
if (SignedInUser.IdOf(request) is not { } userId)
{
    // Nobody is signed in.
}

await using var context = await contexts.CreateDbContextAsync(cancellationToken);

var agent = await context.Set<Agent>()
    .OwnedBy(userId)
    .SingleOrDefaultAsync(agent => agent.Id == id, cancellationToken);

if (agent is null)
{
    // Not found: there is no such agent, or it is someone else's.
}
```

| Rule | Detail |
|---|---|
| The user comes from the session. | `SignedInUser.IdOf` in Accounts reads who the request is signed in as. The user is never taken from a path, a query, a form, or a body. An id in a request says which row, and never whose. |
| The query is scoped where it is asked. | `OwnedBy(userId)` comes first, for what is read and for what is updated or deleted. A row is not read first and compared afterwards. A statement that is written as SQL names `user_id` itself. |
| **A row of another user is not found.** | The scoped query finds nothing, so the answer is the one for a row that does not exist: 404 from a page or the API. It is not "forbidden", which would tell that the id is someone's. Nothing looks the row up unscoped to tell the two apart. |
| The check is where the data is asked for. | A method of a component that serves a request takes the user's id and scopes by it. So it holds for a request that no page of the application made. |
| A component that may not use Accounts is handed the user by id. | The Decision Engine reads the record of a cycle, and runs a test cycle of an agent, for the user that the page hands it, which is the one of the session: `IRecordedCycles`, `IRiskCheckRecords`, and `ITestCycles`. What it asks of Agents for a test cycle is asked by that owner as well: `IAgentTeams.FindAsync` takes the user and the agent, and an agent of another user is not found. See [cycles.md](cycles.md). Experience works out the track records of the user that a page hands it, `ITrackRecords`, and asks the Decision Engine for the closed trades of that user alone, `IClosedTrades`: neither may use Accounts. It works out the strength scores of that user in the same way, `IRoleStrengths`, from those trades and from its own rows of that user, the scored calls and the shadow trades. The page of a model's experience record has one address for every user, and each sees the record of their own trades there. Whether a user left the platform-wide board is read and stored for the user that the page hands over as well, `IBoardContribution`. See [experience.md](experience.md). The dashboard is handed its figures in the same way: `IAgentEquities` reads the agents of the user that the page hands it by owner, and their positions and orders through those agents, and `IRecordedCycles.LatestOfUserAsync` reads the latest cycles of that user. Its alerts are asked of Notifications with the request, `INotifications.UnreadByModeAsync`. See [dashboard.md](dashboard.md). |
| A new row belongs to whoever is signed in. | And a row that points at another row of the user, such as an agent at a key, finds that row through `OwnedBy` too. |

**An agent is the first resource that a request names by an id.** A key is named by its provider or its environment, of which every user has one, so no path names the key of another user. The path of an agent names the agent, so a user can ask for the id of an agent of another user: its pages answer "not found", and a form that is posted at them changes nothing. `AgentPageTests` has the test with two users. See [agents.md](agents.md).

`OwnedBy` cannot be called inside the projection of another query, where it does not translate to SQL. A count of a user's rows inside such a projection names the owner itself: see `TwoFactor.StateAsync`.

### What is not asked for by owner

- **A row that is found by a secret token.** A session is found by the hash of the token in its cookie, and so are a sign-in that waits for a code, a verification link, and a reset link. No user is named, because this is how a request comes to have one: the token is the proof. See [accounts.md](accounts.md).
- **Background work.** A job has no user who is signed in. It works across users, takes the owner from the row it works on, and writes what it writes with that owner. It never takes an owner from what came in from outside, such as the output of a model. `IRunningAgents` of Agents is asked this way: it says the agents of every user that run, for the job that starts their cycles, and no page or endpoint asks it. See [cycles.md](cycles.md). The job that settles the orders works this way too: it finds the agents of every user that have something to settle, and each order says whose it is, which is whose key a call about it is made with, and who is told what became of the position. See [orders.md](orders.md). What watches the loss limits of an agent works this way as well: it is handed the agent of a cycle, or reads the agent of a position that was reconciled through `IAgentHalts.FindAsync` of Agents, and halts that agent for its own user. `IAgentHalts.EndDailyHaltsAsync` reads the agents of every user that their daily loss limit halted, and resumes each under the lock of its own account. See "Halting an agent" in [agents.md](agents.md). The job of Experience that scores the calls of the analysts and follows the shadow trades works this way too: it reads the rows of every user that are due, and writes what became of each to that row alone, which carries its owner. The candles that it reads are the same for everyone. See "The job" in [experience.md](experience.md). The job of the Decision Engine that has the lessons written works this way as well: it reads the lessons of every user that are due, reads the position of each for the user that its row names, calls the model of that user's agent with that user's own key, and writes what the model wrote to that row alone. What a model wrote names no owner, and none is taken from it. See "Writing a lesson" in [experience.md](experience.md). The job of Agents that reassigns the roles by strength works this way too: it reads the agents of every user that opted in and are due, asks Experience for the scores of the owner of each agent, by the id that its row carries, and gives a role a model under the lock of its own account, held to what the keys of that user can use. So the scores of one user never reassign a role of another. See "Reassigning the roles automatically" in [agents.md](agents.md). What tells of an outage of the exchange works this way as well: it reads the users whose agents run on a market in an environment, and raises one notification for each of them, which is that user's own row and says nothing of anyone else (NFR-REL-4). See "When the exchange is out" in [agents.md](agents.md).
- **The signals that a page follows.** The database says that a row of a user changed, with the owner that the row carries, and the listener of the instance hands that to what follows that owner, and to nothing else: `ILiveSignals` in `Platform`. A signal holds nothing of the row. What a page then shows is asked for by owner, as ever, for the user of its connection: `ICycleProgress` and `IOpenPositions` of the Decision Engine take the user that the page hands over, and an agent of another user is not found. The prices that are followed for a dashboard are those of what the user of its connection holds, asked for by that owner as well: `IOpenPositions.HeldByUserAsync`. So a user receives updates only for their own agents (FR-NTF-2). See [live-updates.md](live-updates.md).
- **The platform-wide board.** It shows the track record of each AI provider and of each model across all users (FR-EXP-10), so the trades that it is worked out from are read for every user: `IBoardTrades` of Experience, which the Decision Engine answers. It is the one read for a page of a user that is not asked for by owner, and it is a question of its own, so that what asks for the trades of one user, `IClosedTrades`, never comes by those of another. What keeps it from showing a row of another user is what it answers with, and not whose rows it reads:
  - A record is what the trades of at least `Experience:Board:MinUsers` users and `Experience:Board:MinTrades` trades add up to, and a record that fewer contribute to is in no answer.
  - An answer holds no user, no agent, no position, no symbol, and no amount of money. The owner of a trade is read as an id, to count the users of a record, and is in no answer.
  - A user who left the board is left out where the positions are read. Whether a user left it is that user's own row, asked for with `OwnedBy`.
  - No operator is needed for it: it is no function about a user, and every signed-in user reads the same board.

  See "The platform-wide board" in [experience.md](experience.md), which says what the rule does not keep from a reader as well.
- **Operator functions.** See below.

## Operators

An operator is a user with the status screens and the safety controls of the service (FR-OPS-1). The check for it is a check of its own.

| What | Detail |
|---|---|
| Who | A user whose address is listed in `Application:Operators` **and is verified**. The listed address is compared as an account stores its own: trimmed and in lower case. |
| Why verified | Anyone can register an address that is not theirs and sign in with it. Only whoever reads the mail of the address can verify it. See "An address that was registered again" in [accounts.md](accounts.md). |
| When it is decided | On every request, as the request is signed in, from the configuration file as it was read at startup. A change to the list applies after a restart. See [configuration.md](configuration.md). |
| Where it is stored | Nowhere. No column says what kind of account one is, because there is one kind. |
| The check | A page of an operator function carries `[Authorize(Policy = OperatorAccess.Policy)]`, and an endpoint requires the same policy. An action that is not guarded by its endpoint asks `OperatorAccess.IsOperator(user)`. |
| The answer | A visitor is sent to sign in. A user who is not an operator gets status 403, and the log has a warning: `User {UserId} was refused {Path}, which is not for them`. |

**The two checks are separate, both ways:**

- Being an operator opens no row of another user. `OwnedBy` knows nothing of operators, so the pages of an operator's own account work for them as for anyone.
- Owning something satisfies no part of the policy.
- An operator function that is about every user, or about a user who is not the one signed in, asks the database without `OwnedBy`. That is why it requires the policy. The platform-wide board reads across users without it, and shows nothing of one user: see "What is not asked for by owner".

An operator function asks in one of the two ways of "The check", and in no other. `AuditRulesTests` watches for both: what an operator does is recorded in the audit trail (FR-OPS-5), so each operator function that is built is listed in `AuditRules.RecordedOperatorFunctions` with the actions that record it, and one that is not listed fails a test. See "The audit trail" in [accounts.md](accounts.md).

**A screen that only shows is no operator function.** The operator status screen, `/operator`, has its route and its place in the bar of an operator (FR-UI-2): it requires the policy, shows how the service stands, and does nothing. It has no form, and shows nothing of any user, so there is nothing of it to record, and nothing that the Risk Guard would have to be told. `AuditRules.OperatorScreensThatOnlyShow` lists it with why, and the rule that tells whether an operator function is listed leaves a listed screen out. `AuditRulesTests` holds a listed screen to it: its page has no form but the button of the bar, which signs out. The first form that is put on it fails that test, and the screen is then an operator function, which is listed with what records it. Anything else that requires the policy counts as before, whether its address is under that of the screen or not. The bar shows the link to the screen by asking the same policy, with `AuthorizeView`, and not by `IsOperator`. See "The operator status screen" below.

**Every operator function is listed.** `AuditRulesTests.Every_operator_function_is_listed_with_what_records_it` fails for a route that requires the policy, and for a type outside Accounts that asks `IsOperator`, that is neither a screen that only shows nor listed in `AuditRules.RecordedOperatorFunctions`. Nothing of the requirement waits for the operator functions any more, so this test is what sees one that is built later.

### The operator status screen

The screen shows scheduler health, queue depth, last catalog sync per provider, Binance rate-limit usage, clock drift, table sizes, and error rates (FR-OPS-2), and with them where the calls to the AI providers are paused and where Binance is out (NFR-REL-4). Before them it shows how available the service was, held against its target (NFR-REL-1). It is a page of the Web UI, `Pages/Operator/OperatorStatus.razor`, and no component of its own: each reading is said by the component that keeps it, behind an interface, and the page puts them together and holds the words, in `OperatorStatusPages`.

| Reading | Who says it | From what |
|---|---|---|
| Availability | `IAvailability` of `Platform` | When the service said that it was up, in `up_spans`, and the maintenance that was announced in advance, in `maintenance_windows`. See "Availability" in [reliability.md](reliability.md). |
| Scheduler health | `IBackgroundWorkStatus` of `Platform` | The latest run of each job, in `background_jobs`, and the schedule of each registered job. See "The status of the jobs" in [background-work.md](background-work.md). |
| Queue depth | `ICycleLoad` of the Decision Engine | The cycles of the close that is under way, in memory. See "The depth of the queue" in [cycles.md](cycles.md). |
| Last catalog sync | `ICatalogSync.StatusAsync` of the Catalog | The rows of `catalog_sync_runs`. See "The status of the syncs" in [catalog.md](catalog.md). |
| Calls to the AI providers | `ICallPauses` of the Decision Engine | What the AI Provider Gateway keeps of the pauses of the calls to each provider, and of the keys that a provider refuses, in memory: the pages may not use that gateway. See "The breakers of the AI providers" in [reliability.md](reliability.md). |
| Rate-limit usage | `IExchangeLimits.Usage` of the Exchange Gateway | The one limiter of the gateway, in memory. See [exchange.md](exchange.md). |
| Clock drift | `IExchangeClock.Drift` of the Exchange Gateway | The latest reading of each of Binance's clocks, in memory. See [exchange.md](exchange.md). |
| Outages of Binance | `IExchangeOutages.Answers` of the Exchange Gateway | Whether Binance answered the latest calls on each market in each environment, in memory. See "Outages" in [exchange.md](exchange.md). |
| Table sizes | `IDatabaseSizes` of `Platform` | The catalog of the database. See "The sizes of the tables" in [persistence.md](persistence.md). |
| Error rates | `ILoggedProblems` of `Platform` | The errors and warnings that were logged, counted in memory. See "The counts of errors and warnings" in [logging.md](logging.md). |

| Rule | Detail |
|---|---|
| **It only reads** | It has no form and no control. What an operator does is on a page of its own, which the screen leads to with a link: the global halt, the accounts, the catalog sync, and the maintenance. |
| **It is never a page that follows** | It is rendered on the server for each request, says of when it is, and is loaded again for how the service stands now. |
| **It shows nothing of any user** | No reading names a user, an agent, or a key: a queue is numbers, a sync names a provider, the keys that a provider refuses are a number, and a count of errors holds no word of an event (FR-OPS-5). `GlobalHaltTests.The_pages_of_an_operator_show_nothing_of_any_user` holds it to that, with a user who has keys and two-factor authentication. |
| **What is of one instance says so** | The queue, the pauses of the calls, the limits, the clock, the outages, and the counts are kept in memory by the instance that answered. The availability, the jobs, the syncs, and the sizes are read from the database, and are the same on every instance. |
| **Unknown is said as unknown** | A clock that was not read has no drift, and is not shown with a drift of nothing. A job that has not run is not said to be late. A month that nothing was measured of has no availability, and is not shown with one of nothing or of a hundred. |

### The global halt

The one operator function that is built is the global halt (FR-OPS-3): while it is on, no agent opens a new position, and open positions are still managed and protected. What it does to a plan and to an entry that rests is in "The global halt" of [risk-guard.md](risk-guard.md).

| Rule | Detail |
|---|---|
| **It has a page of its own** | `/operator/global-halt`, which the operator status screen leads to. It says what the halt does and whom it acts on, whether it is on and since when, and has one button, which switches it the other way. The page is what asks before the halt is switched: it has nothing else to do on it. |
| **It asks in both ways** | The page carries `[Authorize(Policy = OperatorAccess.Policy)]`, for a form that is posted as for the page itself. And what switches the halt, `IGlobalHaltSwitch.SwitchAsync` of Agents, asks `OperatorAccess.IsOperator` of the user of its request again: whatever calls it, nobody else switches the halt. |
| **It is never a page that follows** | The page is rendered on the server for each request, so who is an operator is decided with each one. `AuditRulesTests` holds every operator function to that. |
| **It is stored in the database** | As the row `global-halt` of `runtime_flags`: whether it is on, who switched it last, and when (NFR-CFG-6). The configuration file has no key for it, and `SettingsFileTests` refuses one. A halt that was never switched has no row, and is off. Nothing is cached: a switch holds from the next check on, on every instance. |
| **Every switch is recorded** | As `GlobalHaltSwitchedOn` or `GlobalHaltSwitchedOff`, with the target `runtime-flag:global-halt`, in the transaction that switches (FR-OPS-5). A form that is sent twice, or for what the halt already is, switches nothing and records nothing. |
| **It shows nothing of any user** | Not who switched the halt, and no agent, position, or key: the page reads the flag, and nothing else (FR-OPS-5). The audit trail says who switched it. |
| **An operator does not delete their account** | Their events in the audit trail would go with it. See "Deleting the account" in [accounts.md](accounts.md). |

**When the application cannot be reached**, the halt is one row, and whoever runs the database can set it there. That is not recorded in the audit trail, so it is for an emergency, and is written down by whoever does it:

```sql
INSERT INTO runtime_flags (name, value, changed_by, changed_at) VALUES ('global-halt', TRUE, NULL, now())
ON CONFLICT (name) DO UPDATE SET value = EXCLUDED.value, changed_by = NULL, changed_at = EXCLUDED.changed_at;
```

`FALSE` in place of `TRUE` switches it off.

### Disabling and enabling an account

An operator disables the account of a user, and enables it again (FR-OPS-4). A disabled account is signed out on every browser at once and is not signed in, and its agents that run wait for its user. What it does to the account is in "Disabling an account" of [accounts.md](accounts.md), and what it does to the agents in "When an account is disabled" of [agents.md](agents.md).

| Rule | Detail |
|---|---|
| **It has two pages of its own** | `/operator/accounts` finds an account by its email address, and leads to `/operator/accounts/{id}`, the page of that account. That page says the address and whether the account is disabled, and since when, and has one button, which switches it the other way. It is what asks before the account is switched. |
| **An account is found by its address** | The operator types it. There is no list of the accounts. The page of an account names it by its id, so no address is in the address of a page. |
| **It asks in both ways** | Both pages carry `[Authorize(Policy = OperatorAccess.Policy)]`. And `IAccountDisabling` of Accounts asks `OperatorAccess.IsOperator` of the user of its request again, for finding as for switching: whatever calls it, nobody else does either. |
| **It asks without `OwnedBy`** | It is about a user who is not the one signed in, which is why it requires the policy. |
| **It is never a page that follows** | As every operator function. |
| **The account of an operator is not disabled** | Their own included: an operator who is disabled switches no global halt. Whoever is to be no operator is taken off the list of the configuration file first. The page of such an account has a sentence in place of the button. |
| **Every switch is recorded** | As `AccountDisabled` or `AccountEnabled`, in the transaction that switches (FR-OPS-5). The event carries the operator as its user, and names the other account as its target, `user:<id>`. A form that is sent twice, or for what the account already is, switches nothing and records nothing. Finding an account changes nothing, and is not recorded. |
| **It shows the address and nothing else** | The page of an account shows the address that the operator typed and whether the account is disabled: no agent, no position, no key, and not whether it has two-factor authentication (FR-OPS-5). It does not say who disabled it: the audit trail does. |

**When the application cannot be reached**, whoever runs the database can enable an account there, or disable one. That is not recorded in the audit trail, and it ends no session and moves no agent: a session of a disabled account is not found all the same, so its user is signed out with their next request.

```sql
UPDATE users SET disabled_at = NULL WHERE email = 'ada@example.com';
```

### Syncing the catalog on demand

An operator has the model catalog synced, outside its schedule (FR-CAT-2, FR-OPS-4). What a sync does is in [catalog.md](catalog.md).

| Rule | Detail |
|---|---|
| **It has a page of its own** | `/operator/catalog-sync`, which the operator status screen leads to. It says what a sync does, shows the last sync of each provider, and has one button. |
| **It asks in both ways** | The page carries the policy. And `IOperatorCatalogSync.SyncAsync` of Credentials asks `OperatorAccess.IsOperator` of the user of its request again. |
| **It is in Credentials because of what it uses** | The Catalog syncs, and may not use Accounts, which keeps the audit trail. Credentials may use both, and already tells the Catalog of the keys of users. No key of a user has a part in this. |
| **It is recorded when it is asked for** | As `CatalogSyncRequested`, with the target `catalog:models`, in a transaction of its own that is over before a provider is asked (FR-OPS-5). The rows of `catalog_sync_runs`, which the Catalog writes, say what came of it, with the cause `OnDemand`. |
| **It is not given up with the request** | Once recorded, the syncs run until they are over or the application stops. The page waits for them, up to a minute, and then asks for itself again, so a reload asks for no second sync. |
| **With no catalog key there is nothing to sync** | Where the configuration file holds a catalog key for no provider, the page says so in place of the button, and a form that is sent all the same asks no provider and records nothing. |
| **It shows no key** | The page says whether a provider is synced with the key of the service, and never the key. It shows nothing of any user. |

### Announcing maintenance

An operator announces maintenance, cancels it before it starts, and ends it while it is under way (NFR-REL-6). What an announcement does, and what it does not, is in "Announced maintenance" in [reliability.md](reliability.md).

| Rule | Detail |
|---|---|
| **It has a page of its own** | `/operator/maintenance`, which the operator status screen leads to. It says what an announcement does, shows the maintenance that is announced and not over, and has a form of a start and an end, and a button for each row of what is announced. |
| **It asks in both ways** | The page carries the policy. And each member of `IMaintenanceAnnouncements` of Accounts asks `OperatorAccess.IsOperator` of the user of its request again. |
| **It is in Accounts because of what it uses** | What is announced is of the service, and `Platform` keeps it, which may use no component and so not the audit trail. Accounts opens the transaction, locks the account of the operator, has `MaintenanceWindows` of `Platform` write the row inside it, and records the event. |
| **Each action is recorded** | As `MaintenanceAnnounced`, `MaintenanceCancelled`, or `MaintenanceEnded`, with the target `maintenance:<id>`, in the transaction that does it (FR-OPS-5). What changed nothing is not recorded: a form that was sent twice, or maintenance that had started or ended meanwhile. |
| **It is stored in the database** | As a row of `maintenance_windows`, whose rows belong to the service (NFR-CFG-6). The row says nothing of who announced it, so it stays as it is when that account is deleted, and keeps nothing of it. |
| **It acts on no user and on no agent** | It pauses nothing, and closes no position. Nothing that trades may read it: see "Announced maintenance" in [reliability.md](reliability.md). |
| **It shows nothing of any user** | Not who announced what, and no agent, position, or key. |

## No tenants and no billing

- `users` is the only table of accounts, and an account has no type. Fair-use limits come from the configuration file and are the same for every user: no account carries a limit of its own. See "The registration switch and fair-use limits" in [accounts.md](accounts.md).
- [`IndividualServiceRules.cs`](../tests/Ecnanib.Tests/Architecture/IndividualServiceRules.cs) lists the words that no table, column, page, or endpoint may be named with: tenant, organization, workspace, team, member, share, plan, subscription, payment, invoice, billing, trial, tier, and paid.
- The rule reads names and not meanings. A name that uses one of the words in another sense, such as the team of four AI roles that runs an agent, or a trade plan, is accepted in that file by name, with what it means. So such a name is a decision made in one visible place.
- The package of a payment service is on the list of the deployment guard. See [architecture.md](architecture.md).

## Adding a kind of resource

1. Add the entity and its configuration, as [persistence.md](persistence.md) describes. Implement `IUserOwned`, and point `user_id` at `users` with a foreign key, or carry it along to the row this one hangs on. Declare that key with `OnDelete(DeleteBehavior.Cascade)`.
2. Read and change the rows through `OwnedBy(userId)`, with the user from `SignedInUser`.
3. Answer "not found" when the scoped query finds nothing.
4. Write a test with two users: a request of one for a row of the other is refused, and what one does changes nothing of the other. Mark it `[ProvesOwnershipOf("table")]`.
5. Give both users of the first test in `AccountDeletionTests` a row in the table. That test fails until they have one, and then shows that deleting an account deletes from the table.
6. List each column that could say who a user is in `PersonalDataRules`, with what it holds. See "Personal data" in [accounts.md](accounts.md).
7. Add the rows to the export of the user's data, or list the table in `ExportRules` with why it is left out. See "Exporting your data" in [accounts.md](accounts.md).

If the rows belong to the service instead, list the table in `OwnershipRules.ServiceTables` with what it holds.

## Not built yet

- Telling the users that a global halt is on. Today a user sees it where a plan of an agent was rejected: the rule "Global halt" of the check says so.
- A reason for disabling an account, a list of the accounts that are disabled, and an email that tells the user of it. Their user is told at the sign-in, with the right password, and of each agent that was moved with a notification.
- Exporting or deleting a disabled account by its user. Both need a session, which a disabled account has none of.
- Readings of every instance on the operator status screen. The queue, the pauses of the calls, the limits, the clock, the outages, and the counts are those of the instance that answered, and version 1 targets one.
- A history of the sizes of the tables, and a status screen that refreshes itself.
- What a protected endpoint under `/api` answers to a visitor. See "Not built yet" in [accounts.md](accounts.md).
- Checking again whether the user of a connection is an operator. Who the user is, and whether they are an operator, is decided when the connection of a page that follows is made. Whether the session of the connection still stands is asked again: see "The session of a connection" in [accounts.md](accounts.md). No page that follows is an operator function, and `AuditRulesTests` holds the ones that are built to it.
