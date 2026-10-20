using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.1, 15m: 4H bias; pullback to EMA20/EMA50, the session VWAP or the last breakout level; close beyond the prior candle on 1.2× volume; session, no-entry windows and funding filters.</summary>
internal sealed class IntradayTrendPullbackStrategy : StrategyBase
{
    public const string Id = "intraday-trend-pullback";

    public IntradayTrendPullbackStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Intraday Trend Pullback",
        CandleInterval.FifteenMinutes,
        1,
        "4H bias from EMA50 and structure; pullback to the 15m EMA20/EMA50, session VWAP or the last breakout level; trigger on a close beyond the prior candle with 1.2× volume; stop beyond the pullback extreme; 50% off at 1.5R, trail the rest.",
        [
            .. CommonParameters.Common(0.5m, entryTimeout),
            CommonParameters.BiasEmaPeriod("Bias EMA period (4H)", 50),
            CommonParameters.SwingLookback(),
            CommonParameters.PullbackToleranceAtr(),
            CommonParameters.PullbackLookback(6),
            CommonParameters.VolumeAveragePeriod(),
            CommonParameters.VolumeMultiple(),
            StrategyParameter.Integer(SpecificParameters.SessionStartHour, "Session start (UTC hour)", 13, 0, 23),
            StrategyParameter.Integer(SpecificParameters.SessionEndHour, "Session end (UTC hour)", 20, 0, 23),
            StrategyParameter.Decimal(SpecificParameters.FundingRateExtreme, "Funding-rate extreme (per 8h, as a fraction)", 0.001m, 0.0001m, 0.05m),
            CommonParameters.PartialTakeProfitR(1.5m),
            CommonParameters.MinRewardR(2m),
            CommonParameters.MaxStopDistanceAtr(3m),
        ],
        [CandleInterval.FourHours],
        []);

    protected override IEnumerable<SeriesNeed> Needs(Frame f) =>
    [
        Need(f.Descriptor.Timeframe, f.CommonMinimum, f.Params.Int(SpecificParameters.VolumeAveragePeriod) + 1, f.Params.Int(SpecificParameters.PullbackLookback) + 1),
        Need(CandleInterval.FourHours, f.Params.Int(SpecificParameters.BiasEmaPeriod), 2 * f.SwingLookback + 1),
    ];

    protected override BiasDirection Bias(Frame f)
    {
        var htf = f.Htf(CandleInterval.FourHours);
        var bias = HtfBias.Bias(htf, f.Last.Close, f.Params.Int(SpecificParameters.BiasEmaPeriod), f.SwingLookback, out var ema, out var structure);
        f.Output.Level("htfEma", ema);
        f.Output.Indicator("htfClose", htf[^1].Close);
        f.Output.Indicator("htfEma", ema);
        f.Output.Indicator("htfStructure", Swings.Sign(structure));
        return bias;
    }

    protected override string NoBiasNote => "No 4H bias: price and structure do not agree";

    protected override void Describe(Frame f, Direction? d)
    {
        var vwap = SessionVwap.Compute(f.Candles)[^1];
        f.Output.Level("sessionVwap", vwap);
        if (d is { } direction && Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, direction) is { } level)
            f.Output.Level("breakoutLevel", level);
        var period = f.Params.Int(SpecificParameters.VolumeAveragePeriod);
        f.Output.Indicator("sessionVwap", vwap);
        f.Output.Indicator("volume", f.Last.Volume);
        f.Output.Indicator("volumeAverage", VolumeAverage.Before(f.Candles, f.N - 1, period));
        f.Output.Indicator("volumeRatio", Triggers.VolumeRatio(f.Candles, f.N - 1, period));
        if (f.Mark is { } mark)
            f.Output.Indicator("fundingRate", mark.FundingRate);
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var lookback = f.Params.Int(SpecificParameters.PullbackLookback);
        var tolerance = f.Params.Dec(SpecificParameters.PullbackToleranceAtr) * f.AtrValue!.Value;
        var levels = new List<LevelSeries> { new("emaFast", f.EmaFastSeries), new("emaSlow", f.EmaSlowSeries) };
        var vwap = SessionVwap.Compute(f.Candles);
        if (vwap[^1] is not null)
            levels.Add(new LevelSeries("sessionVwap", vwap));
        if (Breakouts.BreakoutLevel(f.SwingPoints, f.Last.Close, d) is { } breakoutLevel)
            levels.Add(LevelSeries.Constant("breakoutLevel", breakoutLevel, f.N));

        var pullback = Pullbacks.Find(f.Candles, levels, lookback, tolerance, d);
        if (pullback is null)
        {
            f.Reason = $"no pullback to the EMAs, the session VWAP or the breakout level within {lookback} candles";
            return null;
        }

        var prior = d.Opposite(f.Prev);
        if (!Triggers.ClosesBeyondPrior(f.Last, f.Prev, d))
        {
            f.Reason = $"close {Prices.Format(f.Last.Close)} not beyond the prior candle's {d.High} {Prices.Format(prior)}";
            return null;
        }

        var ratio = Triggers.VolumeRatio(f.Candles, f.N - 1, f.Params.Int(SpecificParameters.VolumeAveragePeriod));
        var multiple = f.Params.Dec(SpecificParameters.VolumeMultiple);
        if (ratio is null || ratio < multiple)
        {
            f.Reason = $"volume {(ratio is { } r ? Prices.Format(r) : "n/a")}× average, below {Prices.Format(multiple)}×";
            return null;
        }

        f.Output.Level("pullbackExtreme", pullback.Extreme);
        return new Candidate(
            f.Last.Close,
            pullback.Extreme,
            $"15m close {d.Below} {Prices.Format(pullback.Extreme)} (pullback extreme) or 4H bias no longer {d.Word}",
            $"{d.Capitalized}: pullback to {pullback.Level} held; close {Prices.Format(f.Last.Close)} beyond the prior {d.High} {Prices.Format(prior)}; volume {Prices.Format(ratio.Value)}× average");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate)
    {
        yield return Filters.Session(f.At, f.Params.Int(SpecificParameters.SessionStartHour), f.Params.Int(SpecificParameters.SessionEndHour));
        yield return Filters.NoEntryWindows(f.Agent.EntryWindows, f.At);
        if (f.IsFutures)
            yield return Filters.FundingRate(f.Mark, f.Params.Dec(SpecificParameters.FundingRateExtreme), d);
    }
}
