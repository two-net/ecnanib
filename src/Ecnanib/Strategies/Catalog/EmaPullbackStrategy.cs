using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.4, 1D EMA Pullback: the Position Breakout regime; a daily close holding EMA20 or EMA50 after a pullback; the same filters; no fixed final target.</summary>
internal sealed class EmaPullbackStrategy : StrategyBase
{
    public const string Id = "ema-pullback";

    public EmaPullbackStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "EMA Pullback",
        CandleInterval.OneDay,
        1,
        "Same regime as the position breakout; daily close holding EMA20 or EMA50 in an uptrend; stop beyond the swing; one third off at 3R, breakeven, trail.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.RegimeEmaPeriod(),
            CommonParameters.RegimeRisingLookback(),
            CommonParameters.SwingLookback(),
            CommonParameters.PullbackToleranceAtr(),
            CommonParameters.PullbackLookback(5),
            CommonParameters.VolumeAveragePeriod(),
            CommonParameters.PartialTakeProfitR(3m),
            CommonParameters.MaxStopDistanceAtr(5m),
            CommonParameters.MaxExtensionPercent(),
            CommonParameters.MinDailyQuoteVolume(),
            CommonParameters.BtcRegimeEmaPeriod(),
        ],
        [CandleInterval.OneWeek],
        [new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)]);

    protected override IEnumerable<SeriesNeed> Needs(Frame f) =>
    [
        Need(f.Descriptor.Timeframe, f.CommonMinimum,
            f.Params.Int(SpecificParameters.RegimeEmaPeriod) + f.Params.Int(SpecificParameters.RegimeRisingLookback),
            f.Params.Int(SpecificParameters.PullbackLookback) + 1,
            f.Params.Int(SpecificParameters.VolumeAveragePeriod) + 1),
        Need(CandleInterval.OneWeek, 2 * f.SwingLookback + 1),
    ];

    protected override BiasDirection Bias(Frame f) => DailyRegime(f);

    protected override string NoBiasNote => "No daily regime: EMA200 slope, price or weekly structure disagree";

    protected override void Describe(Frame f, Direction? d) => DescribeDailyFilters(f, d);

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var lookback = f.Params.Int(SpecificParameters.PullbackLookback);
        var tolerance = f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * f.AtrValue!.Value;
        var levels = new List<LevelSeries> { new("emaFast", f.EmaFastSeries), new("emaSlow", f.EmaSlowSeries) };
        var pullback = Pullbacks.Find(f.Candles, levels, lookback, tolerance, d);
        if (pullback is null)
        {
            f.Reason = $"no pullback to EMA{f.EmaFastPeriod} or EMA{f.EmaSlowPeriod} within {lookback} candles";
            return null;
        }

        if (!Triggers.HoldsLevel(f.Last, pullback.LevelPrice, d))
        {
            f.Reason = $"close {Prices.Format(f.Last.Close)} does not hold {pullback.Level} ({Prices.Format(pullback.LevelPrice)})";
            return null;
        }

        var weeklySwing = Swings.Last(Swings.Find(f.Htf(CandleInterval.OneWeek), f.SwingLookback), d.IsLong ? SwingKind.Low : SwingKind.High)?.Price;
        f.Output.Level("pullbackExtreme", pullback.Extreme);
        return new Candidate(
            f.Last.Close,
            pullback.Extreme,
            $"daily close {d.Below} EMA{f.EmaSlowPeriod} ({Prices.Format(f.EmaSlowSeries[^1] ?? 0)}) or weekly structure break (close {d.Below} {(weeklySwing is { } w ? Prices.Format(w) : "the last weekly swing")})",
            $"{d.Capitalized}: pullback to {pullback.Level} held; close {Prices.Format(f.Last.Close)} holds {Prices.Format(pullback.LevelPrice)}");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate) => DailyFilters(f, d);

    protected override bool HasFinalTarget => false;

    /// <summary>The A.4 regime: rising (falling) daily EMA200 with the close above (below) it and weekly higher (lower) highs and lows; emits its levels and indicators.</summary>
    internal static BiasDirection DailyRegime(Frame f)
    {
        var weekly = f.Htf(CandleInterval.OneWeek);
        var lookback = f.Params.Int(SpecificParameters.RegimeRisingLookback);
        var bias = HtfBias.Regime(f.Candles, weekly, f.Params.Int(SpecificParameters.RegimeEmaPeriod), lookback, f.SwingLookback, out var ema, out var weeklyStructure);
        var weeklySwings = Swings.Find(weekly, f.SwingLookback);
        f.Output.Level("regimeEma", ema[^1]);
        f.Output.Level("weeklySwingHigh", Swings.Last(weeklySwings, SwingKind.High)?.Price);
        f.Output.Level("weeklySwingLow", Swings.Last(weeklySwings, SwingKind.Low)?.Price);
        f.Output.Indicator("regimeEma", ema[^1]);
        f.Output.Indicator("regimeEmaChange", Ema.Change(ema, lookback));
        f.Output.Indicator("weeklyClose", weekly.Count == 0 ? null : weekly[^1].Close);
        f.Output.Indicator("weeklyStructure", Swings.Sign(weeklyStructure));
        f.Output.Indicator("htfStructure", Swings.Sign(weeklyStructure));
        return bias;
    }

    /// <summary>The extension, liquidity and BTC regime indicators the 1D strategies share.</summary>
    internal static void DescribeDailyFilters(Frame f, Direction? d)
    {
        var direction = d ?? Direction.Long;
        if (f.EmaSlowSeries[^1] is { } emaSlow)
        {
            Filters.Extension(f.Last.Close, emaSlow, f.Params.Dec(SpecificParameters.MaxExtensionPercent), direction, out var percent);
            f.Output.Indicator("extensionPercent", percent);
        }

        Filters.Liquidity(f.Agent.Symbol, f.Candles, f.Params.Int(SpecificParameters.VolumeAveragePeriod), f.Params.Dec(SpecificParameters.MinDailyQuoteVolume), out var average);
        f.Output.Indicator("averageDailyQuoteVolume", average);
        var btc = f.Reference(Filters.BtcSymbol, CandleInterval.OneWeek);
        Filters.BtcRegime(f.Agent.Symbol, btc, f.Params.Int(SpecificParameters.BtcRegimeEmaPeriod), f.SwingLookback, direction, out var regime, out var btcEma);
        if (btc.Count > 0)
            f.Output.Indicator("btcWeeklyClose", btc[^1].Close);
        f.Output.Indicator("btcWeeklyEma", btcEma);
        if (regime is { } r)
            f.Output.Indicator("btcRegime", r);
    }

    internal static IEnumerable<FilterResult> DailyFilters(Frame f, Direction d)
    {
        yield return Filters.BtcRegime(f.Agent.Symbol, f.Reference(Filters.BtcSymbol, CandleInterval.OneWeek), f.Params.Int(SpecificParameters.BtcRegimeEmaPeriod), f.SwingLookback, d, out _, out _);
        yield return Filters.Extension(f.Last.Close, f.EmaSlowSeries[^1] ?? f.Last.Close, f.Params.Dec(SpecificParameters.MaxExtensionPercent), d, out _);
        yield return Filters.Liquidity(f.Agent.Symbol, f.Candles, f.Params.Int(SpecificParameters.VolumeAveragePeriod), f.Params.Dec(SpecificParameters.MinDailyQuoteVolume), out _);
        yield return Filters.NoEntryWindows(f.Agent.EntryWindows, f.At);
    }
}
