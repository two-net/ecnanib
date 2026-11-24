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

A row that belongs to no user belongs to the service: vocabulary, the catalog, the prompts, the state of the jobs. Such a table is listed, with what it holds, in [`OwnershipRules.cs`](../tests/Ecnanib.Tests/Persistence/OwnershipRules.cs). That file is the source of truth. To list a table there is to decide that no user owns its rows.

| Rows of users | Rows of the service |
|---|---|
| `sessions`, `pending_sign_ins`, `two_factor_recovery_codes`, `legal_acceptances`, `audit_events`, `ai_credentials`, `exchange_credentials`, `agents`, `agent_roles`, `agent_state_changes`, `decision_cycles`, `role_outputs`, `risk_checks`, `risk_check_rules`, `analyst_calls`, `shadow_trades`, `lessons`, `board_opt_outs`, `email_opt_ins`, `orders`, `fills`, `positions`, `fundings`, `position_roles`, `position_results`, `equity_marks`, `equity_snapshots`, `notifications` | `roles`, `ai_providers`, `ai_models`, `catalog_sync_runs`, `prompt_templates`, `background_jobs` |

`users` is the account itself. No table holds both kinds of row.

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
- **Background work.** A job has no user who is signed in. It works across users, takes the owner from the row it works on, and writes what it writes with that owner. It never takes an owner from what came in from outside, such as the output of a model. `IRunningAgents` of Agents is asked this way: it says the agents of every user that run, for the job that starts their cycles, and no page or endpoint asks it. See [cycles.md](cycles.md). The job that settles the orders works this way too: it finds the agents of every user that have something to settle, and each order says whose it is, which is whose key a call about it is made with, and who is told what became of the position. See [orders.md](orders.md). What watches the loss limits of an agent works this way as well: it is handed the agent of a cycle, or reads the agent of a position that was reconciled through `IAgentHalts.FindAsync` of Agents, and halts that agent for its own user. `IAgentHalts.EndDailyHaltsAsync` reads the agents of every user that their daily loss limit halted, and resumes each under the lock of its own account. See "Halting an agent" in [agents.md](agents.md). The job of Experience that scores the calls of the analysts and follows the shadow trades works this way too: it reads the rows of every user that are due, and writes what became of each to that row alone, which carries its owner. The candles that it reads are the same for everyone. See "The job" in [experience.md](experience.md). The job of the Decision Engine that has the lessons written works this way as well: it reads the lessons of every user that are due, reads the position of each for the user that its row names, calls the model of that user's agent with that user's own key, and writes what the model wrote to that row alone. What a model wrote names no owner, and none is taken from it. See "Writing a lesson" in [experience.md](experience.md). The job of Agents that reassigns the roles by strength works this way too: it reads the agents of every user that opted in and are due, asks Experience for the scores of the owner of each agent, by the id that its row carries, and gives a role a model under the lock of its own account, held to what the keys of that user can use. So the scores of one user never reassign a role of another. See "Reassigning the roles automatically" in [agents.md](agents.md).
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

An operator function asks in one of the two ways of "The check", and in no other. `AuditRulesTests` watches for both, to tell when the first operator function is built: what an operator does is recorded in the audit trail (FR-OPS-5). See "The audit trail" in [accounts.md](accounts.md).

**A screen that only shows is no operator function.** The operator status screen, `/operator`, has its route and its place in the bar of an operator (FR-UI-2): it requires the policy, says what it is to show, and does nothing. It has no form, asks the database nothing, and shows nothing of any user, so there is nothing of it to record, and nothing that the Risk Guard would have to be told. `AuditRules.OperatorScreensThatOnlyShow` lists it with why, and the rule that tells when the first operator function is built leaves a listed screen out. `AuditRulesTests` holds a listed screen to it: its page has no form but the button of the bar, which signs out. The first form that is put on it fails that test, the screen is taken off the list, and what waits for the operator functions fails until it is built. Anything else that requires the policy counts as before, whether its address is under that of the screen or not. The bar shows the link to the screen by asking the same policy, with `AuthorizeView`, and not by `IsOperator`. See "Screens whose feature is not built" in [layout.md](layout.md).

No operator function is built yet.

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

- The operator functions: the global halt, the status screen, and the account controls, each recorded in the audit trail. The events of an account go with it when it is deleted, so what happens to the events of an operator who deletes their own account is to be decided with them. See "Not built yet" in [accounts.md](accounts.md).
- What a protected endpoint under `/api` answers to a visitor. See "Not built yet" in [accounts.md](accounts.md).
- Checking again whether the user of a connection is an operator. Who the user is, and whether they are an operator, is decided when the connection of a page that follows is made. Whether the session of the connection still stands is asked again: see "The session of a connection" in [accounts.md](accounts.md). No page that follows is an operator function.
