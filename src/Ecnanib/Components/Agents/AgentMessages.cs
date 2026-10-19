using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Components.Agents;

/// <summary>User-facing texts shared by the agents pages.</summary>
internal static class AgentMessages
{
    public const string NoAgents = "You have no agents yet. Create one to trade a symbol on one market and timeframe with one strategy and one council.";

    public const string PaperHint = "Paper mode simulates fills against live Binance prices with no key; live mode sends orders with the chosen key.";

    public const string DraftHint = "The agent is a draft: choose its strategy, council and risk limits below, then start it.";

    public const string RunningEditHint = "While the agent is running only its name can be changed; pause it to edit the strategy parameters, council or risk limits.";

    public static string ForSave(AgentSaveResult result) => result.Status switch
    {
        AgentSaveStatus.InvalidInput => result.Errors is { Count: > 0 } errors ? string.Join(" ", errors.Select(e => e.Message)) : "Check the values you entered.",
        AgentSaveStatus.LimitReached => result.Limit is { } limit
            ? $"You already have {limit.Current} of {limit.Max} agents allowed per user."
            : "The limit on agents per user is reached.",
        AgentSaveStatus.NotFound => "The agent no longer exists.",
        AgentSaveStatus.InvalidState => result.Detail ?? "That cannot be changed in the agent's current state.",
        AgentSaveStatus.ExchangeUnavailable => result.Detail ?? "Binance could not be reached; try again.",
        AgentSaveStatus.Conflict => result.Detail ?? "The agent changed meanwhile; reload the page and try again.",
        _ => string.Empty,
    };

    public static string ForAction(AgentActionResult result) => result.Status switch
    {
        AgentActionStatus.NotFound => "The agent no longer exists.",
        AgentActionStatus.InvalidState => result.Detail ?? "That action is not allowed in the agent's current state.",
        AgentActionStatus.LimitReached => result.Limit is { } limit
            ? $"You already have {limit.Current} of {limit.Max} running or paused agents allowed per user."
            : "The limit on running agents per user is reached.",
        AgentActionStatus.DisclaimerRequired => (result.Detail ?? "Accept the risk disclaimer first.") + " You can do that on the settings page.",
        AgentActionStatus.KeyUnavailable => "The agent cannot start: " + (result.Detail ?? "its Binance key cannot be used."),
        AgentActionStatus.ProviderKeyMissing => "The agent cannot start: " + (result.Detail ?? "a council provider has no valid key."),
        AgentActionStatus.StrategyUnavailable => "The agent cannot start: " + (result.Detail ?? "its strategy is no longer available."),
        AgentActionStatus.InvalidConfiguration => "The agent cannot start: " + (result.Detail ?? "check its configuration."),
        AgentActionStatus.Failed => result.Detail ?? "The action could not be completed; the agent is unchanged.",
        _ => string.Empty,
    };

    public static string StateClass(AgentState state) => "status-" + state.ToString().ToLowerInvariant();

    public static string Describe(AgentState state, PauseCause? cause) => (state, cause) switch
    {
        (AgentState.Paused, PauseCause.DailyLoss) => "Paused (daily loss limit)",
        (AgentState.Paused, PauseCause.KeyInvalid) => "Paused (key invalid)",
        _ => state.ToString(),
    };

    public static string Outcome(EvaluationOutcome outcome) => outcome switch
    {
        EvaluationOutcome.NoAction => "No action",
        EvaluationOutcome.InProgress => "In progress",
        _ => outcome.ToString(),
    };

    public static string Timeframe(CandleInterval timeframe) => AgentsOptions.Timeframes.Contains(timeframe) ? AgentsOptions.KeyOf(timeframe) : timeframe.ToLabel();

    public static string Market(MarketType market) => market == MarketType.Spot ? "Spot" : "USDⓈ-M Futures";

    public static string Mode(AgentMode mode) => mode == AgentMode.Paper ? "Paper" : "Live";

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
