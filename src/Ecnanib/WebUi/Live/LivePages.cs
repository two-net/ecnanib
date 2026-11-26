using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.WebUi.Animation;
using Ecnanib.WebUi.Pages;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.AspNetCore.WebUtilities;

namespace Ecnanib.WebUi.Live;

/// <summary>
/// What the parts of a page that follow what changes say, in one place: the steps of a cycle
/// that runs, what each role of its team does (FR-ANI-1), the position of an agent, and the
/// price that it is valued at (FR-NTF-2). Each is said in plain language, as words, and none
/// relies on a colour (NFR-USA-2, FR-THM-5). See docs/live-updates.md.
/// </summary>
public static class LivePages
{
    /// <summary>The heading of the position of an agent.</summary>
    public static string PositionHeading => "Position";

    /// <summary>What the page says of an agent that holds no position.</summary>
    public static string NoPosition => "The agent holds no position.";

    /// <summary>What a step that a cycle has left behind says.</summary>
    public static string StepDone => "done";

    /// <summary>
    /// What the screen of an agent says of its team while no cycle of the agent runs
    /// (FR-RPT-2). The state of the agent, which the page says above it, says whether the team
    /// is called at the next close.
    /// </summary>
    public static string AtRest => "The team is at rest: no cycle of the agent runs at the moment.";

    /// <summary>The heading of a cycle that runs, which says what started it.</summary>
    public static string Running(CycleKind kind) => kind == CycleKind.Test ? "A test cycle is running" : "A cycle is running";

    /// <summary>What a cycle that runs is for, and since when it runs.</summary>
    public static string RunningFor(RunningCycle cycle, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var runs = $"It is for the candle that closed at {times.At(cycle.ClosedAt)}, and started at {times.AtSecond(cycle.StartedAt)}.";

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
    public static string StepNow(DateTimeOffset since, PageTimes times) => $"now, since {times.AtSecond(since)}";

    /// <summary>The heading of a cycle that ended a moment ago, which says what started it.</summary>
    public static string Ended(CycleKind kind) => kind == CycleKind.Test ? "A test cycle has ended" : "A cycle has ended";

    /// <summary>What a cycle that ended a moment ago was for, and when it ended.</summary>
    public static string EndedFor(RunningCycle cycle, DateTimeOffset endedAt, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var ended = $"It was for the candle that closed at {times.At(cycle.ClosedAt)}, and ended at {times.AtSecond(endedAt)}.";

        return cycle.Kind == CycleKind.Test
            ? $"{ended} You ran it as a test: it placed no orders. Its timeline shows the rest."
            : $"{ended} Its timeline shows the rest.";
    }

    /// <summary>
    /// What a role of the team does now, as a word (FR-ANI-1). The page says what each word
    /// means above the team: see AgentReportPages.TeamExplained.
    /// </summary>
    public static string Describe(RoleState state) => state switch
    {
        RoleState.Waiting => "waiting",
        RoleState.Working => "working",
        RoleState.Done => "done",
        RoleState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No such state of a role."),
    };

    /// <summary>The name that the stylesheet knows what a role does by: <c>role-working</c>.</summary>
    public static string ClassOf(RoleState state) => state switch
    {
        RoleState.Waiting => "role-waiting",
        RoleState.Working => "role-working",
        RoleState.Done => "role-done",
        RoleState.Failed => "role-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No such state of a role."),
    };

    /// <summary>
    /// The names that the stylesheet knows a stage of the pipeline by: <c>pipeline-stage</c>,
    /// with <c>stage-handed</c> once the work was handed to it.
    /// </summary>
    public static string StageClassOf(bool handed) => handed ? "pipeline-stage stage-handed" : "pipeline-stage";

