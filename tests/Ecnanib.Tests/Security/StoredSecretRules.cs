using Ecnanib.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Security;

/// <summary>
/// Which columns hold a stored secret, in one place. A re-encryption routine moves all stored
/// values to the active master key (FR-KEY-7), so it must know every column that holds one. A
/// component declares such a column with <c>AddStoredSecret</c>; <c>docs/security.md</c> explains
/// the rule, and <see cref="StoredSecretRuleTests"/> enforces it.
/// </summary>
/// <remarks>
/// The rule cannot tell what a column holds. It goes by the column that every stored secret has
/// beside it: the id of the master key that encrypted it (FR-KEY-6), whose name ends in "key id".
/// A column with such a name belongs to a declared secret, or is listed here with what it holds.
/// </remarks>
internal static class StoredSecretRules
{
    /// <summary>
    /// The columns whose name ends in "key id" and that do not name a master key, each with what
    /// it holds. To add one is to decide, here where it shows, that no stored secret is beside it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotTheIdOfAMasterKey = new Dictionary<string, string>();

    /// <summary>What breaks the rule in the application's model, each with what to do about it.</summary>
    /// <param name="model">The model.</param>
    /// <param name="declared">The columns that the components declared to hold a stored secret.</param>
    public static IReadOnlyList<string> FindViolations(IModel model, IEnumerable<StoredSecretColumn> declared) =>
        FindViolations(model, declared, NotTheIdOfAMasterKey);

    /// <summary>What breaks the rule in a model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="declared">The columns that are declared to hold a stored secret.</param>
    /// <param name="notTheIdOfAMasterKey">The columns that are named like the id of a master key and are not one, each as <c>table.column</c>.</param>
    public static IReadOnlyList<string> FindViolations(
        IModel model, IEnumerable<StoredSecretColumn> declared, IReadOnlyDictionary<string, string> notTheIdOfAMasterKey)
    {
        var violations = new List<string>();

        // The columns that name the master key of a declared secret, each as table.column.
        var besideASecret = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var column in declared)
        {
            var declaredAs = $"{column.Entity.Name}.{column.Value}";

            if (model.FindEntityType(column.Entity) is not { } entity)
            {
                violations.Add(
                    $"{declaredAs}: is declared to hold a stored secret, but {column.Entity.Name} is not an entity of the model. " +
                    "Take the declaration away.");

                continue;
            }

            var value = entity.FindProperty(column.Value);
            var keyId = entity.FindProperty(column.KeyId);

            if (value is null)
            {
                violations.Add($"{declaredAs}: is declared to hold a stored secret, but is not stored. Map it as a column.");
            }

            if (keyId is null)
            {
                violations.Add(
                    $"{declaredAs}: {column.Entity.Name}.{column.KeyId} is declared to name its master key, but is not stored. " +
                    "Map it as a column.");
            }

            if (value is null || keyId is null)
            {
                continue;
            }

            var name = NameOf(entity, value);

            if (!values.Add(name))
            {
                violations.Add($"{name}: is declared to hold a stored secret more than once. Declare it once.");

                continue;
            }

            if (!besideASecret.Add(NameOf(entity, keyId)))
            {
                violations.Add(
                    $"{name}: {NameOf(entity, keyId)} names its master key, and that of another stored secret. " +
                    "Give each stored secret a column of its own for the id of its master key.");
            }

            if (value.IsNullable != keyId.IsNullable)
            {
                violations.Add(
                    $"{name}: may be empty where {NameOf(entity, keyId)} may not, or the other way round. A value and the " +
                    "id of its master key are there together. Make both required, or neither.");
            }
        }

        var namedLikeOne = (
            from entity in model.GetEntityTypes()
            from property in entity.GetProperties()
            where property.Name.EndsWith("KeyId", StringComparison.Ordinal)
            select NameOf(entity, property))
            .ToHashSet(StringComparer.Ordinal);

        violations.AddRange(
            from name in namedLikeOne
            where !besideASecret.Contains(name) && !notTheIdOfAMasterKey.ContainsKey(name)
            select $"{name}: is named as the id of a master key, and no stored secret is declared beside it. Declare the " +
                $"column of the value with AddStoredSecret, or list this one in {nameof(StoredSecretRules)}." +
                $"{nameof(NotTheIdOfAMasterKey)} with what it holds.");

        violations.AddRange(
            from name in notTheIdOfAMasterKey.Keys
            where !namedLikeOne.Contains(name)
            select $"{name}: is listed, but the model has no column of that name that ends in \"key id\". Take it off the list.");

        violations.AddRange(
            from name in notTheIdOfAMasterKey.Keys
            where besideASecret.Contains(name)
            select $"{name}: is listed as not the id of a master key, but a stored secret is declared beside it. Take it off the list.");

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    /// <summary>The columns of a model that are declared to hold a stored secret, each as <c>table.column</c>.</summary>
    public static IReadOnlyList<string> ColumnsOf(IModel model, IEnumerable<StoredSecretColumn> declared) =>
    [
        .. from column in declared
           let entity = model.FindEntityType(column.Entity)
           where entity?.FindProperty(column.Value) is not null
           select NameOf(entity, entity.FindProperty(column.Value)!),
    ];

    private static string NameOf(IEntityType entity, IProperty property) =>
        $"{entity.GetTableName() ?? entity.ClrType.Name}.{property.GetColumnName()}";
}
