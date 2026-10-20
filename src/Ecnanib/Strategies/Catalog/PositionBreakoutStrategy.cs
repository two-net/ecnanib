using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Catalog;

/// <summary>Appendix A.4, 1D Position Breakout: rising daily EMA200 with price above and weekly higher highs and lows; a daily close above a consolidation of at least 20 days on volume expansion; extension, liquidity and BTC regime filters; no fixed final target.</summary>
internal sealed class PositionBreakoutStrategy : StrategyBase
{
    public const string Id = "position-breakout";

    public PositionBreakoutStrategy(IOptions<AgentsOptions> options)
        : base(options)
    {
    }

    protected override StrategyDescriptor CreateDescriptor(int entryTimeout) => new(
        Id,
        "Position Breakout",
        CandleInterval.OneDay,
        1,
        "Rising daily EMA200 with price above and weekly higher highs and lows; daily close above a consolidation of at least 20 days with volume expansion; stop beyond the swing; one third off at 3R, breakeven, trail under daily swings or EMA50.",
        [
            .. CommonParameters.Common(0.33m, entryTimeout),
            CommonParameters.RegimeEmaPeriod(),
            CommonParameters.RegimeRisingLookback(),
            CommonParameters.SwingLookback(),
            StrategyParameter.Integer(SpecificParameters.ConsolidationMinDays, "Consolidation length at least (days)", 20, 5, 200),
            CommonParameters.RangeMaxHeightAtr(8m),
            CommonParameters.VolumeAveragePeriod(),
            CommonParameters.VolumeMultiple(),
            StrategyParameter.Flag(SpecificParameters.WeeklyCloseConfirmation, "Require a weekly close beyond the consolidation", false),
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
            f.Params.Int(SpecificParameters.ConsolidationMinDays) + 1,
            f.Params.Int(SpecificParameters.VolumeAveragePeriod) + 1),
        Need(CandleInterval.OneWeek, 2 * f.SwingLookback + 1),
    ];

    protected override BiasDirection Bias(Frame f) => EmaPullbackStrategy.DailyRegime(f);

    protected override string NoBiasNote => "No daily regime: EMA200 slope, price or weekly structure disagree";

    protected override void Describe(Frame f, Direction? d)
    {
        EmaPullbackStrategy.DescribeDailyFilters(f, d);
        var period = f.Params.Int(SpecificParameters.VolumeAveragePeriod);
        f.Output.Indicator("volume", f.Last.Volume);
        f.Output.Indicator("volumeAverage", VolumeAverage.Before(f.Candles, f.N - 1, period));
        f.Output.Indicator("volumeRatio", Triggers.VolumeRatio(f.Candles, f.N - 1, period));
        if (f.AtrValue is { } atr && atr > 0 && Ranges.Box(f.Candles, f.N - 1, f.Params.Int(SpecificParameters.ConsolidationMinDays), f.Params.Dec(SpecificParameters.RangeMaxHeightAtr), atr) is { } box)
        {
            f.Output.Level("rangeTop", box.Top);
            f.Output.Level("rangeBottom", box.Bottom);
            f.Output.Indicator("rangeHeightAtr", box.Height / atr);
        }
    }

    protected override Candidate? FindCandidate(Frame f, Direction d)
    {
        var atr = f.AtrValue!.Value;
        var days = f.Params.Int(SpecificParameters.ConsolidationMinDays);
        var box = Ranges.Box(f.Candles, f.N - 1, days, f.Params.Dec(SpecificParameters.RangeMaxHeightAtr), atr);
        if (box is null)
        {
            f.Reason = $"no consolidation of at least {days} days before today";
            return null;
        }

        var boundary = d.IsLong ? box.Top : box.Bottom;
        if (!d.Beyond(f.Last.Close, boundary))
        {
            f.Reason = $"close {Prices.Format(f.Last.Close)} not beyond the consolidation {(d.IsLong ? "top" : "bottom")} {Prices.Format(boundary)}";
            return null;
        }

        var ratio = Triggers.VolumeRatio(f.Candles, f.N - 1, f.Params.Int(SpecificParameters.VolumeAveragePeriod));
        var multiple = f.Params.Dec(SpecificParameters.VolumeMultiple);
        if (ratio is null || ratio < multiple)
        {
            f.Reason = $"volume {(ratio is { } r ? Prices.Format(r) : "n/a")}× average, below {Prices.Format(multiple)}×";
            return null;
        }

        var weekly = f.Htf(CandleInterval.OneWeek);
        if (f.Params.Flag(SpecificParameters.WeeklyCloseConfirmation) && (weekly.Count == 0 || !d.Beyond(weekly[^1].Close, boundary)))
        {
            f.Reason = $"the last weekly close {(weekly.Count == 0 ? "n/a" : Prices.Format(weekly[^1].Close))} has not confirmed the breakout of {Prices.Format(boundary)}";
            return null;
        }

        var swing = f.SwingPoints.LastOrDefault(p => p.Kind == (d.IsLong ? SwingKind.Low : SwingKind.High) && p.Index >= box.Start);
        var basis = swing?.Price ?? (d.IsLong ? box.Bottom : box.Top);
        var weeklySwing = Swings.Last(Swings.Find(weekly, f.SwingLookback), d.IsLong ? SwingKind.Low : SwingKind.High)?.Price;
        return new Candidate(
            f.Last.Close,
            basis,
            $"daily close back {d.Below} {Prices.Format(boundary)} (consolidation {(d.IsLong ? "top" : "bottom")}) or weekly structure break (close {d.Below} {(weeklySwing is { } w ? Prices.Format(w) : "the last weekly swing")})",
            $"{d.Capitalized}: close {Prices.Format(f.Last.Close)} beyond a {days}-day consolidation at {Prices.Format(boundary)}; volume {Prices.Format(ratio.Value)}× average");
    }

    protected override IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate) => EmaPullbackStrategy.DailyFilters(f, d);

    protected override bool HasFinalTarget => false;
}
