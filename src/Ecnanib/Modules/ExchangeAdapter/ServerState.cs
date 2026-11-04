using System.Globalization;
using System.Net.Http.Headers;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>An order-rate limit, such as 100 orders in 10 seconds.</summary>
internal sealed record OrderLimit(TimeSpan Interval, int Limit);

/// <summary>Why a request wasn't sent, and when it may be.</summary>
internal sealed record Refusal(ExchangeFailure Failure, string Reason, TimeSpan RetryAfter);

/// <summary>
/// What this process knows about one Binance server: request weight, bans and backoff (EXT-BIN-04),
/// order counts per key, and the clock offset (FR-EXE-10). It's in memory only, because the weight it
/// tracks resets every minute, and Binance reports it again on every response.
/// </summary>
internal sealed class ServerState
{
    private const string UsedWeightHeader = "X-MBX-USED-WEIGHT-";
    private const string OrderCountHeader = "X-MBX-ORDER-COUNT-";

    private static readonly TimeSpan WeightInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Requests in flight together each get a 418, a second or so apart, and a ban that much longer is
    /// the same ban.
    /// </summary>
    private static readonly TimeSpan BanTolerance = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly int _headroomPercent;
    private readonly Dictionary<Guid, KeyOrders> _keys = [];
    private readonly Dictionary<(Guid, string), DateTimeOffset> _unsettled = [];

    private int _weightLimit;
    private IReadOnlyList<OrderLimit> _orderLimits;
    private int _observedWeight;
    private long _observedWeightWindow = -1;
    private int _reservedWeight;
    private DateTimeOffset? _backoffUntil;
    private DateTimeOffset? _bannedUntil;
    private TimeSpan? _clockOffset;
    private DateTimeOffset? _clockSyncedAt;
    private DateTimeOffset _clockRetryAt;

    public ServerState(ExchangeServer server, Uri baseUrl, int headroomPercent)
    {
        Server = server;
        BaseUrl = baseUrl;
        _headroomPercent = headroomPercent;

        // Binance's published limits, until exchange information gives the current ones.
        if (ExchangeServers.MarketOf(server) == Market.Futures)
        {
            _weightLimit = 2400;
            _orderLimits = [new OrderLimit(TimeSpan.FromSeconds(10), 300), new OrderLimit(TimeSpan.FromMinutes(1), 1200)];
        }
        else
        {
            _weightLimit = 6000;
            _orderLimits = [new OrderLimit(TimeSpan.FromSeconds(10), 100), new OrderLimit(TimeSpan.FromDays(1), 200_000)];
        }
    }

    public ExchangeServer Server { get; }

    public Uri BaseUrl { get; }

    /// <summary>One clock sync at a time, so a burst of signed requests makes one time request.</summary>
    public SemaphoreSlim ClockGate { get; } = new(1, 1);

    /// <summary>Binance's time minus this server's. Zero before the first sync.</summary>
    public TimeSpan ClockOffset
    {
        get
        {
            lock (_gate)
            {
                return _clockOffset ?? TimeSpan.Zero;
            }
        }
    }

    public DateTimeOffset? ClockSyncedAt
    {
        get
        {
            lock (_gate)
            {
                return _clockSyncedAt;
            }
        }
    }

    /// <summary>Until then, a failed sync keeps the old offset rather than trying again on every request.</summary>
    public DateTimeOffset ClockRetryAt
    {
        get
        {
            lock (_gate)
            {
                return _clockRetryAt;
            }
        }
    }

    public void SetClock(TimeSpan offset, DateTimeOffset syncedAt)
    {
        lock (_gate)
        {
            _clockOffset = offset;
            _clockSyncedAt = syncedAt;
        }
    }

    public void DeferClockSync(DateTimeOffset until)
    {
        lock (_gate)
        {
            _clockRetryAt = until;
        }
    }

    /// <summary>
    /// Remembers an order sent with an unknown outcome, which Binance could still accept until
    /// <paramref name="settlesAt"/> (FR-EXE-05).
    /// </summary>
    public void RecordUnsettled(Guid credentialId, string clientOrderId, DateTimeOffset settlesAt, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var settled in _unsettled.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToList())
            {
                _unsettled.Remove(settled);
            }

