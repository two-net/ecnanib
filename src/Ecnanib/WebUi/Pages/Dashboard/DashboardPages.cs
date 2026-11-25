using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.WebUi.Animation;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.WebUi.Pages.Dashboard;

/// <summary>
/// What the dashboard says, in one place (NFR-USA-4): its texts, how it writes an amount, and
/// the ids of its parts, which are there once for each mode (FR-RPT-1). See docs/dashboard.md.
/// </summary>
public static class DashboardPages
{
    /// <summary>The name of the dashboard, in its heading and in the bar at the top.</summary>
    public const string Title = "Dashboard";

    /// <summary>What the dashboard is.</summary>
    public const string Explained =
        "All of your agents at a glance: what they are worth, what they made today, what they hold, how each of them is doing, " +
        "what their teams decided last, and what the service has to tell you.";

    /// <summary>That Demo and Live are never added up (FR-BIN-11).</summary>
    public const string Apart = "Demo and Live are shown apart, each under its own heading, and are never added up.";

    /// <summary>What the figures of the dashboard are, and what they are not.</summary>
    public const string FiguresExplained =
        "The equity of an agent is the capital that you allocated to it, plus its net profit or loss to date, after fees and " +
        "funding, with what its open position would make at the latest price. Profit and loss today is the equity now, less " +
        "the equity as the UTC day began. Amounts are written to 2 decimal places, and amounts in different assets are never " +
        "added up.";

    /// <summary>The name of the total equity of a mode.</summary>
    public const string TotalEquity = "Total equity";

    /// <summary>The name of what the agents of a mode made in the UTC day.</summary>
    public const string MadeToday = "Profit and loss today";

    /// <summary>What is written in place of a figure that is not known: never a number.</summary>
    public const string NotKnown = "Not known at the moment";

    /// <summary>The heading of the open positions of a mode.</summary>
    public const string PositionsHeading = "Open positions";

    /// <summary>The heading of the agents of a mode, each with its card.</summary>
    public const string AgentsHeading = "Agents";

    /// <summary>The heading of the recent decisions of a mode.</summary>
    public const string DecisionsHeading = "Recent decisions";

    /// <summary>The heading of the alerts of a mode.</summary>
    public const string AlertsHeading = "Alerts";

    /// <summary>The heading of the alerts that are about no mode.</summary>
    public const string AccountAlertsHeading = "Alerts about your account";

    /// <summary>What the alerts that are about no mode are.</summary>
    public const string AccountAlertsExplained =
        "What the service has told you that is about neither Demo nor Live, such as a key of an AI provider, and that you have " +
        "not read, latest first.";

    /// <summary>What is written where no stop-loss or no take-profit rests on the exchange for a position: the price alone protects nothing.</summary>
    public const string NoneRests = "None rests";

    /// <summary>What a card says of an agent that holds no position.</summary>
    public const string HoldsNothing = "Holds no position";

    /// <summary>The heading of the column that says the price that a position is valued at.</summary>
    public const string PriceHeading = "Valued at";

    /// <summary>Which price a position is valued at, and what "would make now" leaves out, under the table of the positions.</summary>
    public const string PricesExplained =
        "A position on Futures is valued at its mark price, and one on Spot at the price that its symbol last traded at. What " +
        "it would make is before fees and funding.";

    /// <summary>The modes of the dashboard, in the order of its sections: Live first, where real money is at stake.</summary>
    public static IReadOnlyList<TradingMode> Modes { get; } = [TradingMode.Live, TradingMode.Demo];

    /// <summary>The heading of the section of a mode.</summary>
    public static string Heading(TradingMode mode) => $"Your agents in {mode}";

    /// <summary>What a mode is, under the heading of its section.</summary>
    public static string ModeExplained(TradingMode mode) => mode == TradingMode.Live
        ? "In Live your agents trade in your real Binance account, with real money."
        : "In Demo your agents trade in Binance's test environment, and no money is at stake.";

    /// <summary>What the section of a mode says when its user has no agent in it.</summary>
    public static string NoAgents(TradingMode mode) => $"You have no agent in {mode}.";

    /// <summary>What the section of a mode says when none of its agents holds a position.</summary>
    public static string NoPositions(TradingMode mode) => $"No agent in {mode} holds a position.";

