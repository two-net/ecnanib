using System.Reflection;
using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Architecture;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>The kinds of data that an AI provider is sent, and no other (NFR-SEC-8).</summary>
internal enum SentData
{
    MarketData,
    AgentSettings,
    PositionState,
    Lessons,
}

/// <summary>
/// What holds for the prompt templates beyond what the database refuses, in one place. Templates
/// are managed by the system and delivered as seed data (FR-PRM-7): nothing of the application
/// but the seed creates one. And each role output records the template versions it used
/// (FR-PRM-6): role outputs are not built, so the rule waits for the table that stores them.
/// And what composes a prompt is given no template: the modules follow from the settings of the
/// agent, so a mismatched combination cannot be chosen (FR-PRM-4). And what it is given is what an
/// AI provider is sent: it is limited to market data, agent settings, position state, and
/// lessons (NFR-SEC-8), and none of it can say whose agent the prompt is for (FR-PRM-9).
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

    /// <summary>
    /// What a caller hands over that is no data: which role is called, which the instructions of
    /// the prompt follow from, and what cancels the question.
    /// </summary>
    public static readonly IReadOnlySet<string> NoData =
        new HashSet<string> { "ComposeAsync(role)", "ComposeAsync(cancellationToken)" };

    /// <summary>
    /// Every value that a caller hands to what composes a prompt, each with the kind of data
    /// that it is. It is what an AI provider is sent besides the templates, so the list is the
    /// limit of NFR-SEC-8: market data, agent settings, position state, and lessons. To list a
    /// value is to decide, here where it shows, that a provider is sent it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SentData> Sent = new Dictionary<string, SentData>
    {
        ["ComposeAsync(parts).Agent.Market"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Timeframe"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Symbol"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.QuoteAsset"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.RiskPerTradePercent"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.MinRewardToRisk"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.LeverageCap"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.LiquidationBuffer"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.DailyLossPercent"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.DrawdownPercent"] = SentData.AgentSettings,
        ["ComposeAsync(parts).Agent.Limits.MaxEntryDistancePercent"] = SentData.AgentSettings,

        // The equity is the capital that was allocated with the profit or loss to date: what
        // the agent has to trade with now.
        ["ComposeAsync(parts).Agent.Equity"] = SentData.PositionState,
        ["ComposeAsync(parts).Agent.Position.Side"] = SentData.PositionState,
        ["ComposeAsync(parts).Agent.Position.EntryPrice"] = SentData.PositionState,
        ["ComposeAsync(parts).Agent.Position.Quantity"] = SentData.PositionState,
        ["ComposeAsync(parts).Agent.Position.StopLoss[]"] = SentData.PositionState,
        ["ComposeAsync(parts).Agent.Position.TakeProfit[]"] = SentData.PositionState,

        ["ComposeAsync(parts).DataPackage"] = SentData.MarketData,
        ["ComposeAsync(parts).Lessons[]"] = SentData.Lessons,
    };

    /// <summary>
    /// The values that are text, each with what it holds. A number, a flag, and a member of a
    /// fixed set cannot say who someone is, and text can: so each text that a provider is sent
    /// is listed with why it says nothing of the user (FR-PRM-9).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FreeText = new Dictionary<string, string>
    {
        ["ComposeAsync(parts).Agent.Symbol"] = "A symbol of Binance that the configuration file lists, such as BTCUSDT.",
        ["ComposeAsync(parts).Agent.QuoteAsset"] = "The asset that the symbol is priced in, as Binance names it, such as USDT.",
        ["ComposeAsync(parts).DataPackage"] =
            "The data package of a cycle, which the service writes from market data and from what the roles answered " +
            "before. It names no user and no agent.",
        ["ComposeAsync(parts).Lessons[]"] =
            "A lesson that a model wrote about a closed trade: the setup, what was expected, what happened, and a " +
            "takeaway. It names no user and no agent.",
    };

    /// <summary>
    /// The values that a caller hands to an interface as data: what <see cref="GivenTo"/> finds
    /// that holds nothing further, without what is <see cref="NoData"/>.
    /// </summary>
    public static IReadOnlyList<Given> DataGivenTo(Type composition) =>
    [
        .. GivenTo(composition).Where(given =>
            !NoData.Contains(given.Where)
            && !given.What.IsArray
            && !given.What.IsGenericType
            && (given.What.IsEnum || given.What.Namespace?.StartsWith("Ecnanib", StringComparison.Ordinal) != true)),
    ];

    /// <summary>
    /// What breaks the limit on what a provider is sent, each with what to do about it: a value
    /// that is handed over and not listed with its kind, a value that is listed and not handed
    /// over, an identifier, and a text that does not say what it holds.
    /// </summary>
    public static IReadOnlyList<string> FindUnlisted(Type composition)
    {
        var data = DataGivenTo(composition);
        var violations = new List<string>();

        violations.AddRange(
            from given in data
            where !Sent.ContainsKey(given.Where)
            select $"{given.Where}: is handed over, and is not listed. List it in {nameof(PromptRules)}.{nameof(Sent)} as market " +
                "data, agent settings, position state, or lessons, or do not hand it over.");

        violations.AddRange(
            from listed in Sent.Keys
            where data.All(given => given.Where != listed)
            select $"{listed}: is listed, but is not handed over. Take it off the list.");

        violations.AddRange(
            from given in data
            where given.What == typeof(Guid)
            select $"{given.Where}: is an identifier. A prompt holds no account identifier (FR-PRM-9).");

        violations.AddRange(
            from given in data
            where given.What == typeof(string) && !FreeText.ContainsKey(given.Where)
            select $"{given.Where}: is text, and does not say what it holds. List it in {nameof(PromptRules)}.{nameof(FreeText)}.");

        violations.AddRange(
            from listed in FreeText.Keys
            where data.All(given => given.Where != listed || given.What != typeof(string))
            select $"{listed}: is listed as text, but no text is handed over there. Take it off the list.");

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    /// <summary>A call to a model, as a caller of it is found by.</summary>
    public const string ModelCall = "Ecnanib.AiProviderGateway.IAiProviderGateway::CompleteAsync";

    /// <summary>What a call to a model is made with: its constructor, as a caller of it is found by.</summary>
    public const string ModelCallRequest = "Ecnanib.AiProviderGateway.AiCallRequest::.ctor";

    /// <summary>The one component that calls a model, and so the one that sends a provider a prompt.</summary>
    public const string CallsModels = "Ecnanib.DecisionEngine.";

    /// <summary>
    /// The types of an assembly that call a model, or put together what a call is made with,
    /// each by its full name. Whatever does sends a provider data.
    /// </summary>
    public static IReadOnlyList<string> CallersOfModels(Assembly assembly) =>
    [
        .. DependencyChecker.FindCallersOf(assembly, ModelCall)
            .Concat(DependencyChecker.FindCallersOf(assembly, ModelCallRequest))
            .Distinct()
            .Order(StringComparer.Ordinal),
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
