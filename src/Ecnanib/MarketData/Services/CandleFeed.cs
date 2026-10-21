using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.MarketData.Services;

/// <summary>
/// The market data the evaluations see (FR-DATA-02..05). Every series a tracked agent needs, its own timeframe, the
/// higher timeframes and the reference series its strategy declares, is backfilled from the exchange when the agent
/// is tracked and then followed on the shared candle stream, which reconnects and polls on its own (FR-DATA-03); a
/// hole the stream left is repaired from the exchange in the background. Series are shared between agents and
/// released by the last one. Closed candles inside the retention period are written through to <c>candles</c>
/// (FR-DATA-05). Futures symbols have their mark price followed (FR-DATA-04). An evaluation gets a window of exactly
/// the configured depth ending with its candle, so the engine's seeded indicators are the same on every run.
/// </summary>
internal sealed class CandleFeed : IMarketDataLoader, IDisposable
{
    /// <summary>Backfills started by tracking run at most this many at a time; an evaluation that needs a series loads it at once regardless.</summary>
    internal const int MaxConcurrentBackfills = 4;

    /// <summary>A hole of at most this many candles is closed with one ranged read; a wider one with a fresh backfill.</summary>
    internal const int MaxRangedRepair = 1000;

    private readonly IExchangeClientFactory _exchange;
    private readonly IMarketStreams _streams;
    private readonly FuturesMetrics _metrics;
    private readonly CandleStore _store;
    private readonly IOptions<MarketDataOptions> _options;
    private readonly IOptions<RetentionOptions> _retention;
    private readonly TimeProvider _time;
    private readonly ILogger<CandleFeed> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<(MarketKey Key, CandleInterval Interval), CandleSeries> _series = [];
    private readonly Dictionary<Guid, AgentHold> _agents = [];
    private readonly SemaphoreSlim _backfills = new(MaxConcurrentBackfills, MaxConcurrentBackfills);
    private bool _disposed;

    public CandleFeed(
        IExchangeClientFactory exchange,
        IMarketStreams streams,
        FuturesMetrics metrics,
        CandleStore store,
        IOptions<MarketDataOptions> options,
        IOptions<RetentionOptions> retention,
        TimeProvider time,
        ILogger<CandleFeed> logger)
    {
        _exchange = exchange;
        _streams = streams;
        _metrics = metrics;
        _store = store;
        _options = options;
        _retention = retention;
        _time = time;
        _logger = logger;
    }

    /// <summary>The series held right now, for tests and the status page.</summary>
    internal IReadOnlyList<CandleSeries> Series
    {
        get
        {
            lock (_lock)
                return _series.Values.ToList();
        }
    }

    internal CandleSeries? Find(MarketKey key, CandleInterval interval)
    {
        lock (_lock)
            return _series.GetValueOrDefault((key, interval));
    }

    // --- IMarketDataLoader ----------------------------------------------------------------------------------------

    public void Track(MarketDataNeeds needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var wanted = Needed(needs.Key, needs.Timeframe, needs.Strategy);
        var started = new List<CandleSeries>();
        var released = new List<CandleSeries>();
        lock (_lock)
        {
            if (_disposed)
                return;
            var hold = HoldLocked(needs.AgentId);
            foreach (var id in wanted)
            {
                if (hold.Series.Add(id))
                    AcquireLocked(needs.AgentId, id.Key, id.Interval, started);
            }

            foreach (var id in hold.Series.Where(id => !wanted.Contains(id)).ToList())
            {
                hold.Series.Remove(id);
                ReleaseLocked(needs.AgentId, id, released);
            }

            if (needs.Key.Market == MarketType.UsdFutures)
                hold.Mark ??= _metrics.Acquire(needs.Key);
        }

        foreach (var series in released)
            Release(series);
        foreach (var series in started)
            _ = LoadInBackgroundAsync(series);
    }

    public void Untrack(Guid agentId)
    {
        var released = new List<CandleSeries>();
        IDisposable? mark;
        lock (_lock)
        {
            if (!_agents.Remove(agentId, out var hold))
                return;
            foreach (var id in hold.Series)
                ReleaseLocked(agentId, id, released);
            mark = hold.Mark;
        }

        mark?.Dispose();
        foreach (var series in released)
            Release(series);
    }

