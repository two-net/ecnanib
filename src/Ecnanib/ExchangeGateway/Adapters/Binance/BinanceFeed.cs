using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The account event feeds of Binance (FR-BIN-7): one connection for each account that is
/// followed, on which Binance says what changes in it, as it happens. What it hears leaves here
/// as <see cref="ExchangeAccountChange"/>: that something of a symbol changed in an account, and
/// nothing of what. See "The account event feed" in docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the Futures market</b> a call that carries the API key alone begins the stream of an
/// account, and Binance answers with a key that names the stream, which is then read at the
/// address of the feed. The key of the stream is valid for an hour, and the same call that
/// renews it is made every half hour, which is when <see cref="StateOf"/> says that a renewal
/// is due. Nothing here keeps the key of a stream: it is in the address that the connection was
/// opened with, and nowhere else.
/// </para>
/// <para>
/// <b>On the Spot market</b> the feed is Binance's WebSocket API: one request is sent on the
/// connection, which subscribes to the events of the account of the API key, signed with the
/// secret key as a call is. That is the one thing that a connection ever sends. It needs no
/// renewal.
/// </para>
/// <para>
/// <b>Neither key of a credential is kept</b> (FR-KEY-9): each is used for the call that it
/// came with, and the bytes that the secret key was read into are cleared. No log event holds a
/// key, the key of a stream, or anything that Binance said of an account: an event names the
/// user, the market, the environment, and the host.
/// </para>
/// <para>
/// <b>Opening a feed costs of the limit on requests</b>, so it passes the limiter (FR-BIN-4),
/// and no more than a few are opened in a minute, whatever asks: Binance also limits how many
/// connections an address opens. An attempt that Binance answers with its signal of too many
/// requests tells the limiter.
/// </para>
/// <para>
/// It follows in memory, for one process, as the limiter counts. A feed that nobody kept for
/// <see cref="ExchangeFeedTimes.IdleAfter"/> is closed, so an instance that no longer does the
/// background work follows nothing after that.
/// </para>
/// </remarks>
internal sealed class BinanceFeed : IExchangeAccountFeed, IDisposable
{
    /// <summary>How long after it was begun or renewed the feed of the Futures market is renewed: half of what its key is valid for.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(30);

    /// <summary>How many feeds are opened in a minute, at most, for all users.</summary>
    public const int MostOpenedAMinute = 20;

    /// <summary>
    /// How long Binance may take to take a connection. It is less than a call may take: a
    /// connection is made in a moment or not at all, and an address that does not answer keeps
    /// whoever follows the accounts from listening to the feeds that are open.
    /// </summary>
    public static readonly TimeSpan ConnectsWithin = TimeSpan.FromSeconds(5);

    // The request that subscribes to the events of an account, on the Spot market.
    private const string Subscribe = "userDataStream.subscribe.signature";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    // How often the feeds that nobody kept are looked for.
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    private readonly BinanceCall _call;
    private readonly BinanceLimiter _limiter;
    private readonly BinanceClock _watch;
    private readonly BinanceSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<BinanceFeed> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<Account, Followed> _followed = [];
    private readonly HashSet<Account> _opening = [];
    private readonly Queue<long> _opened = new();
    private readonly HashSet<ExchangeAccountChange> _changes = [];
    private readonly ITimer _sweep;
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public BinanceFeed(
        BinanceCall call, BinanceLimiter limiter, BinanceClock watch, BinanceSettings settings, TimeProvider clock, ILogger<BinanceFeed> logger)
    {
        _call = call;
        _limiter = limiter;
        _watch = watch;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _sweep = clock.CreateTimer(_ => EndWhatNobodyKept(), null, Sweep, Sweep);
    }

    public bool IsAvailable(Market market, TradingMode mode) => _settings.FeedUrl(market, mode) is not null;

    public ExchangeFeedState StateOf(Guid userId, Market market, TradingMode mode)
    {
        lock (_gate)
        {
            if (!_followed.TryGetValue(new Account(userId, market, mode), out var followed))
            {
                return ExchangeFeedState.NotFollowed;
            }

            return followed.RenewAt is { } due && _clock.GetTimestamp() >= due ? ExchangeFeedState.RenewalDue : ExchangeFeedState.Followed;
        }
    }

