using System.Globalization;
using System.Text.Json;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Agents;

/// <summary>
/// The texts of the pages of the cycles of an agent, in one place (NFR-USA-4): the list of its
/// cycles, the timeline of a cycle, and the page that runs a test cycle. They say how a cycle
/// ended, what each role was called with and answered, what became of its orders, and why it
/// placed none (FR-CYC-10). Every outcome is explained in plain language (NFR-USA-2). See "The
/// timeline" in docs/team.md.
/// </summary>
/// <remarks>
/// What the Risk Guard made of a plan is said by <see cref="RiskCheckPages"/>, on the timeline
/// as on the page of the risk checks.
/// </remarks>
public static class CyclePages
{
    /// <summary>The title and the heading of the list of the cycles of an agent.</summary>
    public static string TitleOf(string agentName) => $"Cycles of the agent {agentName}";

    /// <summary>The title and the heading of the timeline of a cycle.</summary>
    public static string TimelineTitleOf(string agentName) => $"Cycle of the agent {agentName}";

    /// <summary>The title and the heading of the page that runs a test cycle.</summary>
    public static string TestTitleOf(string agentName) => $"Test cycle of the agent {agentName}";

    /// <summary>What stands before the badge of the mode of the agent, under the heading of a page.</summary>
    public static string ModeLabel => "Mode:";

    /// <summary>What leads to the list of the cycles, from the other pages of the agent.</summary>
    public static string Link => "Cycles of the agent";

    /// <summary>What leads from a cycle of the list to its timeline.</summary>
    public static string TimelineLink => "Timeline";

    /// <summary>What leads from a check of the Risk Guard to the timeline of its cycle.</summary>
    public static string TimelineOfCheckLink => "Timeline of the cycle of this check";

    /// <summary>What leads from a page of the cycles to the state of the agent.</summary>
    public static string StateLink => "State of the agent";

    /// <summary>What leads from a page of the cycles to the settings of the agent.</summary>
    public static string SettingsLink => "Settings of the agent";

    /// <summary>What leads from a page of the cycles back to the list of the agents.</summary>
    public static string BackToAgents => "Back to your agents";

    /// <summary>What leads to the keys, where a key keeps a role from working in a test cycle.</summary>
    public static string KeysLink => "Your keys";

    /// <summary>What a cycle is, and what is recorded of it, in plain language.</summary>
    public static string Explained =>
        "Each time a candle of the agent's timeframe closes while the agent runs, its team runs once: a cycle. Every cycle that " +
        "started is recorded when it ends, however it ends: with a summary of the data that the team decided on, what each role " +
        "answered, what the Risk Guard made of the plan, the orders that were placed, and the outcome. A cycle that made no " +
        "decision, or placed no order, is recorded as well.";

    /// <summary>What the list says of an agent that has no cycle.</summary>
    public static string NoneYet =>
        "No cycle of this agent has been recorded yet. A cycle is recorded when it ends, for an agent that runs when a candle of " +
        "its timeframe closes.";

    /// <summary>What the list says above its table when it does not hold every cycle of the agent.</summary>
    public static string LatestListed(int listed, int total) => $"The latest {Whole(listed)} of {Whole(total)} cycles, latest first.";

    /// <summary>What the list says above its table when it holds every cycle of the agent.</summary>
    public static string EveryListed => "Every cycle of the agent, latest first.";

    /// <summary>The name of the table of the cycles, which is read out and not shown.</summary>
    public static string ListCaption => "The cycles of the agent";

    /// <summary>The heading of the column that says when the candle of a cycle closed.</summary>
    public static string CandleCloseHeading => "Candle close";

    /// <summary>The heading of the column that says what started a cycle.</summary>
    public static string KindHeading => "Kind";

    /// <summary>The heading of the column that says the mode that a cycle ran in.</summary>
    public static string ModeHeading => "Mode";

    /// <summary>The heading of the column that says how a cycle ended.</summary>
    public static string OutcomeHeading => "Outcome";

    /// <summary>The heading of the column that says what the outcome of a cycle means.</summary>
    public static string MeaningHeading => "What it means";

    /// <summary>The heading of the column that says how long a cycle took.</summary>
    public static string TookHeading => "Took";

    /// <summary>The heading of the column that leads to the timeline of a cycle.</summary>
    public static string RecordHeading => "Record";

    /// <summary>What the timeline says above its entries.</summary>
    public static string TimelineExplained =>
        "What happened in this cycle, in the order it happened. The outcome of a cycle is the decision of the agent's team. What " +
        "the Risk Guard made of the plan, and the orders, stand beside it.";

    /// <summary>What stands in bold before what the timeline of a test cycle says under its heading.</summary>
    public static string TestLabel => "Test cycle.";

    /// <summary>
    /// What the timeline says of its cycle, before the badge of the mode that the cycle ran in:
    /// <c>The cycle for the candle that closed at 17 November 2026, 09:00 UTC, in</c>.
    /// </summary>
    /// <param name="closedAt">When the candle closed, as the page writes a time.</param>
    public static string TimelineOf(string closedAt) => $"The cycle for the candle that closed at {closedAt}, in";

    /// <summary>What stands before how a cycle ended, as a word.</summary>
    public static string OutcomeLabel => "Outcome:";

    /// <summary>The heading of the entry where the candle of a cycle closed.</summary>
    public static string CandleClosedHeading => "The candle closed";

    /// <summary>Which candle closed, as the summary of the data package of a cycle says it.</summary>
    public static string CandleClosed(DataPackageSummary package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return $"The {package.Timeframe.Code()} candle of {package.Symbol} closed.";
    }

    /// <summary>What the timeline says in place of that for a cycle that kept no summary of a data package.</summary>
    public static string CandleOfCycleClosed => "The candle that the cycle is for closed.";