    /// <summary>What the section of a mode says when no cycle was recorded in it.</summary>
    public static string NoDecisions(TradingMode mode) => $"No team has decided anything in {mode} yet.";

    /// <summary>What the section of a mode says when its user has read everything that is about it.</summary>
    public static string NoAlerts(TradingMode mode) => $"Nothing about {mode} waits to be read.";

    /// <summary>What the recent decisions of a mode are.</summary>
    public static string DecisionsExplained(TradingMode mode) =>
        $"The latest cycles that a candle close started in {mode}, latest first, each with how it ended. A test cycle is not " +
        "among them: it is on the page of the cycles of its agent.";

    /// <summary>What the alerts of a mode are, and how many of them the page lists.</summary>
    public static string AlertsExplained(TradingMode mode, UnreadOfMode unread)
    {
        ArgumentNullException.ThrowIfNull(unread);

        return $"What the service has told you about {mode} and you have not read, latest first. {Listed(unread)}".TrimEnd();
    }

    /// <summary>How many of the notifications that its user has not read a list holds, where it does not hold them all.</summary>
    public static string Listed(UnreadOfMode unread)
    {
        ArgumentNullException.ThrowIfNull(unread);

        return unread.Total > unread.Latest.Count ? $"The latest {Count(unread.Latest.Count)} of {Count(unread.Total)}." : string.Empty;
    }

    /// <summary>The name of the table of the open positions of a mode, which is read out and not shown.</summary>
    public static string PositionsCaption(TradingMode mode) => $"The open positions of your agents in {mode}";

    /// <summary>The name of the table of the recent decisions of a mode.</summary>
    public static string DecisionsCaption(TradingMode mode) => $"The recent decisions of your agents in {mode}";

    /// <summary>
    /// The id of a part of the section of a mode: the section is on the page once for each
    /// mode, and an id is on a page once.
    /// </summary>
    public static string Id(TradingMode mode, string part) => $"dashboard-{mode.ToString().ToLowerInvariant()}-{part}";

    /// <summary>How many agents a total counts, and what was allocated to them.</summary>
    public static string Counted(EquityTotal total)
    {
        ArgumentNullException.ThrowIfNull(total);

        var agents = total.Agents == 1 ? "1 agent" : $"{Count(total.Agents)} agents";

        return $"Of {agents}, with {Amount(total.Capital, total.Asset)} of allocated capital.";
    }

    /// <summary>
    /// What the figures of a total are valued at, or why they are not known. A figure that is
    /// not known is never written as a number: what would be missing of it is what an open
    /// position is worth.
    /// </summary>
    /// <param name="total">The total.</param>
    /// <param name="equities">The equities that it adds up.</param>
    public static string Valued(EquityTotal total, IEnumerable<AgentEquity> equities)
    {
        ArgumentNullException.ThrowIfNull(total);
        ArgumentNullException.ThrowIfNull(equities);

        if (total.Equity is not null)
        {
            return total.PricedAt is { } pricedAt
                ? $"Open positions are valued at prices as of {PageTimes.AtSecond(pricedAt)}."
                : "No agent holds a position, so no price is in these figures.";
        }

        // The symbols whose price is not known, each once, in the order of their names.
        var unpriced = equities
            .Where(equity => equity.Position is not null && equity.Price is null)
            .Select(equity => equity.Position!.Symbol)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return unpriced.Count > 0
            ? $"An open position cannot be valued right now: the price of {string.Join(", ", unpriced)} is not known at the moment. " +
              "The page asks for it, and shows the figures once it is read."
            : "A part of what an agent made is not known yet: an order that closed a part of a position says no price that it " +
              "was executed at so far. The figures are shown once it does.";
    }

    /// <summary>An amount of an asset, to 2 decimal places: <c>1012.4 USDT</c>.</summary>
    public static string Amount(decimal amount, string asset) => AmountFigure(amount, asset).Text;

    /// <summary>That amount as a figure that counts when it changes (FR-ANI-2): the number, and the asset after it.</summary>
    public static PageFigure AmountFigure(decimal amount, string asset) =>
        PageFigure.Of(Rounded(amount).ToString("0.##", CultureInfo.InvariantCulture), after: $" {asset}");

