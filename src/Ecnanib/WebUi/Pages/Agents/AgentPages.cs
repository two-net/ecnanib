using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// What the pages of a user's agents have in common: the names of their forms, how a setting is
/// written, and what they say about a setting that cannot be stored (FR-AGT-1, FR-AGT-2). Every
/// setting is explained in plain language (NFR-USA-2).
/// </summary>
public static class AgentPages
{
    /// <summary>The name of the form that creates an agent, or changes one.</summary>
    public const string SaveForm = "save-agent";

    /// <summary>The name of the form that deletes an agent.</summary>
    public const string DeleteForm = "delete-agent";

    /// <summary>
    /// The value of the button that shows the symbols of the chosen market and stores nothing.
    /// A page is not interactive, so the list of symbols follows the market with a press of it.
    /// </summary>
    public const string ShowSymbols = "symbols";

    /// <summary>What a page says to a user whose email address is not verified, in place of the form that creates an agent (FR-ACC-1).</summary>
    public const string VerifyFirst =
        "You can create an agent once your email address is verified. Open the link in the email you were sent when you registered.";

    /// <summary>What the pages say about when the market, symbol, and timeframe of an agent can be changed (FR-AGT-8).</summary>
    public const string StructureLocked =
        "The market, the symbol, and the timeframe can be changed only while the agent is a draft, or is stopped with no open position or orders.";

    /// <summary>What a page says when the exchange did not say which symbols a market has.</summary>
    public const string SymbolsUnknown =
        "Binance gave no answer about which symbols the market has, so the symbol could not be checked and nothing was saved. Try again in a moment.";

    /// <summary>
    /// The name of the form that copies an agent. The list has one for each agent, and a page's
    /// forms each have a name of their own.
    /// </summary>
    public static string CloneForm(Guid agentId) => $"clone-agent-{agentId}";

    /// <summary>What a page says to a user who has as many agents as the fair-use limit allows (FR-ACC-7).</summary>
    public static string LimitReached(int maxAgents) =>
        $"You have {maxAgents.ToString(CultureInfo.InvariantCulture)} agents, which is the most one account can have. Delete one to create another.";

    /// <summary>A number as a page writes it and as a form takes it: digits and a decimal point, with no zeros after the last digit that counts.</summary>
    public static string Number(decimal number) => number.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>The capital of an agent, with the asset it is in: <c>1000 USDT</c>.</summary>
    public static string Capital(AgentView agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return $"{Number(agent.AllocatedCapital)} {agent.QuoteAsset}";
    }