    public async Task<MarketSnapshot> LoadAsync(Agent agent, StrategyDescriptor strategy, MarketKey key, Candle closed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(closed);

        var timeframe = agent.Timeframe;
        var own = Acquire(agent.Id, key, timeframe);
        await EnsureReadyAsync(own, cancellationToken).ConfigureAwait(false);
        if (!own.Contains(closed.OpenTime))
        {
            switch (own.Insert(closed))
            {
                case InsertOutcome.Inserted:
                    PersistInBackground(own, [closed]);
                    if (own.Hole is { } hole)
                    {
                        _logger.LogWarning("Series {Series} is missing {Missing} candle(s) from {From:O} before the evaluated candle; repairing", own, hole.Missing, hole.From);
                        await EnsureReadyAsync(own, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case InsertOutcome.Older:
                    throw new InvalidOperationException($"Candle {closed.OpenTime:O} is older than the {own} series holds.");
                case InsertOutcome.NotClosed:
                    throw new ArgumentException("The evaluated candle must be closed.", nameof(closed));
            }
        }

        var candles = own.Window(closed.OpenTime, own.Depth);
        if (candles.Count == 0 || candles[^1].OpenTime != closed.OpenTime)
            throw new InvalidOperationException($"Series {own} does not hold the evaluated candle {closed.OpenTime:O}.");

        var cutoff = closed.OpenTime + timeframe.ToTimeSpan();
        var windows = new Dictionary<(MarketKey, CandleInterval), IReadOnlyList<Candle>>();
        var higher = new Dictionary<CandleInterval, IReadOnlyList<Candle>>();
        foreach (var interval in strategy.HigherTimeframes.Where(i => i != timeframe).Distinct())
            higher[interval] = await WindowAsync(agent.Id, key, interval, cutoff, windows, tolerate: false, cancellationToken).ConfigureAwait(false);

        var references = new Dictionary<ReferenceSeries, IReadOnlyList<Candle>>();
        foreach (var reference in strategy.References.Distinct())
        {
            // Reference series always come from production public data, so a testnet agent judges the regime on real-market prices.
            var referenceKey = new MarketKey(ExchangeEnvironment.Production, key.Market, reference.Symbol);
            references[reference] = await WindowAsync(agent.Id, referenceKey, reference.Interval, cutoff, windows, tolerate: true, cancellationToken).ConfigureAwait(false);
        }

        var mark = key.Market == MarketType.UsdFutures ? await MarkPriceAsync(agent.Id, key, cancellationToken).ConfigureAwait(false) : null;
        return new MarketSnapshot(candles, higher, references, mark);
    }

    public void Dispose()
    {
        List<CandleSeries> series;
        List<IDisposable> marks;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            series = _series.Values.ToList();
            marks = _agents.Values.Select(h => h.Mark).OfType<IDisposable>().ToList();
            _series.Clear();
            _agents.Clear();
        }

        foreach (var mark in marks)
            mark.Dispose();
        foreach (var s in series)
            s.Dispose();
    }

    // --- Series lifecycle -----------------------------------------------------------------------------------------

    private static HashSet<(MarketKey Key, CandleInterval Interval)> Needed(MarketKey key, CandleInterval timeframe, StrategyDescriptor? strategy)
    {
        var needed = new HashSet<(MarketKey, CandleInterval)> { (key, timeframe) };
        if (strategy is null)
            return needed;
        foreach (var interval in strategy.HigherTimeframes.Where(i => i != timeframe))
            needed.Add((key, interval));
        foreach (var reference in strategy.References)
            needed.Add((new MarketKey(ExchangeEnvironment.Production, key.Market, reference.Symbol), reference.Interval));
        return needed;
    }

    private AgentHold HoldLocked(Guid agentId)
    {
        if (!_agents.TryGetValue(agentId, out var hold))
        {
            hold = new AgentHold();
            _agents[agentId] = hold;
        }

        return hold;
    }

    /// <summary>The series for an evaluation that asks for one the agent is not tracked on (a strategy upgraded while Paused); loaded by the caller.</summary>
    private CandleSeries Acquire(Guid agentId, MarketKey key, CandleInterval interval)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var hold = HoldLocked(agentId);
            hold.Series.Add((key, interval));
            return AcquireLocked(agentId, key, interval, started: null);
        }
    }

    private CandleSeries AcquireLocked(Guid agentId, MarketKey key, CandleInterval interval, List<CandleSeries>? started)
    {
        if (!_series.TryGetValue((key, interval), out var series))
        {
            series = new CandleSeries(key, interval, _options.Value.BackfillFor(interval));
            _series[(key, interval)] = series;
            try
            {
                series.Subscription = _streams.SubscribeCandles(key, interval, (candle, _) => OnClosedAsync(series, candle));
            }
            catch
            {
                _series.Remove((key, interval));
                series.Dispose();
                throw;
            }

            started?.Add(series);
            _logger.LogInformation("Series {Series} is followed; backfilling {Depth} candle(s)", series, series.Depth);
        }

        series.Users.Add(agentId);
        return series;
    }

    private void ReleaseLocked(Guid agentId, (MarketKey Key, CandleInterval Interval) id, List<CandleSeries> released)
    {
        if (!_series.TryGetValue(id, out var series))
            return;
        series.Users.Remove(agentId);
        if (series.Users.Count > 0)
            return;
        _series.Remove(id);
        released.Add(series);
    }

    private void Release(CandleSeries series)
    {
        series.Dispose();
        _logger.LogInformation("Series {Series} is no longer followed", series);
    }

    // --- Loading --------------------------------------------------------------------------------------------------

    /// <summary>The backfill tracking starts, or a repair the stream asked for, bounded so a restart with many agents stays orderly.</summary>
    private async Task LoadInBackgroundAsync(CandleSeries series)
    {
        try
        {
            await _backfills.WaitAsync(series.Cancellation.Token).ConfigureAwait(false);
            try
            {
                await EnsureReadyAsync(series, series.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _backfills.Release();
            }
        }
        catch (OperationCanceledException) when (series.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Series {Series} could not be loaded; the next evaluation tries again", series);
        }
    }

    /// <summary>
    /// Backfills a series not loaded yet, repairs its hole or refills one a repair left short, sharing a load in
    /// flight and trying once more after a failure; throws when the series is still not ready.
    /// </summary>
    private async Task EnsureReadyAsync(CandleSeries series, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 2 && !series.IsReady; attempt++)
        {
            try
            {
                await series.RunExclusiveAsync(token => LoadSeriesAsync(series, token), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (series.IsReady)
            return;
        if (series.Disposed)
            throw new InvalidOperationException($"Series {series} is no longer followed.");
        throw failure ?? new InvalidOperationException($"Series {series} could not be made contiguous.");
    }

    /// <summary>
    /// One load: a ranged read for a small hole, otherwise the configured depth of recent closed candles. Whatever is
    /// left before a hole is dropped; a series a ranged read left short is refilled by the next load, and one the
    /// exchange's own history cannot fill (a hole in what it returns, or fewer candles than the depth) is accepted
    /// as it is.
    /// </summary>
    private async Task LoadSeriesAsync(CandleSeries series, CancellationToken cancellationToken)
    {
        var data = _exchange.GetPublicData(series.Key.Environment, series.Key.Market);
        var hole = series.IsLoaded ? series.Hole : null;
        IReadOnlyList<Candle> inserted;
        if (hole is { Missing: <= MaxRangedRepair } gap)
        {
            var candles = await data.GetCandlesAsync(series.Key.Symbol, series.Interval, gap.From, gap.To, MaxRangedRepair, cancellationToken).ConfigureAwait(false);
            inserted = series.Merge(candles);
            _logger.LogInformation("Series {Series} repaired: {Inserted} of {Missing} missing candle(s) from {From:O} read", series, inserted.Count, gap.Missing, gap.From);
        }
        else
        {
            var candles = await data.GetRecentCandlesAsync(series.Key.Symbol, series.Interval, series.Depth, closedOnly: true, cancellationToken).ConfigureAwait(false);
            inserted = series.Merge(candles);
            series.MarkLoaded(exhausted: candles.Count < series.Depth || !CandleSeries.IsContiguous(candles, series.Span));
            _logger.LogInformation("Series {Series} backfilled: {Inserted} candle(s) read, {Count} held from {First:O} to {Last:O}", series, inserted.Count, series.Count, series.First?.OpenTime, series.Last?.OpenTime);
        }

        var dropped = series.KeepContiguousTail();
        if (dropped > 0)
            _logger.LogWarning("Series {Series} dropped {Dropped} candle(s) before a hole the exchange did not fill; {Count} contiguous candle(s) remain", series, dropped, series.Count);
        PersistInBackground(series, inserted);
    }

    /// <summary>
    /// The window of a higher-timeframe or reference series closed by the cutoff. A candle closing exactly at the
    /// cutoff belongs in it; the stream may be a moment behind, so it is read from the exchange when missing.
    /// </summary>
    private async Task<IReadOnlyList<Candle>> WindowAsync(Guid agentId, MarketKey key, CandleInterval interval, DateTimeOffset cutoff, Dictionary<(MarketKey, CandleInterval), IReadOnlyList<Candle>> windows, bool tolerate, CancellationToken cancellationToken)
    {
        if (windows.TryGetValue((key, interval), out var computed))
            return computed;

        var series = Acquire(agentId, key, interval);
        try
        {
            await EnsureReadyAsync(series, cancellationToken).ConfigureAwait(false);
            await EnsureClosedByAsync(series, cutoff, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (tolerate)
        {
            _logger.LogWarning(ex, "Reference series {Series} for agent {AgentId} could not be loaded; evaluating with the {Count} candle(s) it holds", series, agentId, series.Count);
        }

        var window = series.WindowClosedBy(cutoff, series.Depth);
        windows[(key, interval)] = window;
        return window;
    }

    private async Task EnsureClosedByAsync(CandleSeries series, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var required = series.Interval.OpenTimeOf(cutoff.AddTicks(-1));
        if (required + series.Span > cutoff || series.Contains(required))
            return;

        var data = _exchange.GetPublicData(series.Key.Environment, series.Key.Market);
        var from = series.Last is { } last && last.OpenTime < required ? last.OpenTime + series.Span : required;
        var candles = await data.GetCandlesAsync(series.Key.Symbol, series.Interval, from, required, MaxRangedRepair, cancellationToken).ConfigureAwait(false);
        var inserted = series.Merge(candles);
        if (inserted.Count > 0)
        {
            _logger.LogDebug("Series {Series}: the candle closing at {Cutoff:O} was read from the exchange ahead of the stream", series, cutoff);
            PersistInBackground(series, inserted);
        }
        else
        {
            _logger.LogDebug("Series {Series}: the candle closing at {Cutoff:O} is not available yet", series, cutoff);
        }
    }

    private async Task<MarkPrice?> MarkPriceAsync(Guid agentId, MarketKey key, CancellationToken cancellationToken)
    {
        if (_metrics.Latest(key) is { } streamed)
            return streamed;
        try
        {
            return await _exchange.GetPublicData(key.Environment, key.Market).GetMarkPriceAsync(key.Symbol, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mark price of {Symbol} for agent {AgentId} could not be read; evaluating without it", key.Symbol, agentId);
            return null;
        }
    }

    // --- Stream -----------------------------------------------------------------------------------------------------

    /// <summary>Runs ahead of the scheduler's intake on the shared stream, so it only inserts; a hole is repaired in the background.</summary>
    private ValueTask OnClosedAsync(CandleSeries series, Candle candle)
    {
        if (series.Insert(candle) != InsertOutcome.Inserted)
            return default;
        PersistInBackground(series, [candle]);
        if (series.IsLoaded && series.Hole is { } hole)
        {
            _logger.LogWarning("Series {Series} is missing {Missing} candle(s) from {From:O}; repairing", series, hole.Missing, hole.From);
            _ = LoadInBackgroundAsync(series);
        }

        return default;
    }

    // --- Persistence ------------------------------------------------------------------------------------------------

    /// <summary>Writes the candles inside the retention period through to <c>candles</c>; a failure is logged and never fails the series (FR-DATA-05).</summary>
    private void PersistInBackground(CandleSeries series, IReadOnlyList<Candle> candles)
    {
        var cutoff = _time.GetUtcNow() - _retention.Value.CandleRetention;
        var kept = candles.Where(c => c.OpenTime >= cutoff).ToList();
        if (kept.Count > 0)
            _ = PersistAsync(series, kept);
    }

    private async Task PersistAsync(CandleSeries series, IReadOnlyList<Candle> candles)
    {
        try
        {
            await _store.StoreManyAsync(series.Key, series.Interval, candles, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Count} candle(s) of {Series} could not be stored", candles.Count, series);
        }
    }

    private sealed class AgentHold
    {
        public HashSet<(MarketKey Key, CandleInterval Interval)> Series { get; } = [];

        public IDisposable? Mark { get; set; }
    }
}