    /// <summary>
    /// A profit or a loss in an asset, to 2 decimal places, with its sign, so that it is told
    /// apart without its colour (FR-THM-5): <c>+12.4 USDT</c>, <c>-5.99 USDT</c>, <c>0 USDT</c>.
    /// </summary>
    public static string Signed(decimal amount, string asset) => SignedFigure(amount, asset).Text;

    /// <summary>That profit or loss as a figure that counts when it changes (FR-ANI-2): the number with its sign, and the asset after it.</summary>
    public static PageFigure SignedFigure(decimal amount, string asset) =>
        PageFigure.Of(Rounded(amount).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture), after: $" {asset}");

    /// <summary>An amount that may not be known: the amount, or that it is not known, and never nothing.</summary>
    public static string AmountOrNot(decimal? amount, string asset) => AmountFigureOrNot(amount, asset)?.Text ?? NotKnown;

    /// <summary>That amount as a figure, or null when it is not known: the page then says <see cref="NotKnown"/>.</summary>
    public static PageFigure? AmountFigureOrNot(decimal? amount, string asset) => amount is { } known ? AmountFigure(known, asset) : null;

    /// <summary>A profit or a loss that may not be known.</summary>
    public static string SignedOrNot(decimal? amount, string asset) => SignedFigureOrNot(amount, asset)?.Text ?? NotKnown;

    /// <summary>That profit or loss as a figure, or null when it is not known.</summary>
    public static PageFigure? SignedFigureOrNot(decimal? amount, string asset) => amount is { } known ? SignedFigure(known, asset) : null;

    /// <summary>The market, the symbol, and the timeframe of an agent, as its card says them.</summary>
    public static string Trades(Market market, Timeframe timeframe) => $"on {market}, {timeframe.Code()}";

    /// <summary>The state of an agent, with since when, as its card says it.</summary>
    public static string StateOf(AgentState state, DateTimeOffset since) => $"{AgentPages.Describe(state)}, since {PageTimes.At(since)}";

    /// <summary>
    /// Whether the card of an agent says why the agent is in its state: an agent that is halted,
    /// or that waits for its user, which is what its user has to know of at a glance.
    /// </summary>
    public static bool SaysWhy(AgentState state) => state is AgentState.Halted or AgentState.AttentionRequired;

    /// <summary>What an agent holds, as its card says it: <c>Holds 0.01 in BTCUSDT, long</c>.</summary>
    public static string Holds(OpenPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return $"Holds {AgentPages.Number(position.Quantity)} in {position.Symbol}, {Side(position).ToLowerInvariant()}";
    }

    /// <summary>The side of a position, as a word.</summary>
    public static string Side(OpenPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return position.Side == PositionSide.Long ? "Long" : "Short";
    }

    /// <summary>The price of an order that protects a position, or that none rests for it.</summary>
    public static string Rests(decimal? price) => price is { } resting ? AgentPages.Number(resting) : NoneRests;

    /// <summary>What an open position would make at the price that it is valued at, before fees and funding, or that this is not known.</summary>
    public static string WouldMake(AgentEquity equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return SignedOrNot(equity.Unrealized, equity.Asset);
    }

    /// <summary>That as a figure, or null when it is not known.</summary>
    public static PageFigure? WouldMakeFigure(AgentEquity equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return SignedFigureOrNot(equity.Unrealized, equity.Asset);
    }

    /// <summary>The price that an open position is valued at, with when it was read, or that it is not known.</summary>
    public static string PriceOf(AgentEquity equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return PriceFigure(equity)?.Text ?? NotKnown;
    }

    /// <summary>That price as a figure, with when it was read after it, or null when it is not known.</summary>
    public static PageFigure? PriceFigure(AgentEquity equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return equity.Price is { } price
            ? PageFigure.Of(AgentPages.Number(price.Price), after: $", as of {PageTimes.AtSecond(price.ReadAt)}")
            : null;
    }

    /// <summary>The text of the link from an alert to the page of the state of the agent that it is about.</summary>
    public static string StateLink(string agentName) => $"State of {agentName}";

    private static decimal Rounded(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
