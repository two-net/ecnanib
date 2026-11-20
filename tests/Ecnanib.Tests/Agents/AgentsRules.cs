using System.Reflection;
using Ecnanib.Agents;
using Ecnanib.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// What of Agents is built, in one place. Other components hold Agents to what it does:
/// Credentials to asking whether a key lets an agent start, to asking which models a user can
/// select, and to being told when a key goes, the Catalog to being told when a model goes, and
/// the audit trail to recording what starts, stops, closes, and switches the mode. Their tests
/// ask here what Agents can do. This file is the source of truth: <c>docs/agents.md</c> explains
/// it, and <see cref="AgentsRulesTests"/> holds it to the code.
/// </summary>
/// <remarks>
/// <para>
/// Agents keeps the agents of users and their settings, gives the roles of their teams their
/// models, starts, pauses, resumes, and stops them, closes their positions by hand, and switches
/// them between Demo and Live. So an agent is created in Demo, and runs with the Binance
/// credential of its user for its mode and with the models of its roles, each with the key of
/// its user for the provider of the model.
/// </para>
/// <para>
/// Every operation of a public interface of Agents is listed here, as one that keeps settings,
/// as a control, or as a switch of the mode: one that is added fails
/// <see cref="AgentsRulesTests"/> until someone lists it, and so decides what it is. And a
/// foreign key from a table of Agents to the catalog's models is a model that an agent uses:
/// <see cref="ModelChoices(IModel)"/> finds each, and the tests of Credentials and of the
/// Catalog hold Agents to what follows from it.
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
        ["IStoredAgents.RoleChoicesAsync"] =
            "Reads the models that a user can give the roles of an agent, and the provider that each role has by default.",
        ["IStoredAgents.RoleRecommendationsAsync"] =
            "Reads the models that have a strength score in each role on a market and a timeframe, strongest first, from " +
            "what the user's own agents did: what a role is recommended from (FR-TEAM-4). It changes no agent.",
        ["IStoredAgents.CreateAsync"] = "Creates an agent, which is a draft in Demo, with the models that were chosen for its roles.",
        ["IStoredAgents.SaveAsync"] =
            "Changes the settings of an agent, the models of its roles among them, and neither its state nor its mode.",
        ["IStoredAgents.CloneAsync"] = "Creates a copy of an agent, which is a draft in Demo, with the models of its roles.",
        ["IStoredAgents.DeleteAsync"] = "Deletes an agent that is a draft, or is stopped with nothing open.",
        ["IAgentControls.HistoryAsync"] = "Reads the changes of the state of one agent.",
        ["IAgentModes.BlockOfAsync"] = "Reads what stands in the way of switching the mode of an agent. It switches nothing.",
        ["IAgentExposure.HasOpenPositionOrOrdersAsync"] =
            "Asked by Agents, and answered by whatever keeps positions and orders. It is no operation on an agent.",
        ["IAgentExposure.HasOpenPositionAsync"] =
            "Asked by Agents, and answered by whatever keeps positions. It is no operation on an agent.",
        ["IAgentExposure.AccountConflictAsync"] =
            "Asked by Agents before an agent runs, and answered by whatever keeps positions, which reads the account of the " +
            "user at the exchange. It is no operation on an agent, and changes nothing at the exchange.",
        ["IRunningAgents.OfTimeframesAsync"] =
            "Reads the agents of all users that run cycles, for what starts a cycle when a candle closes. It changes no agent.",
        ["IRunningAgents.FindAsync"] =
            "Reads one agent when it runs cycles, for what starts its cycle when the turn of the cycle comes. It changes no agent.",
        ["IAgentTrading.StateUnderLockAsync"] =
            "Reads the state of one agent under the lock of its account, for what writes an order of the agent. It changes no agent.",
        ["IAgentHalts.FindAsync"] =
            "Reads one agent in whatever state it is, for what watches its loss limits when its account is reconciled. It " +
            "changes no agent.",
        ["IAgentTeams.FindAsync"] =
            "Reads one agent of a user in whatever state it is, with its team and with what keeps the team from working, for " +
            "what runs a test cycle of it, which places no order (FR-CYC-12). It changes no agent.",
    };

    /// <summary>
    /// The controls of Agents, each with what it does: the operations that do more with an agent
    /// than keep its settings. With them an agent can run, so everything that waited for that is
    /// built, and the tests that waited hold Agents to it: asking what blocks a start
    /// (<c>AgentStartBlockTests</c>), being told when a key goes (<c>KeyDependentsTests</c>), and
    /// recording the action in the audit trail (<c>AuditRulesTests</c>). To list a control that
    /// is added later is to decide that it does all of that which applies to it. None of them
    /// switches the mode of an agent: see <see cref="ModeOperations"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ControlOperations = new Dictionary<string, string>
    {
        ["IAgentControls.StartAsync"] =
            "Starts an agent. It asks what blocks the start as far as the key and the account at the exchange go, and records " +
            "the start in the audit trail.",
        ["IAgentControls.PauseAsync"] = "Pauses an agent that runs, and records the pause in the audit trail.",
        ["IAgentControls.ResumeAsync"] =
            "Resumes an agent, which is a start: it asks what blocks it as far as the key and the account at the exchange go, " +
            "and records it in the audit trail.",
        ["IAgentControls.StopAsync"] =
            "Stops an agent, with what its user chose for its open position, and records the stop in the audit trail.",
        ["IAgentControls.ClosePositionAsync"] =
            "Closes the position of an agent by hand: it records the request in the audit trail, and asks whatever keeps positions.",
        ["IAgentExposure.ClosePositionAtMarketAsync"] =
            "Asked by Agents of whatever keeps positions, after Agents recorded the request of the user. Agents places no order.",
        ["IAgentTrading.RequireAttentionAsync"] =
            "Moves an agent that runs to Attention required, because its position is open with no stop-loss and could not be " +
            "closed, or because the account of its user at the exchange conflicts with how it trades. It is the service's " +
            "doing, and no action of a user: it starts nothing, and the change of the state records it.",
        ["IAgentHalts.HaltAsync"] =
            "Moves an agent that runs to Halted, because it reached its daily loss limit or its drawdown limit. It is the " +
            "service's doing, and no action of a user: it starts nothing, and the change of the state records it.",
        ["IAgentHalts.EndDailyHaltsAsync"] =
            "Resumes the agents that their daily loss limit halted on an earlier UTC day, which is a start: it asks what " +
            "blocks it as far as the key and the team go. It is the service's doing, and no action of a user: the change " +
            "of the state records it, and the audit trail does not.",
    };

    /// <summary>
    /// The operations of Agents that switch the mode of an agent, each with what it does. An
    /// agent in Live trades with real money, so a switch to Live takes a valid Live key, the
    /// confirmation of the risk, and a fresh proof of who the user is (FR-AGT-7), and every
    /// switch is recorded in the audit trail (<c>AuditRulesTests</c>). To list one that is
    /// added later is to decide that it does all of that which applies to it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ModeOperations = new Dictionary<string, string>
    {
        ["IAgentModes.SwitchToLiveAsync"] =
            "Switches an agent from Demo to Live, with a valid Live key, the confirmation of the risk, and a fresh proof of " +
            "who its user is, and records the switch in the audit trail.",
        ["IAgentModes.SwitchToDemoAsync"] = "Switches an agent from Live back to Demo, and records the switch in the audit trail.",
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

    /// <summary>The operations of Agents that are listed neither as keeping settings, nor as a control, nor as a switch of the mode.</summary>
    public static IReadOnlyList<string> UnlistedOperations() =>
        [
            .. OperationsBeyondSettings()
                .Where(operation => !ControlOperations.ContainsKey(operation) && !ModeOperations.ContainsKey(operation)),
        ];

    /// <summary>
    /// The tables that hold what an agent did, and do not say the mode it did it in: each table
    /// with a foreign key to the table of an entity, other than that table itself, that has no
    /// column <c>mode</c>. An agent is switched between Demo and Live, so what it did is labeled
    /// row by row, and is never read through the mode that the agent is in now: what it did in
    /// Demo is Demo for good, and is not combined with what it did in Live (FR-BIN-11).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="agent">The entity that the rows are of: an agent.</param>
    public static IReadOnlyList<string> TablesOfAgentRowsWithoutMode(IModel model, Type agent) =>
    [
        .. from entity in model.GetEntityTypes()
           where entity.ClrType != agent
               && entity.GetForeignKeys().Any(key => key.PrincipalEntityType.ClrType == agent)
           let table = entity.GetTableName() ?? entity.ClrType.Name
           where entity.GetProperties().All(property => property.GetColumnName() != "mode")
           orderby table
           select table,
    ];

    /// <summary>
    /// The tables with rows of an agent that need not say a mode, each with why. A table is
    /// listed here only when nothing in it is a result of what an agent did.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AgentRowsWithoutMode = new Dictionary<string, string>
    {
        ["agent_roles"] =
            "The model that a role of an agent was given is a setting of the agent, as its risk settings are, and no " +
            "result of what the agent did. It stays as it is when the agent is switched between Demo and Live.",
    };

    /// <summary>
    /// The models that the agents of the application's model use: each foreign key from a table
    /// of Agents to the catalog's models, as <c>table.column</c>. An agent uses the models that
    /// the roles of its team were given (FR-AGT-5).
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
