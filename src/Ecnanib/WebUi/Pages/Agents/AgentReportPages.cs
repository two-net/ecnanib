using System.Globalization;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.WebUi.Animation;
using Ecnanib.WebUi.Pages.Dashboard;
using Ecnanib.WebUi.Pages.Experience;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// What the screen of an agent says of how the agent has performed, in one place (NFR-USA-4): its
/// team, its performance figures, its equity over time, its closed trades, and its latest decisions
/// (FR-RPT-2). Everything is said in plain language (NFR-USA-2). A figure that is not known is
/// said as that, and never as a number, and a profit and a loss are told apart by their sign
/// (FR-THM-5). See docs/agent-screen.md.
/// </summary>
public static class AgentReportPages
{
    /// <summary>The heading of the team of the agent, with what it does now.</summary>
    public static string TeamHeading => "Team";

    /// <summary>
    /// What the page says above the team at work: the order that the roles work in, and what
    /// the word beside each role means (FR-ANI-1, NFR-USA-2).
    /// </summary>
    public static string TeamExplained =>
        "The four roles that decide for the agent each time a candle of its timeframe closes, each with the model that it was given. " +
        "The two analysts work at the same time, then the Strategist, then the Risk Manager. " +
        "Each role says what it does now: it is waiting until the cycle comes to it, working while its model is called, " +
        "done when it gave a valid result, and failed when it gave none.";

    /// <summary>What the page says of a role that was given no model.</summary>
    public static string NoModel => "No model chosen";

    /// <summary>
    /// The model that a role was given, as the team names it: the model, and its provider where
    /// the catalog says it, as the timeline of a cycle names the model of a call.
    /// </summary>
    public static string Model(AgentRoleView role, string? providerName)
    {
        ArgumentNullException.ThrowIfNull(role);

        var model = AgentPages.NameOf(role.Identifier, role.DisplayName);

        return providerName is null ? model : $"{model}, of {providerName}";
    }

    /// <summary>The heading of the performance figures.</summary>
    public static string PerformanceHeading => "Performance";

    /// <summary>The name of the equity.</summary>
    public static string Equity => "Equity";

    /// <summary>The name of the net result: to date among the figures, and of a trade in the list of the trades.</summary>
    public static string NetResult => "Net result";

    /// <summary>The name of the return.</summary>
    public static string Return => "Return";

    /// <summary>The name of the win rate.</summary>
    public static string WinRate => "Win rate";

    /// <summary>The name of the average R.</summary>
    public static string AverageR => "Average R";

    /// <summary>The name of the profit factor.</summary>
    public static string ProfitFactor => "Profit factor";

    /// <summary>The name of the largest drawdown.</summary>
    public static string LargestDrawdown => "Largest drawdown";

    /// <summary>The name of the fees.</summary>
    public static string Fees => "Fees";

    /// <summary>The name of the funding.</summary>
    public static string Funding => "Funding";

    /// <summary>What is written for the largest drawdown of an equity that never fell from a peak.</summary>
    public static string NoDrawdown => "None";

    /// <summary>What is written for something of a trade that is not known.</summary>
    public static string NotKnown => "Not known";

    /// <summary>What is written for the result of a trade that is not recorded yet.</summary>
    public static string NotRecorded => "Not recorded yet";

    /// <summary>The heading of the equity of the agent over time.</summary>
    public static string EquityHeading => "Equity curve";

    /// <summary>What the picture of the equity is called, for whoever does not see it.</summary>
    public static string CurveLabel => "The equity of the agent over time";

    /// <summary>The heading of the closed trades of the agent.</summary>
    public static string TradesHeading => "Trades";

    /// <summary>The id of the heading of the trades, which the links to the pages of the trades lead to.</summary>
    public const string TradesHeadingId = "trades-heading";

    /// <summary>What the page says of an agent that closed no position.</summary>
    public static string NoTrades => "The agent has closed no position yet.";

    /// <summary>The name of the table of the trades, which is read out and not shown.</summary>
    public static string TradesCaption => "The closed trades of the agent";

    /// <summary>The heading of the column that says when a trade was closed.</summary>
    public static string ClosedHeading => "Closed";

    /// <summary>The heading of the column that says the mode that the agent was in when it opened a trade.</summary>
    public static string ModeHeading => "Mode";

    /// <summary>The heading of the column of the symbol of a trade.</summary>
    public static string SymbolHeading => "Symbol";

    /// <summary>The heading of the column of the market of a trade.</summary>
    public static string MarketHeading => "Market";