    /// <summary>What the timeline says of the close of the candle of a cycle that a candle close started.</summary>
    public static string DecidesOnClosedCandles =>
        "A cycle starts when a candle of the agent's timeframe closes, and decides on closed candles only.";

    /// <summary>What the timeline says of the close of the candle of a test cycle, which is run at any time after it (FR-CYC-12).</summary>
    public static string TestDecidesOnClosedCandles =>
        "It was the latest closed candle of the agent's timeframe when the test cycle was run. A cycle decides on closed candles only.";

    /// <summary>The heading of the entry where a cycle that a candle close started began.</summary>
    public static string StartedHeading => "The cycle started";

    /// <summary>The heading of the entry where a test cycle began.</summary>
    public static string TestStartedHeading => "The test cycle started";

    /// <summary>What the timeline of a cycle that a candle close started says where the cycle started.</summary>
    public static string Started =>
        "The turn of the cycle came. Before the team is called, the orders of the agent are settled with the exchange, and its " +
        "loss limits are evaluated.";

    /// <summary>The heading of the summary of the data package of a cycle.</summary>
    public static string PackageHeading => "Data package";

    /// <summary>What the data package is, in plain language.</summary>
    public static string PackageExplained =>
        "What the team decided on. The system computed the indicators, and no model did. The record keeps this summary, and not " +
        "the candles themselves.";

    /// <summary>The name of the table of the candles and the indicators of a data package, which is read out and not shown.</summary>
    public static string PackageCaption => "The candles and the indicators of the data package";

    /// <summary>The heading of the column that names what the table says of each series of candles.</summary>
    public static string LatestCloseHeading => "At the latest close";

    /// <summary>The heading of the column of a series of candles, by the code of its timeframe: <c>1H candles</c>.</summary>
    public static string CandlesOf(string timeframe) => $"{timeframe} candles";

    /// <summary>The name of the row that says how many closed candles of a series the package held.</summary>
    public static string PackageCandles => "Closed candles in the package";

    /// <summary>The name of the row that says when the latest candle of a series closed.</summary>
    public static string PackageThrough => "Through";

    /// <summary>What that row says of a series that had no closed candle.</summary>
    public static string NoClosedCandle => "no closed candle yet";

    /// <summary>The name of the row that says the price that the latest candle opened at.</summary>
    public static string LatestOpen => "Open";

    /// <summary>The name of the row that says the highest price of the latest candle.</summary>
    public static string LatestHigh => "High";

    /// <summary>The name of the row that says the lowest price of the latest candle.</summary>
    public static string LatestLow => "Low";

    /// <summary>The name of the row that says the price that the latest candle closed at.</summary>
    public static string LatestClose => "Close";

    /// <summary>The name of the row that says the volume of the latest candle.</summary>
    public static string LatestVolume => "Volume";

    /// <summary>The name of the row that says how many candles the indicators of a series were computed from.</summary>
    public static string ComputedFrom => "Candles that the indicators were computed from";

    /// <summary>The name of the row of the simple moving average of 20 candles.</summary>
    public static string Sma20 => "SMA20";

    /// <summary>The name of the row of the simple moving average of 50 candles.</summary>
    public static string Sma50 => "SMA50";

    /// <summary>The name of the row of the simple moving average of 200 candles.</summary>
    public static string Sma200 => "SMA200";

    /// <summary>The name of the row of the exponential moving average of 20 candles.</summary>
    public static string Ema20 => "EMA20";

    /// <summary>The name of the row of the exponential moving average of 50 candles.</summary>
    public static string Ema50 => "EMA50";

    /// <summary>The name of the row of the relative strength index of 14 candles.</summary>
    public static string Rsi14 => "RSI14";

    /// <summary>The name of the row of the average true range of 14 candles.</summary>
    public static string Atr14 => "ATR14";

    /// <summary>The name of the row of the average volume of 20 candles.</summary>
    public static string VolumeAverage20 => "Average volume of 20 candles";

    /// <summary>What an indicator says when its series had too few candles for it.</summary>
    public static string NotAvailable => "not available";

    /// <summary>What the summary of a data package says of an agent that had no order resting on the exchange.</summary>
    public static string NoOpenOrders => "Open orders: none.";

    /// <summary>What stands above the orders of the agent that rested on the exchange.</summary>
    public static string OpenOrdersLabel => "Open orders:";

    /// <summary>
    /// What a call of a role says of the model that it was made with, after what the role does:
    /// <c>Model: Claude Sonnet, of Anthropic.</c>
    /// </summary>
    public static string CalledWith(RoleCallView call) => $"Model: {Model(call)}.";

    /// <summary>Which versions of the prompt templates the prompt of a call was composed from.</summary>
    public static string ComposedFrom(IEnumerable<TemplateVersion> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        return $"Its prompt was composed from {string.Join(", ", templates.Select(Says))}.";
    }

    /// <summary>The heading of a check of the Risk Guard: what the plan that it checked does.</summary>
    public static string CheckHeading(PlanAction action) => $"Risk Guard: {RiskCheckPages.Describe(action)}";

    /// <summary>What follows what has become of an order, after a comma: the order is shown as it is now.</summary>
    public static string AsItIsNow => "as it is now";

    /// <summary>The identifiers of an order: the one that it carries, and the one that Binance knows it by, if any.</summary>
    public static string Identifiers(CycleOrderView order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return order.ExchangeOrderId is { } number
            ? $"It carries the identifier {order.ClientOrderId} at Binance, which knows it as the order {number}."
            : $"It carries the identifier {order.ClientOrderId}. Binance has said no order number for it.";
    }

    /// <summary>The heading of the entry that says why a cycle placed no order.</summary>
    public static string NoOrderHeading => "No order";

    /// <summary>The heading of the entry where a cycle ended, with how it ended.</summary>
    public static string EndedHeading(CycleKind kind, CycleOutcome outcome) =>
        kind == CycleKind.Test ? $"The test cycle ended: {Describe(outcome)}" : $"The cycle ended: {Describe(outcome)}";

