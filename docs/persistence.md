# Persistence

Ecnanib keeps its data in one PostgreSQL database and nowhere else. The schema is defined by the code: the entities and their mappings are the model, migrations are generated from the model, and the application applies the pending ones each time it starts (NFR-DAT-1 to -4, -7, -8, -10).

The object-relational mapper is Entity Framework Core with the Npgsql provider. The plumbing is in `src/Ecnanib/Platform/Persistence`. The rules below are enforced by tests in `tests/Ecnanib.Tests/Persistence` and `tests/Ecnanib.Tests/Architecture`.

## The database

| Where | Database |
|---|---|
| Development | `docker compose up -d` starts PostgreSQL 18 with the database, user, and password that the tracked configuration file names. |
| A deployment | An empty database and a role that owns it, named in `ConnectionStrings:EcnanibDbContext` of the deployment's configuration file. See [configuration.md](configuration.md). |
| Tests | A PostgreSQL container that the tests start themselves, and that is removed when the run ends. They need Docker running, and nothing else. |

The application does not create the database, so its role needs no right to. It creates and changes the tables inside it.

`ConnectionStrings:EcnanibDbContext` is the standard place of a .NET application's connection string, named after the context. It is checked with the rest of the configuration file: a value that is not a PostgreSQL connection string is reported there, by its key.

## At startup

Before it serves a request or starts any scheduled work, the application brings the database to its model (NFR-DAT-3):

1. It opens one session and takes a PostgreSQL advisory lock on it. If another instance holds the lock, it logs that it is waiting and tries again until the lock is free.
2. It applies the pending migrations on that same session, all in one transaction.
3. It releases the lock, and only then starts the host.

The lock and the migrations share a session, so the lock cannot be lost while a migration runs. Starting a second instance is safe: it waits, finds nothing pending, and starts (NFR-ARC-4). The lock shows in `pg_locks` as an advisory lock with the key `DatabaseMigration.LockKey`.

Because the lock belongs to a session, the connection must be direct, or go through a connection pooler in session mode. Behind a pooler in transaction mode the session changes between commands, and the application logs a warning that it did not keep the lock.

Once it runs, the application holds another advisory lock, with the key `BackgroundJobs.LockKey`, for as long as it is the instance that runs the background work. The same holds for that one: it needs a direct connection or a pooler in session mode. See [background-work.md](background-work.md).

The log says what happened:

```
Applying 1 database migrations, from 20261111050450_Initial to 20261111050450_Initial
The database was migrated to 20261111050450_Initial
```

or, when there was nothing to do:

```
The database is up to date at 20261111050450_Initial
```

### When it fails

If the server cannot be reached, the database does not exist, a migration fails, or the model is ahead of its migrations, the application does not start. It logs the error once, as `Critical`, with the cause in full, and exits with code 1. The migrations of one start run in a single transaction, so a failure leaves nothing half done. Fix the cause and start again.

There is one exception. An operation that PostgreSQL cannot run inside a transaction, such as creating an index concurrently, ends that transaction, so a migration with one is applied in steps. No migration has one today.

A migration has no time limit of its own. One that takes long holds up the start, and the instances waiting behind it.

## The model

An entity lives in the component that owns it, and its mapping sits beside it as an `IEntityTypeConfiguration`:

```csharp
public sealed class AiProvider(AiProviderCode code, string displayName)
{
    public AiProviderCode Code { get; } = code;

    public string DisplayName { get; } = displayName;
}

internal sealed class AiProviderConfiguration : IEntityTypeConfiguration<AiProvider>
{
    public void Configure(EntityTypeBuilder<AiProvider> builder)
    {
        builder.ToTable("ai_providers");
        builder.HasKey(provider => provider.Code);
        builder.Property(provider => provider.DisplayName);
    }
}
```

`EcnanibDbContext` finds every configuration in the application by itself. It has no property per entity, because `Platform` may not name a component's types. See [architecture.md](architecture.md).

