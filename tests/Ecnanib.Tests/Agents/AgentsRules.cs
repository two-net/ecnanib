using System.Reflection;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// What of Agents is built, in one place. Other components hold Agents to what it does:
/// Credentials to asking whether a key lets an agent start and to being told when a key goes,
/// and the audit trail to recording what starts, stops, and closes. The Catalog still waits, for
/// what is told when a model goes. Their tests ask here what Agents can do. This file is the
/// source of truth: <c>docs/agents.md</c> explains it, and <see cref="AgentsRulesTests"/> holds
/// it to the code.
/// </summary>
/// <remarks>
/// <para>
/// Agents keeps the agents of users and their settings, and starts, pauses, resumes, and stops
/// them, and closes their positions by hand. It switches none to Live, and gives no role a
/// model. So an agent is in Demo, runs with the Binance credential of its user, and uses no
/// model.
/// </para>
/// <para>
/// Every operation of a public interface of Agents is listed here, as one that keeps settings
/// or as a control: one that is added fails <see cref="AgentsRulesTests"/> until someone lists
/// it, and so decides what it is. And a foreign key from a table of Agents to the catalog's
/// models is a model that an agent uses: the tests that wait for a model choice fail from then
/// on.
/// </para>
/// </remarks>
internal static class AgentsRules
{
    /// <summary>The namespace of the component that keeps the agents.</summary>
    public const string Namespace = "Ecnanib.Agents";

    /// <summary>
    /// The operations of Agents that keep the settings of agents, or only read or answer, each
    /// with what it does. None of them starts, pauses, resumes, or stops an agent, switches its
    /// mode, or closes a position. To list one that is added later is to decide, here where it
    /// shows, that it does none of those either.
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
        ["IAgentControls.HistoryAsync"] = "Reads the changes of the state of one agent.",
        ["IAgentExposure.HasOpenPositionOrOrdersAsync"] =
            "Asked by Agents, and answered by whatever keeps positions and orders. It is no operation on an agent.",
        ["IAgentExposure.HasOpenPositionAsync"] =
            "Asked by Agents, and answered by whatever keeps positions. It is no operation on an agent.",
    };

    /// <summary>
    /// The controls of Agents, each with what it does: the operations that do more with an agent
    /// than keep its settings. With them an agent can run, so everything that waited for that is
    /// built, and the tests that waited hold Agents to it: asking what blocks a start
    /// (<c>AgentStartBlockTests</c>), being told when a key goes (<c>KeyDependentsTests</c>), and
    /// recording the action in the audit trail (<c>AuditRulesTests</c>). To list a control that
    /// is added later is to decide that it does all of that which applies to it. None of them
    /// switches the mode of an agent.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ControlOperations = new Dictionary<string, string>
    {
        ["IAgentControls.StartAsync"] =
            "Starts an agent. It asks what blocks the start as far as the key goes, and records the start in the audit trail.",
        ["IAgentControls.PauseAsync"] = "Pauses an agent that runs, and records the pause in the audit trail.",
        ["IAgentControls.ResumeAsync"] =
            "Resumes an agent, which is a start: it asks what blocks it as far as the key goes, and records it in the audit trail.",
        ["IAgentControls.StopAsync"] =
            "Stops an agent, with what its user chose for its open position, and records the stop in the audit trail.",
        ["IAgentControls.ClosePositionAsync"] =
            "Closes the position of an agent by hand: it records the request in the audit trail, and asks whatever keeps positions.",
        ["IAgentExposure.ClosePositionAtMarketAsync"] =
            "Asked by Agents of whatever keeps positions, after Agents recorded the request of the user. Agents places no order.",
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
    /// The operations of Agents that are not listed as keeping settings: the controls, and
    /// whatever was added and is not listed yet. While there was none, no agent could run. There
    /// are some now, so what other components asked of an agent that can run is asked for good.
    /// </summary>
    public static IReadOnlyList<string> OperationsBeyondSettings() =>
        [.. Operations().Where(operation => !SettingsOperations.ContainsKey(operation))];

    /// <summary>The operations of Agents that are listed neither as keeping settings nor as a control.</summary>
    public static IReadOnlyList<string> UnlistedOperations() =>
        [.. OperationsBeyondSettings().Where(operation => !ControlOperations.ContainsKey(operation))];

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
