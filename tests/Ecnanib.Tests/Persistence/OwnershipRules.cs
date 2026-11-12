using Ecnanib.Accounts;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Persistence;

/// <summary>
/// Whose the rows of each table are, in one place. Every table but <c>users</c> holds rows that
/// belong to a user, and carry that user, or rows that belong to the service (FR-ACC-5,
/// NFR-DAT-9). This file is the source of truth for the second kind: <c>docs/ownership.md</c>
/// explains the rule, and <see cref="OwnershipModelTests"/> enforces it. The rows of a user go
/// with the account when it is deleted (FR-ACC-9), which is held to here as well.
/// </summary>
internal static class OwnershipRules
{
    /// <summary>
    /// The tables whose rows belong to the service and to no user, each with what it holds. A
    /// table that is not here must carry its owner. To add one is to decide that no user owns
    /// its rows, and that every user may see what the application shows of them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ServiceTables = new Dictionary<string, string>
    {
        ["roles"] = "The four roles on the team that runs an agent: vocabulary, seeded through migrations.",
        ["ai_providers"] = "The four AI providers: seeded through migrations.",
        ["prompt_templates"] = "The prompts that the system supplies, which are the same for every user.",
        ["background_jobs"] = "The latest run of each scheduled job of the application.",
    };

    /// <summary>The tables of the application's model that break the rule, each with what to do about it.</summary>
    public static IReadOnlyList<string> FindViolations(IModel model) =>
        FindViolations(model, typeof(User), ServiceTables);