    /// <summary>The heading of the column that says whether a trade was long or short.</summary>
    public static string SideHeading => "Side";

    /// <summary>The heading of the column that says how much a position held.</summary>
    public static string QuantityHeading => "Quantity";

    /// <summary>The heading of the column of the price that a position was entered at.</summary>
    public static string EntryPriceHeading => "Entry price";

    /// <summary>The heading of the column of the price that a position was closed at.</summary>
    public static string ExitPriceHeading => "Exit price";

    /// <summary>The heading of the column that says how a position was closed.</summary>
    public static string ExitReasonHeading => "How it closed";

    /// <summary>The heading of the column of the result of a trade in R.</summary>
    public static string InRHeading => "In R";

    /// <summary>The heading of the column that says when a trade was opened.</summary>
    public static string OpenedHeading => "Opened";

    /// <summary>The heading of the column that says how long a trade was held.</summary>
    public static string HeldHeading => "Held";

    /// <summary>What the pages of the trades are called, for whoever does not see how they are laid out.</summary>
    public static string TradesPagesLabel => "Pages of trades";

    /// <summary>What leads to the page of the trades that were closed after those shown.</summary>
    public static string NewerTrades => "Newer trades";

    /// <summary>What leads to the page of the trades that were closed before those shown.</summary>
    public static string OlderTrades => "Older trades";

    /// <summary>What the page says under the trades, when the result of one of them is not recorded yet.</summary>
    public static string RecordedLater => "The result of a trade is recorded some minutes after it was closed.";

    /// <summary>The heading of the latest decisions of the team.</summary>
    public static string DecisionsHeading => "Decisions";

    /// <summary>The mode that a decision was made in, as a word, before how its cycle ended: <c>In Demo</c>.</summary>
    public static string InMode(TradingMode mode) => $"In {ModeOf(mode)}";

    /// <summary>What the page says above the decisions.</summary>
    public static string DecisionsExplained =>
        "The latest decisions of the team, the latest first: what each role answered, and what the Risk Guard made of the plan, with every " +
        "rule. A cycle that you ran as a test is not among them: it is on the list of the cycles.";

    /// <summary>What the page says of an agent whose team has decided nothing yet.</summary>
    public static string NoDecisions =>
        "The team has decided nothing yet: no cycle of the agent was recorded. A cycle starts when a candle of its timeframe closes, while the agent runs.";

    /// <summary>What leads from a decision to the whole record of its cycle.</summary>
    public static string WholeRecord => "The whole record of this cycle, as a timeline";

    /// <summary>What the page says before the link to every cycle of the agent.</summary>
    public static string EveryCycle => "Every cycle of the agent, also the earlier ones and the test cycles:";

    /// <summary>The heading of the orders of a decision.</summary>
    public static string OrdersHeading => "Orders";

    /// <summary>What the page says above the performance figures: which mode they are of, and that nothing of the other is in them (FR-BIN-11).</summary>
    public static string PerformanceExplained(TradingMode mode) =>
        $"What the agent has made and lost in {AgentPages.NameOf(mode)}, to date. Nothing of what it did in {Other(mode)} is in these figures.";

    /// <summary>
    /// What the figures of the money are valued at, or why they are not known. A figure that is
    /// not known is never written as a number: what would be missing of it is what the open
    /// position is worth.
    /// </summary>
    public static string Valued(AgentEquity equity, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(equity);

        if (equity.Equity is not null)
        {
            return equity.Price is { } price
                ? $"The open position is valued at the price as of {times.AtSecond(price.ReadAt)}."
                : "The agent holds no position, so no price is in these figures.";
        }

        return equity is { Position: { } position, Price: null }
            ? $"The open position cannot be valued right now: the price of {position.Symbol} is not known at the moment. " +
              "The page asks for it, and shows the figures once it is read."
            : "A part of what the agent made is not known yet: an order that closed a part of a position says no price that it " +
              "was executed at so far. The figures are shown once it does.";
    }

    /// <summary>What the return is a share of.</summary>
    public static string ReturnExplained(AgentEquity equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return $"The return is the net result in percent of the {DashboardPages.Amount(equity.Capital, equity.Asset)} that the agent is allocated now.";
    }

