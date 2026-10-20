using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Engine;

/// <summary>A candle series the strategy needs with the minimum number of candles that makes it evaluable.</summary>
internal sealed record SeriesNeed(CandleInterval Interval, int Count);

/// <summary>A trade candidate before the stop, target and filter gates: entry, the price the stop is placed beyond, and the texts.</summary>
internal sealed record Candidate(decimal Entry, decimal StopBasis, string Invalidation, string Rationale);

/// <summary>
/// The working set of one evaluation: the context, the common parameters, the lazily computed own-series indicators,
/// the output being built and the reason the strategy found no candidate. Never leaves the module.
/// </summary>
internal sealed class Frame
{
    private decimal?[]? _atr;
    private decimal?[]? _emaFast;
    private decimal?[]? _emaSlow;
    private IReadOnlyList<SwingPoint>? _swings;

    public Frame(StrategyContext context, StrategyDescriptor descriptor)
    {
        Context = context;
        Descriptor = descriptor;
        Params = new ParameterReader(context.Parameters);
        AtrPeriod = Params.Int(StrategyParameters.AtrPeriod);
        EmaFastPeriod = Params.Int(StrategyParameters.EmaFastPeriod);
        EmaSlowPeriod = Params.Int(StrategyParameters.EmaSlowPeriod);
        StopBufferAtr = Params.Dec(StrategyParameters.StopBufferAtr);
        TrailingMethod = Params.Choice(StrategyParameters.TrailingMethod);
        SwingLookback = Params.Int(SpecificParameters.SwingLookback);
    }

    public StrategyContext Context { get; }

    public StrategyDescriptor Descriptor { get; }

    public ParameterReader Params { get; }

    public OutputBuilder Output { get; } = new();

    /// <summary>Why no candidate was found, in the words of the note.</summary>
    public string? Reason { get; set; }

    public Agent Agent => Context.Agent;

    public bool IsSpot => Agent.MarketType == MarketType.Spot;

    public bool IsFutures => Agent.MarketType == MarketType.UsdFutures;

    public MarkPrice? Mark => Context.Market.MarkPrice;

    public IReadOnlyList<Candle> Candles => Context.Market.Candles;

    public int N => Candles.Count;

    public Candle Last => Candles[^1];

    public Candle Prev => Candles[^2];

    /// <summary>The evaluation instant: the close boundary of the evaluated candle (FR-STR-01).</summary>
    public DateTimeOffset At => Last.OpenTime + Descriptor.Timeframe.ToTimeSpan();

    public int AtrPeriod { get; }

    public int EmaFastPeriod { get; }

    public int EmaSlowPeriod { get; }

    public decimal StopBufferAtr { get; }

    public string TrailingMethod { get; }

    public int SwingLookback { get; }

    public decimal?[] AtrSeries => _atr ??= Atr.Compute(Candles, AtrPeriod);

    public decimal? AtrValue => N == 0 ? null : AtrSeries[^1];

    public decimal?[] EmaFastSeries => _emaFast ??= Ema.Compute(Candles, EmaFastPeriod);

    public decimal?[] EmaSlowSeries => _emaSlow ??= Ema.Compute(Candles, EmaSlowPeriod);

    public IReadOnlyList<SwingPoint> SwingPoints => _swings ??= Swings.Find(Candles, SwingLookback);

    public IReadOnlyList<Candle> Htf(CandleInterval interval) => Context.Market.Higher(interval);

    public IReadOnlyList<Candle> Reference(string symbol, CandleInterval interval) => Context.Market.Reference(new ReferenceSeries(symbol, interval));

    public int Count(CandleInterval interval) => interval == Descriptor.Timeframe ? N : Htf(interval).Count;

    /// <summary>The minimum own candles every strategy needs: the slow EMA, the ATR seed and one confirmed swing.</summary>
    public int CommonMinimum => Math.Max(EmaSlowPeriod, Math.Max(AtrPeriod + 1, 2 * SwingLookback + 1));
}