    /// <summary>How long a cycle ran, after what its outcome means: <c>The cycle ran for 12.5 seconds.</c></summary>
    public static string RanFor(TimeSpan length) => $"The cycle ran for {Seconds(length)}.";

    /// <summary>What leads to the page that runs a test cycle of an agent, and what its button says (FR-CYC-12).</summary>
    public static string TestLink => "Run a test cycle";

    /// <summary>What the list of the cycles says of test cycles, beside what a cycle is.</summary>
    public static string TestsListed =>
        "A test cycle, which you run yourself at any time, is recorded in the same way and is listed here as a test. It places no orders.";

    /// <summary>What a test cycle is, in plain language (FR-CYC-12).</summary>
    public static string TestExplained =>
        "A test cycle runs the team of the agent once, now, on the latest closed candle of its timeframe, in whatever state the " +
        "agent is, so that you see what the team would decide. It is recorded as every cycle is, and marked as a test: with a " +
        "summary of the data that the team decided on, what each role answered, what the Risk Guard made of the plan, and the " +
        "outcome.";

    /// <summary>That a test cycle places no order, and changes nothing.</summary>
    public static string TestPlacesNoOrder =>
        "A test cycle places no order, whatever its team decides and whatever the Risk Guard makes of the plan. It settles no " +
        "order with Binance, moves no stop-loss, closes no position, and evaluates no loss limit, so it halts nothing: " +
        "nothing changes at Binance.";

    /// <summary>What the page that runs a test cycle says of an agent in Live (FR-UI-3).</summary>
    public static string TestInLive => "The agent is in Live. A test cycle places no order in your real Binance account either.";

    /// <summary>What stands before the state of the agent, in bold, on the page that runs a test cycle.</summary>
    public static string TestAgentIs => "The agent is";

    /// <summary>What follows the state of the agent there, after a comma.</summary>
    public static string TestDoesNotStart => "and a test cycle does not start it.";

    /// <summary>What a test cycle costs its user (FR-KEY-1, FR-CYC-3).</summary>
    public static string TestCosts =>
        "It calls the model of each role on your own keys, as every cycle does, so your providers charge you for the calls. The " +
        "Sentiment Analyst searches live where its model can, which may add charges of its provider. A key that a provider " +
        "refuses in a call is checked again, as after every such call, and is marked as not valid when it is refused again.";

    /// <summary>How long a test cycle takes, and what the page does meanwhile.</summary>
    public static string TestTakes =>
        "It takes a minute or two, and this page waits for it: it then shows the test cycle as a timeline. One test cycle of " +
        "yours runs at a time. It is not given up when you leave this page: it is on the list of the cycles of the agent once " +
        "it has ended.";

    /// <summary>What the stale limit of a test cycle counts from (FR-CYC-9).</summary>
    public static string TestStaleLimit =>
        "A decision that completes later than the stale limit of the agent's timeframe opens no position. A cycle that a candle " +
        "close starts counts the limit from that close. A test cycle is run at any time, so it counts the limit from the moment " +
        "you run it.";

    /// <summary>What the page says when the team of the agent keeps a test cycle from running (FR-TEAM-1).</summary>
    public static string TestRolesBlock =>
        "No test cycle was run, and no model was called: a cycle runs once every role of the agent's team has a model that your " +
        "keys can use.";

    /// <summary>What the page says when a test cycle of the user runs already.</summary>
    public static string TestAlreadyRunning =>
        "No test cycle was started: a test cycle of yours is running already, and one runs at a time. It is on the list of the " +
        "cycles of its agent once it has ended.";

    /// <summary>What the page says when the exchange did not give the candles of a test cycle.</summary>
    public static string TestNoCandles =>
        "No test cycle was run, and no model was called: Binance did not give the candles of the agent's symbol. Try again in a moment.";

    /// <summary>What the page says when an error or a stop ended a test cycle and no record of it was found.</summary>
    public static string TestNotRecorded =>
        "The test cycle ended early, and no record of it was found, so there is nothing to show of it here. If it was stored " +
        "after all, it is on the list of the cycles of the agent. Otherwise, try again.";

    /// <summary>What the timeline of a test cycle says under its heading.</summary>
    public static string TestCycleRan => "You ran this cycle as a test. It placed no orders, whatever its team decided.";

    /// <summary>What the timeline of a test cycle says where the cycle started.</summary>
    public static string TestStarted =>
        "You ran the test cycle. A test cycle settles no order with the exchange and evaluates no loss limit: nothing changes " +
        "at the exchange, and nothing halts the agent. Its team decides on what the records of the agent say it holds.";

    /// <summary>What started a cycle, in a word.</summary>
    public static string Describe(CycleKind kind) => kind switch
    {
        CycleKind.Scheduled => "Scheduled",
        CycleKind.Test => "Test",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No such kind."),
    };

    /// <summary>How a cycle ended, in a word or two.</summary>
    public static string Describe(CycleOutcome outcome) => outcome switch
    {
        CycleOutcome.Hold => "Hold",
        CycleOutcome.Unreviewed => "Reduces risk",
        CycleOutcome.Approved => "Approved",
        CycleOutcome.Reduced => "Reduced",
        CycleOutcome.Vetoed => "Vetoed",
        CycleOutcome.Stale => "Stale",
        CycleOutcome.NoDecision => "No decision",
        CycleOutcome.Failed => "Failed",
        CycleOutcome.Stopped => "Stopped",
        CycleOutcome.Halted => "Halted",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such outcome."),
    };

