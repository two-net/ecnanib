# Ownership

The only account type is the individual user. Every resource belongs to exactly one user, and a request reaches only what belongs to the user it is signed in as (FR-ACC-5, NFR-SEC-3, NFR-DAT-9). There are no tenants, organizations, workspaces, teams, or shared resources. The service is free: there are no plans, subscriptions, payments, invoices, trials, or paid tiers, and nothing behaves differently for one user than for another (FR-ACC-6). Operator functions are checked separately from ownership.

The vocabulary is in `src/Ecnanib/Platform/Persistence/Ownership.cs`. Who is signed in, and who is an operator, is in `src/Ecnanib/Accounts`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Persistence`, `tests/Ecnanib.Tests/Accounts`, and `tests/Ecnanib.Tests/Architecture`.

## The rules

| Rule | How it is enforced |
|---|---|
| Every table holds rows of users, which carry their owner, or rows of the service. | `OwnershipModelTests` reads the model. A table that says neither fails it. |
| The owner of a row is an account. | The owner has a foreign key that ends at `users.id`. The same test checks it. |
| Each kind of row that belongs to a user has a test that another user cannot reach it. | A test marked `[ProvesOwnershipOf("table")]`. `OwnershipModelTests` fails for a table without one. |
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
- **A component that may not use Accounts cannot point at `users`.** The Decision Engine is one. Its rows carry the owner along in the foreign key to the row they hang on: `(agent_id, user_id)` to `agents (id, user_id)`. The database then holds the two owners to being the same, and the row it hangs on points at the account.
- The owner is an id. It tells nothing about the person, so it is not personal data, and a component that must not see personal data may hold it.

A row that belongs to no user belongs to the service: vocabulary, the catalog, the prompts, the state of the jobs. Such a table is listed, with what it holds, in [`OwnershipRules.cs`](../tests/Ecnanib.Tests/Persistence/OwnershipRules.cs). That file is the source of truth. To list a table there is to decide that no user owns its rows.

| Rows of users | Rows of the service |
|---|---|
| `sessions`, `pending_sign_ins`, `two_factor_recovery_codes`, `legal_acceptances`, `audit_events` | `roles`, `ai_providers`, `prompt_templates`, `background_jobs` |

`users` is the account itself. No table holds both kinds of row.

## Asking for a user's data

Whatever serves a request takes the user from the session, and asks for the rows of that user. Agents are not built yet. Asking for one will look like this:

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
| A new row belongs to whoever is signed in. | And a row that points at another row of the user, such as an agent at a key, finds that row through `OwnedBy` too. |

`OwnedBy` cannot be called inside the projection of another query, where it does not translate to SQL. A count of a user's rows inside such a projection names the owner itself: see `TwoFactor.StateAsync`.

### What is not asked for by owner

- **A row that is found by a secret token.** A session is found by the hash of the token in its cookie, and so are a sign-in that waits for a code, a verification link, and a reset link. No user is named, because this is how a request comes to have one: the token is the proof. See [accounts.md](accounts.md).
- **Background work.** A job has no user who is signed in. It works across users, takes the owner from the row it works on, and writes what it writes with that owner. It never takes an owner from what came in from outside, such as the output of a model.
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
- An operator function that is about every user, or about a user who is not the one signed in, asks the database without `OwnedBy`. That is why it requires the policy.

No operator function is built yet.

## No tenants and no billing

- `users` is the only table of accounts, and an account has no type. Fair-use limits come from the configuration file and are the same for every user: no account carries a limit of its own. See "The registration switch and fair-use limits" in [accounts.md](accounts.md).
- [`IndividualServiceRules.cs`](../tests/Ecnanib.Tests/Architecture/IndividualServiceRules.cs) lists the words that no table, column, page, or endpoint may be named with: tenant, organization, workspace, team, member, share, plan, subscription, payment, invoice, billing, trial, tier, and paid.
- The rule reads names and not meanings. A name that uses one of the words in another sense, such as the team of four AI roles that runs an agent, or a trade plan, is accepted in that file by name, with what it means. So such a name is a decision made in one visible place.
- The package of a payment service is on the list of the deployment guard. See [architecture.md](architecture.md).

## Adding a kind of resource

1. Add the entity and its configuration, as [persistence.md](persistence.md) describes. Implement `IUserOwned`, and point `user_id` at `users` with a foreign key, or carry it along to the row this one hangs on.
2. Read and change the rows through `OwnedBy(userId)`, with the user from `SignedInUser`.
3. Answer "not found" when the scoped query finds nothing.
4. Write a test with two users: a request of one for a row of the other is refused, and what one does changes nothing of the other. Mark it `[ProvesOwnershipOf("table")]`.

If the rows belong to the service instead, list the table in `OwnershipRules.ServiceTables` with what it holds.

## Not built yet

- The first resources that a request names by an id: keys and agents. The tests of today are about the rows that Accounts keeps, none of which a request names by its id.
- The operator functions: the global halt, the status screen, and the account controls, each recorded in the audit trail.
- What a protected endpoint under `/api` answers to a visitor. See "Not built yet" in [accounts.md](accounts.md).
- Checking again inside an interactive page. Who the user is, and whether they are an operator, is decided when the connection of such a page is made. See "Not built yet" in [accounts.md](accounts.md).