    /// <summary>A timeframe as a choice names it: its code, and what a candle of it is.</summary>
    public static string Describe(Timeframe timeframe) => timeframe switch
    {
        Timeframe.FifteenMinutes => "15m: 15-minute candles",
        Timeframe.OneHour => "1H: 1-hour candles",
        Timeframe.FourHours => "4H: 4-hour candles",
        Timeframe.OneDay => "1D: 1-day candles",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "The timeframe has no description."),
    };

    /// <summary>The state of an agent, as users read it.</summary>
    public static string Describe(AgentState state) => state switch
    {
        AgentState.Draft => "Draft",
        AgentState.Running => "Running",
        AgentState.Paused => "Paused",
        AgentState.Halted => "Halted",
        AgentState.AttentionRequired => "Attention required",
        AgentState.Stopped => "Stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state has no description."),
    };

    /// <summary>A role of the team that runs an agent, as users read it, with its job (SRS 4.5).</summary>
    public static (string Name, string Job) Describe(Role role) => role switch
    {
        Role.MarketAnalyst => ("Market Analyst", "Reads price, volume, and indicators, and on Futures also funding and open interest."),
        Role.SentimentAnalyst => ("Sentiment Analyst", "Reads news and social context for the symbol and the wider market."),
        Role.Strategist => ("Strategist", "Combines both reports, recalled lessons, and the current position into a trade plan."),
        Role.RiskManager => ("Risk Manager", "Reviews the plan independently, and approves, reduces, or vetoes it."),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no description."),
    };

    /// <summary>The bounds of a risk setting that is a percentage, and its default, as a hint under its field.</summary>
    public static string Within(Bounded<decimal> bounds) =>
        $"From {Number(bounds.Minimum)} to {Number(bounds.Maximum)} percent. The default is {Number(bounds.Default)}.";

    /// <summary>The bounds of the leverage cap, and its default, as a hint under its field.</summary>
    public static string Within(Bounded<int> bounds) =>
        $"A whole number from {Number(bounds.Minimum)} to {Number(bounds.Maximum)}. The default is {Number(bounds.Default)}.";

    /// <summary>What is wrong with what was entered for a setting. It says what the setting takes.</summary>
    /// <param name="field">The setting.</param>
    /// <param name="problem">What is wrong with it.</param>
    /// <param name="bounds">The hard bounds of the risk settings, which a number outside them is told.</param>
    public static string Says(AgentField field, AgentProblem problem, AggressiveSettings bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        return (field, problem) switch
        {
            (AgentField.Name, AgentProblem.Missing) => "Enter a name.",
            (AgentField.Name, AgentProblem.TooLong) =>
                $"That name is too long. A name has {AgentRules.MaximumNameLength.ToString(CultureInfo.InvariantCulture)} characters at most.",
            (AgentField.Name, AgentProblem.NotInOneLine) => "A name is in one line. Enter it without a line break.",
            (AgentField.Name, AgentProblem.Taken) => "You have another agent of that name. Give this one a name of its own.",

            (AgentField.Market, _) => "Choose a market: Spot or Futures.",
            (AgentField.Timeframe, _) => "Choose a timeframe: 15m, 1H, 4H, or 1D.",

            (AgentField.Symbol, AgentProblem.NotOnMarket) =>
                "The chosen market does not have that symbol. Choose one of the symbols listed for the market.",
            (AgentField.Symbol, _) => "Choose a symbol.",

            (AgentField.AllocatedCapital, AgentProblem.Missing) => "Enter the capital to allocate.",
            (AgentField.AllocatedCapital, AgentProblem.TooPrecise) =>
                $"Enter the capital with {AgentRules.CapitalDecimals.ToString(CultureInfo.InvariantCulture)} decimal places at most.",
            (AgentField.AllocatedCapital, AgentProblem.OutOfBounds) =>
                $"Enter a capital above 0, and {Number(AgentRules.MaximumCapital)} at most.",
            (AgentField.AllocatedCapital, _) => "Enter the capital as a number, with digits and a decimal point: 1000 or 250.5.",

            (AgentField.LeverageCap, AgentProblem.Missing) => "Enter the leverage cap.",
            (AgentField.LeverageCap, AgentProblem.OutOfBounds) =>
                $"The leverage cap is from {Number(bounds.LeverageCap.Minimum)} to {Number(bounds.LeverageCap.Maximum)}.",
            (AgentField.LeverageCap, _) => "Enter the leverage cap as a whole number.",

            (_, AgentProblem.Missing) => "Enter a percentage.",
            (_, AgentProblem.TooPrecise) =>
                $"Enter the percentage with {AgentRules.PercentDecimals.ToString(CultureInfo.InvariantCulture)} decimal places at most.",
            (_, AgentProblem.OutOfBounds) when PercentBoundsOf(field, bounds) is { } within =>
                $"This setting is from {Number(within.Minimum)} to {Number(within.Maximum)} percent.",
            _ => "Enter the percentage as a number, with digits and a decimal point: 2 or 0.5.",
        };
    }

    /// <summary>What the list says when an agent could not be copied, or null for an outcome that says nothing.</summary>
    public static string? SaysOfCopy(SaveAgentOutcome outcome, int maxAgents) => outcome switch
    {
        SaveAgentOutcome.EmailNotVerified => VerifyFirst,
        SaveAgentOutcome.AgentLimitReached => LimitReached(maxAgents),
        _ => null,
    };

    /// <summary>What a page says to a user who may not create one more agent, or null to one who may.</summary>
    public static string? Says(AgentCreation creation, int maxAgents) => creation switch
    {
        AgentCreation.EmailNotVerified => VerifyFirst,
        AgentCreation.AgentLimitReached => LimitReached(maxAgents),
        _ => null,
    };

    /// <summary>The bounds of a risk setting that is a percentage, or null for a setting that is none.</summary>
    private static Bounded<decimal>? PercentBoundsOf(AgentField field, AggressiveSettings bounds) => field switch
    {
        AgentField.RiskPerTradePercent => bounds.RiskPerTradePercent,
        AgentField.DailyLossPercent => bounds.DailyLossPercent,
        AgentField.DrawdownPercent => bounds.DrawdownPercent,
        AgentField.MaxEntryDistancePercent => bounds.MaxEntryDistancePercent,
        _ => null,
    };
}
