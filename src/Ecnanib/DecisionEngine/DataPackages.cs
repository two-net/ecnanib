using System.Text;
using Ecnanib.Agents;
using Ecnanib.ExchangeGateway;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Builds the data package of a cycle (FR-CYC-2): the candles of the agent's timeframe, which the
/// cycle was handed, the candles of the higher timeframes and what Futures adds, which it asks
/// the gateway for, the indicators, which it computes, and what the agent holds, which whatever
/// keeps positions and orders says. See "The data package" in docs/cycles.md.
/// </summary>
internal sealed class DataPackages(
    IExchangeMarketData market,
    IEnumerable<IAgentHoldings> holdings,
    TradingSettings settings,
    TimeProvider clock,
    ILogger<DataPackages> logger) : IDataPackages
{
    // What keeps positions and orders, when something does. The one that was registered last
    // answers, as a service is found.
    private readonly IAgentHoldings? _holdings = holdings.LastOrDefault();

    public async Task<DataPackage> BuildAsync(CycleStart cycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var agent = cycle.Agent;
        var own = cycle.Candles.Candles;

        if (own.Count == 0 || own[^1].ClosedAt != cycle.ClosedAt)
        {
            throw new ArgumentException(
                "A cycle is handed the closed candles of its agent's timeframe through its close (FR-CYC-1). " +
                "No data package is built from others.",
                nameof(cycle));
        }

        var began = clock.GetTimestamp();

        // Everything is asked for at once. The gateway shares each read among the agents that
        // need it, so the cycles of one symbol cause one read of each (FR-BIN-3).
        var intervals = TimeframeContext.Of(agent.Timeframe);

        Task<ExchangeCandles>[] context =
        [
            .. intervals.Select(interval => market.GetCandlesAsync(agent.Market, agent.Mode, agent.Symbol, interval, cancellationToken)),
        ];

        // The mark price, the funding, and the open interest are of Futures alone.
        var onFutures = agent.Market == Market.Futures;
        var markPrice = onFutures ? market.GetMarkPriceAsync(agent.Mode, agent.Symbol, cancellationToken) : null;
        var openInterest = onFutures ? market.GetOpenInterestAsync(agent.Mode, agent.Symbol, cancellationToken) : null;

        var held = _holdings is null
            ? Task.FromResult(AgentHoldings.NothingYet(agent))
            : _holdings.OfAsync(agent, cancellationToken);

        await Task.WhenAll([.. context, held, .. onFutures ? [markPrice!, openInterest!] : Array.Empty<Task>()]);

        var higher = new List<CandleSeries>(intervals.Count);

        for (var index = 0; index < intervals.Count; index++)
        {
            var read = await context[index];

            if (read.Outcome != ExchangeCallOutcome.Succeeded)
            {
                throw new DataPackageException($"the {TimeframeContext.Code(intervals[index])} candles", read.Outcome);
            }

            // Only what had closed by the close of the cycle: a package holds nothing later.
            higher.Add(SeriesOf(
                intervals[index],
                [.. read.Candles.Where(candle => candle.ClosedAt <= cycle.ClosedAt)],
                settings.DataPackage.ContextCandles));
        }

        var futures = onFutures ? FuturesOf(await markPrice!, await openInterest!) : null;
        var holds = await held;

        var candles = SeriesOf(CandleIntervals.Of(agent.Timeframe), own, settings.DataPackage.Candles);
        var package = new DataPackage(
            cycle.ClosedAt,
            AgentContext.Of(agent, holds.Equity, holds.Position),
            holds.Orders,
            candles,
            higher,
            futures,
            DataPackageText.Of(agent, candles, higher, holds.Orders, futures));

        logger.LogInformation(
            "The data package of agent {AgentId} for the close at {ClosedAt} was built in {DurationMs} ms: {CandleCount} candles of " +
            "{Timeframe}, {ContextCandleCount} of {ContextTimeframes}, and {OrderCount} open orders",
            agent.Id,
            cycle.ClosedAt,
            Math.Round(clock.GetElapsedTime(began).TotalMilliseconds, 1),
            candles.Candles.Count,
            agent.Timeframe.Code(),
            higher.Sum(series => series.Candles.Count),
            string.Join(", ", intervals.Select(TimeframeContext.Code)),
            holds.Orders.Count);

        return package;
    }

    /// <summary>
    /// The most recent candles of a series, with the indicators of the series. The indicators
    /// are computed from every closed candle that the exchange gave, and not only from those
    /// that the package holds: more candles make an average more exact.
    /// </summary>
    private static CandleSeries SeriesOf(CandleInterval interval, IReadOnlyList<ExchangeCandle> closed, int most) =>
        new(
            interval,
            closed.Count <= most ? closed : [.. closed.Skip(closed.Count - most)],
            closed.Count,
            Indicators.Of(closed));

    private static FuturesData FuturesOf(ExchangeMarkPrice markPrice, ExchangeOpenInterest openInterest)
    {
        if (markPrice.Outcome != ExchangeCallOutcome.Succeeded)
        {
            throw new DataPackageException("the mark price and the funding", markPrice.Outcome);
        }

        if (openInterest.Outcome != ExchangeCallOutcome.Succeeded)
        {
            throw new DataPackageException("the open interest", openInterest.Outcome);
        }

        // A reading that succeeded has every one of them.
        return new FuturesData(
            markPrice.MarkPrice!.Value, markPrice.FundingRate!.Value, markPrice.NextFundingAt!.Value, openInterest.OpenInterest!.Value);
    }
}

