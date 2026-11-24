using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.WebUi.Live;

/// <summary>
/// What the parts of a page that follow what changes say, in one place: the steps of a cycle
/// that runs, the position of an agent, and the price that it is valued at (FR-NTF-2). Each is
/// said in plain language, as words, and none relies on a colour (NFR-USA-2, FR-THM-5). See
/// docs/live-updates.md.
/// </summary>
public static class LivePages
{
    /// <summary>The heading of the position of an agent.</summary>
    public const string PositionHeading = "Position";

    /// <summary>What the page says of an agent that holds no position.</summary>
    public const string NoPosition = "The agent holds no position.";

    /// <summary>What a step that a cycle has left behind says.</summary>
    public const string StepDone = "done";

    /// <summary>
    /// What the screen of an agent says of its team while no cycle of the agent runs
    /// (FR-RPT-2). The state of the agent, which the page says above it, says whether the team
    /// is called at the next close.
    /// </summary>
    public const string AtRest = "The team is at rest: no cycle of the agent runs at the moment.";

    /// <summary>The heading of a cycle that runs, which says what started it.</summary>
    public static string Running(CycleKind kind) => kind == CycleKind.Test ? "A test cycle is running" : "A cycle is running";

    /// <summary>What a cycle that runs is for, and since when it runs.</summary>
    public static string RunningFor(RunningCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var runs = $"It is for the candle that closed at {PageTimes.At(cycle.ClosedAt)}, and started at {PageTimes.AtSecond(cycle.StartedAt)}.";

        return cycle.Kind == CycleKind.Test
            ? $"{runs} You ran it as a test: it places no orders. Its steps are listed as it comes to them, and its timeline shows the rest when it has ended."
            : $"{runs} Its steps are listed as it comes to them, and its timeline shows the rest when it has ended.";
    }

    /// <summary>A step of a cycle, as a person reads it.</summary>
    public static string Describe(CycleStep step) => step switch
    {
        CycleStep.Settling => "Orders settled with the exchange, and loss limits evaluated",
        CycleStep.DataPackage => "Data package",
        CycleStep.Analysts => "Market Analyst and Sentiment Analyst",
        CycleStep.Strategist => "Strategist",
        CycleStep.RiskManager => "Risk Manager",
        CycleStep.RiskGuard => "Risk Guard",
        CycleStep.Orders => "Orders",
        CycleStep.Recording => "Record of the cycle",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "No such step."),
    };

    /// <summary>What the step that a cycle is at says: <c>now, since 15 November 2026, 09:20:07 UTC</c>.</summary>
    public static string StepNow(DateTimeOffset since) => $"now, since {PageTimes.AtSecond(since)}";

    /// <summary>
    /// The open position of an agent in a sentence. On the Futures market it says the side: a
    /// position of the Spot market is of what the agent bought.
    /// </summary>
    public static string Says(OpenPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        var side = position.Market == Market.Futures ? (position.Side == PositionSide.Long ? "A long position" : "A short position") : "A position";

        return $"{side} of {AgentPages.Number(position.Quantity)} in {position.Symbol}, entered at {AgentPages.Number(position.EntryPrice)} " +
            $"{position.QuoteAsset}, open since {PageTimes.At(position.OpenedAt)}.";
    }

    /// <summary>
    /// The protection of a position, as it rests on the exchange. A price that no order rests
    /// for protects nothing, and is said as that.
    /// </summary>
    public static string Protection(OpenPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return (position.StopLoss, position.TakeProfit) switch
        {
            ({ } stopLoss, { } takeProfit) =>
                $"Its stop-loss rests on the exchange at {AgentPages.Number(stopLoss)}, and its take-profit at {AgentPages.Number(takeProfit)}.",
            ({ } stopLoss, null) =>
                $"Its stop-loss rests on the exchange at {AgentPages.Number(stopLoss)}. No take-profit rests there for it.",
            (null, { } takeProfit) =>
                $"No stop-loss rests on the exchange for it. Its take-profit rests there at {AgentPages.Number(takeProfit)}.",
            _ => "No stop-loss and no take-profit rest on the exchange for it.",
        };
    }

    /// <summary>A price that is followed, with when Binance was asked for it.</summary>
    public static string Says(LivePrice followed, WatchedPrice price, string? quoteAsset)
    {
        ArgumentNullException.ThrowIfNull(followed);
        ArgumentNullException.ThrowIfNull(price);

        var amount = quoteAsset is null ? AgentPages.Number(price.Price) : $"{AgentPages.Number(price.Price)} {quoteAsset}";

        return followed.Kind == ExchangePriceKind.Mark
            ? $"The mark price of {followed.Symbol} is {amount}, as of {PageTimes.AtSecond(price.ReadAt)}."
            : $"{followed.Symbol} last traded at {amount}, as of {PageTimes.AtSecond(price.ReadAt)}.";
    }

    /// <summary>What the page says while no price of a symbol is known: none was read yet, or Binance gave none.</summary>
    public static string NoPrice(LivePrice followed)
    {
        ArgumentNullException.ThrowIfNull(followed);

        return followed.Kind == ExchangePriceKind.Mark
            ? $"The mark price of {followed.Symbol} is not known at the moment."
            : $"The price of {followed.Symbol} is not known at the moment.";
    }

    /// <summary>
    /// What a position would make at the price that is followed, before fees and funding. It
    /// says whether that is a profit or a loss in words.
    /// </summary>
    public static string Unrealized(OpenPosition position, WatchedPrice price)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(price);

        var made = Math.Round(position.UnrealizedAt(price.Price), 8, MidpointRounding.AwayFromZero);

        return made switch
        {
            > 0m => $"At that price the position would make {AgentPages.Number(made)} {position.QuoteAsset}, before fees and funding.",
            < 0m => $"At that price the position would lose {AgentPages.Number(-made)} {position.QuoteAsset}, before fees and funding.",
            _ => "At that price the position would make nothing and lose nothing, before fees and funding.",
        };
    }

    /// <summary>What a part of a page says when it stopped following because of an error, with the reference of that error in the log (NFR-LOG-6).</summary>
    public static string Stopped(string reference) =>
        $"This part of the page stopped following what changes, because of an error. Reload the page to see it as it is now. Reference: {reference}";
}