    public void Keep(Guid userId, Market market, TradingMode mode)
    {
        lock (_gate)
        {
            if (_followed.TryGetValue(new Account(userId, market, mode), out var followed))
            {
                followed.KeptAt = _clock.GetTimestamp();
            }
        }
    }

    public async Task<ExchangeFeedOutcome> FollowAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        // Said without what was given: a key is in no error.
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A feed is of the account of a user, and says whose.", nameof(userId));
        }

        if (_settings.FeedUrl(market, mode) is not { } address)
        {
            return ExchangeFeedOutcome.NotAvailable;
        }

        var account = new Account(userId, market, mode);
        Followed? followed;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_followed.TryGetValue(account, out followed))
            {
                followed.KeptAt = _clock.GetTimestamp();
            }
            else if (!_opening.Add(account))
            {
                // Another is opening it at this moment.
                return ExchangeFeedOutcome.NotNow;
            }
        }

        if (followed is not null)
        {
            return await RenewAsync(account, followed, apiKey, cancellationToken);
        }

        try
        {
            return await OpenAsync(account, address, apiKey, secretKey, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _opening.Remove(account);
            }
        }
    }

    public async Task<IReadOnlyList<ExchangeAccountChange>> TakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        Task arrived;

        lock (_gate)
        {
            if (_changes.Count > 0 || wait <= TimeSpan.Zero)
            {
                return Taken();
            }

            arrived = _arrived.Task;
        }

        try
        {
            await arrived.WaitAsync(wait, _clock, cancellationToken);
        }
        catch (TimeoutException)
        {
            // None came in that time.
        }

        lock (_gate)
        {
            return Taken();
        }
    }

    public void Dispose()
    {
        List<Followed> ended;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ended = [.. _followed.Values];
        }

        _sweep.Dispose();

        foreach (var followed in ended)
        {
            followed.End(BinanceFeedEnd.Stopped);
        }
    }

    /// <summary>
    /// Renews the feed of an account that is followed, where it is due: on the Futures market
    /// Binance ends a stream that is not renewed within an hour. A feed that is not due, and one
    /// of the Spot market, is followed as it is.
    /// </summary>
    private async Task<ExchangeFeedOutcome> RenewAsync(Account account, Followed followed, string apiKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (followed.RenewAt is not { } due || _clock.GetTimestamp() < due)
            {
                return ExchangeFeedOutcome.Following;
            }
        }

        var renewed = await _call.AskWithKeyAsync(
            BinanceEndpoint.FuturesFeedKeep, account.Mode, account.UserId, apiKey, answer => (bool?)(answer.ValueKind == JsonValueKind.Object), cancellationToken);

        if (renewed.Outcome != ExchangeCallOutcome.Succeeded)
        {
            // It stays as it is: a stream that Binance ends says so, and is opened anew then.
            return OutcomeOf(renewed.Outcome);
        }

        lock (_gate)
        {
            followed.RenewAt = _clock.GetTimestamp() + Ticks(RenewEvery);
        }

        return ExchangeFeedOutcome.Following;
    }

    /// <summary>Opens the feed of an account, and reads it from then on.</summary>
    private async Task<ExchangeFeedOutcome> OpenAsync(
        Account account, Uri address, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        // So many in a minute, and no more, for all users: Binance limits how many connections
        // an address opens, and a start of the application would open one for every account at once.
        lock (_gate)
        {
            var now = _clock.GetTimestamp();

            while (_opened.TryPeek(out var at) && _clock.GetElapsedTime(at, now) >= Minute)
            {
                _opened.Dequeue();
            }

            if (_opened.Count >= MostOpenedAMinute)
            {
                return ExchangeFeedOutcome.NotNow;
            }

            _opened.Enqueue(now);
        }

        var began = _clock.GetTimestamp();
        var target = address;
        long? renewAt = null;

        if (account.Market == Market.Futures)
        {
            // The key that names the stream of the account. It passes the limiter with the call.
            var started = await _call.AskWithKeyAsync(
                BinanceEndpoint.FuturesFeedStart, account.Mode, account.UserId, apiKey, StreamKeyOf, cancellationToken);

            if (started.Outcome != ExchangeCallOutcome.Succeeded)
            {
                return NotFollowed(account, address, OutcomeOf(started.Outcome));
            }

            target = new Uri($"{address.AbsoluteUri.TrimEnd('/')}/ws/{Uri.EscapeDataString(started.Value!)}");
            renewAt = _clock.GetTimestamp() + Ticks(RenewEvery);
        }
        else if (!await _limiter.EnterAsync(BinanceEndpoint.SpotFeed, account.Mode, account.UserId, cancellationToken))
        {
            return NotFollowed(account, address, ExchangeFeedOutcome.NotNow);
        }

        var endpoint = account.Market == Market.Futures ? BinanceEndpoint.FuturesFeedStart : BinanceEndpoint.SpotFeed;
        var (connection, status) = await BinanceFeedConnection.OpenAsync(target, ConnectsWithin, _clock, cancellationToken);

        if (connection is null)
        {
            // Binance's signal that it has had too many, which holds for the calls as well.
            if (status is 429 or 418)
            {
                _limiter.Throttled(endpoint, account.Mode, banned: status == 418, retryAfter: null);

                return NotFollowed(account, address, ExchangeFeedOutcome.NotNow);
            }

            return NotFollowed(account, address, ExchangeFeedOutcome.Failed);
        }

        if (account.Market == Market.Spot)
        {
            ExchangeFeedOutcome? refused;

            try
            {
                refused = await SubscribeAsync(account, connection, apiKey, secretKey, cancellationToken);
            }
            catch
            {
                // A caller that cancels, or an answer that nothing here reads: the connection is let go.
                connection.Dispose();

                throw;
            }

            if (refused is { } why)
            {
                connection.Dispose();

                return NotFollowed(account, address, why);
            }
        }

        var followed = new Followed(connection, _clock.GetTimestamp(), renewAt);

        lock (_gate)
        {
            if (_disposed)
            {
                connection.Dispose();

                return ExchangeFeedOutcome.Failed;
            }

            _followed[account] = followed;
        }

        _logger.LogInformation(
            "The account event feed of user {UserId} on {Market} {Environment} is followed, at {Host}, after {DurationMs} ms",
            account.UserId,
            account.Market.ToString(),
            account.Mode.ToString(),
            address.Host,
            Math.Round(_clock.GetElapsedTime(began).TotalMilliseconds, 1));

        // What happened in the account before this was not heard: everything of it is asked about.
        Say(new ExchangeAccountChange(account.UserId, account.Market, account.Mode, Symbol: null));

        followed.Reading = Task.Run(() => ReadAsync(account, address, followed));

        return ExchangeFeedOutcome.Following;
    }

    /// <summary>
    /// Sends the request that subscribes to the events of an account, on the Spot market, and
    /// waits for Binance to answer it. The request carries the API key, the time, and their
    /// signature under the secret key, as a signed call does, and nothing else.
    /// </summary>
    /// <returns>Why the account is not followed, or null when Binance took the subscription.</returns>
    private async Task<ExchangeFeedOutcome?> SubscribeAsync(
        Account account, BinanceFeedConnection connection, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        // What is signed is the parameters in the order of their names, as Binance says.
        var signature = BinanceCall.Sign(
            $"apiKey={apiKey}&timestamp={timestamp.ToString(CultureInfo.InvariantCulture)}", secretKey);

        using var limit = new CancellationTokenSource(BinanceCall.Limit, _clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        try
        {
            if (!await connection.SubscribeAsync(RequestOf(id, apiKey, signature, timestamp), either.Token))
            {
                return ExchangeFeedOutcome.Failed;
            }

            // The answer to the request, which carries its id. Nothing else is expected before it.
            while (await connection.ReceiveAsync(either.Token) is { } message)
            {
                if (message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("id", out var answered)
                    || answered.ValueKind != JsonValueKind.String
                    || answered.GetString() != id)
                {
                    continue;
                }

                var status = message.TryGetProperty("status", out var said)
                    && said.ValueKind == JsonValueKind.Number
                    && said.TryGetInt32(out var number)
                        ? number
                        : (int?)null;

                if (status == 200)
                {
                    return null;
                }

                var code = message.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var named)
                    && named.ValueKind == JsonValueKind.Number
                    && named.TryGetInt32(out var errorCode)
                        ? errorCode
                        : (int?)null;

                if (code is { } logged)
                {
                    _logger.LogWarning(
                        "Binance answered the subscription to the account event feed on {Market} {Environment} with its error {BinanceCode}",
                        account.Market.ToString(),
                        account.Mode.ToString(),
                        logged);
                }

                // Binance's own word that the clock of the service is not near its own.
                if (BinanceCall.RefusesTime(code))
                {
                    _watch.Refused(account.Market, account.Mode);
                }

                if (status is 429 or 418)
                {
                    _limiter.Throttled(BinanceEndpoint.SpotFeed, account.Mode, banned: status == 418, retryAfter: null);

                    return ExchangeFeedOutcome.NotNow;
                }

                return BinanceCall.Refuses(status, code) ? ExchangeFeedOutcome.KeyRefused : ExchangeFeedOutcome.Failed;
            }

            return ExchangeFeedOutcome.Failed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Binance took longer than the limit.
            return ExchangeFeedOutcome.Failed;
        }
    }

    /// <summary>Reads the feed of an account until it ends, and says what it hears as changes.</summary>
    private async Task ReadAsync(Account account, Uri address, Followed followed)
    {
        var end = BinanceFeedEnd.Closed;

        try
        {
            while (true)
            {
                if (await followed.Connection.ReceiveAsync(followed.Ending) is not { } message)
                {
                    end = followed.Connection.Ended;

                    break;
                }

                var heard = BinanceFeedEvents.Read(account.Market, message);

                if (heard.Ends)
                {
                    end = BinanceFeedEnd.Expired;

                    break;
                }

                foreach (var symbol in heard.Symbols)
                {
                    Say(new ExchangeAccountChange(account.UserId, account.Market, account.Mode, symbol));
                }

                if (heard.Everything)
                {
                    Say(new ExchangeAccountChange(account.UserId, account.Market, account.Mode, Symbol: null));
                }
            }
        }
        catch (OperationCanceledException)
        {
            end = followed.EndedAs ?? BinanceFeedEnd.Stopped;
        }
        catch (Exception error)
        {
            // Nothing that an event holds may end the reading of the others without a word.
            end = BinanceFeedEnd.Broken;

            _logger.LogError(
                error,
                "The account event feed of user {UserId} on {Market} {Environment} could not be read",
                account.UserId,
                account.Market.ToString(),
                account.Mode.ToString());
        }

        bool stopped;

        lock (_gate)
        {
            if (_followed.TryGetValue(account, out var current) && ReferenceEquals(current, followed))
            {
                _followed.Remove(account);
            }

            stopped = _disposed;
        }

        followed.Dispose();

        var level = end == BinanceFeedEnd.Broken ? LogLevel.Warning : LogLevel.Information;

        _logger.Log(
            level,
            "The account event feed of user {UserId} on {Market} {Environment} ended, at {Host}: {FeedEnd}",
            account.UserId,
            account.Market.ToString(),
            account.Mode.ToString(),
            address.Host,
            end.ToString());

        // What happens in the account from here on is not heard: everything of it is asked
        // about, unless nobody wants to know any more.
        if (!stopped && end is not (BinanceFeedEnd.NotKept or BinanceFeedEnd.Stopped))
        {
            Say(new ExchangeAccountChange(account.UserId, account.Market, account.Mode, Symbol: null));
        }
    }

    /// <summary>Ends the feeds that nobody has said are still wanted for a while.</summary>
    private void EndWhatNobodyKept()
    {
        List<Followed> idle;

        lock (_gate)
        {
            var now = _clock.GetTimestamp();

            idle = [.. _followed.Values.Where(followed => _clock.GetElapsedTime(followed.KeptAt, now) >= ExchangeFeedTimes.IdleAfter)];
        }

        foreach (var followed in idle)
        {
            followed.End(BinanceFeedEnd.NotKept);
        }
    }

    private void Say(ExchangeAccountChange change)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _changes.Add(change);
            _arrived.TrySetResult();
        }
    }

    // Under the gate.
    private IReadOnlyList<ExchangeAccountChange> Taken()
    {
        if (_changes.Count == 0)
        {
            return [];
        }

        ExchangeAccountChange[] taken = [.. _changes];

        _changes.Clear();
        _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return taken;
    }

    private ExchangeFeedOutcome NotFollowed(Account account, Uri address, ExchangeFeedOutcome outcome)
    {
        _logger.LogWarning(
            "The account event feed of user {UserId} on {Market} {Environment} could not be followed, at {Host}: {ExchangeFeedOutcome}",
            account.UserId,
            account.Market.ToString(),
            account.Mode.ToString(),
            address.Host,
            outcome.ToString());

        return outcome;
    }

    private long Ticks(TimeSpan time) => (long)(time.TotalSeconds * _clock.TimestampFrequency);

    private static ExchangeFeedOutcome OutcomeOf(ExchangeCallOutcome outcome) => outcome switch
    {
        ExchangeCallOutcome.Succeeded => ExchangeFeedOutcome.Following,
        ExchangeCallOutcome.AuthenticationFailed => ExchangeFeedOutcome.KeyRefused,
        ExchangeCallOutcome.RateLimited => ExchangeFeedOutcome.NotNow,
        _ => ExchangeFeedOutcome.Failed,
    };

    /// <summary>The key that names the stream of an account, as Binance answers with it, or null when the answer has none.</summary>
    private static string? StreamKeyOf(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object
        && answer.TryGetProperty("listenKey", out var key)
        && key.ValueKind == JsonValueKind.String
        && key.GetString() is { Length: > 0 } named
            ? named
            : null;

    /// <summary>The request that subscribes to the events of an account, as Binance's WebSocket API takes it.</summary>
    private static string RequestOf(string id, string apiKey, string signature, long timestamp)
    {
        using var written = new MemoryStream();

        using (var json = new Utf8JsonWriter(written))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("method", Subscribe);
            json.WriteStartObject("params");
            json.WriteString("apiKey", apiKey);
            json.WriteString("signature", signature);
            json.WriteNumber("timestamp", timestamp);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(written.ToArray());
    }

    /// <summary>The account of a user on a market in an environment.</summary>
    private readonly record struct Account(Guid UserId, Market Market, TradingMode Mode);

    /// <summary>The feed of an account that is followed.</summary>
    private sealed class Followed(BinanceFeedConnection connection, long keptAt, long? renewAt) : IDisposable
    {
        private readonly CancellationTokenSource _ending = new();

        public BinanceFeedConnection Connection => connection;

        /// <summary>When somebody last said that the feed is wanted. Read and written under the gate.</summary>
        public long KeptAt { get; set; } = keptAt;

        /// <summary>When its renewal is due, or null for a feed that needs none. Read and written under the gate.</summary>
        public long? RenewAt { get; set; } = renewAt;

        /// <summary>The reading of the feed, which ends when the feed does.</summary>
        public Task? Reading { get; set; }

        /// <summary>Canceled when the feed is ended from here.</summary>
        public CancellationToken Ending => _ending.Token;

        /// <summary>Why it was ended from here, once it was.</summary>
        public BinanceFeedEnd? EndedAs { get; private set; }

        public void End(BinanceFeedEnd why)
        {
            EndedAs ??= why;

            try
            {
                _ending.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It has ended by itself meanwhile.
            }
        }

        public void Dispose()
        {
            connection.Dispose();
            _ending.Dispose();
        }
    }
}