    /// <summary>Which trades the win rate, the average R, and the profit factor are of, and what is to be noted about them.</summary>
    public static string Counted(AgentFigures figures, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(figures);

        var record = figures.Record;

        if (record.Trades == 0)
        {
            return figures.Closed == 0
                ? $"The win rate, the average R, and the profit factor are of the closed trades of the agent in {AgentPages.NameOf(mode)}. It has closed none yet."
                : $"The win rate, the average R, and the profit factor are of the closed trades of the agent in {AgentPages.NameOf(mode)}. {Uncounted(figures.Closed)}";
        }

        var said = new List<string>
        {
            $"The win rate, the average R, and the profit factor are of {ExperiencePages.Trades(record.Trades)} in {AgentPages.NameOf(mode)}, in R: the amount that was risked on a trade.",
        };

        if (figures.Closed > record.Trades)
        {
            said.Add(Uncounted(figures.Closed - record.Trades));
        }

        if (ExperiencePages.Notes(record) is var notes && notes != ExperiencePages.NoNote)
        {
            said.Add(notes);
        }

        return string.Join(' ', said);
    }

    /// <summary>What the largest drawdown is, with the fall as an amount and when it was.</summary>
    public static string DrawdownExplained(EquityDrawdown? drawdown, string asset, PageTimes times)
    {
        const string What = "The largest drawdown is the deepest fall of the recorded equity from its peak, in percent of the peak.";

        if (drawdown is null)
        {
            return $"{What} The recorded equity has not fallen from a peak.";
        }

        var from = drawdown.PeakAt is { } peakAt ? $"its peak of {times.At(peakAt)}" : "the capital that the agent began with";

        return $"{What} It was a fall of {DashboardPages.Amount(drawdown.Amount, asset)} from {from}, to {times.At(drawdown.LowAt)}.";
    }

    /// <summary>What the fees and the funding are, and what is not in them.</summary>
    public static string CostsExplained(AgentFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        const string What =
            "The fees are what Binance charged for the executions of the orders of the agent. The funding is what its positions " +
            "received on the Futures market, less what they paid. Both are in the net result.";

        return figures.FeesValued ? What : $"{What} A fee that could not be valued is not in the fees.";
    }

    /// <summary>A share in percent, with its sign, so that a profit is told from a loss without a colour (FR-THM-5): <c>+1.25 percent</c>.</summary>
    public static string SignedPercent(decimal? percent) => SignedPercentFigure(percent)?.Text ?? DashboardPages.NotKnown;

