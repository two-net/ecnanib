using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;

namespace Ecnanib.Exchange.Streaming;

/// <summary>
/// Closed-candle stream for one symbol and interval. Every closed candle is delivered exactly once, whether it came
/// from the socket, from the polling fallback or from the late-close safety poll (FR-AGT-09 depends on this).
/// </summary>
internal sealed class CandleStreamSubscription : StreamSubscriptionBase<Candle>
{
    private const int GapFillLimit = 1000;

    private readonly IMarketStreamSource _source;
    private readonly MarketKey _key;
    private readonly CandleInterval _interval;
    private readonly object _dedupeLock = new();
    private DateTimeOffset? _lastDeliveredOpen;

    public CandleStreamSubscription(MarketKey key, CandleInterval interval, IMarketStreamSource source, ExchangeStreamOptions options, TimeProvider time, ILogger logger)
        : base(NameFor(key, interval), options, time, logger)
    {
        _key = key;
        _interval = interval;
        _source = source;
    }

    public static string NameFor(MarketKey key, CandleInterval interval) =>
        $"candles:{key.Environment}:{key.Market}:{key.Symbol}:{interval.ToLabel()}".ToLowerInvariant();

    public DateTimeOffset? LastDeliveredOpen
    {
        get
        {
            lock (_dedupeLock)
            {
                return _lastDeliveredOpen;
            }
        }
    }

    protected override Task<StreamSubscriptionHandle> SubscribeAsync(CancellationToken ct) =>
        _source.SubscribeCandlesAsync(_key, _interval, OnStreamUpdate, ct);

    protected override bool Accept(Candle candle)
    {
        if (!candle.IsClosed)
            return false;

        lock (_dedupeLock)
        {
            if (_lastDeliveredOpen is { } last && candle.OpenTime <= last)
                return false;
            _lastDeliveredOpen = candle.OpenTime;
            return true;
        }
    }

    protected override async Task PollAsync(CancellationToken ct)
    {
        var last = LastDeliveredOpen;
        var candles = last is { } open
            ? await _source.PollCandlesAsync(_key, _interval, open + _interval.ToTimeSpan(), GapFillLimit, ct).ConfigureAwait(false)
            : await _source.PollCandlesAsync(_key, _interval, null, 2, ct).ConfigureAwait(false);

        foreach (var candle in candles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime))
            Deliver(candle);
    }

    /// <summary>Polls shortly after each boundary: a short grace while degraded, a longer one as a safety net while connected.</summary>
    protected override DateTimeOffset NextPollAt(DateTimeOffset now, bool connected)
    {
        var grace = connected ? Options.LateCloseGrace : Options.CandleCloseGrace;
        var candidate = CandleClock.OpenTimeOf(now, _interval) + grace;
        return candidate > now ? candidate : candidate + _interval.ToTimeSpan();
    }

    /// <summary>While connected, poll only if the candle that should have closed at the last boundary was not delivered.</summary>
    protected override bool ShouldPollWhenConnected(DateTimeOffset now)
    {
        var expectedOpen = CandleClock.OpenTimeOf(now, _interval) - _interval.ToTimeSpan();
        var last = LastDeliveredOpen;
        return last is null || last < expectedOpen;
    }
}