/// <summary>
/// Reads what Binance says on an account event feed, as far as the gateway takes it: which
/// symbols an event is about, and nothing of what it says of them. See
/// <see cref="IExchangeAccountFeed"/>.
/// </summary>
/// <remarks>
/// <para>
/// On the Futures market an event names itself with <c>e</c>: an order that changed
/// (<c>ORDER_TRADE_UPDATE</c>, <c>TRADE_LITE</c>), a conditional order that changed
/// (<c>ALGO_UPDATE</c>), and what the account holds (<c>ACCOUNT_UPDATE</c>), each with its symbol,
/// and <c>listenKeyExpired</c>, with which Binance ends the stream. On the Spot market an event
/// comes inside <c>event</c>: an order (<c>executionReport</c>) and a list of orders
/// (<c>listStatus</c>), each with its symbol, and <c>eventStreamTerminated</c>, which ends the
/// subscription.
/// </para>
/// <para>
/// An event of one of those kinds that does not say its symbol where it is looked for is taken
/// as one about everything of the account. Anything else that Binance sends says nothing here.
/// </para>
/// </remarks>
internal static class BinanceFeedEvents
{
    /// <summary>What a message of a feed says.</summary>
    /// <param name="Symbols">The symbols that something changed in.</param>
    /// <param name="Everything">Whether it is about everything of the account: it is of a kind that says a change, and did not say where.</param>
    /// <param name="Ends">Whether Binance ended the stream of the account with it.</param>
    public readonly record struct Heard(IReadOnlyList<string> Symbols, bool Everything, bool Ends);

