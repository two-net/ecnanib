using System.Reflection;
using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Architecture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// What holds for the prompt templates beyond what the database refuses, in one place. Templates
/// are managed by the system and delivered as seed data (FR-PRM-7): nothing of the application
/// but the seed creates one. And each role output records the template versions it used
/// (FR-PRM-6): role outputs are not built, so the rule waits for the table that stores them.
/// <c>docs/prompts.md</c> explains both, and <see cref="PromptRulesTests"/> holds them to the code.
/// </summary>
internal static class PromptRules
{
    /// <summary>The configuration of the template's table, which declares the rows that are seeded.</summary>
    public const string Seed = "Ecnanib.DecisionEngine.PromptTemplateConfiguration";

    /// <summary>What creates a template in the code: its constructor, as a caller of it is found by.</summary>
    public static readonly string TemplateConstructor = $"{typeof(PromptTemplate).FullName}::.ctor";

    /// <summary>
    /// The types of an assembly that create a template, each by its full name. A row that the
    /// application stores is created by its code first, so whatever is not the seed and is found
    /// here could store a template that no migration delivered.
    /// </summary>
    public static IReadOnlyList<string> CreatorsOfTemplates(Assembly assembly) =>
        DependencyChecker.FindCallersOf(assembly, TemplateConstructor);

    /// <summary>
    /// The tables of a model that hold what a role answered, by their names: a table whose name
    /// has the word "role" in it, and the word "output" or "call".
    /// </summary>
    public static IReadOnlyList<string> TablesOfRoleOutputs(IModel model) =>
        [.. RoleOutputs(model).Select(TableOf).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The tables of role outputs whose rows name no template: neither the table itself has a
    /// foreign key to the table of the templates, nor has a table that hangs on it. A role
    /// output records the template versions it used (FR-PRM-6), and a version is a row.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="template">The entity that is a version of a template.</param>
    public static IReadOnlyList<string> RoleOutputTablesWithoutTemplates(IModel model, Type template) =>
    [
        .. from output in RoleOutputs(model)
           where !PointsAt(output, template)
               && !model.GetEntityTypes().Any(other =>
                   other.GetForeignKeys().Any(key => key.PrincipalEntityType == output) && PointsAt(other, template))
           let table = TableOf(output)
           orderby table
           select table,
    ];

    private static IEnumerable<IEntityType> RoleOutputs(IModel model) =>
        from entity in model.GetEntityTypes()
        let words = TableOf(entity).Split('_')
        where words.Any(word => word is "role" or "roles")
            && words.Any(word => word is "output" or "outputs" or "call" or "calls")
        select entity;

    private static bool PointsAt(IEntityType entity, Type principal) =>
        entity.GetForeignKeys().Any(key => key.PrincipalEntityType.ClrType == principal);

    private static string TableOf(IReadOnlyEntityType entity) => entity.GetTableName() ?? entity.ClrType.Name;
}
