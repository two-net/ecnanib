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
/// And what composes a prompt is given no template: the modules follow from the settings of the
/// agent, so a mismatched combination cannot be chosen (FR-PRM-4).
/// <c>docs/prompts.md</c> explains them, and <see cref="PromptRulesTests"/> holds them to the code.
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

    /// <summary>
    /// The types that name a template, or say which one. What composes a prompt is given none of
    /// them: the modules follow from the market and the timeframe of the agent (FR-PRM-4).
    /// </summary>
    public static readonly IReadOnlySet<Type> Templates =
        new HashSet<Type> { typeof(PromptTemplate), typeof(TemplatesInUse), typeof(PromptTemplateKind) };

    /// <summary>
    /// Everything that a caller hands to an interface: each parameter of each of its methods,
    /// and what a parameter holds, down to the values. A market that is handed over in two
    /// places is found twice, so that two places which could disagree show.
    /// </summary>
    public static IReadOnlyList<Given> GivenTo(Type composition) =>
    [
        .. from method in composition.GetMethods()
           from parameter in method.GetParameters()
           from given in Within($"{method.Name}({parameter.Name})", parameter.ParameterType, [])
           select given,
    ];

    /// <summary>One thing that a caller hands over.</summary>
    /// <param name="Where">Where it is handed over: the parameter, and the path to the value inside it.</param>
    /// <param name="What">Its type.</param>
    internal sealed record Given(string Where, Type What);

    private static IEnumerable<Given> Within(string where, Type type, IReadOnlyList<Type> holders)
    {
        yield return new Given(where, type);

        // What a list or an optional value is of.
        var of = type.IsArray ? [type.GetElementType()!] : type.IsGenericType ? type.GetGenericArguments() : [];

        foreach (var given in of.SelectMany(element => Within($"{where}[]", element, holders)))
        {
            yield return given;
        }

        // What a type of the application holds. A type that holds itself is looked into once.
        if (type.Namespace?.StartsWith("Ecnanib", StringComparison.Ordinal) != true || type.IsEnum || holders.Contains(type))
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var given in Within($"{where}.{property.Name}", property.PropertyType, [.. holders, type]))
            {
                yield return given;
            }
        }
    }

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