    /// <summary>What the outcome of a cycle means, in plain language.</summary>
    public static string Explain(CycleOutcome outcome) => outcome switch
    {
        CycleOutcome.Hold => "The team decided to hold: nothing changes, and no order is placed.",
        CycleOutcome.Unreviewed =>
            "The team decided to close the position or to adjust its protection. Such a plan reduces risk, so the Risk Manager is " +
            "not asked about it, and it went on to the Risk Guard as planned.",
        CycleOutcome.Approved =>
            "The team decided to open a position, and the Risk Manager approved the plan as it was. It went on to the Risk Guard.",
        CycleOutcome.Reduced =>
            "The team decided to open a position, and the Risk Manager reduced the risk of the plan. It went on to the Risk Guard " +
            "with the lower risk, and with nothing else of it changed.",
        CycleOutcome.Vetoed =>
            "The team planned to open a position, and the Risk Manager vetoed the plan. Nothing of it went on, and no order was placed.",
        CycleOutcome.Stale =>
            "The decision was complete later than the stale limit of the agent's timeframe, so it opens no position. Of a plan to " +
            "reverse, the close alone went on, because closing reduces risk.",
        CycleOutcome.NoDecision =>
            "A role gave no valid result, also when it was called once more. The cycle ended with no decision and no new entry. " +
            "An open position stays as it is, with its protective orders.",
        CycleOutcome.Failed =>
            "An error ended the cycle before its team had decided: its data could not be read from the exchange, or something " +
            "else failed. No decision was made, and an open position stays as it is, with its protective orders.",
        CycleOutcome.Stopped =>
            "The cycle was stopped before its team had decided: the service was stopping, or the next candle close was near. " +
            "No decision was made.",
        CycleOutcome.Halted =>
            "A loss limit of the agent was reached as the cycle began. The agent was halted and its position was closed, and its " +
            "team was not called.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such outcome."),
    };

    /// <summary>
    /// What the outcome of a cycle means, in plain language, for a cycle of a kind: a test cycle
    /// is stopped for other reasons than a cycle that a candle close starts, and counts its stale
    /// limit from another moment (FR-CYC-12).
    /// </summary>
    public static string Explain(CycleOutcome outcome, CycleKind kind) => (kind, outcome) switch
    {
        (CycleKind.Test, CycleOutcome.Stale) =>
            "The decision was complete later than the stale limit of the agent's timeframe, counted from the moment the test " +
            "cycle was run, so it opens no position. Of a plan to reverse, the close alone went on to the Risk Guard, because " +
            "closing reduces risk.",
        (CycleKind.Test, CycleOutcome.Stopped) =>
            "The test cycle was stopped before its team had decided: the service was stopping, or the test cycle ran longer " +
            "than a test cycle may. No decision was made.",
        _ => Explain(outcome),
    };

    /// <summary>How long after the close of its candle something happened: <c>2.5 seconds after the candle closed</c>.</summary>
    public static string After(DateTimeOffset closedAt, DateTimeOffset at) =>
        at <= closedAt ? "as the candle closed" : $"{Seconds(at - closedAt)} after the candle closed";

    /// <summary>
    /// How long after its start something happened in a test cycle, which is run at any time
    /// after the close of its candle: <c>2.5 seconds after the test cycle started</c>.
    /// </summary>
    public static string AfterStart(DateTimeOffset startedAt, DateTimeOffset at) =>
        at <= startedAt ? "as the test cycle started" : $"{Seconds(at - startedAt)} after the test cycle started";

    /// <summary>A length of time in seconds, to a tenth: <c>1.5 seconds</c>.</summary>
    public static string Seconds(TimeSpan length)
    {
        var seconds = Math.Round((decimal)Math.Max(length.TotalSeconds, 0d), 1, MidpointRounding.AwayFromZero);

        return seconds == 1m ? "1 second" : $"{AgentPages.Number(seconds)} seconds";
    }

    /// <summary>Why a cycle kept no summary of a data package.</summary>
    public static string NoPackage(CycleOutcome outcome) => outcome switch
    {
        CycleOutcome.Halted => "No data package was built: a loss limit halted the agent before its team was called.",
        CycleOutcome.Failed or CycleOutcome.Stopped => "The cycle ended before its data package was built, so it kept no summary of one.",
        _ => "This cycle kept no summary of its data package: it was recorded before a summary was kept.",
    };

    /// <summary>What the agent was and held, as the summary of a data package says it.</summary>
    public static string Says(DataPackageSummary package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var position = package.Position is { } held
            ? $"It held a {(held.Side == PositionSide.Long ? "long" : "short")} position of {Number(held.Quantity)}, entered at " +
              $"{Number(held.EntryPrice)}, with {Resting("a stop-loss", held.StopLoss)} and {Resting("a take-profit", held.TakeProfit)}."
            : "It held no position.";

        return $"The agent traded {package.Symbol} on {AgentPages.NameOf(package.Market)}, on {package.Timeframe.Code()} candles. Its equity was " +
            $"{Number(package.Equity)} {package.QuoteAsset}. {position}";
    }

    /// <summary>The limits of the agent, as the summary of a data package says them.</summary>
    public static string Limits(DataPackageSummary package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var limits = package.Limits;
        var futures = package.Market == Market.Futures
            ? $", a leverage of at most {limits.LeverageCap.ToString(CultureInfo.InvariantCulture)}, a liquidation buffer of at least {Number(limits.LiquidationBuffer)}"
            : string.Empty;

        return $"Its limits were: at most {Number(limits.RiskPerTradePercent)} percent of its equity at risk in a trade, a reward of at " +
            $"least {Number(limits.MinRewardToRisk)} times the risk{futures}, a daily loss limit of {Number(limits.DailyLossPercent)} " +
            $"percent, a drawdown limit of {Number(limits.DrawdownPercent)} percent, and an entry at most " +
            $"{Number(limits.MaxEntryDistancePercent)} percent from the current price.";
    }

    /// <summary>An order that rested on the exchange, as the summary of a data package says it.</summary>
    public static string Says(OpenOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var kind = order.Kind switch
        {
            OpenOrderKind.Entry => "Entry",
            OpenOrderKind.StopLoss => "Stop-loss",
            OpenOrderKind.TakeProfit => "Take-profit",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Kind, "No such kind of order."),
        };

        return $"{kind}: {(order.Side == OrderSide.Buy ? "buy" : "sell")} {Number(order.Quantity)} at {Number(order.Price)}";
    }

    /// <summary>What Futures adds to a data package, as its summary says it.</summary>
    public static string Says(FuturesData futures, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(futures);

        return $"Futures: a mark price of {Number(futures.MarkPrice)}, a funding rate of {Number(futures.FundingRate * 100m)} percent, " +
            $"the next funding at {times.At(futures.NextFundingAt)}, and an open interest of {Number(futures.OpenInterest)}.";
    }

    /// <summary>A price or a quantity that the system read, with every digit that it has.</summary>
    public static string Value(decimal? number) => number is { } value ? Number(value) : NotAvailable;

    /// <summary>An indicator that the system worked out, to eight digits that count, or that it is not available.</summary>
    public static string Indicator(decimal? indicator)
    {
        if (indicator is not { } value)
        {
            return NotAvailable;
        }

        if (value == 0m)
        {
            return "0";
        }

        // The digits before the point are all written, and what follows it is rounded.
        var before = (int)Math.Floor(Math.Log10((double)Math.Abs(value))) + 1;

        return Number(Math.Round(value, Math.Clamp(8 - before, 0, 28), MidpointRounding.AwayFromZero));
    }

    /// <summary>The model that a call was made with, and its provider.</summary>
    public static string Model(RoleCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);

        return $"{AgentPages.NameOf(call.ModelIdentifier, call.ModelName)}, of {call.ProviderName}";
    }

    /// <summary>What a call of a role is called on the timeline: the role, and whether it is the call that was made once more.</summary>
    public static string Title(RoleCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var role = AgentPages.Describe(call.Role).Name;

        return call.Attempt > 1 ? $"{role}, called once more" : role;
    }

    /// <summary>How a call ended, in a word or two.</summary>
    public static string Describe(RoleCallOutcome outcome, RoleValidation validation) => (outcome, validation) switch
    {
        (RoleCallOutcome.Succeeded, RoleValidation.Valid) => "Valid result",
        (RoleCallOutcome.Succeeded, _) => "Rejected result",
        (RoleCallOutcome.InvalidResult, _) => "No structured answer",
        (RoleCallOutcome.TimedOut, _) => "Timed out",
        (RoleCallOutcome.AuthenticationFailed, _) => "Key refused",
        (RoleCallOutcome.ModelNotFound, _) => "Model not found",
        (RoleCallOutcome.Failed, _) => "Call failed",
        (RoleCallOutcome.NoKey, _) => "Not called",
        (RoleCallOutcome.Canceled, _) => "Stopped",
        (RoleCallOutcome.ProviderPaused, _) => "Provider paused",
        (RoleCallOutcome.KeyPaused, _) => "Key paused",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "No such outcome."),
    };

    /// <summary>How a call ended and what was made of its answer, in plain language.</summary>
    public static string Says(RoleCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);

        return (call.Outcome, call.Validation) switch
        {
            (RoleCallOutcome.Succeeded, RoleValidation.Valid) =>
                "The model answered, and its answer is valid: it is inside the fixed schema of the role and the allowed ranges.",
            (RoleCallOutcome.Succeeded, _) =>
                $"The model answered, and its answer was rejected. {Rejected(call.Rejection)} A rejected answer is rejected whole: " +
                "nothing of it is used.",
            (RoleCallOutcome.InvalidResult, _) =>
                "The model gave no structured answer: a refusal, an answer that was cut short, or text that is not what was asked for.",
            (RoleCallOutcome.TimedOut, _) => "The provider did not answer within its time limit.",
            (RoleCallOutcome.AuthenticationFailed, _) => "The provider refused your key, so the model was not reached.",
            (RoleCallOutcome.ModelNotFound, _) => "The provider has no such model any more.",
            (RoleCallOutcome.Failed, _) => "The call failed: no connection, a limit of the provider, or a fault of it.",
            (RoleCallOutcome.NoKey, _) => "No call was made: you have no key for this provider that can be used.",
            (RoleCallOutcome.Canceled, _) =>
                "The call was under way when the cycle was stopped. The provider may have charged for it.",
            (RoleCallOutcome.ProviderPaused, _) =>
                "No call was made: the calls to this provider were paused, because it had left several calls in a row without " +
                "an answer, whoever made them. They are made again by themselves when the pause is over.",
            (RoleCallOutcome.KeyPaused, _) =>
                "No call was made: the calls with your key for this provider were paused, because the provider had refused " +
                "several in a row. That is a limit on the requests of the key, an account at the provider that has run out, " +
                "or a model that does not take the call. They are made again by themselves when the pause is over.",
            _ => throw new ArgumentOutOfRangeException(nameof(call), call.Outcome, "No such outcome."),
        };
    }

    /// <summary>
    /// Why an answer was rejected, in plain language, from what the record of the call keeps: a
    /// field of the fixed schema and the rule that it broke.
    /// </summary>
    public static string Rejected(string? rejection)
    {
        const string Separator = ": ";

        if (rejection is null
            || rejection.LastIndexOf(Separator, StringComparison.Ordinal) is not (> 0 and var at)
            || !Enum.TryParse<RejectionRule>(rejection[(at + Separator.Length)..], out var rule)
            || !Enum.IsDefined(rule))
        {
            return "It is outside the fixed schema of the role or the allowed ranges.";
        }

        var field = rejection[..at];
        var what = field == RoleRejection.Answer ? "The answer" : $"Its field {field}";

        return rule switch
        {
            RejectionRule.NotJson => "It is not the structured answer that was asked for.",
            RejectionRule.TooLong => "It is longer than an answer may be.",
            RejectionRule.WrongType => $"{what} has a value of another kind than the schema gives it.",
            RejectionRule.MissingField => $"{what} is not there, and the schema asks for it.",
            RejectionRule.UnknownField => $"{what} holds a field that the schema does not define.",
            RejectionRule.DuplicateField => $"{what} is there twice.",
            RejectionRule.NotAllowed => $"{what} has a value that is none of those that the schema lists for it.",
            RejectionRule.OutOfRange => $"{what} is outside the range that it is allowed.",
            RejectionRule.TooMany => $"{what} has more entries than it may have.",
            RejectionRule.TextTooLong => $"{what} is longer than it may be.",
            RejectionRule.Empty => $"{what} is empty, and must say something.",
            RejectionRule.Required => $"{what} has no value, and the action of the plan or the verdict of the review needs one.",
            RejectionRule.NotExpected =>
                $"{what} has a value, and the action of the plan or the verdict of the review has no place for one.",
            RejectionRule.HoldsWhatAPromptNeverHolds =>
                "It holds what a prompt never holds: a secret, an email address, or an identifier. A later role would read it, so it is not kept.",
            RejectionRule.NotOnMarket => "The action of the plan is not one that the market of the agent allows.",
            RejectionRule.PositionOpen =>
                "The plan opens a position, and the agent held one already: an agent holds one position at most, and adds to none.",
            RejectionRule.NoPosition => "The plan closes, reverses, or adjusts the protection of a position, and the agent held none.",
            RejectionRule.AddsRisk =>
                "The plan moves the stop-loss in the direction that adds risk. A stop-loss moves only in the direction that reduces risk.",
            _ => throw new ArgumentOutOfRangeException(nameof(rejection), rule, "No such rule."),
        };
    }

    /// <summary>
    /// The mark of the service on a report of the Sentiment Analyst: whether it rests on live
    /// data (FR-CYC-3). The mark is the service's, and not the model's. Null for every other call,
    /// and for a call that gave no valid report.
    /// </summary>
    public static string? Mark(RoleCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call is not { Role: Role.SentimentAnalyst, Outcome: RoleCallOutcome.Succeeded, Validation: RoleValidation.Valid })
        {
            return null;
        }

        return call.LiveSearches > 0
            ? "The service marks this report \"live data\": its model ran a live search for it."
            : "The service marks this report \"no live data\": nothing shows that it rests on a live search, so the Strategist is " +
              "told to give it no weight.";
    }

    /// <summary>How long a call took, and what its provider counted for it.</summary>
    public static string Usage(RoleCallView call)
    {
        ArgumentNullException.ThrowIfNull(call);

        // A call that was not made took no time: with no key, or while the calls were paused.
        var made = call.Outcome is not (RoleCallOutcome.NoKey or RoleCallOutcome.ProviderPaused or RoleCallOutcome.KeyPaused);
        var took = made ? $"It took {Seconds(TimeSpan.FromMilliseconds(call.LatencyMs))}." : null;
        var tokens = (call.InputTokens, call.OutputTokens) switch
        {
            ({ } input, { } output) => $"The provider counted {Whole(input)} tokens in and {Whole(output)} tokens out.",
            ({ } input, null) => $"The provider counted {Whole(input)} tokens in.",
            (null, { } output) => $"The provider counted {Whole(output)} tokens out.",
            _ => "The provider counted no tokens.",
        };
        var searches = call.LiveSearches switch
        {
            null => null,
            0 => "It was offered a live search and ran none, so its report counts as resting on no live data.",
            1 => "It ran 1 live search.",
            { } ran => $"It ran {Whole(ran)} live searches.",
        };

        return string.Join(' ', new[] { took, tokens, searches }.Where(said => said is not null));
    }

    /// <summary>A version of a prompt template that a call used, in words.</summary>
    public static string Says(TemplateVersion template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var version = $"version {Whole(template.Version)}";

        return template.Kind switch
        {
            PromptTemplateKind.Role when Enum.TryParse<Role>(template.Subject, out var role) && Enum.IsDefined(role) =>
                $"the instructions of the {AgentPages.Describe(role).Name}, {version}",
            PromptTemplateKind.Role => $"the instructions of the role {template.Subject}, {version}",
            PromptTemplateKind.Market => $"the module of the market {template.Subject}, {version}",
            PromptTemplateKind.Timeframe => $"the module of the timeframe {template.Subject}, {version}",
            PromptTemplateKind.Profile => $"the module of the profile {template.Subject}, {version}",
            PromptTemplateKind.Lesson => $"the instructions for the lesson of a closed position, {version}",
            _ => throw new ArgumentOutOfRangeException(nameof(template), template.Kind, "No such kind of template."),
        };
    }

    /// <summary>
    /// The structured result of a call, field by field, as a person reads it. A field of the
    /// schema of a role is named in words. A rejected answer may hold a field that no schema
    /// defines: it is shown under the name that the model gave it.
    /// </summary>
    /// <param name="result">What the record of the call keeps: one line of JSON, or null.</param>
    public static IReadOnlyList<ResultField> Fields(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(result);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? [.. document.RootElement.EnumerateObject().Select(field => new ResultField(Label(field.Name), Values(field.Name, field.Value)))]
                : [new ResultField("Answer", [Written(null, document.RootElement)])];
        }
        catch (JsonException)
        {
            // Only JSON is kept, so this is a row that nothing of the application wrote.
            return [new ResultField("Answer", [result])];
        }
    }

    /// <summary>What an order of a cycle is called on the timeline.</summary>
    public static string Describe(OrderPurpose purpose) => purpose switch
    {
        OrderPurpose.Entry => "Entry order",
        OrderPurpose.StopLoss => "Stop-loss order",
        OrderPurpose.TakeProfit => "Take-profit order",
        OrderPurpose.Close => "Order that closes the position",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "No such purpose."),
    };

    /// <summary>What an order of a cycle does, in plain language.</summary>
    public static string Says(CycleOrderView order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var does = $"{(order.Side == OrderSide.Buy ? "Buys" : "Sells")} {Number(order.Quantity)} in {order.Symbol}";
        var how = (order.Type, order.Price) switch
        {
            (OrderType.Market, _) => "at the market",
            (OrderType.Limit, { } price) => $"at {Number(price)} or better, where it rests until it is executed or taken off",
            (OrderType.Stop, { } price) => $"at the market once the price reaches {Number(price)}, and rests on the exchange until then",
            (OrderType.Limit, null) => "at a price, where it rests until it is executed or taken off",
            (OrderType.Stop, null) => "at the market once the price reaches its trigger, and rests on the exchange until then",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, "No such kind of order."),
        };
        var why = order.Purpose switch
        {
            OrderPurpose.Entry => "It opens the position.",
            OrderPurpose.StopLoss => "It closes the position at a loss when the price reaches it.",
            OrderPurpose.TakeProfit => "It closes the position at a profit when the price reaches it.",
            OrderPurpose.Close => "It closes the position.",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Purpose, "No such purpose."),
        };
        var protection = order is { Purpose: OrderPurpose.Entry, StopLoss: { } stopLoss }
            ? order.TakeProfit is { } takeProfit
                ? $" Its position is protected by a stop-loss at {Number(stopLoss)} and a take-profit at {Number(takeProfit)}."
                : $" Its position is protected by a stop-loss at {Number(stopLoss)}."
            : string.Empty;

        return $"{does}, {how}. {why}{protection}";
    }

    /// <summary>What has become of an order, in a word or two.</summary>
    public static string Describe(OrderStatus status) => status switch
    {
        OrderStatus.Sending => "Being sent",
        OrderStatus.Unknown => "Not known",
        OrderStatus.NotPlaced => "Not placed",
        OrderStatus.Rejected => "Refused by the exchange",
        OrderStatus.Open => "Rests on the exchange",
        OrderStatus.PartiallyFilled => "Partly executed",
        OrderStatus.Filled => "Executed",
        OrderStatus.Canceled => "Taken off the exchange",
        OrderStatus.Expired => "Ended by the exchange",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No such status."),
    };

    /// <summary>What has become of an order, in plain language, with what is executed of it.</summary>
    public static string Became(CycleOrderView order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var status = order.Status switch
        {
            OrderStatus.Sending => "The order is being sent, or was cut off while it was: the exchange is asked about it before anything else is done with it.",
            OrderStatus.Unknown => "The exchange did not say whether it took the order. It is asked about it before anything else is done with it.",
            OrderStatus.NotPlaced => "The order was not placed: it was not sent, or the exchange does not have it.",
            OrderStatus.Rejected => "The exchange refused the order.",
            OrderStatus.Open => "The order rests on the exchange, and none of it is executed.",
            OrderStatus.PartiallyFilled => "A part of the order is executed, and the rest is still with the exchange.",
            OrderStatus.Filled => "All of the order is executed.",
            OrderStatus.Canceled => "The order was taken off the exchange.",
            OrderStatus.Expired => "The exchange ended the order.",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order.Status, "No such status."),
        };

        if (order.ExecutedQuantity <= 0m)
        {
            return status;
        }

        var executed = order.AveragePrice is { } price
            ? $"{Number(order.ExecutedQuantity)} of {Number(order.Quantity)} is executed, at an average price of {Number(price)}."
            : $"{Number(order.ExecutedQuantity)} of {Number(order.Quantity)} is executed.";

        return $"{status} {executed}";
    }

    /// <summary>What a timeline says of a cycle that placed no order, and why it placed none.</summary>
    /// <param name="cycle">The cycle, which has no order.</param>
    public static string NoOrder(CycleRecord cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (cycle.Kind == CycleKind.Test)
        {
            // A test cycle places none, whatever became of its plan: it says what a cycle that
            // a candle close starts would have done with the same plan (FR-CYC-12).
            if (cycle.Checks.Count == 0)
            {
                return cycle.Outcome is CycleOutcome.Approved or CycleOutcome.Reduced or CycleOutcome.Unreviewed or CycleOutcome.Stale
                    ? "A test cycle places no order. This one ended before the Risk Guard had checked its plan."
                    : "A test cycle places no order. Nothing of a plan went on to the Risk Guard either.";
            }

            return cycle.Checks.Any(check => check.Verdict == RiskGuard.GuardVerdict.Accepted)
                ? "A test cycle places no order. The Risk Guard accepted the plan: in a cycle that a candle close starts, it " +
                  "would have gone on to the orders."
                : "A test cycle places no order. The Risk Guard rejected the plan: a cycle that a candle close starts would " +
                  "have placed none either.";
        }

        if (cycle.NoEntry is { } reason)
        {
            return $"The Risk Guard accepted the plan, and no entry was placed. {Explain(reason)}";
        }

        if (cycle.Checks.Count == 0)
        {
            return cycle.Outcome is CycleOutcome.Approved or CycleOutcome.Reduced or CycleOutcome.Unreviewed or CycleOutcome.Stale
                ? "The cycle placed no order: it ended before the Risk Guard had checked its plan."
                : "The cycle placed no order: nothing of a plan went on to the Risk Guard.";
        }

        return cycle.Checks.Any(check => check.Verdict == RiskGuard.GuardVerdict.Accepted)
            ? "The cycle placed no order: what its plan asked for needed none, or the cycle ended before one was written. What a " +
              "plan closes or protects may be gone by the time its orders are due."
            : "The cycle placed no order: the Risk Guard rejected its plan.";
    }

    /// <summary>Why no entry was placed for a plan that the Risk Guard accepted, in plain language.</summary>
    public static string Explain(NoEntryReason reason) => reason switch
    {
        NoEntryReason.SpotLongOnly =>
            "The entry would sell, on Spot. An agent on Spot buys, and sells what it bought to exit: it does not short.",
        NoEntryReason.NoSizeOrStopLoss => "The order had no quantity above zero, or the plan had no stop-loss.",
        NoEntryReason.EntriesPaused =>
            "New entries were paused: the clock of the service was not close enough to Binance's, or could not be compared with it.",
        NoEntryReason.ExchangeOutage =>
            "New entries were paused: Binance had left several calls in a row without an answer. No position is opened while " +
            "Binance is out, and entries go on by themselves when it answers again.",
        NoEntryReason.NoStopOrders =>
            "The symbol takes no stop order, or Binance did not say its rules. No position is opened where no stop-loss can rest.",
        NoEntryReason.NoPairOrders =>
            "The symbol takes no pair of a stop-loss and a take-profit on Spot. No position is opened whose protection cannot rest.",
        NoEntryReason.NoTakeProfitOrders =>
            "The symbol takes no take-profit order on Futures. No position is opened whose take-profit cannot rest beside its stop-loss.",
        NoEntryReason.NotRunning =>
            "The agent no longer opened positions: it was paused or stopped, or it waited for you, while its team decided.",
        NoEntryReason.Holding => "The agent held a position by then, or had an order that was not over. An agent holds one position at a time.",
        NoEntryReason.AccountUnknown =>
            "How your Binance Futures account is set could not be read. An entry is placed only in an account that was checked.",
        NoEntryReason.AccountConflict =>
            "Your Binance Futures account conflicts with how the agent trades: it is in hedge mode or in Multi-Assets mode, or holds " +
            "a position in the symbol that the agent did not open. The agent waits for you: see its notifications for what to change.",
        NoEntryReason.QuoteBalance =>
            "Your Binance account held less of the asset that the symbol is priced in than the entry needed.",
        NoEntryReason.Margin => "Your Binance Futures wallet had less available than the margin of the entry.",
        NoEntryReason.BalanceUnknown => "What your Binance account holds could not be read. An entry is placed only when it is known to be covered.",
        NoEntryReason.MarginRefused =>
            "Binance refused to set isolated margin, or the leverage of the order, for the symbol. It does not change them while the " +
            "account has open orders or a position in the symbol.",
        NoEntryReason.MarginNotSet =>
            "It is not known whether the symbol has isolated margin and the leverage of the order. No entry is placed at a leverage " +
            "that was not checked.",
        NoEntryReason.ReverseNotCompleted =>
            "The plan was to reverse, and the other side was not opened: the agent held no position any more, its position could " +
            "not be closed, or the cycle was being stopped once it was. A position is opened only when the one before it is closed.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No such reason."),
    };

    // The names of the fields of the schemas of the roles, as a person reads them. See "The
    // schemas" in docs/team.md.
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bias"] = "Bias",
        ["keyLevels"] = "Key levels",
        ["volatility"] = "Volatility",
        ["notableEvents"] = "Notable events",
        ["confidence"] = "Confidence",
        ["liveDataAvailable"] = "Live data, as the model says",
        ["action"] = "Action",
        ["entryType"] = "Entry type",
        ["entryPrice"] = "Entry price",
        ["stopLoss"] = "Stop-loss",
        ["takeProfit"] = "Take-profit",
        ["riskFraction"] = "Risk fraction",
        ["verdict"] = "Verdict",
        ["rationale"] = "Rationale",
    };

    private static string Label(string field) => Labels.TryGetValue(field, out var label) ? label : field;

    /// <summary>The values of a field: each entry of a list is one, and anything else is one.</summary>
    private static IReadOnlyList<string> Values(string field, JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength() == 0 ? ["none"] : [.. value.EnumerateArray().Select(entry => Written(field, entry))]
            : [Written(field, value)];

    /// <summary>A value of a result, as a person reads it.</summary>
    private static string Written(string? field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "none",
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",

        // One of a fixed set of names is written in words: open_long is "open long". A text of
        // the model's own, such as its rationale, stays as it wrote it.
        JsonValueKind.String when field is not ("rationale" or "notableEvents") && Named(value.GetString()) is { } named => named,
        JsonValueKind.String => value.GetString() ?? string.Empty,

        // The risk fraction is a share of the agent's equity (FR-RSK-5), which a person reads as a percentage.
        JsonValueKind.Number when field == "riskFraction" && value.TryGetDecimal(out var fraction) =>
            $"{Number(fraction)}, which is {Number(fraction * 100m)} percent of the agent's equity",
        JsonValueKind.Number => value.TryGetDecimal(out var number) ? Number(number) : value.GetRawText(),

        JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(part => $"{Label(part.Name).ToLowerInvariant()} {Written(part.Name, part.Value)}")),
        JsonValueKind.Array => string.Join("; ", value.EnumerateArray().Select(entry => Written(field, entry))),
        _ => value.GetRawText(),
    };

    /// <summary>A name of a fixed set in words, or null for a text that is none: it has a space, or is long.</summary>
    private static string? Named(string? text) =>
        text is { Length: > 0 and <= 32 } && text.All(character => character is '_' || char.IsAsciiLetterLower(character))
            ? text.Replace('_', ' ')
            : null;

    private static string Resting(string what, decimal? price) => price is { } at ? $"{what} at {Number(at)}" : $"no {what[2..]} resting";

    /// <summary>A number with every digit that it has, and no zero after the last of them.</summary>
    private static string Number(decimal number) => number.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Whole(int number) => number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A field of the structured result of a role call, as a page shows it.</summary>
/// <param name="Name">The field, in words where the schema of a role defines it.</param>
/// <param name="Values">Its value, or the entries of a list.</param>
public sealed record ResultField(string Name, IReadOnlyList<string> Values);
