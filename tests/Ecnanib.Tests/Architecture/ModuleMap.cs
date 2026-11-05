namespace Ecnanib.Tests.Architecture;

/// <summary>
/// The allowed dependencies between units, and the single source of truth for them.
/// See docs/architecture.md for the rules and for how to change this map.
/// </summary>
internal static class ModuleMap
{
    public const string Kernel = "Kernel";
    public const string Platform = "Platform";
    public const string WebUi = "WebUi";

    /// <summary>
    /// Unit to the units whose Contracts it may use. Every unit may also use Kernel.
    /// The Web UI may use neither adapter nor Platform (2.4, FR-ADM-01, FR-KEY-09).
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Allowed { get; } = new Dictionary<string, string[]>
    {
        [WebUi] =
        [
            "Administration", "Agents", "AiCouncil", "Catalog", "Credentials", "DecisionCycle", "Execution",
            "Experience", "Identity", "MarketData", "Notifications", "RiskEngine", "Scheduler",
        ],
        ["Administration"] =
        [
            Platform, "Identity", "AiCouncil", "Catalog", "Agents", "Scheduler", "DecisionCycle", "ExchangeAdapter",
        ],
        ["Scheduler"] = [Platform, "Agents", "DecisionCycle", "Execution"],
        ["Experience"] = [Platform, "DecisionCycle", "Execution", "MarketData", "AiCouncil", "Agents", "Catalog"],
        ["DecisionCycle"] = [Platform, "Agents", "MarketData", "AiCouncil", "RiskEngine", "Execution", "Notifications"],
        // MarketData and RiskEngine for the runtime context in prompts: market data, indicators and risk settings (FR-PRM-01).
        ["AiCouncil"] = [Platform, "AiAdapters", "Credentials", "Catalog", "MarketData", "RiskEngine"],
        ["Execution"] =
        [
            Platform, "Agents", "Credentials", "ExchangeAdapter", "MarketData", "RiskEngine", "Notifications",
        ],
        ["Agents"] = [Platform, "Identity", "Credentials", "Catalog", "MarketData", "RiskEngine", "Notifications"],
        ["MarketData"] = [Platform, "ExchangeAdapter"],
        ["Catalog"] = [Platform, "Credentials", "AiAdapters", "Notifications"],
        ["Credentials"] = [Platform, "Identity", "ExchangeAdapter", "AiAdapters", "Notifications"],
        ["Notifications"] = [Platform, "Identity"],
        ["Identity"] = [Platform],
        ["RiskEngine"] = [],
        ["ExchangeAdapter"] = [],
        ["AiAdapters"] = [],
        [Platform] = [],
        [Kernel] = [],
    };

    /// <summary>The units with an entry class that Program.cs must call.</summary>
    public static IEnumerable<string> Registered => Allowed.Keys.Where(unit => unit is not (WebUi or Kernel));
}
