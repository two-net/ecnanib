using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.2, 1H Range Breakout–Retest: daily bias; a close outside a range of at least 24 candles on volume; the retest holds; stop beyond the retest extreme.</summary>
internal sealed class RangeBreakoutRetestStrategy : StrategyBase
{
    public const string Id = "range-breakout-retest";

    public RangeBreakoutRetestStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Range Breakout–Retest",
        CandleInterval.OneHour,
        1,
        "Range of at least 24 candles; 1H close outside on volume; retest holds; stop beyond the retest extreme; one third off at 2R, breakeven, trail behind swings.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.BiasEmaPeriod("Bias EMA period (daily)", 20),
            CommonParameters.SwingLookback(),
            CommonParameters.RangeMinCandles(24),
            CommonParameters.RangeMaxHeightAtr(5m),
            CommonParameters.VolumeAveragePeriod(),
            CommonParameters.VolumeMultiple(),
            CommonParameters.RetestMaxCandles(12),
            CommonParameters.PullbackToleranceAtr("Retest tolerance (× ATR)"),
            CommonParameters.PartialTakeProfitR(2m),
            CommonParameters.MinRewardR(2m),
            CommonParameters.TypicalRewardR(),
            CommonParameters.MaxStopDistanceAtr(3m),
            CommonParameters.WeekendSizeMultiplier(),
        ],
        [CandleInterval.OneDay],
        []);

    protected override IEnumerable<SeriesNeed> Needs(Frame f) =>
    [
        Need(f.Descriptor.Timeframe, f.CommonMinimum, f.Params.Int(SpecificParameters.RangeMinCandles) + f.Params.Int(SpecificParameters.RetestMaxCandles) + 1, f.Params.Int(SpecificParameters.VolumeAveragePeriod) + 1),
        Need(CandleInterval.OneDay, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
    ];

    protected override BiasDirection Bias(Frame f)
    {
        var daily = f.Htf(CandleInterval.OneDay);
        var bias = HtfBias.Bias(daily, f.Last.Close, f.Params.Int(SpecificParameters.BiasEmaPeriod), f.SwingLookback, out var ema, out var structure);
        f.Output.Level("htfEma", ema);
        f.Output.Indicator("htfClose", daily[^1].Close);
        f.Output.Indicator("htfEma", ema);
        f.Output.Indicator("htfStructure", Swings.Sign(structure));
        return bias;
    }

    protected override string NoBiasNote => "No daily bias: price and structure do not agree";

    protected override void Describe(Frame f, Direction? d)
    {
        if (d is not { } direction || f.AtrValue is not { } atr || atr <= 0)
            return;
        if (FindBreakout(f, direction, atr) is { } breakout)
        {
            f.Output.Level("rangeTop", breakout.Box.Top);
            f.Output.Level("rangeBottom", breakout.Box.Bottom);
            f.Output.Indicator("rangeHeightAtr", breakout.Box.Height / atr);
            f.Output.Indicator("breakoutVolumeRatio", Triggers.VolumeRatio(f.Candles, breakout.Index, f.Params.Int(SpecificParameters.VolumeAveragePeriod)));
        }
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var atr = f.AtrValue!.Value;
        var minCandles = f.Params.Int(SpecificParameters.RangeMinCandles);
        var breakout = FindBreakout(f, d, atr);
        if (breakout is null)
        {
            f.Reason = $"no {d.Word} breakout of a range of at least {minCandles} candles within the last {f.Params.Int(SpecificParameters.RetestMaxCandles)} candles";
            return null;
        }

        var retest = Breakouts.Retest(f.Candles, breakout, f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * atr, d);
        if (retest is null)
        {
            f.Reason = $"the retest of {Prices.Format(breakout.Boundary)} after the breakout at candle {breakout.Index} has not held yet";
            return null;
        }

        f.Output.Level("retestExtreme", retest.Extreme);
        return new Candidate(
            f.Last.Close,
            retest.Extreme,
            $"1H close back inside the range ({d.Below} {Prices.Format(breakout.Boundary)})",
            $"{d.Capitalized}: breakout of a {breakout.Box.End - breakout.Box.Start + 1}-candle range at {Prices.Format(breakout.Boundary)}; retest held; close {Prices.Format(f.Last.Close)}");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate)
    {
        yield return Filters.NoEntryWindows(f.Agent.EntryWindows, f.At);
        yield return Filters.Weekend(f.At, f.Params.Dec(SpecificParameters.WeekendSizeMultiplier));
    }

    protected override decimal? FallbackRewardR(Frame f) => f.Params.Dec(SpecificParameters.TypicalRewardR);

    private static Breakout? FindBreakout(Frame f, Direction d, decimal atr) => Breakouts.Find(
        f.Candles, atr, d,
        f.Params.Int(SpecificParameters.RangeMinCandles), f.Params.Dec(SpecificParameters.RangeMaxHeightAtr),
        lastIndex: f.N - 2, maxAge: f.Params.Int(SpecificParameters.RetestMaxCandles),
        f.Params.Int(SpecificParameters.VolumeAveragePeriod), f.Params.Dec(SpecificParameters.VolumeMultiple));
}
