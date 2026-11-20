using System.Globalization;
using Ecnanib.Experience;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.Extensions.Primitives;

namespace Ecnanib.WebUi.Pages.Experience;

/// <summary>
/// What the experience screens say, in one place: the statements that every one of them makes
/// (FR-EXP-11), how a track record is written (FR-EXP-2), how its filter is named in an address
/// and read from one, which mode a screen shows and why (FR-EXP-9), and how a strength score is
/// worked out and written, with its formula (FR-EXP-3, FR-EXP-4). See docs/experience.md.
/// </summary>
public static class ExperiencePages
{
    /// <summary>The name of the experience screen, in its heading and in the bar at the top.</summary>
    public const string Title = "Experience";

    /// <summary>What the experience screen says first: what a track record is, and what it is measured in.</summary>
    public const string Explained =
        "This is how your agents' trades came out for each AI provider and each model. "
        + "Every figure is in R: the amount that was risked on a trade, which is the distance from its entry to its stop-loss, times its quantity. "
        + "A trade that made twice what it risked is +2 R, and one that was stopped out lost about 1 R.";

    /// <summary>The heading of the statements that every experience screen makes (FR-EXP-11).</summary>
    public const string StatementsHeading = "What these figures cannot tell";

    /// <summary>That past results do not predict future results (FR-EXP-11).</summary>
    public const string PastResults = "Past results do not predict future results.";

    /// <summary>That Demo results differ from Live (FR-EXP-11).</summary>
    public const string DemoDiffers =
        "Demo results differ from Live results. Demo is Binance's test environment, with no real funds: an order is not filled there as it is on the real exchange.";

    /// <summary>That Demo and Live are never added up (FR-EXP-9, FR-BIN-11).</summary>
    public const string NeverAddedUp = "Results in Demo and results in Live are never added up.";

    /// <summary>What is written in place of a measure that a record cannot say.</summary>
    public const string NotAvailable = "Not available";

    /// <summary>What is written for the profit factor of a record that has no losing trade: there is nothing to divide by.</summary>
    public const string NoLosingTrade = "No losing trade";

    /// <summary>What is written in the notes of a record that has none.</summary>
    public const string NoNote = "None";

    /// <summary>The mark of a record that counts fewer trades than the minimum sample (FR-EXP-11).</summary>
    public const string SmallSample = "Small sample.";

    /// <summary>What a record follows, which says why a trade can leave it.</summary>
    public const string FollowsPositions =
        "A track record is worked out from the positions that are stored. When you delete an agent, its positions are deleted with it, and its trades leave the record.";

    /// <summary>The name of the choice of a filter in an address: the role.</summary>
    public const string RoleKey = "role";

    /// <summary>The name of the choice of a filter in an address: the market.</summary>
    public const string MarketKey = "market";

    /// <summary>The name of the choice of a filter in an address: the timeframe.</summary>
    public const string TimeframeKey = "timeframe";

    /// <summary>The name of the choice of a filter in an address: the mode.</summary>
    public const string ModeKey = "mode";

    /// <summary>The name of the choice of a filter in an address: the period.</summary>
    public const string PeriodKey = "period";

    /// <summary>That small samples are unreliable, with the sample size that a record is marked below (FR-EXP-11).</summary>
    public static string SmallSamples(int minSamples) =>
        $"Small samples are unreliable. A record of fewer than {Count(minSamples)} closed trades is marked as a small sample: its figures can change a great deal with the next few trades.";

    /// <summary>A role as an address names it: <c>market-analyst</c>.</summary>
    public static string Token(Role role) => role switch
    {
        Role.MarketAnalyst => "market-analyst",
        Role.SentimentAnalyst => "sentiment-analyst",
        Role.Strategist => "strategist",
        Role.RiskManager => "risk-manager",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no name in an address."),
    };

