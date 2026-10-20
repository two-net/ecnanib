using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.3, 4H Consolidation Breakout: weekly bias with daily agreement; a 4H close outside a range of at least 42 candles, with an optional retest; stop beyond the boundary (or the retest extreme).</summary>
internal sealed class ConsolidationBreakoutStrategy : StrategyBase
{
    public const string Id = "consolidation-breakout";

    public ConsolidationBreakoutStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Consolidation Breakout",
        CandleInterval.FourHours,
        1,
        "Range of at least 42 candles; 4H close outside, optional retest; stop beyond the range boundary; one third off at 3R, breakeven, trail.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.BiasEmaPeriod("Bias EMA period (weekly and daily)", 20),
            CommonParameters.SwingLookback(),
            CommonParameters.RangeMinCandles(42),
            CommonParameters.RangeMaxHeightAtr(6m),
            StrategyParameter.Flag(SpecificParameters.RequireRetest, "Wait for a retest of the boundary", false),
            CommonParameters.RetestMaxCandles(6),
            CommonParameters.PullbackToleranceAtr("Retest tolerance (× ATR)"),
            CommonParameters.VolumeAveragePeriod(),
            CommonParameters.PartialTakeProfitR(3m),
            CommonParameters.MinRewardR(3m),
            CommonParameters.MaxStopDistanceAtr(3m),
            CommonParameters.WeekendSizeMultiplier(),
            StrategyParameter.Integer(SpecificParameters.ExpectedHoldCandles, "Expected hold for the funding projection (candles)", 18, 1, 500),
            CommonParameters.BtcRegimeEmaPeriod(),
        ],
        [CandleInterval.OneDay, CandleInterval.OneWeek],
        [new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)]);

    protected override IEnumerable<SeriesNeed> Needs(Frame f)
    {
        var range = f.Params.Int(SpecificParameters.RangeMinCandles) + (f.Params.Flag(SpecificParameters.RequireRetest) ? f.Params.Int(SpecificParameters.RetestMaxCandles) : 0) + 1;
        return
        [
            Need(f.Descriptor.Timeframe, f.CommonMinimum, range, f.Params.Int(SpecificParameters.VolumeAveragePeriod) + 1),
            Need(CandleInterval.OneDay, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
            Need(CandleInterval.OneWeek, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
        ];
    }

    protected override BiasDirection Bias(Frame f) => SwingPullbackStrategy.WeeklyBiasWithDailyAgreement(f);

    protected override string NoBiasNote => "No weekly bias with daily agreement";

    protected override void Describe(Frame f, Direction? d)
    {
        SwingPullbackStrategy.DescribeBtcAndFunding(f, d);
        f.Output.Indicator("volumeRatio", Triggers.VolumeRatio(f.Candles, f.N - 1, f.Params.Int(SpecificParameters.VolumeAveragePeriod)));
        if (d is { } direction && f.AtrValue is { } atr && atr > 0 && FindBreakout(f, direction, atr) is { } breakout)
        {
            f.Output.Level("rangeTop", breakout.Box.Top);
            f.Output.Level("rangeBottom", breakout.Box.Bottom);
            f.Output.Indicator("rangeHeightAtr", breakout.Box.Height / atr);
        }
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var atr = f.AtrValue!.Value;
        var minCandles = f.Params.Int(SpecificParameters.RangeMinCandles);
        var requireRetest = f.Params.Flag(SpecificParameters.RequireRetest);
        var breakout = FindBreakout(f, d, atr);
        if (breakout is null)
        {
            f.Reason = requireRetest
                ? $"no {d.Word} breakout of a range of at least {minCandles} candles within the last {f.Params.Int(SpecificParameters.RetestMaxCandles)} candles"
                : $"no {d.Word} close outside a range of at least {minCandles} candles";
            return null;
        }

        var invalidation = $"4H close back inside the range ({d.Below} {Prices.Format(breakout.Boundary)})";
        var width = breakout.Box.End - breakout.Box.Start + 1;
        if (!requireRetest)
        {
            return new Candidate(f.Last.Close, breakout.Boundary, invalidation,
                $"{d.Capitalized}: close {Prices.Format(f.Last.Close)} outside a {width}-candle range at {Prices.Format(breakout.Boundary)}");
        }

        var retest = Breakouts.Retest(f.Candles, breakout, f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * atr, d);
        if (retest is null)
        {
            f.Reason = $"the retest of {Prices.Format(breakout.Boundary)} after the breakout at candle {breakout.Index} has not held yet";
            return null;
        }

        f.Output.Level("retestExtreme", retest.Extreme);
        return new Candidate(f.Last.Close, retest.Extreme, invalidation,
            $"{d.Capitalized}: breakout of a {width}-candle range at {Prices.Format(breakout.Boundary)}; retest held; close {Prices.Format(f.Last.Close)}");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate) => SwingPullbackStrategy.FourHourFilters(f, d);

    private static Breakout? FindBreakout(Frame f, Direction d, decimal atr)
    {
        var requireRetest = f.Params.Flag(SpecificParameters.RequireRetest);
        return Breakouts.Find(
            f.Candles, atr, d,
            f.Params.Int(SpecificParameters.RangeMinCandles), f.Params.Dec(SpecificParameters.RangeMaxHeightAtr),
            lastIndex: requireRetest ? f.N - 2 : f.N - 1, maxAge: requireRetest ? f.Params.Int(SpecificParameters.RetestMaxCandles) : 0,
            volumePeriod: null, volumeMultiple: 0m);
    }
}