            _unsettled[(credentialId, clientOrderId)] = settlesAt;
        }
    }

    /// <summary>When an order sent with an unknown outcome stops being able to take effect, or null when it can't any more.</summary>
    public DateTimeOffset? UnsettledUntil(Guid credentialId, string clientOrderId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_unsettled.TryGetValue((credentialId, clientOrderId), out var settlesAt))
            {
                return null;
            }

            if (settlesAt > now)
            {
                return settlesAt;
            }

            _unsettled.Remove((credentialId, clientOrderId));

            return null;
        }
    }

    /// <summary>
    /// Reserves the request's weight, and an order for an endpoint that places one, or says why it can't
    /// be sent. A ban stops everything. Normal requests stop at the headroom, while protective ones may
    /// use it (FR-EXE-09). Release the reservation once the response is in.
    /// </summary>
    public Refusal? TryReserve(BinanceEndpoint endpoint, RequestPriority priority, Guid? credentialId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_bannedUntil > now)
            {
                return new Refusal(ExchangeFailure.Banned, "Binance has banned this server's address", _bannedUntil.Value - now);
            }

            if (_backoffUntil > now)
            {
                return new Refusal(ExchangeFailure.Throttled, "backing off after HTTP 429", _backoffUntil.Value - now);
            }

            var serverNow = now + (_clockOffset ?? TimeSpan.Zero);
            var weight = Math.Max(endpoint.Weight, 1);

            if (UsedWeight(serverNow) + weight > Cap(_weightLimit, priority))
            {
                return new Refusal(ExchangeFailure.Throttled, "the request weight would pass the headroom", UntilWindowEnds(serverNow, WeightInterval));
            }

            KeyOrders? orders = null;

            if (endpoint.PlacesOrder && credentialId is { } id)
            {
                orders = KeyFor(id);

                if (orders.BackoffUntil > now)
                {
                    return new Refusal(ExchangeFailure.Throttled, "backing off the key after its order-rate limit", orders.BackoffUntil.Value - now);
                }

                foreach (var limit in _orderLimits)
                {
                    if (orders.Count(limit.Interval, serverNow) + orders.Reserved + 1 > Cap(limit.Limit, priority))
                    {
                        return new Refusal(ExchangeFailure.Throttled, "the key's order-rate limit would be reached", UntilWindowEnds(serverNow, limit.Interval));
                    }
                }
            }

            _reservedWeight += weight;

            if (orders is not null)
            {
                orders.Reserved++;
            }

            return null;
        }
    }

    public void Release(BinanceEndpoint endpoint, Guid? credentialId)
    {
        lock (_gate)
        {
            _reservedWeight -= Math.Max(endpoint.Weight, 1);

            if (endpoint.PlacesOrder && credentialId is { } id)
            {
                KeyFor(id).Reserved--;
            }
        }
    }

    /// <summary>
    /// Takes the used weight and order counts from a response's headers, and returns the weight Binance
    /// reports in use with its limit, for the log (FR-LOG-08). That leaves out requests still in flight,
    /// including this one's own reservation, which Binance has already counted.
    /// </summary>
    public (int UsedWeight, int WeightLimit) Observe(HttpResponseHeaders headers, Guid? credentialId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var serverNow = now + (_clockOffset ?? TimeSpan.Zero);

            foreach (var (name, values) in headers)
            {
                if (name.StartsWith(UsedWeightHeader, StringComparison.OrdinalIgnoreCase)
                    && TryParseInterval(name.AsSpan(UsedWeightHeader.Length), out var interval)
                    && interval == WeightInterval
                    && TryParseCount(values, out var used))
                {
                    // Responses to concurrent requests arrive in any order, and weight only grows within a window.
                    var window = Window(serverNow, WeightInterval);
                    _observedWeight = window == _observedWeightWindow ? Math.Max(_observedWeight, used) : used;
                    _observedWeightWindow = window;
                }
                else if (credentialId is { } id
                    && name.StartsWith(OrderCountHeader, StringComparison.OrdinalIgnoreCase)
                    && TryParseInterval(name.AsSpan(OrderCountHeader.Length), out var orderInterval)
                    && TryParseCount(values, out var count))
                {
                    KeyFor(id).Observe(orderInterval, count, serverNow);
                }
            }

            return (ObservedWeight(serverNow), _weightLimit);
        }
    }

    /// <summary>After HTTP 429: wait as long as Binance says, or until the minute ends.</summary>
    public DateTimeOffset BackOff(TimeSpan? retryAfter, DateTimeOffset now)
    {
        lock (_gate)
        {
            var until = now + (retryAfter ?? UntilWindowEnds(now + (_clockOffset ?? TimeSpan.Zero), WeightInterval));
            _backoffUntil = until > _backoffUntil ? until : _backoffUntil ?? until;

            return _backoffUntil.Value;
        }
    }

    /// <summary>After HTTP 429 for too many orders (-1015), which is the key's limit, not the server's.</summary>
    public DateTimeOffset BackOffKey(Guid credentialId, TimeSpan? retryAfter, DateTimeOffset now)
    {
        lock (_gate)
        {
            var orders = KeyFor(credentialId);
            var shortest = _orderLimits.Count == 0 ? TimeSpan.FromSeconds(10) : _orderLimits.Min(limit => limit.Interval);
            var until = now + (retryAfter ?? UntilWindowEnds(now + (_clockOffset ?? TimeSpan.Zero), shortest));
            orders.BackoffUntil = until > orders.BackoffUntil ? until : orders.BackoffUntil ?? until;

            return orders.BackoffUntil.Value;
        }
    }

    /// <summary>
    /// After HTTP 418. True when it's a new ban, or clearly a longer one, so observers hear of each ban once.
    /// </summary>
    public bool Ban(DateTimeOffset until, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_bannedUntil is { } current && current > now && until <= current + BanTolerance)
            {
                _bannedUntil = until > current ? until : current;

                return false;
            }

            _bannedUntil = until;

            return true;
        }
    }

    /// <summary>The current limits, from exchange information. A limit it doesn't give keeps its previous value.</summary>
    public void SetLimits(int? weightLimit, IReadOnlyList<OrderLimit> orderLimits)
    {
        lock (_gate)
        {
            if (weightLimit > 0)
            {
                _weightLimit = weightLimit.Value;
            }

            if (orderLimits.Count > 0)
            {
                _orderLimits = orderLimits;
            }
        }
    }

    public ExchangeServerUsage Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new ExchangeServerUsage(
                Server,
                UsedWeight(now + (_clockOffset ?? TimeSpan.Zero)),
                _weightLimit,
                _headroomPercent,
                _backoffUntil > now ? _backoffUntil : null,
                _bannedUntil > now ? _bannedUntil : null,
                _clockOffset,
                _clockSyncedAt);
        }
    }

    /// <summary>A header's interval, such as 1M or 10S, as Binance writes it (SECOND, MINUTE, HOUR, DAY).</summary>
    internal static bool TryParseInterval(ReadOnlySpan<char> text, out TimeSpan interval)
    {
        interval = default;

        if (text.Length < 2 || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            return false;
        }

        interval = char.ToUpperInvariant(text[^1]) switch
        {
            'S' => TimeSpan.FromSeconds(number),
            'M' => TimeSpan.FromMinutes(number),
            'H' => TimeSpan.FromHours(number),
            'D' => TimeSpan.FromDays(number),
            _ => default,
        };

        return interval > TimeSpan.Zero;
    }

    /// <summary>Binance's windows are fixed, aligned to the epoch in its own time, so a minute starts on the minute.</summary>
    private static long Window(DateTimeOffset serverNow, TimeSpan interval) =>
        serverNow.ToUnixTimeMilliseconds() / (long)interval.TotalMilliseconds;

    private static TimeSpan UntilWindowEnds(DateTimeOffset serverNow, TimeSpan interval)
    {
        var length = (long)interval.TotalMilliseconds;
        var elapsed = serverNow.ToUnixTimeMilliseconds() % length;

        return TimeSpan.FromMilliseconds(length - elapsed);
    }

    private static bool TryParseCount(IEnumerable<string> values, out int count)
    {
        count = 0;
        var value = values.LastOrDefault();

        return value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    private int ObservedWeight(DateTimeOffset serverNow) =>
        _observedWeightWindow == Window(serverNow, WeightInterval) ? _observedWeight : 0;

    private int UsedWeight(DateTimeOffset serverNow) => ObservedWeight(serverNow) + _reservedWeight;

    private int Cap(int limit, RequestPriority priority) =>
        priority == RequestPriority.Protective ? limit : limit * (100 - _headroomPercent) / 100;

    private KeyOrders KeyFor(Guid credentialId)
    {
        if (!_keys.TryGetValue(credentialId, out var orders))
        {
            orders = new KeyOrders();
            _keys[credentialId] = orders;
        }

        return orders;
    }

    /// <summary>One key's orders, which Binance limits per account (EXT-BIN-04).</summary>
    private sealed class KeyOrders
    {
        private readonly Dictionary<TimeSpan, (int Count, long Window)> _observed = [];

        public int Reserved { get; set; }

        public DateTimeOffset? BackoffUntil { get; set; }

        public void Observe(TimeSpan interval, int count, DateTimeOffset serverNow)
        {
            var window = Window(serverNow, interval);

            _observed[interval] = _observed.TryGetValue(interval, out var observed) && observed.Window == window
                ? (Math.Max(observed.Count, count), window)
                : (count, window);
        }

        public int Count(TimeSpan interval, DateTimeOffset serverNow) =>
            _observed.TryGetValue(interval, out var observed) && observed.Window == Window(serverNow, interval) ? observed.Count : 0;
    }
}