    /// <summary>A market as an address names it: <c>spot</c> or <c>futures</c>.</summary>
    public static string Token(Market market) => market switch
    {
        Market.Spot => "spot",
        Market.Futures => "futures",
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "The market has no name in an address."),
    };

    /// <summary>A mode as an address names it: <c>demo</c> or <c>live</c>.</summary>
    public static string Token(TradingMode mode) => mode switch
    {
        TradingMode.Demo => "demo",
        TradingMode.Live => "live",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode has no name in an address."),
    };

    /// <summary>A period as an address names it: its days, and nothing for the one that has no start.</summary>
    public static string Token(TrackPeriod period) =>
        period.Days() is { } days ? days.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A period as a choice names it.</summary>
    public static string Describe(TrackPeriod period) =>
        period.Days() is { } days ? $"Last {Count(days)} days" : "All time";

    /// <summary>A role as users read it.</summary>
    public static string NameOf(Role role) => AgentPages.Describe(role).Name;

    /// <summary>The role of a record of a model: the role, or that it is any of them.</summary>
    public static string NameOf(Role? role) => role is { } named ? NameOf(named) : "Any role";

    /// <summary>
    /// The filter that an address names, or null when it names none: a choice that is none of
    /// the choices there are, or one that is made twice. A choice that is absent, or empty, is
    /// not made. So a page never shows something else than what its address asks for: it is
    /// not found instead.
    /// </summary>
    /// <param name="query">What the address says after its path.</param>
    public static TrackRecordFilter? FilterOf(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Each choice is matched as it is written: `live`, and neither `Live` nor `1`.
        return Chosen(query[RoleKey], Enum.GetValues<Role>(), Token, out var role)
            && Chosen(query[MarketKey], Enum.GetValues<Market>(), Token, out var market)
            && Chosen(query[TimeframeKey], Timeframes.All, Timeframes.Code, out var timeframe)
            && Chosen(query[ModeKey], Enum.GetValues<TradingMode>(), Token, out var mode)
            && Chosen(query[PeriodKey], TrackPeriods.All.Where(period => period != TrackPeriod.AllTime), Token, out var period)
                ? new TrackRecordFilter(role, market, timeframe, mode, period ?? TrackPeriod.AllTime)
                : null;
    }

    /// <summary>The path of the experience screen with a filter, which a link there keeps.</summary>
    public static string Path(TrackRecordFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return WebUiModule.ExperiencePath + QueryOf(filter, withRole: true);
    }

    /// <summary>
    /// The path of the page of a model's experience record with a filter. The page says every
    /// role, so the role of the filter is not in it.
    /// </summary>
    public static string ModelPath(Guid modelId, TrackRecordFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return WebUiModule.ModelExperiencePath(modelId) + QueryOf(filter, withRole: false);
    }

    /// <summary>
    /// Which mode a screen shows, and why (FR-EXP-9): the one that was asked for, or Live where
    /// it has enough closed trades, and otherwise Demo.
    /// </summary>
    public static string ModeExplained(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var enough = $"the {Count(scope.MinSamples)} that are enough to go by";

        return (scope.ModeChosen, scope.Mode) switch
        {
            (false, _) => $"These are your results in {scope.Mode}, which you chose.",
            (true, TradingMode.Live) =>
                $"These are your results in Live: with {Trades(scope.LiveTrades)}, Live has {enough}.",
            (true, _) =>
                $"These are your results in Demo: with {Trades(scope.LiveTrades)}, Live has fewer than {enough}.",
        };
    }

    /// <summary>The mode that a screen does not show, which it leads to.</summary>
    public static TradingMode OtherThan(TradingMode mode) => mode == TradingMode.Live ? TradingMode.Demo : TradingMode.Live;

    /// <summary>The link to the results of the mode that a screen does not show, with how many trades it would count.</summary>
    public static string OtherModeLink(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var other = OtherThan(scope.Mode);

        return $"Show your results in {other}, with {Trades(other == TradingMode.Live ? scope.LiveTrades : scope.DemoTrades)}";
    }

    /// <summary>Which trades a screen counts by when they were closed.</summary>
    public static string PeriodExplained(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.ClosedFrom is { } from
            ? $"Counted are the trades that were closed since {PageTimes.At(from)}."
            : "Counted are the trades of all time.";
    }

    /// <summary>For whom a trade counts on the experience screen, which says why its rows do not add up.</summary>
    public static string CountsFor(Role? role) => role is { } chosen
        ? $"A trade counts for the model that held the role of {NameOf(chosen)} when its position was opened, and for the provider of that model."
        : "A trade counts once for every model of the team that decided it, and once for every provider of that team. "
            + "So the rows do not add up to the number of your trades, and a record says how the teams with a model did, and not what the model did alone.";

    /// <summary>For which trades a model has a record on its page.</summary>
    public const string CountsForModel =
        "A trade counts for this model when the model held a role of the team as the position was opened. "
        + "A record says how the teams with the model did, and not what the model did alone.";

    /// <summary>What a screen says when it counts no trade.</summary>
    public static string NoTrades(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return $"No closed trade is counted here in {scope.Mode}.";
    }

    /// <summary>What the page of a model says when the model has no counted trade of the user.</summary>
    public static string NoTradesOfModel(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return $"Nothing is recorded here about how this model has traded for you in {scope.Mode}. Its track record is shown once a trade of a team with it is closed.";
    }

    /// <summary>
    /// The closed trades that a screen leaves out because their result is not recorded yet, or
    /// null when there are none.
    /// </summary>
    public static string? Awaiting(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.AwaitingResult switch
        {
            0 => null,
            1 => "1 closed trade is not counted yet: its result is recorded some minutes after the close.",
            var count => $"{Count(count)} closed trades are not counted yet: their results are recorded some minutes after the close.",
        };
    }

    /// <summary>
    /// The closed trades that a screen leaves out because their result says no R, or null when
    /// there are none.
    /// </summary>
    public static string? WithoutR(TrackRecordScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.WithoutR switch
        {
            0 => null,
            1 => "1 closed trade is not counted: what was risked on it is not known, so its result has no R.",
            var count => $"{Count(count)} closed trades are not counted: what was risked on them is not known, so their results have no R.",
        };
    }

    /// <summary>A number of closed trades, in words: <c>1 closed trade</c>, <c>12 closed trades</c>.</summary>
    public static string Trades(int count) => count == 1 ? "1 closed trade" : $"{Count(count)} closed trades";

    /// <summary>The wins among the counted trades, in percent with one decimal place at most: <c>62.5 percent</c>.</summary>
    public static string WinRate(TrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.WinRate is { } rate ? $"{Rounded(rate * 100m, 1).ToString("0.#", CultureInfo.InvariantCulture)} percent" : NotAvailable;
    }

    /// <summary>
    /// A result in R with two decimal places at most, and with its sign, so that a profit is told
    /// from a loss without a colour (FR-THM-5): <c>+1.25 R</c>, <c>-0.5 R</c>, <c>0 R</c>.
    /// </summary>
    public static string InR(decimal? result) =>
        result is { } known ? $"{Rounded(known, 2).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)} R" : NotAvailable;

    /// <summary>The profit factor of a record with two decimal places at most, or why it has none.</summary>
    public static string ProfitFactor(TrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record switch
        {
            { ProfitFactor: { } factor } => Rounded(factor, 2).ToString("0.##", CultureInfo.InvariantCulture),
            { Trades: > 0, Losses: 0, TotalR: not null } => NoLosingTrade,
            _ => NotAvailable,
        };
    }

    /// <summary>The largest drawdown of a record in R, as the fall that it is: <c>3.2 R</c>.</summary>
    public static string Drawdown(TrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.LargestDrawdownR is { } fall
            ? $"{Rounded(fall, 2).ToString("0.##", CultureInfo.InvariantCulture)} R"
            : NotAvailable;
    }

    /// <summary>
    /// How long a trade was held on average, in its two largest units: <c>2 days 4 hours</c>,
    /// <c>3 hours 20 minutes</c>, <c>45 minutes</c>, <c>30 seconds</c>.
    /// </summary>
    public static string Held(TimeSpan? time)
    {
        if (time is not { } held)
        {
            return NotAvailable;
        }

        if (held < TimeSpan.FromMinutes(1))
        {
            return Unit((long)Math.Round(held.TotalSeconds, MidpointRounding.AwayFromZero), "second");
        }

        // To the minute, which is as exact as an average of trades is read.
        var minutes = (long)Math.Round(held.TotalMinutes, MidpointRounding.AwayFromZero);
        var (days, hours, left) = (minutes / 1440, minutes % 1440 / 60, minutes % 60);

        return (days, hours, left) switch
        {
            ( > 0, 0, _) => Unit(days, "day"),
            ( > 0, _, _) => $"{Unit(days, "day")} {Unit(hours, "hour")}",
            (_, > 0, 0) => Unit(hours, "hour"),
            (_, > 0, _) => $"{Unit(hours, "hour")} {Unit(left, "minute")}",
            _ => Unit(left, "minute"),
        };
    }

    /// <summary>What is to be noted about a record: that it is a small sample, and how many of its results are estimates.</summary>
    public static string Notes(TrackRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var estimates = record.Estimates switch
        {
            0 => null,
            1 => "1 result is an estimate.",
            var count => $"{Count(count)} results are estimates.",
        };

        return string.Join(' ', new[] { record.SmallSample ? SmallSample : null, estimates }.OfType<string>()) is { Length: > 0 } notes
            ? notes
            : NoNote;
    }

    /// <summary>What an estimate is, which a screen says under a table that may mark one.</summary>
    public const string EstimatesExplained =
        "A result is an estimate when not all of it is what Binance said: a position that was liquidated or closed at Binance by hand, or a fee that could not be valued. Such a trade is counted all the same.";

    /// <summary>The heading of the strength scores on an experience screen.</summary>
    public const string StrengthHeading = "Strength by role";

    /// <summary>What is written for a slice that has fewer samples than the minimum (FR-EXP-4).</summary>
    public const string NotEnoughData = "Not enough data";

    /// <summary>How an analyst is measured (FR-EXP-3).</summary>
    public const string AnalystMeasure =
        "Market Analyst and Sentiment Analyst: the share of the calls that were not neutral whose direction matched the price change "
        + "over the next 4 closed candles, minus 50 percent. It is written in percentage points: +10 is right 60 times in 100. "
        + "A sample is a call that was scored.";

    /// <summary>How the Strategist is measured (FR-EXP-3).</summary>
    public const string StrategistMeasure =
        "Strategist: the average R of the plans that were executed. A sample is a closed trade.";

    /// <summary>How the Risk Manager is measured (FR-EXP-3).</summary>
    public const string RiskManagerMeasure =
        "Risk Manager: the average R of the plans that it approved, minus the average R that the plans which it vetoed would have produced. "
        + "A sample is a plan that it reviewed and that has a result: an approved one whose trade is closed, or a vetoed one whose shadow trade has ended. "
        + "The measure is said once there is at least one of each.";

    /// <summary>What a shadow trade is, and that it places no order (FR-EXP-5).</summary>
    public const string ShadowTradesExplained =
        "What a vetoed plan would have produced is worked out as a shadow trade: against the candles that closed after it, with its own entry, "
        + "stop-loss, and take-profit and with standard fees, and with the stop-loss taken to be hit first when both fall inside one candle. "
        + "A shadow trade never places an order.";

    /// <summary>What the strength scores follow, which says why a sample can leave them.</summary>
    public const string StrengthsFollow =
        "A strength score is worked out from the cycles and the positions that are stored. When you delete an agent, they are deleted with it, "
        + "and its calls, its trades, and its shadow trades leave the scores.";

    /// <summary>What a strength score is, and of which mode the ones on a screen are.</summary>
    public static string StrengthExplained(TradingMode mode) =>
        $"A strength score says how well a model did one role, on one market and one timeframe, in {mode}. "
        + "It is the measure of the role, damped toward zero for a small sample, so that a few lucky results do not look like strength.";

    /// <summary>The formula of the strength score, as a screen shows it, with the damping of the configuration file (FR-EXP-4).</summary>
    public static string StrengthFormula(int damping) =>
        $"Strength score = measure × n ÷ (n + {Count(damping)}), where n is the sample size.";

    /// <summary>From how many samples on a slice has a score (FR-EXP-4).</summary>
    public static string EnoughExplained(int minSamples) =>
        $"A slice of fewer than {Count(minSamples)} samples says \"{NotEnoughData}\", and has no score.";

    /// <summary>What a screen says when no slice has a sample.</summary>
    public static string NoStrengths(TradingMode mode) =>
        $"Nothing is measured here in {mode} yet: no call of an analyst is scored, no plan is executed, and no vetoed plan has a result.";

    /// <summary>
    /// A measure or a score of an analyst, which is a share above or below a half, in percentage
    /// points with one decimal place at most, and with its sign: <c>+12.5 percentage points</c>.
    /// </summary>
    public static string InPoints(decimal? fraction) =>
        fraction is { } known
            ? $"{Rounded(known * 100m, 1).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} percentage points"
            : NotAvailable;

    /// <summary>The measure of a slice, in the unit of its role: percentage points for an analyst, and R for the Strategist and the Risk Manager.</summary>
    public static string Measure(RoleStrength slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return InUnitOf(slice.Role, slice.Measure.Value);
    }

    /// <summary>The strength score of a slice, in the unit of its measure.</summary>
    public static string Score(RoleStrength slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return InUnitOf(slice.Role, slice.Score);
    }

    /// <summary>The sample size of a slice, and for a Risk Manager what it is made of: <c>6 (4 approved, 2 vetoed)</c>.</summary>
    public static string Samples(RoleStrength slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return slice.Measure.Reviewed is { } reviewed
            ? $"{Count(slice.Measure.Samples)} ({Count(reviewed.Approved)} approved, {Count(reviewed.Vetoed)} vetoed)"
            : Count(slice.Measure.Samples);
    }

    /// <summary>The calls of analysts that a screen leaves out because they are not scored yet, or null when there are none.</summary>
    public static string? CallsAwaiting(RoleStrengthView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.CallsAwaiting switch
        {
            0 => null,
            1 => "1 call of an analyst is not counted yet: it is scored once 4 candles have closed after it.",
            var count => $"{Count(count)} calls of analysts are not counted yet: each is scored once 4 candles have closed after it.",
        };
    }

    /// <summary>The calls of analysts that a screen leaves out because they could not be scored, or null when there are none.</summary>
    public static string? CallsWithoutCandles(RoleStrengthView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.CallsWithoutCandles switch
        {
            0 => null,
            1 => "1 call of an analyst is not counted: the candles after it could no longer be had from Binance.",
            var count => $"{Count(count)} calls of analysts are not counted: the candles after them could no longer be had from Binance.",
        };
    }

    /// <summary>The shadow trades that a screen leaves out because they have not ended yet, or null when there are none.</summary>
    public static string? ShadowTradesOpen(RoleStrengthView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.ShadowTradesOpen switch
        {
            0 => null,
            1 => "1 vetoed plan is not counted yet: its shadow trade has reached neither its stop-loss nor its take-profit.",
            var count => $"{Count(count)} vetoed plans are not counted yet: their shadow trades have reached neither their stop-loss nor their take-profit.",
        };
    }

    /// <summary>The shadow trades that a screen leaves out because they ended with no result, or null when there are none.</summary>
    public static string? ShadowTradesWithoutResult(RoleStrengthView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        const string why =
            "its entry was not reached, its stop-loss or take-profit was on the wrong side of its entry, or the candles after it could no longer be had from Binance";

        return view.ShadowTradesWithoutResult switch
        {
            0 => null,
            1 => $"1 vetoed plan is not counted, because its shadow trade has no result: {why}.",
            var count => $"{Count(count)} vetoed plans are not counted, because their shadow trades have no result: for each, {why}.",
        };
    }

    /// <summary>
    /// Whether a choice of an address is one that can be made: it is absent or empty, and so
    /// not made, or it names one of the choices as that one is written. A choice that is made
    /// twice names none.
    /// </summary>
    private static bool Chosen<TChoice>(StringValues said, IEnumerable<TChoice> choices, Func<TChoice, string> token, out TChoice? chosen)
        where TChoice : struct
    {
        chosen = null;

        if (said.Count > 1)
        {
            return false;
        }

        if (said.Count == 0 || string.IsNullOrEmpty(said[0]))
        {
            return true;
        }

        chosen = choices.Cast<TChoice?>().SingleOrDefault(choice => string.Equals(token(choice!.Value), said[0], StringComparison.Ordinal));

        return chosen is not null;
    }

    /// <summary>The choices of a filter as an address says them after its path, or nothing for a filter that chooses nothing.</summary>
    private static string QueryOf(TrackRecordFilter filter, bool withRole)
    {
        var choices = new (string Key, string? Value)[]
        {
            (RoleKey, withRole && filter.Role is { } role ? Token(role) : null),
            (MarketKey, filter.Market is { } market ? Token(market) : null),
            (TimeframeKey, filter.Timeframe?.Code()),
            (ModeKey, filter.Mode is { } mode ? Token(mode) : null),
            (PeriodKey, filter.Period == TrackPeriod.AllTime ? null : Token(filter.Period)),
        };

        var made = choices.Where(choice => choice.Value is not null).Select(choice => $"{choice.Key}={Uri.EscapeDataString(choice.Value!)}").ToList();

        return made.Count == 0 ? string.Empty : "?" + string.Join('&', made);
    }

    /// <summary>A measure or a score in the unit of its role.</summary>
    private static string InUnitOf(Role role, decimal? value) =>
        role is Role.MarketAnalyst or Role.SentimentAnalyst ? InPoints(value) : InR(value);

    private static decimal Rounded(decimal number, int places) => Math.Round(number, places, MidpointRounding.AwayFromZero);

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string Unit(long count, string unit) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? string.Empty : "s")}";
}
