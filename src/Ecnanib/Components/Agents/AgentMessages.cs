using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.ModelSync.Abstractions;

namespace Ecnanib.Components.Agents;

/// <summary>User-facing texts shared by the agents pages.</summary>
internal static class AgentMessages
{
    public const string NoAgents = "You have no agents yet. Create one to trade a symbol on one market and timeframe with one strategy and one council.";

    public const string PaperHint = "Paper mode simulates fills against live Binance prices with no key; live mode sends orders with the chosen key.";

    public const string DraftHint = "The agent is a draft: finish its setup (strategy, council and risk limits), then review and start it.";

    public const string SetupIntro = "Every step is saved on its own; a draft moves to the next step when it is saved. Nothing trades until the agent is started on the last step.";

    /// <summary>The notice the agent page shows after an action, from the status it was redirected with.</summary>
    public static string? DetailNotice(string? status, string? detail) => status switch
    {
        "started" => "The agent is running and will be evaluated at the next candle close.",
        "paused" => "The agent is paused: it keeps managing open positions and opens nothing new.",
        "resumed" => "The agent is running again.",
        "stopped" => "The agent was stopped." + (detail is null ? "" : " " + detail),
        _ => null,
    };

    /// <summary>The notice the setup wizard shows after a save, from the status it was redirected with.</summary>
    public static string? SetupNotice(string? status, string? detail) => status switch
    {
        "created" => "The draft was created. Choose its strategy, council and risk limits, then review and start it.",
        "saved" => "The changes were saved.",
        "unchanged" => "Nothing changed.",
        "cloned" => "This is the copy; it starts as a draft.",
        "upgraded" => "The agent now runs the newest version of its strategy." + (detail is null ? "" : " " + detail),
        "window-added" => "The no-entry window was added; it applies from the next evaluation.",
        "window-removed" => "The no-entry window was removed.",
        "council-defaults" => "The council uses the default assignments by strength.",
        "council-single" => "One model now performs all four roles in sequence.",
        _ => null,
    };

    public static string Stamp(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "never";

    public static string Markets(ExchangeKeyView key) => (key.AllowSpot, key.AllowFutures) switch
    {
        (true, true) => "Spot and Futures",
        (true, false) => "Spot",
        (false, true) => "Futures",
        _ => "no market",
    };

    public const string RunningEditHint = "While the agent is running only its name can be changed; pause it to edit the strategy parameters, strategy version, no-entry windows, council or risk limits.";

    public const string UpgradeHint = "Upgrading keeps the parameters the new version still accepts and resets the others to their defaults.";

    public const string WindowsHint = "No new entry is opened while a window is active (UTC); the agent keeps managing open positions. Windows apply from the next evaluation.";

    public const string StopAllHint = "The kill switch: every running, paused or failed agent is stopped, its open orders are cancelled, and its open positions are closed at market or left open as unmanaged, as you choose.";

    /// <summary>The outcome of "stop all my agents" (FR-EXE-10): the count stopped and, when some could not be, each with its reason.</summary>
    public static string StoppedAll(AgentBulkActionResult result, bool closePositions)
    {
        var head = $"{Plural(result.Stopped, "agent was", "agents were")} stopped" + (closePositions ? ", orders cancelled and positions closing at market." : ", orders cancelled and positions left open as unmanaged.");
        if (result.Failed.Count == 0)
            return head;
        return head + " " + Plural(result.Failed.Count, "agent could", "agents could") + " not be stopped: "
            + string.Join("; ", result.Failed.Select(f => $"{f.Name} ({ForAction(f.Result)})")) + ".";
    }

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

    /// <summary>"Every Friday 21:00 → Monday 01:00 UTC" or "2026-11-05 12:00 → 2026-11-05 16:00 UTC".</summary>
    public static string Describe(EntryWindowView window) => window.Kind == EntryWindowKind.Recurring
        ? $"Every {window.StartDay} {window.StartTime:HH\\:mm} → {window.EndDay} {window.EndTime:HH\\:mm} UTC"
        : $"{window.StartAt:yyyy-MM-dd HH:mm} → {window.EndAt:yyyy-MM-dd HH:mm} UTC";

    public static string Kind(EntryWindowKind kind) => kind == EntryWindowKind.Recurring ? "Every week" : "One-off";

    public const string DeprecatedMark = "Deprecated model";

    /// <summary>FR-SYNC-03: one role's model left its provider's list; the replacement offered is the provider's newest available catalog model, when there is one.</summary>
    public static string DeprecatedModel(CouncilAssignmentView role, CatalogModel? replacement)
    {
        var provider = ProviderNames.Display(role.Provider);
        var head = $"{provider} no longer lists {role.Model}.";
        return replacement is null
            ? $"{head} No {provider} model is available in the catalog: sync it on the providers page or enter a model id by hand."
            : $"{head} Choose a replacement; {provider}'s newest model is {Label(replacement)}.";
    }

    /// <summary>The page-level prompt naming every enabled role on a deprecated model (FR-SYNC-03).</summary>
    public static string DeprecatedModels(IReadOnlyList<CouncilAssignmentView> roles) =>
        $"{Plural(roles.Count, "council role uses", "council roles use")} a model its provider no longer lists: "
        + string.Join(", ", roles.Select(r => $"{CouncilRoles.Display(r.Role)} ({r.Model} on {ProviderNames.Display(r.Provider)})"))
        + ". Choose a replacement in the council.";

    private static string Label(CatalogModel model) => model.DisplayName == model.ModelId ? model.ModelId : $"{model.DisplayName} ({model.ModelId})";
}
