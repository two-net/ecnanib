using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Components.Agents;

/// <summary>
/// What an agent's chart shows (FR-UI-03): its timeframe's candles, the position's stop and targets, its trades'
/// entries and exits, and its decision cycles. Every record is from the agent's current environment and pair, so the
/// chart never mixes Demo and Live (FR-EXP-08).
/// </summary>
/// <param name="Closed">Closed candles, oldest first.</param>
/// <param name="Forming">The candle open now, from the live watch, or null before it arrives.</param>
/// <param name="Side">The open position's side, or null with nothing open.</param>
/// <param name="Stop">The open position's active stop, the tightest of its parts'.</param>
/// <param name="Targets">The open position's take-profits.</param>
/// <param name="Trades">Trades that were open at some point in the window.</param>
/// <param name="Cycles">Cycles whose candle closed in the window, oldest first.</param>
public sealed record ChartModel(
    Timeframe Timeframe,
    TradingEnvironment Environment,
    string Symbol,
    string QuoteAsset,
    IReadOnlyList<Candle> Closed,
    Candle? Forming,
    PositionSide? Side,
    decimal? Stop,
    IReadOnlyList<decimal> Targets,
    IReadOnlyList<TradeRecord> Trades,
    IReadOnlyList<CycleMarker> Cycles)
{
    /// <summary>How many closed candles the chart shows, with the forming one after them.</summary>
    public const int CandleCount = 120;

    /// <summary>The closed candles and the forming one, oldest first, each once.</summary>
    public IReadOnlyList<Candle> Candles =>
        Forming is { } forming && (Closed.Count == 0 || forming.OpenTime > Closed[^1].OpenTime) ? [.. Closed, forming] : Closed;
}

/// <summary>How a cycle ended, as its marker's shape says it. Its title says it in words.</summary>
public enum CycleMarkKind
{
    /// <summary>Still running.</summary>
    Deciding,

    /// <summary>An order was sent: a filled circle.</summary>
    Acted,

    /// <summary>The Strategist held: a hollow circle.</summary>
    Held,

    /// <summary>Waiting for the user's approval: a filled square.</summary>
    Waiting,

    /// <summary>The Risk Critic, the Risk Engine, a loss limit or the AI budget stopped it: a hollow square.</summary>
    Blocked,

    /// <summary>It failed, ran out of time or was interrupted: a cross.</summary>
    Failed,
}

/// <summary>
/// Where everything on an agent's chart goes, as percentages of the chart's box, so the SVG needs no viewBox: it fills
/// whatever width it gets, its lines and text keep their size, and nothing is stretched. Pure arithmetic, so a test can
/// check it without rendering. Times are in <see cref="DisplayTime.Zone"/>. See The chart in docs/architecture.md.
/// </summary>
public sealed class ChartLayout
{
    /// <summary>The band the candles and price lines use, from the top.</summary>
    public const double PlotTop = 5;

    public const double PlotBottom = 80;

    /// <summary>The row the cycles' markers sit on, below the candles.</summary>
    public const double CycleRow = 88;

    /// <summary>The time labels' baseline.</summary>
    public const double TimeRow = 98;

    public const double Left = 1;

    public const double Right = 99;

    /// <summary>A candle's body is this share of its slot, so neighbours never touch.</summary>
    private const double BodyShare = 0.6;

    /// <summary>A body this thin still shows, for a candle that opened and closed at one price.</summary>
    private const double MinimumBody = 0.2;

    private const int PriceTickCount = 4;

    private const int TimeTickCount = 4;

    private ChartLayout()
    {
    }

    public required IReadOnlyList<CandleShape> Candles { get; init; }

    public required IReadOnlyList<ChartTick> PriceTicks { get; init; }

    public required IReadOnlyList<ChartTick> TimeTicks { get; init; }

    public required ChartLine? Stop { get; init; }

    public required IReadOnlyList<ChartLine> Targets { get; init; }

    public required IReadOnlyList<TradeMark> Trades { get; init; }

    public required IReadOnlyList<CycleMark> Cycles { get; init; }

    /// <summary>What the chart shows, in a sentence, for a screen reader.</summary>
    public required string Summary { get; init; }

    /// <summary>The layout, or null when there are no candles to draw.</summary>
    public static ChartLayout? Of(ChartModel model, DisplayTime time)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(time);

        var candles = model.Candles;

        if (candles.Count == 0)
        {
            return null;
        }

        var length = TimeframeLength.Of(model.Timeframe);
        var first = candles[0].OpenTime;
        var end = candles[^1].OpenTime + length;
        var trades = model.Trades.Where(trade => trade.OpenedAt < end && (trade.ClosedAt is null || trade.ClosedAt >= first)).ToList();