    /// <summary>That share as a figure that counts when it changes (FR-ANI-2), or null when it is not known.</summary>
    public static PageFigure? SignedPercentFigure(decimal? percent) =>
        percent is { } known
            ? PageFigure.Of(Rounded(known).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture), after: " percent")
            : null;

    /// <summary>The largest drawdown as the fall that it is, in percent of the peak: <c>4.2 percent</c>.</summary>
    public static string Drawdown(EquityDrawdown? drawdown) =>
        drawdown is null ? NoDrawdown : $"{Rounded(drawdown.Percent).ToString("0.##", CultureInfo.InvariantCulture)} percent";

    /// <summary>What the page says above the curve: what is recorded, when, and in which mode.</summary>
    public static string EquityExplained(TradingMode mode) =>
        $"The equity of the agent as it was recorded in {AgentPages.NameOf(mode)}: its capital, plus its net profit or loss, with what its open position " +
        "was worth then. It is recorded when it changes, and once in a quarter of an hour while the agent holds a position.";

    /// <summary>What the page says of an agent whose equity was not recorded in its mode.</summary>
    public static string NoEquity(TradingMode mode) =>
        $"The equity of the agent was not recorded in {AgentPages.NameOf(mode)} yet. It is recorded once the agent runs.";

    /// <summary>What the curve says, in words: where the equity began, where it is, and how low and how high it was.</summary>
    public static string Says(EquityCurve curve, string asset, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(curve);

        if (curve is not { First: { } first, Latest: { } latest, Lowest: { } lowest, Highest: { } highest })
        {
            return string.Empty;
        }

        var recorded = curve.Recorded == 1 ? "Recorded once" : $"Recorded {Count(curve.Recorded)} times";

        if (curve.Recorded == 1)
        {
            return $"{recorded}: {DashboardPages.Amount(first.Equity, asset)} at {times.At(first.At)}. A line is drawn once it is recorded again.";
        }

        return $"{recorded}, from {DashboardPages.Amount(first.Equity, asset)} at {times.At(first.At)} to " +
            $"{DashboardPages.Amount(latest.Equity, asset)} at {times.At(latest.At)}. " +
            $"Lowest: {DashboardPages.Amount(lowest.Equity, asset)} at {times.At(lowest.At)}. " +
            $"Highest: {DashboardPages.Amount(highest.Equity, asset)} at {times.At(highest.At)}. " +
            "The line holds each value until the next, and the dashed line is the capital that the agent is allocated.";
    }

    /// <summary>How many trades are listed, and which page of them it is.</summary>
    public static string Listed(AgentTrades trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var listed = $"{ExperiencePages.Trades(trades.Total)}, the latest close first, each with the mode that the agent was in.";

        return trades.Pages > 1 ? $"{listed} Page {Count(trades.Page)} of {Count(trades.Pages)}." : listed;
    }

    /// <summary>
    /// A mode as a word, so that Demo is told from Live wherever a trade or a decision is listed
    /// (FR-BIN-11). The page has the one badge, which is that of its agent (FR-UI-3).
    /// </summary>
    public static string ModeOf(TradingMode mode) => AgentPages.NameOf(mode);

    /// <summary>The side of a trade, as a word.</summary>
    public static string Side(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Side == PositionSide.Long ? "Long" : "Short";
    }

    /// <summary>A quantity or a price of a trade, or that it is not known.</summary>
    public static string Number(decimal? number) =>
        number is { } known ? AgentPages.Number(Math.Round(known, 8, MidpointRounding.AwayFromZero)) : NotKnown;

    /// <summary>How a position was closed, in plain language.</summary>
    public static string Describe(ExitReason? reason) => reason switch
    {
        ExitReason.StopLoss => "Stop-loss",
        ExitReason.TakeProfit => "Take-profit",
        ExitReason.TeamClose => "Closed by the team",
        ExitReason.UserClose => "Closed by you",
        ExitReason.ProtectionFailed => "Closed by the service: its stop-loss could not be placed",
        ExitReason.LossLimit => "Closed by the service: a loss limit was reached",
        ExitReason.ClosedAtExchange => "Closed at Binance, by no order of the agent",
        _ => NotKnown,
    };

    /// <summary>The net result of a trade, with its sign, or that it is not recorded yet. An estimate says so.</summary>
    public static string Result(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        if (trade.Result is not { } result)
        {
            return NotRecorded;
        }

        var made = DashboardPages.Signed(result.NetResult, trade.Asset);

        return trade.Estimate ? $"{made}, an estimate" : made;
    }

    /// <summary>The result of a trade in R, or that it is not recorded yet.</summary>
    public static string InR(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Result is { } result ? ExperiencePages.InR(result.ResultInR) : NotRecorded;
    }

    /// <summary>The fees of a trade, or that its result is not recorded yet.</summary>
    public static string FeesOf(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Result is { } result ? DashboardPages.Amount(result.Fees, trade.Asset) : NotRecorded;
    }

    /// <summary>The funding of a trade, with its sign, or that its result is not recorded yet.</summary>
    public static string FundingOf(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Result is { } result ? DashboardPages.Signed(result.Funding, trade.Asset) : NotRecorded;
    }

    /// <summary>How long a trade was held, or that its result is not recorded yet.</summary>
    public static string HeldOf(AgentTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Result is { } result ? ExperiencePages.Held(result.Held) : NotRecorded;
    }

    /// <summary>What a decision is called: the close of the candle that its cycle was for.</summary>
    public static string DecisionOf(CycleRecord cycle, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return $"Candle close at {times.At(cycle.ClosedAt)}";
    }

    /// <summary>The id of a part of a decision: several decisions are on the page, and an id is on a page once.</summary>
    public static string Id(CycleRecord cycle, string part)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return $"decision-{cycle.Id:N}-{part}";
    }

    /// <summary>
    /// Which page of the trades an address asks for. Null for an address that names a page which
    /// there is not: no whole number from 1, or a page twice. A page that is absent or empty is
    /// the first.
    /// </summary>
    public static int? TradesPageOf(IQueryCollection query) => PageNumbers.Of(query, WebUiModule.AgentTradesPageKey);

    /// <summary>The other mode, as a word.</summary>
    private static string Other(TradingMode mode) => AgentPages.NameOf(mode == TradingMode.Live ? TradingMode.Demo : TradingMode.Live);

    /// <summary>How many closed trades are in no figure, and why.</summary>
    private static string Uncounted(int count) => count == 1
        ? "1 closed trade is not counted: its result is not recorded yet, or says no R."
        : $"{Count(count)} closed trades are not counted: their results are not recorded yet, or say no R.";

    private static decimal Rounded(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