    private static readonly Heard Nothing = new([], Everything: false, Ends: false);
    private static readonly Heard Unread = new([], Everything: true, Ends: false);
    private static readonly Heard Ended = new([], Everything: false, Ends: true);

    public static Heard Read(Market market, JsonElement message)
    {
        // Not JSON, or longer than a message is: it may have said a change.
        if (message.ValueKind == JsonValueKind.Undefined)
        {
            return Unread;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return Nothing;
        }

        // An event of the Spot market is inside "event", and one of a combined stream inside "data".
        var said = Inner(message, "event") ?? Inner(message, "data") ?? message;

        if (Text(said, "e") is not { } kind)
        {
            return Nothing;
        }

        return market == Market.Futures
            ? kind switch
            {
                "ORDER_TRADE_UPDATE" or "ALGO_UPDATE" => Of(Inner(said, "o") is { } order ? Symbol(order, "s") : null),
                "TRADE_LITE" => Of(Symbol(said, "s")),
                "ACCOUNT_UPDATE" => OfAccount(said),
                "listenKeyExpired" => Ended,
                _ => Nothing,
            }
            : kind switch
            {
                "executionReport" or "listStatus" => Of(Symbol(said, "s")),
                "eventStreamTerminated" => Ended,
                _ => Nothing,
            };
    }