/// <summary>
/// Writes a data package as a role reads it: the candles, the indicators, the open orders, and
/// what Futures adds. A line ends with a line feed on every system, and a number is written the
/// same in every country, so the same package gives the same text.
/// </summary>
/// <remarks>
/// No line begins as a heading of a prompt does. The equity, the position, and the limits of the
/// agent are not written: the prompt states them as the context of the agent.
/// </remarks>
internal static class DataPackageText
{
    /// <summary>What an indicator says when its series has too few candles for it.</summary>
    private const string NotAvailable = "not available";

    // An average is worked out, and has more digits than mean anything.
    private const int SignificantDigits = 8;

    public static string Of(
        Agent agent,
        CandleSeries candles,
        IReadOnlyList<CandleSeries> context,
        IReadOnlyList<OpenOrder> orders,
        FuturesData? futures)
    {
        var text = new StringBuilder();

        Series(text, "Closed", candles, explained: true);

        foreach (var series in context)
        {
            text.Append('\n');
            Series(text, "Context, closed", series, explained: false);
        }

        text.Append('\n');
        Orders(text, orders);

        if (futures is not null)
        {
            text.Append('\n');
            text.Append("Futures: mark price ").Append(Written.Number(futures.MarkPrice))
                .Append(", funding rate ").Append(Written.Number(futures.FundingRate * 100m)).Append('%')
                .Append(", next funding ").Append(Written.Time(futures.NextFundingAt))
                .Append(", open interest ").Append(Written.Number(futures.OpenInterest));

            if (BaseAssetOf(agent) is { } asset)
            {
                text.Append(' ').Append(asset);
            }

            text.Append('\n');
        }

        // No line feed after the last line: the prompt separates its parts itself.
        return text.ToString().TrimEnd('\n');
    }

    private static void Series(StringBuilder text, string what, CandleSeries series, bool explained)
    {
        var code = TimeframeContext.Code(series.Interval);

        if (series.Candles.Count == 0)
        {
            // A symbol that is new to the exchange has no closed week yet.
            text.Append(what).Append(' ').Append(code).Append(" candles: none, the exchange has closed no candle of that length yet\n");

            return;
        }

        text.Append(what).Append(' ').Append(code).Append(" candles: ").Append(Written.Number(series.Candles.Count))
            .Append(", oldest first, through ").Append(Written.Time(series.Candles[^1].ClosedAt)).Append('\n');

        if (explained)
        {
            text.Append("Each line: close time (UTC), open, high, low, close, volume\n");
        }

        foreach (var candle in series.Candles)
        {
            text.Append(Written.Time(candle.ClosedAt))
                .Append(", ").Append(Written.Number(candle.Open))
                .Append(", ").Append(Written.Number(candle.High))
                .Append(", ").Append(Written.Number(candle.Low))
                .Append(", ").Append(Written.Number(candle.Close))
                .Append(", ").Append(Written.Number(candle.Volume))
                .Append('\n');
        }

        var indicators = series.Indicators;

        text.Append("Indicators at that close, computed by the system from ").Append(Written.Number(series.ComputedFrom))
            .Append(" closed ").Append(code).Append(series.ComputedFrom == 1 ? " candle:\n" : " candles:\n");
        text.Append("- Moving averages of the close: SMA").Append(Indicators.ShortAverage).Append(' ').Append(Value(indicators.Sma20))
            .Append(", SMA").Append(Indicators.MediumAverage).Append(' ').Append(Value(indicators.Sma50))
            .Append(", SMA").Append(Indicators.LongAverage).Append(' ').Append(Value(indicators.Sma200))
            .Append(", EMA").Append(Indicators.ShortAverage).Append(' ').Append(Value(indicators.Ema20))
            .Append(", EMA").Append(Indicators.MediumAverage).Append(' ').Append(Value(indicators.Ema50)).Append('\n');
        text.Append("- Relative strength: RSI").Append(Indicators.RelativeStrengthPeriod).Append(' ')
            .Append(indicators.Rsi14 is { } strength ? Written.Number(Math.Round(strength, 2, MidpointRounding.AwayFromZero)) : NotAvailable)
            .Append('\n');
        text.Append("- Average true range: ATR").Append(Indicators.TrueRangePeriod).Append(' ').Append(Value(indicators.Atr14)).Append('\n');
        text.Append("- Volume: ").Append(Written.Number(series.Candles[^1].Volume)).Append(" in the latest candle, average of ")
            .Append(Indicators.VolumeAverage).Append(" candles ").Append(Value(indicators.VolumeAverage20)).Append('\n');
    }

    private static void Orders(StringBuilder text, IReadOnlyList<OpenOrder> orders)
    {
        if (orders.Count == 0)
        {
            text.Append("Open orders: none\n");

            return;
        }

        text.Append("Open orders:\n");

        foreach (var order in orders)
        {
            var kind = order.Kind switch
            {
                OpenOrderKind.Entry => "entry",
                OpenOrderKind.StopLoss => "stop-loss",
                OpenOrderKind.TakeProfit => "take-profit",
                _ => throw new ArgumentOutOfRangeException(nameof(orders), order.Kind, "No such kind of order."),
            };

            text.Append("- ").Append(kind).Append(": ").Append(order.Side == OrderSide.Buy ? "buy " : "sell ")
                .Append(Written.Number(order.Quantity)).Append(" at ").Append(Written.Number(order.Price)).Append('\n');
        }
    }

    private static string Value(decimal? indicator) =>
        indicator is { } value ? Written.Number(value, SignificantDigits) : NotAvailable;

    /// <summary>The asset that is traded: the symbol without the asset that it is priced in, where it ends with that.</summary>
    private static string? BaseAssetOf(Agent agent) =>
        agent.Symbol.Length > agent.QuoteAsset.Length && agent.Symbol.EndsWith(agent.QuoteAsset, StringComparison.Ordinal)
            ? agent.Symbol[..^agent.QuoteAsset.Length]
            : null;
}
