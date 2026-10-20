using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.3, 4H Swing Pullback: weekly structure and EMA20 with the daily agreeing; pullback to EMA50 or the breakout level; BTC regime, weekend and projected-funding filters.</summary>
internal sealed class SwingPullbackStrategy : StrategyBase
{
    public const string Id = "swing-pullback";

    public SwingPullbackStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Swing Pullback",
        CandleInterval.FourHours,
        1,
        "Weekly structure and weekly EMA20 with the daily agreeing; pullback to the 4H EMA50 or breakout level; trigger on a 4H close in the trend direction; stop beyond the swing; one third off at 3R, breakeven, trail behind 4H swings or EMA50.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.BiasEmaPeriod("Bias EMA period (weekly and daily)", 20),
            CommonParameters.SwingLookback(),
            CommonParameters.PullbackToleranceAtr(),
            CommonParameters.PullbackLookback(5),
            CommonParameters.PartialTakeProfitR(3m),
            CommonParameters.MinRewardR(3m),
            CommonParameters.MaxStopDistanceAtr(3m),
            CommonParameters.WeekendSizeMultiplier(),
            StrategyParameter.Integer(SpecificParameters.ExpectedHoldCandles, "Expected hold for the funding projection (candles)", 18, 1, 500),
            CommonParameters.BtcRegimeEmaPeriod(),
        ],
        [CandleInterval.OneDay, CandleInterval.OneWeek],
        [new ReferenceSeries(Filters.BtcSymbol, CandleInterval.OneWeek)]);

    protected override IEnumerable<SeriesNeed> Needs(Frame f) =>
    [
        Need(f.Descriptor.Timeframe, f.CommonMinimum, f.Params.Int(SpecificParameters.PullbackLookback) + 1),
        Need(CandleInterval.OneDay, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
        Need(CandleInterval.OneWeek, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
    ];

    protected override BiasDirection Bias(Frame f) => WeeklyBiasWithDailyAgreement(f);

    protected override string NoBiasNote => "No weekly bias with daily agreement";

    protected override void Describe(Frame f, Direction? d)
    {
        if (d is { } direction && Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, direction) is { } level)
            f.Output.Level("breakoutLevel", level);
        DescribeBtcAndFunding(f, d);
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var lookback = f.Params.Int(SpecificParameters.PullbackLookback);
        var tolerance = f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * f.AtrValue!.Value;
        var levels = new List<LevelSeries> { new("emaSlow", f.EmaSlowSeries) };
        if (Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, d) is { } breakoutLevel)
            levels.Add(LevelSeries.Constant("breakoutLevel", breakoutLevel, f.N));

        var pullback = Pullbacks.Find(f.Candles, levels, lookback, tolerance, d);
        if (pullback is null)
        {
            f.Reason = $"no pullback to EMA{f.EmaSlowPeriod} or the breakout level within {lookback} candles";
            return null;
        }

        if (!Triggers.ClosesInDirection(f.Last, f.Prev, d))
        {
            f.Reason = $"close {Prices.Format(f.Last.Close)} is not a {d.Word} close beyond the prior close {Prices.Format(f.Prev.Close)}";
            return null;
        }

        var period = f.Params.Int(SpecificParameters.BiasEmaPeriod);
        var weeklyEma = Ema.Last(f.Htf(CandleInterval.OneWeek), period);
        f.Output.Level("pullbackExtreme", pullback.Extreme);
        return new Candidate(
            f.Last.Close,
            pullback.Extreme,
            $"4H close {d.Below} {Prices.Format(pullback.Extreme)} (pullback swing) or weekly close {d.Below} EMA{period} ({Prices.Format(weeklyEma ?? 0)})",
            $"{d.Capitalized}: pullback to {pullback.Level} held; close {Prices.Format(f.Last.Close)} in the trend direction");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate) => FourHourFilters(f, d);

    /// <summary>Weekly bias (EMA and structure), confirmed by the daily; emits the weekly and daily levels and indicators.</summary>
    internal static BiasDirection WeeklyBiasWithDailyAgreement(Frame f)
    {
        var period = f.Params.Int(SpecificParameters.BiasEmaPeriod);
        var weekly = f.Htf(CandleInterval.OneWeek);
        var daily = f.Htf(CandleInterval.OneDay);
        var bias = HtfBias.Bias(weekly, f.Last.Close, period, f.SwingLookback, out var weeklyEma, out var weeklyStructure);
        var weeklySwings = Swings.Find(weekly, f.SwingLookback);
        decimal? dailyEma;
        SwingStructure dailyStructure;
        if (Direction.FromBias(bias) is { } d)
        {
            if (!HtfBias.Agrees(daily, f.Last.Close, period, f.SwingLookback, d, out dailyEma, out dailyStructure))
                bias = BiasDirection.None;
        }
        else
        {
            dailyEma = Ema.Last(daily, period);
            dailyStructure = Swings.Classify(Swings.Find(daily, f.SwingLookback));
        }

        f.Output.Level("weeklyEma", weeklyEma);
        f.Output.Level("dailyEma", dailyEma);
        f.Output.Level("weeklySwingHigh", Swings.Last(weeklySwings, SwingKind.High)?.Price);
        f.Output.Level("weeklySwingLow", Swings.Last(weeklySwings, SwingKind.Low)?.Price);
        f.Output.Indicator("weeklyClose", weekly[^1].Close);
        f.Output.Indicator("weeklyEma", weeklyEma);
        f.Output.Indicator("htfStructure", Swings.Sign(weeklyStructure));
        f.Output.Indicator("dailyClose", daily[^1].Close);
        f.Output.Indicator("dailyEma", dailyEma);
        f.Output.Indicator("dailyStructure", Swings.Sign(dailyStructure));
        return bias;
    }

    /// <summary>The BTC regime and funding indicators the 4H strategies share.</summary>
    internal static void DescribeBtcAndFunding(Frame f, Direction? d)
    {
        var btc = f.Reference(Filters.BtcSymbol, CandleInterval.OneWeek);
        Filters.BtcRegime(f.Agent.Symbol, btc, f.Params.Int(SpecificParameters.BtcRegimeEmaPeriod), f.SwingLookback, d ?? Direction.Long, out var regime, out var btcEma);
        if (btc.Count > 0)
            f.Output.Indicator("btcWeeklyClose", btc[^1].Close);
        f.Output.Indicator("btcWeeklyEma", btcEma);
        if (regime is { } r)
            f.Output.Indicator("btcRegime", r);
        if (f.Mark is { } mark)
        {
            f.Output.Indicator("fundingRate", mark.FundingRate);
            Filters.ProjectedFunding(mark, f.Params.Int(SpecificParameters.ExpectedHoldCandles), f.Descriptor.Timeframe.ToTimeSpan(), d ?? Direction.Long, out var projected);
            f.Output.Indicator("projectedFundingRate", projected);
        }
    }

    internal static IEnumerable<FilterResult> FourHourFilters(Frame f, Direction d)
    {
        yield return Filters.BtcRegime(f.Agent.Symbol, f.Reference(Filters.BtcSymbol, CandleInterval.OneWeek), f.Params.Int(SpecificParameters.BtcRegimeEmaPeriod), f.SwingLookback, d, out _, out _);
        yield return Filters.NoEntryWindows(f.Agent.EntryWindows, f.At);
        yield return Filters.Weekend(f.At, f.Params.Dec(SpecificParameters.WeekendSizeMultiplier));
        if (f.IsFutures)
            yield return Filters.ProjectedFunding(f.Mark, f.Params.Int(SpecificParameters.ExpectedHoldCandles), f.Descriptor.Timeframe.ToTimeSpan(), d, out _);
    }
}
