using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Indicators;
using Ecnanib.Strategies.Rules;
using Microsoft.Extensions.Options;

namespace Ecnanib.Strategies.Engine;

/// <summary>
/// The evaluation template every Appendix A strategy follows (FR-STR-01/02/07): enough history → bias from the higher
/// timeframes → levels and indicators → a spot agent takes no short → candidate (setup and trigger) → stop and
/// target gates → a paused agent gets no setup → filters → the setup. The first reason that stops the chain is the
/// note; <c>Note</c> is never null. Longs are specified; shorts run the same rules through <see cref="Direction"/>.
/// </summary>
internal abstract class StrategyBase : IStrategy
{
    private readonly IOptions<AgentsOptions> _options;
    private StrategyDescriptor? _descriptor;

    protected StrategyBase(IOptions<AgentsOptions> options)
    {
        _options = options;
    }

    public StrategyDescriptor Descriptor => _descriptor ??= CreateDescriptor(_options.Value.DefaultEntryTimeoutCandles);

    public StrategyOutput Evaluate(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var f = new Frame(context, Descriptor);

        foreach (var need in Needs(f))
        {
            var have = f.Count(need.Interval);
            if (have < need.Count)
                return f.Output.Build(Descriptor, BiasDirection.None, null, $"insufficient history: need {need.Count} {need.Interval.ToLabel()} candles, have {have}");
        }

        var bias = Bias(f);
        DescribeOwnSeries(f);
        Describe(f, Direction.FromBias(bias));

        var atr = f.AtrValue;
        if (atr is null or <= 0)
            return Finish(f, bias, null, "ATR is zero; no setup");
        if (Direction.FromBias(bias) is not { } d)
            return Finish(f, bias, null, NoBiasNote);
        if (f.IsSpot && !d.IsLong)
            return Finish(f, bias, null, "Short bias: spot agents take no short setups (FR-STR-07)");

        var candidate = FindCandidate(f, d);
        if (candidate is null)
            return Finish(f, bias, null, $"{d.Capitalized} bias; {f.Reason ?? "no setup"}");

        var stop = candidate.StopBasis - d.Sign * f.StopBufferAtr * atr.Value;
        var r = d.Sign * (candidate.Entry - stop);
        if (r <= 0)
            return Finish(f, bias, null, $"{d.Capitalized} candidate rejected: the entry is not beyond the stop");
        var riskAtr = r / atr.Value;
        f.Output.Indicator("riskAtr", riskAtr);
        f.Output.Indicator("stopDistancePercent", r / candidate.Entry * 100);
        var maxStop = f.Params.Dec(SpecificParameters.MaxStopDistanceAtr);
        if (riskAtr > maxStop)
            return Finish(f, bias, null, $"{d.Capitalized} candidate rejected: stop too far, {Prices.Format(riskAtr)} ATR above {Prices.Format(maxStop)} ATR");

        var targets = Targets.Compute(f, d, candidate.Entry, r, HasFinalTarget, FallbackRewardR(f), out var rewardR, out var finalSource);
        f.Output.Indicator("rewardR", rewardR);

        if (!context.EntriesAllowed)
            return Finish(f, bias, null, $"{d.Capitalized} setup suppressed: the agent is paused");

        var filters = ApplyFilters(f, d, candidate).ToList();
        var blocked = filters.Where(x => x.Blocks).ToList();
        if (blocked.Count > 0)
            return Finish(f, bias, null, $"{d.Capitalized} setup blocked by {string.Join("; ", blocked.Select(b => $"{b.Name}: {b.Detail}"))}");

        var multiplier = filters.Select(x => x.SizeMultiplier).FirstOrDefault(m => m is not null) ?? 1m;
        var setup = new StrategySetup(
            d.Side,
            OrderType.Limit,
            Prices.Round(candidate.Entry),
            Prices.Round(stop),
            targets.Select(Prices.Round).ToList(),
            candidate.Invalidation,
            filters.Select(x => x.ToString()).ToList(),
            Prices.Round(multiplier));
        return Finish(f, bias, setup, finalSource is null ? candidate.Rationale : $"{candidate.Rationale}; final target at the {finalSource}");
    }

    /// <summary>The descriptor at this version; <paramref name="entryTimeout"/> is the configured default of the common entry-timeout parameter.</summary>
    protected abstract StrategyDescriptor CreateDescriptor(int entryTimeout);

    /// <summary>The own series first, then the higher timeframes in descriptor order; reference series are judged by their filters.</summary>
    protected abstract IEnumerable<SeriesNeed> Needs(Frame f);

    /// <summary>The higher-timeframe bias, emitting its levels and indicators.</summary>
    protected abstract BiasDirection Bias(Frame f);

    protected abstract string NoBiasNote { get; }

    /// <summary>Strategy-specific levels and indicators that do not depend on a candidate; <paramref name="d"/> is the bias direction when there is one.</summary>
    protected abstract void Describe(Frame f, Direction? d);

    /// <summary>The setup and trigger rules; null with <see cref="Frame.Reason"/> set when there is no candidate.</summary>
    protected abstract Candidate? FindCandidate(Frame f, Direction d);

    protected abstract IEnumerable<FilterResult> ApplyFilters(Frame f, Direction d, Candidate candidate);

    /// <summary>Whether the rules define a final target beyond the partial one (the 1D strategies trail instead).</summary>
    protected virtual bool HasFinalTarget => true;

    /// <summary>The R multiple of the final target when no prior swing offers <c>minRewardR</c>; null means no final target then.</summary>
    protected virtual decimal? FallbackRewardR(Frame f) => HasFinalTarget ? f.Params.Dec(SpecificParameters.MinRewardR) : null;

    /// <summary>The 4H and 1D rules trail behind EMA50, the 15m and 1H ones behind EMA20.</summary>
    protected virtual bool TrailsSlowEma => Descriptor.Timeframe is CandleInterval.FourHours or CandleInterval.OneDay;

    protected static SeriesNeed Need(CandleInterval interval, params int[] counts) => new(interval, counts.Max());

    private void DescribeOwnSeries(Frame f)
    {
        f.Output.Level("emaFast", f.EmaFastSeries[^1]);
        f.Output.Level("emaSlow", f.EmaSlowSeries[^1]);
        f.Output.Indicator("close", f.Last.Close);
        f.Output.Indicator("emaFast", f.EmaFastSeries[^1]);
        f.Output.Indicator("emaSlow", f.EmaSlowSeries[^1]);
        f.Output.Indicator("atr", f.AtrValue);
    }

    private StrategyOutput Finish(Frame f, BiasDirection bias, StrategySetup? setup, string note)
    {
        var swingHigh = Swings.Last(f.SwingPoints, SwingKind.High)?.Price;
        var swingLow = Swings.Last(f.SwingPoints, SwingKind.Low)?.Price;
        var trailEma = TrailsSlowEma ? f.EmaSlowSeries[^1] : f.EmaFastSeries[^1];
        var bySwing = f.TrailingMethod == StrategyParameters.TrailingBySwing;
        f.Output.Level("swingHigh", swingHigh);
        f.Output.Level("swingLow", swingLow);
        f.Output.Level("trailLong", bySwing ? swingLow : trailEma);
        f.Output.Level("trailShort", bySwing ? swingHigh : trailEma);
        return f.Output.Build(Descriptor, bias, setup, note);
    }
}
