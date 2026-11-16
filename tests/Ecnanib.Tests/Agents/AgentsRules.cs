using System.Reflection;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// What of Agents is built, in one place. Other components wait for Agents: Credentials for what
/// asks whether a key lets an agent start and for what is told when a key goes, the Catalog for
/// what is told when a model goes, and the audit trail for what starts, stops, and switches an
/// agent. Their tests ask here whether what they wait for is there. This file is the source of
/// truth: <c>docs/agents.md</c> explains it, and <see cref="AgentsRulesTests"/> holds it to the code.
/// </summary>
/// <remarks>
/// <para>
/// Agents keeps the agents of users and their settings, and does nothing else with an agent yet:
/// it starts none, switches none to Live, and gives no role a model. So an agent is a draft in
/// Demo, depends on no key, and uses no model.
/// </para>
/// <para>
/// Two things tell that this has changed. An operation that is added to a public interface of
/// Agents is not listed in <see cref="SettingsOperations"/>: until someone lists it, the tests
/// that wait for an agent that can run fail, each with what it waits for. And a foreign key from
/// a table of Agents to the catalog's models is a model that an agent uses: the tests that wait
/// for a model choice fail from then on.
/// </para>
/// </remarks>
internal static class AgentsRules
{
    /// <summary>The namespace of the component that keeps the agents.</summary>
    public const string Namespace = "Ecnanib.Agents";

    /// <summary>
    /// The operations of Agents, each with what it does: every method of a public interface of
    /// the component. None of them starts, pauses, resumes, or stops an agent, switches its mode,
    /// or closes a position. To list one that is added later is to decide, here where it shows,
    /// that it does none of those either, or that everything which waits for it is built: see
    /// what fails when it is not listed.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SettingsOperations = new Dictionary<string, string>
    {
        ["IStoredAgents.ListAsync"] = "Reads the agents of a user.",
        ["IStoredAgents.FindAsync"] = "Reads one agent of a user.",
        ["IStoredAgents.SymbolsAsync"] = "Reads the symbols that an agent can be given on each market.",
        ["IStoredAgents.CreateAsync"] = "Creates an agent, which is a draft in Demo.",
        ["IStoredAgents.SaveAsync"] = "Changes the settings of an agent, and neither its state nor its mode.",
        ["IStoredAgents.CloneAsync"] = "Creates a copy of an agent, which is a draft in Demo.",
        ["IStoredAgents.DeleteAsync"] = "Deletes an agent that is a draft, or is stopped with nothing open.",
        ["IAgentExposure.HasOpenPositionOrOrdersAsync"] =
            "Asked by Agents, and answered by whatever keeps positions and orders. It is no operation on an agent.",
    };

    /// <summary>The operations of the application's Agents component: each as <c>Interface.Method</c>, in order.</summary>
    public static IReadOnlyList<string> Operations() => OperationsOf(typeof(IStoredAgents).Assembly, Namespace);

    /// <summary>The methods of the public interfaces in a namespace of an assembly, or nested in it.</summary>
    public static IReadOnlyList<string> OperationsOf(Assembly assembly, string @namespace) =>
    [
        .. from type in assembly.GetTypes()
           where type is { IsInterface: true, IsPublic: true } && IsWithin(type.Namespace, @namespace)
           from method in type.GetMethods()
           orderby type.Name, method.Name
           select $"{type.Name}.{method.Name}",
    ];

    /// <summary>
    /// The operations of Agents that are not listed as keeping settings. While there is none, no
    /// agent can run: nothing starts one.
    /// </summary>
    public static IReadOnlyList<string> OperationsBeyondSettings() =>
        [.. Operations().Where(operation => !SettingsOperations.ContainsKey(operation))];

    /// <summary>
    /// The models that the agents of the application's model use: each foreign key from a table
    /// of Agents to the catalog's models, as <c>table.column</c>. While there is none, no agent
    /// uses a model.
    /// </summary>
    public static IReadOnlyList<string> ModelChoices(IModel model) => ModelChoices(model, Namespace, typeof(AiModel));

    /// <summary>The foreign keys from the tables of a component to the table of an entity.</summary>
    /// <param name="model">The model.</param>
    /// <param name="namespace">The namespace of the component.</param>
    /// <param name="chosen">The entity that is pointed at.</param>
    public static IReadOnlyList<string> ModelChoices(IModel model, string @namespace, Type chosen) =>
    [
        .. from entity in model.GetEntityTypes()
           where IsWithin(entity.ClrType.Namespace, @namespace)
           from key in entity.GetForeignKeys()
           where key.PrincipalEntityType.ClrType == chosen
           let table = entity.GetTableName() ?? entity.ClrType.Name
           let columns = string.Join(", ", key.Properties.Select(property => property.GetColumnName()))
           orderby table, columns
           select $"{table}.{columns}",
    ];

    /// <summary>
    /// The tables of a model that hold positions or orders, by their names: a table whose name
    /// has the word "position" or "order" in it.
    /// </summary>
    public static IReadOnlyList<string> TablesOfPositionsOrOrders(IModel model) =>
    [
        .. from entity in model.GetEntityTypes()
           let table = entity.GetTableName() ?? entity.ClrType.Name
           where table.Split('_').Any(word => word is "position" or "positions" or "order" or "orders")
           orderby table
           select table,
    ];

    /// <summary>True when a type is of Agents.</summary>
    public static bool IsOfAgents(Type type) => IsWithin(type.Namespace, Namespace);

    /// <summary>True when a namespace is another, or nested in it.</summary>
    private static bool IsWithin(string? name, string @namespace) =>
        name is not null && (name == @namespace || name.StartsWith(@namespace + ".", StringComparison.Ordinal));
}