    /// <summary>The tables of a model that break the rule.</summary>
    /// <param name="model">The model.</param>
    /// <param name="account">The entity that is the account: the owner of everything else.</param>
    /// <param name="serviceTables">The tables whose rows belong to the service.</param>
    public static IReadOnlyList<string> FindViolations(
        IModel model, Type account, IReadOnlyDictionary<string, string> serviceTables)
    {
        var accountEntity = model.FindEntityType(account)
            ?? throw new InvalidOperationException($"The model has no entity {account.Name}.");

        var violations = new List<string>();

        foreach (var entity in model.GetEntityTypes().Where(entity => entity != accountEntity))
        {
            var table = TableOf(entity);
            var carriesOwner = CarriesOwner(entity);

            if (serviceTables.ContainsKey(table))
            {
                if (carriesOwner)
                {
                    violations.Add($"{table}: is listed as a table of the service, but its rows carry an owner. Take it off the list.");
                }

                continue;
            }

            if (!carriesOwner)
            {
                violations.Add(
                    $"{table}: says neither whose its rows are nor that they are the service's. Implement " +
                    $"{nameof(IUserOwned)} on {entity.ClrType.Name}, or list the table in {nameof(OwnershipRules)}.{nameof(ServiceTables)} with what it holds.");

                continue;
            }

            if (entity.FindProperty(nameof(IUserOwned.UserId)) is not { } owner)
            {
                violations.Add($"{table}: {entity.ClrType.Name}.{nameof(IUserOwned.UserId)} is not stored. Map it as a column.");

                continue;
            }

            if (!LeadsToTheAccount(entity, owner, accountEntity, [], AnyKey))
            {
                violations.Add(
                    $"{table}: the owner has no foreign key that ends at {TableOf(accountEntity)}. Point it at the " +
                    "account, or together with the key of the row this one hangs on at that row's owner.");
            }
        }

        var tables = model.GetEntityTypes().Select(TableOf).ToHashSet();
        violations.AddRange(
            from listed in serviceTables.Keys
            where !tables.Contains(listed)
            select $"{listed}: is listed as a table of the service, but the model has no such table. Take it off the list.");

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The tables of the application's model whose rows would not go with their account when it
    /// is deleted (FR-ACC-9), each with what to do about it.
    /// </summary>
    public static IReadOnlyList<string> FindRowsThatStay(IModel model) => FindRowsThatStay(model, typeof(User));

    /// <summary>
    /// The tables of a model whose rows would not go with their account. Deleting an account
    /// deletes one row, the account, and the database deletes what hangs on it: so the foreign
    /// key that carries the owner to the account must delete the row with what it points at, at
    /// every row on the way. A table whose owner does not lead to the account at all is for
    /// <see cref="FindViolations(IModel)"/> to find, and is not repeated here.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="account">The entity that is the account.</param>
    public static IReadOnlyList<string> FindRowsThatStay(IModel model, Type account)
    {
        var accountEntity = model.FindEntityType(account)
            ?? throw new InvalidOperationException($"The model has no entity {account.Name}.");

        return
        [
            .. from entity in model.GetEntityTypes()
               where entity != accountEntity && CarriesOwner(entity)
               let owner = entity.FindProperty(nameof(IUserOwned.UserId))
               where owner is not null
                   && LeadsToTheAccount(entity, owner, accountEntity, [], AnyKey)
                   && !LeadsToTheAccount(entity, owner, accountEntity, [], DeletesWithWhatItPointsAt)
               let table = TableOf(entity)
               orderby table
               select $"{table}: its rows would stay when the account is deleted, or stand in the way of deleting it. " +
                   $"Declare OnDelete(DeleteBehavior.Cascade) on the foreign key that carries the owner to {TableOf(accountEntity)}, " +
                   "at every row on the way.",
        ];
    }

    /// <summary>The tables of a model whose rows belong to users.</summary>
    public static IReadOnlyList<string> UserOwnedTables(IModel model) =>
        [.. model.GetEntityTypes().Where(CarriesOwner).Select(TableOf).Order(StringComparer.Ordinal)];

    private static bool CarriesOwner(IReadOnlyEntityType entity) => typeof(IUserOwned).IsAssignableFrom(entity.ClrType);

    private static string TableOf(IReadOnlyEntityType entity) => entity.GetTableName() ?? entity.ClrType.Name;

    private static bool AnyKey(IForeignKey key) => true;

    private static bool DeletesWithWhatItPointsAt(IForeignKey key) => key.DeleteBehavior == DeleteBehavior.Cascade;

    /// <summary>
    /// Whether the database holds the owner of a row to being an account: by a foreign key from
    /// the owner to the account, or by one that carries the owner along to a row which is held
    /// to it in turn. The second is for a component that may not point at Accounts.
    /// </summary>
    /// <param name="entity">The entity whose rows are asked about.</param>
    /// <param name="owner">The property that holds the owner.</param>
    /// <param name="account">The entity that is the account.</param>
    /// <param name="seen">The entities on the way so far.</param>
    /// <param name="counts">Which foreign keys count as a way to the account.</param>
    private static bool LeadsToTheAccount(
        IEntityType entity, IProperty owner, IEntityType account, HashSet<IEntityType> seen, Func<IForeignKey, bool> counts)
    {
        if (!seen.Add(entity))
        {
            return false;
        }

        foreach (var key in entity.GetForeignKeys().Where(counts))
        {
            var position = key.Properties.ToList().IndexOf(owner);
            if (position < 0)
            {
                continue;
            }

            if (key.PrincipalEntityType == account)
            {
                if (key.Properties.Count == 1 && key.PrincipalKey.IsPrimaryKey())
                {
                    return true;
                }

                continue;
            }

            // Through another row: its owner must be the one this key carries along.
            var carried = key.PrincipalKey.Properties[position];
            if (CarriesOwner(key.PrincipalEntityType)
                && carried.Name == nameof(IUserOwned.UserId)
                && LeadsToTheAccount(key.PrincipalEntityType, carried, account, seen, counts))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Marks a test which shows that the rows of a table are their owner's alone: what a user asks
/// for or does never reaches a row of another user (NFR-SEC-3). Every table that holds rows of
/// users needs at least one, which <see cref="OwnershipModelTests"/> checks.
/// </summary>
/// <param name="tables">The tables the test is about.</param>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ProvesOwnershipOfAttribute(params string[] tables) : Attribute
{
    public IReadOnlyList<string> Tables { get; } = tables;
}
