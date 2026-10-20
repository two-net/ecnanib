using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.2, 1H Trend Pullback: daily bias from EMA20 and structure; pullback to EMA20/EMA50 or the breakout level; a close in the trend direction; weekend size multiplier and no-entry windows.</summary>
internal sealed class TrendPullbackStrategy : StrategyBase
{
    public const string Id = "trend-pullback";

    public TrendPullbackStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Trend Pullback",
        CandleInterval.OneHour,
        1,
        "Daily bias from EMA20 and structure; pullback to the 1H EMA20/EMA50 or breakout level; trigger on a 1H close in the trend direction; stop beyond the swing; one third off at 2R, breakeven, trail behind swings.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.BiasEmaPeriod("Bias EMA period (daily)", 20),
            CommonParameters.SwingLookback(),
            CommonParameters.PullbackToleranceAtr(),
            CommonParameters.PullbackLookback(5),
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
        Need(f.Descriptor.Timeframe, f.CommonMinimum, f.Params.Int(SpecificParameters.PullbackLookback) + 1),
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
        if (d is { } direction && Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, direction) is { } level)
            f.Output.Level("breakoutLevel", level);
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var lookback = f.Params.Int(SpecificParameters.PullbackLookback);
        var tolerance = f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * f.AtrValue!.Value;
        var levels = new List<LevelSeries> { new("emaFast", f.EmaFastSeries), new("emaSlow", f.EmaSlowSeries) };
        if (Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, d) is { } breakoutLevel)
            levels.Add(LevelSeries.Constant("breakoutLevel", breakoutLevel, f.N));

        var pullback = Pullbacks.Find(f.Candles, levels, lookback, tolerance, d);
        if (pullback is null)
        {
            f.Reason = $"no pullback to the EMAs or the breakout level within {lookback} candles";
            return null;
        }

        if (!Triggers.ClosesInDirection(f.Last, f.Prev, d))
        {
            f.Reason = $"close {Prices.Format(f.Last.Close)} is not a {d.Word} close beyond the prior close {Prices.Format(f.Prev.Close)}";
            return null;
        }

        var period = f.Params.Int(SpecificParameters.BiasEmaPeriod);
        var htfEma = Ema.Last(f.Htf(CandleInterval.OneDay), period);
        f.Output.Level("pullbackExtreme", pullback.Extreme);
        return new Candidate(
            f.Last.Close,
            pullback.Extreme,
            $"1H close {d.Below} {Prices.Format(pullback.Extreme)} (pullback swing) or daily close {d.Below} EMA{period} ({Prices.Format(htfEma ?? 0)})",
            $"{d.Capitalized}: pullback to {pullback.Level} held; close {Prices.Format(f.Last.Close)} in the trend direction");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate)
    {
        yield return Filters.NoEntryWindows(f.Agent.EntryWindows, f.At);
        yield return Filters.Weekend(f.At, f.Params.Dec(SpecificParameters.WeekendSizeMultiplier));
    }

    protected override decimal? FallbackRewardR(Frame f) => f.Params.Dec(SpecificParameters.TypicalRewardR);
}