    private static Heard Of(string? symbol) => symbol is null ? Unread : new Heard([symbol], Everything: false, Ends: false);

    /// <summary>What the account holds changed: in the symbols of the positions that the event lists, or of the event itself.</summary>
    private static Heard OfAccount(JsonElement said)
    {
        if (Inner(said, "a") is not { } account)
        {
            return Unread;
        }

        var symbols = new List<string>();

        if (account.TryGetProperty("P", out var positions) && positions.ValueKind == JsonValueKind.Array)
        {
            foreach (var position in positions.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Object || Symbol(position, "s") is not { } symbol)
                {
                    return Unread;
                }

                if (!symbols.Contains(symbol))
                {
                    symbols.Add(symbol);
                }
            }
        }

        if (symbols.Count == 0 && Symbol(account, "S") is { } alone)
        {
            symbols.Add(alone);
        }

        // An event that lists no position is about the wallet alone, as a deposit is.
        return new Heard(symbols, Everything: false, Ends: false);
    }

    private static JsonElement? Inner(JsonElement of, string name) =>
        of.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : null;

    private static string? Text(JsonElement of, string name) =>
        of.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    /// <summary>A symbol as Binance writes one: capital letters and digits, and not long. Anything else is no symbol.</summary>
    private static string? Symbol(JsonElement of, string name) =>
        Text(of, name) is { Length: > 0 and <= 32 } symbol
        && symbol.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            ? symbol
            : null;
}