        // Every price that's drawn, so a stop or an exit far from the candles is still on the chart.
        var prices = candles.SelectMany(candle => new[] { candle.Low, candle.High })
            .Concat(model.Stop is { } stop ? new[] { stop } : Array.Empty<decimal>())
            .Concat(model.Targets)
            .Concat(trades.Where(trade => trade.OpenedAt >= first).Select(trade => trade.EntryPrice))
            .Concat(trades.Where(trade => trade.ClosedAt >= first && trade.ExitPrice is not null).Select(trade => trade.ExitPrice!.Value))
            .ToList();
        var (low, high) = Padded(prices.Min(), prices.Max());
        var scale = new Scale(low, high);
        var slot = (Right - Left) / candles.Count;

        double X(int index) => Left + (slot * (index + 0.5));

        int? IndexOf(DateTimeOffset at)
        {
            if (at < first || at >= end)
            {
                return null;
            }

            var index = candles.Count - 1;

            while (index > 0 && candles[index].OpenTime > at)
            {
                index--;
            }

            return index;
        }

        var shapes = candles.Select((candle, index) =>
        {
            var top = scale.Y(Math.Max(candle.Open, candle.Close));
            var bottom = scale.Y(Math.Min(candle.Open, candle.Close));
            var height = Math.Max(MinimumBody, bottom - top);
            var forming = model.Forming is { } open && open.OpenTime == candle.OpenTime;

            return new CandleShape(
                candle.OpenTime,
                Percent(X(index)),
                Percent(X(index) - (slot * BodyShare / 2)),
                Percent(slot * BodyShare),
                Percent(Math.Min(top, PlotBottom - height)),
                Percent(height),
                Percent(scale.Y(candle.High)),
                Percent(scale.Y(candle.Low)),
                candle.Close >= candle.Open,
                forming,
                CandleTitle(candle, model, time, forming));
        }).ToList();

        var marks = new List<TradeMark>();

        foreach (var trade in trades)
        {
            if (IndexOf(trade.OpenedAt) is { } entry)
            {
                marks.Add(new TradeMark(
                    trade.Id,
                    trade.Side == PositionSide.Long ? TradeMarkKind.EntryLong : TradeMarkKind.EntryShort,
                    Percent(X(entry)),
                    Percent(scale.Y(trade.EntryPrice)),
                    Format(Strings.ChartEntryTitle, time.Format(trade.OpenedAt), AgentText.SideOf(trade.Side), AgentText.Number(trade.Quantity), AgentText.AmountOf(trade.EntryPrice, model.QuoteAsset))));
            }

            if (trade is { ClosedAt: { } closedAt, ExitPrice: { } exitPrice } && IndexOf(closedAt) is { } exit)
            {
                marks.Add(new TradeMark(
                    trade.Id,
                    TradeMarkKind.Exit,
                    Percent(X(exit)),
                    Percent(scale.Y(exitPrice)),
                    Format(
                        Strings.ChartExitTitle,
                        time.Format(closedAt),
                        trade.ExitReason is { } reason ? AgentText.ExitOf(reason) : Strings.ChartExitUnknown,
                        AgentText.AmountOf(exitPrice, model.QuoteAsset))));
            }
        }

        var cycles = new List<CycleMark>();

        foreach (var cycle in model.Cycles)
        {
            if (IndexOf(cycle.CandleClose - length) is { } index && candles[index].OpenTime == cycle.CandleClose - length)
            {
                cycles.Add(new CycleMark(cycle.CycleId, KindOf(cycle.Outcome), Percent(X(index)), CycleTitle(cycle, time)));
            }
        }