| Convention | Meaning |
|---|---|
| Names | Tables and columns are in snake_case: `prompt_templates.is_active`. A configuration names its table with `ToTable`. |
| Enums | Stored as the name of the member, as text. Renaming a member changes stored data and needs a migration. |
| Prices, quantities, and money | `decimal` in the code, which is `numeric` in the database: exact. No floating-point column is allowed, for any purpose (NFR-DAT-8). |
| Timestamps | `DateTimeOffset` or a UTC `DateTime` in the code, which is `timestamp with time zone` in the database. A value that is not in UTC is refused when it is written (NFR-DAT-8). |
| Partitioning | No table is partitioned, by any mechanism (NFR-DAT-4). |
| Shared vocabulary | A lookup table for a type in `Shared`, such as `roles` for `Role`, is mapped in `Platform/Persistence`, so that every component can point at it. |
| Foreign keys between components | Configured in the component that is allowed to reference the other, following the dependency rules. |
| Foreign keys to a seeded table | Declared with `OnDelete(DeleteBehavior.Restrict)`, so that taking a seeded row away can never delete the rows that point at it. |

Operational logs are never written to the database (NFR-DAT-7). The audit trail, which is not a log, will be.

## Using the database

A component takes the context factory and creates a context for each piece of work:

```csharp
internal sealed class TemplateReader(IDbContextFactory<EcnanibDbContext> contexts)
{
    public async Task<List<PromptTemplate>> InUseAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<PromptTemplate>().Where(template => template.IsActive).ToListAsync(cancellationToken);
    }
}
```

The factory is the form to use in a Blazor component, where an injected context would live as long as the user's connection.

Two rules guard the one database:

- Only `Platform` may use the types of the database driver, the `Npgsql` namespace, so no other area opens a connection of its own with them.
- No package for another database may be added. See the deployment guard in [architecture.md](architecture.md).

They do not make `EcnanibDbContext` the only possible way in. Registering another context, or migrating from a component, is not caught by a test and is not done.

A database error is logged once, by whoever handles the exception. The mapper's own error events would log it a second time, so they are written at the `Debug` level. The tracked configuration file sets `Microsoft.EntityFrameworkCore` to `Warning`; set it to `Information` to see every SQL statement. See [logging.md](logging.md).

## Migrations

Migrations are generated from the model, not written by hand, and are versioned with the code in `src/Ecnanib/Platform/Persistence/Migrations`. The tool is pinned in `dotnet-tools.json`. Restore it once after cloning:

```
dotnet tool restore
```

After changing an entity or its configuration, generate a migration:

```
dotnet ef migrations add <Name> --project src/Ecnanib --output-dir Platform/Persistence/Migrations
```

This needs neither a configuration file nor a database. Read the generated file, and commit it together with its `.Designer.cs` file and the changed snapshot.

- `ModelTests` fails when the model has changed and no migration has been generated. The application refuses to start in the same state, which `MigrationFailureTests` covers.
- A migration that has been applied anywhere is never edited. A further change is a further migration.
- A migration that drops or rewrites data needs a backup first (NFR-DAT-11). Marking such migrations is a separate feature.

## Seed data

Roles, providers, and prompt templates are seeded through migrations (NFR-DAT-10). The rows are declared in the entity's configuration with `HasData`, so they are part of the model, and generating a migration turns a changed row into an insert, update, or delete.

| Table | Rows | Declared in |
|---|---|---|
| `roles` | The members of the `Role` enum. | `Platform/Persistence/RoleRow.cs` |
| `ai_providers` | Claude, Gemini, GPT, and Grok, under the codes that name them in the tracked configuration file. | `AiProviderGateway/AiProvider.cs` |
| `prompt_templates` | One template in use for each role, market, timeframe, and profile. | `DecisionEngine/PromptTemplate.cs` |

Models are never seeded: the catalog is filled only by syncing (FR-CAT-1). The default role mapping is configuration, not data (FR-TEAM-3).

A prompt template has a kind (role, market, timeframe, or profile), a subject (which one of that kind, such as `MarketAnalyst` or `15m`), and a version. Version 1 of each carries the outline from the specification. To change a template, add a row with the next version and take the earlier one out of use in the same change: each kind and subject has at most one version in use.

A seeded row is found by its key. Changing a key, such as renaming a member of `Role`, therefore generates a delete and an insert. Edit that one migration by hand so that it updates the row in place, or the rows pointing at it stand in the way.

Keep each seeded text on one line in the source file. A text that spans lines would change with the line endings of a checkout, and the model with it.

## Adding an entity

1. Add the entity and its `IEntityTypeConfiguration` to the component that owns it. Name the table with `ToTable`.
2. Use `decimal` for prices, quantities, and money, and `DateTimeOffset` for timestamps.
3. Generate a migration, read it, and commit it.
4. Run the tests. `ModelTests` checks the model against its migrations and against the rules for columns.