    /// <summary>
    /// The open position of an agent in a sentence. On the Futures market it says the side: a
    /// position of the Spot market is of what the agent bought.
    /// </summary>
    public static string Says(OpenPosition position, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(position);

        var side = position.Market == Market.Futures ? (position.Side == PositionSide.Long ? "A long position" : "A short position") : "A position";

        return $"{side} of {AgentPages.Number(position.Quantity)} in {position.Symbol}, entered at {AgentPages.Number(position.EntryPrice)} " +
            $"{position.QuoteAsset}, open since {times.At(position.OpenedAt)}.";
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
    public static string Says(LivePrice followed, WatchedPrice price, string? quoteAsset, PageTimes times) =>
        PriceFigure(followed, price, quoteAsset, times).Text;

    /// <summary>
    /// That sentence with the price as a figure that counts when it changes (FR-ANI-2). The
    /// words before it and after it are the ones of <see cref="Says(LivePrice, WatchedPrice, string?)"/>.
    /// </summary>
    public static PageFigure PriceFigure(LivePrice followed, WatchedPrice price, string? quoteAsset, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(followed);
        ArgumentNullException.ThrowIfNull(price);

        var asset = quoteAsset is null ? string.Empty : $" {quoteAsset}";
        var read = $"{asset}, as of {times.AtSecond(price.ReadAt)}.";

        return followed.Kind == ExchangePriceKind.Mark
            ? PageFigure.Of(AgentPages.Number(price.Price), $"The mark price of {followed.Symbol} is ", read)
            : PageFigure.Of(AgentPages.Number(price.Price), $"{followed.Symbol} last traded at ", read);
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
    public static string Unrealized(OpenPosition position, WatchedPrice price) =>
        UnrealizedFigure(position, price)?.Text ?? NothingMade;

    /// <summary>What the page says of a position that would make nothing and lose nothing: it has no figure.</summary>
    public static string NothingMade => "At that price the position would make nothing and lose nothing, before fees and funding.";

    /// <summary>
    /// That sentence with what the position would make as a figure that counts when it changes
    /// (FR-ANI-2), or null for a position that would make nothing and lose nothing. The number
    /// is written without a sign, and the words say whether it is a profit or a loss: the value
    /// of a loss is less than nothing, so a figure does not count across the two.
    /// </summary>
    public static PageFigure? UnrealizedFigure(OpenPosition position, WatchedPrice price)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(price);

        var made = Math.Round(position.UnrealizedAt(price.Price), 8, MidpointRounding.AwayFromZero);
        var after = $" {position.QuoteAsset}, before fees and funding.";

        return made switch
        {
            > 0m => PageFigure.Of(AgentPages.Number(made), "At that price the position would make ", after),
            < 0m => PageFigure.Of(AgentPages.Number(-made), "At that price the position would lose ", after, isLoss: true),
            _ => null,
        };
    }

    /// <summary>
    /// The choice of an address that says whether its page follows what changes: a page that
    /// follows may be paused by its user, and then changes no more by itself (NFR-USA-1). An
    /// address without the choice is of a page that follows.
    /// </summary>
    public const string PauseKey = "live";

    /// <summary>What that choice says of a page that is paused: <c>?live=paused</c>.</summary>
    public const string Paused = "paused";

    /// <summary>What a page that follows says of itself, beside the link that pauses it.</summary>
    public static string Follows => "This page follows what changes, and updates by itself.";

    /// <summary>The link that pauses a page that follows.</summary>
    public static string Pause => "Pause live updates";

    /// <summary>The link that has a paused page follow again.</summary>
    public static string Resume => "Resume live updates";

    /// <summary>
    /// What a paused page adds where it shows prices: a price is read for a page that follows
    /// it, and for no other, so a paused page may have none.
    /// </summary>
    public static string PausedPrices => "No price is read for a paused page, so a price may be said to be not known.";

    /// <summary>What a page that is paused says of itself, beside the link that has it follow again: since when it is as it is.</summary>
    public static string PausedAt(DateTimeOffset at, PageTimes times) =>
        $"Live updates are paused. This page shows what was so at {times.AtSecond(at)}, and does not change by itself.";

    /// <summary>
    /// Whether an address asks for its page paused: true for <c>?live=paused</c>, and false for
    /// an address that makes no such choice. Null for a choice that there is not, such as one
    /// that is made twice or says anything else: such an address has no page, as an address
    /// with a page number that is none has not.
    /// </summary>
    public static bool? PausedOf(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var said = query[PauseKey];

        return said.Count switch
        {
            0 => false,
            1 when string.IsNullOrEmpty(said[0]) => false,
            1 when said[0] == Paused => true,
            _ => null,
        };
    }

    /// <summary>
    /// The address of the page of a request, paused or following, with every other choice of
    /// the address as it is: the page of a list, and what a filter chose.
    /// </summary>
    /// <param name="request">The request that the page is written for.</param>
    /// <param name="paused">Whether the address is of the page paused.</param>
    public static string AddressOf(HttpRequest request, bool paused)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The other choices, each written anew, so nothing of the request is written into the
        // page as it came.
        var others = request.Query
            .Where(choice => !string.Equals(choice.Key, PauseKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(choice => choice.Value.Select(value => KeyValuePair.Create(choice.Key, value)));

        var query = QueryString.Create(others);

        if (paused)
        {
            query = query.Add(PauseKey, Paused);
        }

        return request.PathBase.Add(request.Path).ToUriComponent() + query.ToUriComponent();
    }

    /// <summary>
    /// An address of the page of a request, with the pause carried along when the request has
    /// it: where a form of the page leads back to it, and the other pages of its list. So a
    /// page that its user paused stays paused until they have it follow again, and what they do
    /// on it does not have it follow behind their back. A link to another screen carries none.
    /// </summary>
    /// <param name="request">The request that the page is written for, or that its form was sent with.</param>
    /// <param name="address">The address that is led to.</param>
    public static string Kept(HttpRequest? request, string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return request is not null && PausedOf(request.Query) == true
            ? QueryHelpers.AddQueryString(address, PauseKey, Paused)
            : address;
    }

    /// <summary>What a part of a page says when it stopped following because of an error, with the reference of that error in the log (NFR-LOG-6).</summary>
    public static string Stopped(string reference) =>
        $"This part of the page stopped following what changes, because of an error. Reload the page to see it as it is now. Reference: {reference}";
}