        return new ChartLayout
        {
            Candles = shapes,
            PriceTicks = [.. Ticks(low, high).Select(price => new ChartTick(Percent(scale.Y(price)), AgentText.Number(price)))],
            TimeTicks = [.. TimeIndexes(candles.Count).Select(index => new ChartTick(Percent(X(index)), AxisTime(candles[index].OpenTime, model.Timeframe, time)))],
            Stop = model.Stop is { } stopPrice
                ? new ChartLine(Percent(scale.Y(stopPrice)), Format(Strings.ChartStopLabel, AgentText.Number(stopPrice)))
                : null,
            Targets = [.. model.Targets.Select(target => new ChartLine(Percent(scale.Y(target)), Format(Strings.ChartTargetLabel, AgentText.Number(target))))],
            Trades = marks,
            Cycles = cycles,
            Summary = Format(
                Strings.ChartSummary,
                model.Symbol,
                AgentText.TimeframeOf(model.Timeframe),
                time.Format(first),
                time.Format(candles[^1].OpenTime),
                AgentText.AmountOf(candles[^1].Close, model.QuoteAsset)),
        };
    }

    /// <summary>The shape a cycle's outcome takes.</summary>
    public static CycleMarkKind KindOf(CycleOutcome? outcome) => outcome switch
    {
        null => CycleMarkKind.Deciding,
        CycleOutcome.Executed => CycleMarkKind.Acted,
        CycleOutcome.Hold => CycleMarkKind.Held,
        CycleOutcome.AwaitingApproval or CycleOutcome.ReadyToExecute => CycleMarkKind.Waiting,
        CycleOutcome.RejectedByCritic or CycleOutcome.BlockedByRiskEngine or CycleOutcome.PausedForLossLimit or CycleOutcome.OverBudget => CycleMarkKind.Blocked,
        CycleOutcome.Failed or CycleOutcome.Interrupted or CycleOutcome.PastDeadline or CycleOutcome.ExecutionFailed => CycleMarkKind.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Give the outcome a marker."),
    };

    /// <summary>
    /// Steps of 1, 2 or 5 times a power of ten, about <see cref="PriceTickCount"/> of them, inside the range, so the
    /// labels are round numbers.
    /// </summary>
    public static IReadOnlyList<decimal> Ticks(decimal low, decimal high)
    {
        if (high <= low)
        {
            return [low];
        }

        var raw = (double)(high - low) / PriceTickCount;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = (decimal)((raw / magnitude) switch
        {
            < 1.5 => 1,
            < 3.5 => 2,
            < 7.5 => 5,
            _ => 10,
        } * magnitude);
        var ticks = new List<decimal>();

        for (var tick = Math.Ceiling(low / step) * step; tick <= high; tick += step)
        {
            ticks.Add(tick);
        }

        return ticks;
    }

    private static (decimal Low, decimal High) Padded(decimal low, decimal high)
    {
        var pad = high > low ? (high - low) * 0.05m : Math.Max(Math.Abs(high) * 0.01m, 0.00000001m);

        return (low - pad, high + pad);
    }

    private static IEnumerable<int> TimeIndexes(int count) =>
        Enumerable.Range(0, TimeTickCount).Select(tick => (int)Math.Round((count - 1) * (tick + 0.5) / TimeTickCount)).Distinct();

    /// <summary>The time of day for the shorter timeframes, and the date for the longer, in the user's zone.</summary>
    private static string AxisTime(DateTimeOffset openTime, Timeframe timeframe, DisplayTime time) => Format(
        timeframe is Timeframe.FifteenMinutes or Timeframe.OneHour ? Strings.ChartAxisTime : Strings.ChartAxisDate,
        TimeZoneInfo.ConvertTime(openTime, time.Zone));

    private static string CandleTitle(Candle candle, ChartModel model, DisplayTime time, bool forming) => Format(
        forming ? Strings.ChartFormingTitle : Strings.ChartCandleTitle,
        time.Format(candle.OpenTime),
        AgentText.Number(candle.Open),
        AgentText.Number(candle.High),
        AgentText.Number(candle.Low),
        AgentText.Number(candle.Close),
        model.QuoteAsset);

    private static string CycleTitle(CycleMarker cycle, DisplayTime time) => cycle switch
    {
        { Outcome: null } => Format(Strings.ChartCycleDeciding, time.Format(cycle.CandleClose)),
        // A hold says itself in its outcome, so only another proposal is added.
        { Outcome: { } outcome, Action: { } action } when action != TradeAction.Hold =>
            Format(Strings.ChartCycleTitleAction, time.Format(cycle.CandleClose), AgentText.OutcomeOf(outcome), AgentText.ActionOf(action)),
        { Outcome: { } outcome } => Format(Strings.ChartCycleTitle, time.Format(cycle.CandleClose), AgentText.OutcomeOf(outcome)),
    };

    private static string Format(string format, params object?[] values) => string.Format(CultureInfo.CurrentCulture, format, values);

    /// <summary>An SVG length, always with a point for decimals, whatever the culture.</summary>
    private static string Percent(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "%";

    /// <summary>Prices to the chart's band, high at the top.</summary>
    private sealed class Scale(decimal low, decimal high)
    {
        public double Y(decimal price) =>
            PlotTop + ((double)((high - price) / (high - low)) * (PlotBottom - PlotTop));
    }
}

/// <param name="X">The centre, for the wick.</param>
/// <param name="Up">It closed at or above its open, which draws it hollow, so the shape says so as well as the color.</param>
/// <param name="Forming">The candle open now, which changes until it closes.</param>
public sealed record CandleShape(
    DateTimeOffset OpenTime,
    string X,
    string BodyX,
    string BodyWidth,
    string BodyY,
    string BodyHeight,
    string WickTop,
    string WickBottom,
    bool Up,
    bool Forming,
    string Title);

/// <param name="At">Where it goes along its axis.</param>
public sealed record ChartTick(string At, string Label);

/// <summary>A horizontal line across the chart, with its label.</summary>
public sealed record ChartLine(string Y, string Label);

public enum TradeMarkKind
{
    /// <summary>A triangle pointing up.</summary>
    EntryLong,

    /// <summary>A triangle pointing down.</summary>
    EntryShort,

    /// <summary>A square.</summary>
    Exit,
}

public sealed record TradeMark(Guid TradeId, TradeMarkKind Kind, string X, string Y, string Title);

public sealed record CycleMark(Guid CycleId, CycleMarkKind Kind, string X, string Title);
