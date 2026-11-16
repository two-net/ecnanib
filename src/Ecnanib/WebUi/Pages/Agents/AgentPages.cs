using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// What the pages of a user's agents have in common: the names of their forms, how a setting is
/// written, what they say about a setting that cannot be stored, and what they say about the
/// state of an agent and the controls that change it (FR-AGT-1, FR-AGT-2, FR-AGT-10). Every
/// setting and every state is explained in plain language (NFR-USA-2).
/// </summary>
public static class AgentPages
{
    /// <summary>The name of the form that creates an agent, or changes one.</summary>
    public const string SaveForm = "save-agent";

    /// <summary>The name of the form that deletes an agent.</summary>
    public const string DeleteForm = "delete-agent";

    /// <summary>The name of the form that stops an agent.</summary>
    public const string StopForm = "stop-agent";

    /// <summary>The name of the form that closes the open position of an agent.</summary>
    public const string ClosePositionForm = "close-agent-position";

    /// <summary>The value of the choice that closes the open position of an agent that is stopped (FR-AGT-11).</summary>
    public const string ClosePosition = "close";

    /// <summary>The value of the choice that keeps the open position of an agent that is stopped, with its protective orders (FR-AGT-11).</summary>
    public const string KeepPosition = "keep";

    /// <summary>What the control that closes the open position of an agent is called, wherever it is (FR-AGT-12).</summary>
    public const string ClosePositionNow = "Close position now";

    /// <summary>What a page says when the position of an agent could not be closed.</summary>
    public const string NotClosed =
        "The position could not be closed: Binance refused, or gave no answer. It is open as it was, with its stop-loss and " +
        "take-profit orders. Try again, or close it at Binance.";

    /// <summary>What a page says when an agent whose position was to be closed has none.</summary>
    public const string NothingOpen = "This agent has no open position.";

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

    /// <summary>
    /// What a state means, in plain language, wherever the state is shown: whether the agent's
    /// cycles run, whether it opens a position, and what becomes of an open one (SRS 3.3,
    /// NFR-USA-2).
    /// </summary>
    public static string Explain(AgentState state) => state switch
    {
        AgentState.Draft =>
            "The agent is not started yet. It runs no cycle and trades nothing.",
        AgentState.Running =>
            "The agent's team decides once for each candle that closes, may open a position, and manages the open one.",
        AgentState.Paused =>
            "You paused the agent. It runs no cycle and opens no position. An open position stays open with its stop-loss " +
            "and take-profit orders.",
        AgentState.Halted =>
            "A loss limit was reached, and the agent's position was closed. It runs no cycle and opens no position.",
        AgentState.AttentionRequired =>
            "Something that the agent needs is missing or failing, and it waits for you. It runs no cycle and opens no " +
            "position. An open position stays open with its stop-loss and take-profit orders.",
        AgentState.Stopped =>
            "You stopped the agent. It runs no cycle and opens no position. An open position was closed or kept, as you chose.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The state has no explanation."),
    };

    /// <summary>Why an agent came to a state, as its user reads it (FR-AGT-10).</summary>
    public static string Says(AgentStateCause cause) => cause switch
    {
        AgentStateCause.Created => "You created the agent.",
        AgentStateCause.UserStarted => "You started the agent.",
        AgentStateCause.UserPaused => "You paused the agent.",
        AgentStateCause.UserResumed => "You resumed the agent.",
        AgentStateCause.UserStopped => "You stopped the agent. It had no open position.",
        AgentStateCause.UserStoppedClosingPosition => "You stopped the agent, and chose to close its position at market.",
        AgentStateCause.UserStoppedKeepingPosition =>
            "You stopped the agent, and chose to keep its position with its stop-loss and take-profit orders.",
        AgentStateCause.ExchangeKeyDeleted =>
            "Your Binance key for the agent's mode was deleted while the agent ran. Add a key, and resume the agent.",
        AgentStateCause.ExchangeKeyInvalid =>
            "Binance refused your key for the agent's mode while the agent ran. Replace the key, or check it again on the " +
            "page of your keys, and resume the agent.",
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "The cause has no description."),
    };

    /// <summary>The name of the form of a control that starts, pauses, or resumes an agent.</summary>
    public static string FormOf(AgentControl control) => control switch
    {
        AgentControl.Start => "start-agent",
        AgentControl.Pause => "pause-agent",
        AgentControl.Resume => "resume-agent",
        AgentControl.Stop => StopForm,
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no form."),
    };

    /// <summary>What the button of a control says.</summary>
    public static string Describe(AgentControl control) => control switch
    {
        AgentControl.Start => "Start the agent",
        AgentControl.Pause => "Pause the agent",
        AgentControl.Resume => "Resume the agent",
        AgentControl.Stop => "Stop the agent",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no description."),
    };

    /// <summary>What a control does, in plain language, beside its button.</summary>
    public static string Explain(AgentControl control) => control switch
    {
        AgentControl.Start =>
            "The agent runs: its team decides once for each candle that closes, and may open a position.",
        AgentControl.Pause =>
            "The agent runs no cycle and opens no position until you resume it. An open position stays open with its " +
            "stop-loss and take-profit orders.",
        AgentControl.Resume => "The agent runs again, from the next candle that closes.",
        AgentControl.Stop =>
            "The agent runs no cycle and opens no position. You choose what happens to an open position. A stopped agent " +
            "can be started again.",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no explanation."),
    };

    /// <summary>What a page says when a control is not offered in the state that the agent is in.</summary>
    public static string NotOffered(AgentControl control, AgentState state) => control switch
    {
        AgentControl.Start => $"This agent is {InWords(state)}. Only a draft or a stopped agent is started.",
        AgentControl.Pause => $"This agent is {InWords(state)}. Only a running agent is paused.",
        AgentControl.Resume => $"This agent is {InWords(state)}. An agent is resumed when it is paused, is halted, or requires attention.",
        AgentControl.Stop => "This agent is a draft: it was never started, so there is nothing to stop.",
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control has no refusal."),
    };

    /// <summary>What a page says when another active agent keeps an agent from starting (FR-AGT-9).</summary>
    public static string AnotherAgentActive(AgentView agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return $"You have another active agent for {agent.Symbol} on {agent.Market} in {agent.Mode}. One Binance key runs one " +
            "active agent for each market and symbol. Stop the other one first.";
    }

    /// <summary>A state inside a sentence: <c>a draft</c>, <c>running</c>.</summary>
    private static string InWords(AgentState state) => state switch
    {
        AgentState.Draft => "a draft",
        AgentState.AttentionRequired => "waiting for your attention",
        _ => Describe(state).ToLowerInvariant(),
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
