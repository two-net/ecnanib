using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// The one way to Binance. Every request reserves request weight first, is signed if it needs to be, is
/// logged once, and updates its server's limits from the response. Failures come back as an
/// <see cref="ExchangeException"/> saying whether anything may have happened. See Exchange adapter in
/// docs/architecture.md.
/// </summary>
internal sealed partial class BinanceTransport(
    IHttpClientFactory clients,
    BinanceSettings settings,
    BinanceServers servers,
    TimeProvider time,
    IEnumerable<IExchangeBanObserver> observers,
    ILoggerFactory loggerFactory)
{
    /// <summary>The named client, which follows no redirects and logs nothing of its own.</summary>
    public const string ClientName = "Binance";

    /// <summary>The name SRS 7.3 gives exchange calls' log events.</summary>
    public const string Category = "Exchange";

    private const string ApiKeyHeader = "X-MBX-APIKEY";

    /// <summary>Binance's code for a timestamp outside the receive window, which a clock sync fixes.</summary>
    private const int TimestampOutsideWindow = -1021;

    /// <summary>Too many orders: the key's order-rate limit, not the server's weight.</summary>
    private const int TooManyOrders = -1015;

    /// <summary>Futures' answer to a client order ID already in use, which proves an order with it exists.</summary>
    private const int DuplicateClientOrderId = -4116;

    /// <summary>Spot's code for an order it refused, which says "Duplicate order sent." for a client order ID in use.</summary>
    private const int NewOrderRejected = -2010;

    private const int MaximumMessageLength = 200;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Short enough to fit the shutdown drain window. See Jobs in docs/architecture.md.</summary>
    private static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How old the clock offset may be before a signed request syncs it again (FR-EXE-10).</summary>
    private static readonly TimeSpan ClockMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>After a failed sync, how long the old offset is used before trying again.</summary>
    private static readonly TimeSpan ClockRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Binance's shortest ban, for a 418 that doesn't say how long.</summary>
    private static readonly TimeSpan DefaultBan = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Added to a request's receive window to tell when it can no longer take effect: for clock error,
    /// and for time in Binance's queues after it has checked the timestamp.
    /// </summary>
    private static readonly TimeSpan SettleMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Binance's codes for a request whose outcome it doesn't know: an unknown error (-1000), an unexpected
    /// response from its message bus (-1006), and a timeout waiting for its backend (-1007). They can
    /// arrive with a 4XX status.
    /// </summary>
    private static readonly int[] UnknownOutcomeCodes = [-1000, -1006, -1007];

    private readonly ILogger _logger = loggerFactory.CreateLogger(Category);
    private readonly IReadOnlyList<IExchangeBanObserver> _observers = [.. observers];

    /// <summary>
    /// Sends one request. A signed one is retried once, after a clock sync, when Binance says its
    /// timestamp is outside the receive window: that's a rejection, so nothing happened.
    /// </summary>
    /// <param name="parameters">In the order they're sent, without the timestamp, receive window or signature.</param>
    /// <param name="clientOrderId">Logged with the call (FR-LOG-08), and remembered when an order's outcome is unknown.</param>
    /// <exception cref="ExchangeException">Binance refused it, or it wasn't sent, or its outcome is unknown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled a request before it was sent, or a read at any point.</exception>
    public async Task<byte[]> SendAsync(
        ExchangeServer server,
        BinanceEndpoint endpoint,
        IReadOnlyList<(string Name, string Value)> parameters,
        ExchangeKey? key,
        RequestPriority priority,
        string? clientOrderId,
        CancellationToken cancellationToken)
    {
        if (endpoint.Signed != key is not null)
        {
            throw new ArgumentException($"{endpoint.Path} is {(endpoint.Signed ? "signed, so it needs a key" : "public, so it takes no key")}.", nameof(key));
        }

        var state = servers[server];

        for (var attempt = 0; ; attempt++)
        {
            if (key is not null)
            {
                await EnsureClockAsync(state, endpoint, retrying: attempt > 0, priority, cancellationToken);
            }

            // A retried order's first send was rejected, so the caller's token still applies to it.
            cancellationToken.ThrowIfCancellationRequested();

            var (body, retry) = await SendOnceAsync(state, endpoint, parameters, key, priority, clientOrderId, finalAttempt: attempt > 0, cancellationToken);

            if (!retry)
            {
                return body!;
            }
        }
    }

    /// <summary>Sends one request and reads its body as <typeparamref name="T"/>. See <see cref="SendAsync"/>.</summary>
    public async Task<T> SendAsync<T>(
        ExchangeServer server,
        BinanceEndpoint endpoint,
        IReadOnlyList<(string Name, string Value)> parameters,
        ExchangeKey? key,
        RequestPriority priority,
        string? clientOrderId,
        CancellationToken cancellationToken)
    {
        var body = await SendAsync(server, endpoint, parameters, key, priority, clientOrderId, cancellationToken);

        return Read<T>(servers[server], endpoint, body);
    }

    /// <summary>
    /// Syncs the clock when the offset is missing or old, or when Binance has just said it's wrong. A
    /// failed routine sync keeps the old offset, so a protective order isn't stopped by a failed time
    /// request. A -1021 would catch real drift.
    /// </summary>
    /// <exception cref="ExchangeException">The request can't be signed, so it isn't sent.</exception>
    private async Task EnsureClockAsync(
        ServerState state,
        BinanceEndpoint forEndpoint,
        bool retrying,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        var requested = time.GetUtcNow();

        if (!retrying && state.ClockSyncedAt is { } syncedAt && (requested - syncedAt < ClockMaxAge || requested < state.ClockRetryAt))
        {
            return;
        }

        await state.ClockGate.WaitAsync(cancellationToken);

        try
        {
            // Someone else may have synced, or failed to, while this one waited.
            if (state.ClockSyncedAt is { } synced
                && (retrying ? synced >= requested : time.GetUtcNow() - synced < ClockMaxAge || time.GetUtcNow() < state.ClockRetryAt))
            {
                return;
            }

            try
            {
                await SyncClockAsync(state, priority, cancellationToken);
            }
            catch (ExchangeException exception)
            {
                Unsynced(state, forEndpoint, retrying, exception);
            }
        }
        finally
        {
            state.ClockGate.Release();
        }
    }

    private async Task SyncClockAsync(ServerState state, RequestPriority priority, CancellationToken cancellationToken)
    {
        var endpoint = ExchangeServers.MarketOf(state.Server) == Market.Futures ? BinanceEndpoint.FuturesTime : BinanceEndpoint.SpotTime;
        var sent = time.GetUtcNow();
        var (body, _) = await SendOnceAsync(state, endpoint, [], key: null, priority, clientOrderId: null, finalAttempt: true, cancellationToken);
        var received = time.GetUtcNow();
        var serverTime = DateTimeOffset.FromUnixTimeMilliseconds(Read<ServerTimeBody>(state, endpoint, body!).ServerTime);

        // Binance stamped the time somewhere between sending and receiving, most likely half way.
        var offset = serverTime - (sent + ((received - sent) / 2));
        state.SetClock(offset, received);

        _logger.LogDebug("Binance {Server} clock offset is {ClockOffsetMs:0.0} ms", state.Server, offset.TotalMilliseconds);
    }

    /// <summary>Decides what a failed sync means for the request that needed it.</summary>
    private void Unsynced(ServerState state, BinanceEndpoint forEndpoint, bool retrying, ExchangeException exception)
    {
        var server = state.Server;
        var description = ExchangeServers.Describe(server);

        if (retrying)
        {
            // Binance refused the first send, so nothing happened, whatever the sync's own failure was.
            throw new ExchangeException(
                server,
                ExchangeFailure.Rejected,
                $"{description} {forEndpoint.Path} was refused for its timestamp, and the clock couldn't be synced.",
                TimestampOutsideWindow,
                innerException: exception);
        }

        if (state.ClockSyncedAt is not null)
        {
            state.DeferClockSync(time.GetUtcNow() + ClockRetryDelay);
            _logger.LogWarning(exception, "Binance {Server} clock sync failed, so the previous offset stays in use", server);

            return;
        }

        if (exception.Failure is ExchangeFailure.Banned or ExchangeFailure.Throttled)
        {
            // Not sent either, for the same reason, and with the same wait.
            throw new ExchangeException(server, exception.Failure, $"{description} {forEndpoint.Path} wasn't sent: {exception.Message}", exception.Code, exception.RetryAfter, exception);
        }

        throw new ExchangeException(
            server,
            ExchangeFailure.Unavailable,
            $"{description} {forEndpoint.Path} wasn't sent, because the clock couldn't be synced.",
            innerException: exception);
    }

    /// <returns>The body, or Retry when a signed request should be sent again after a clock sync.</returns>
    private async Task<(byte[]? Body, bool Retry)> SendOnceAsync(
        ServerState state,
        BinanceEndpoint endpoint,
        IReadOnlyList<(string Name, string Value)> parameters,
        ExchangeKey? key,
        RequestPriority priority,
        string? clientOrderId,
        bool finalAttempt,
        CancellationToken cancellationToken)
    {
        var server = state.Server;
        var credentialId = key?.CredentialId;

        if (state.TryReserve(endpoint, priority, credentialId, time.GetUtcNow()) is { } refusal)
        {
            _logger.LogWarning(
                "Binance {Server} {Method} {Endpoint} not sent: {Reason}. Retry in {RetryAfterSeconds:0.0} s, client order {ClientOrderId}",
                server,
                endpoint.Method.Method,
                endpoint.Path,
                refusal.Reason,
                refusal.RetryAfter.TotalSeconds,
                clientOrderId);

            throw new ExchangeException(
                server,
                refusal.Failure,
                $"{ExchangeServers.Describe(server)} {endpoint.Path} wasn't sent: {refusal.Reason}.",
                retryAfter: refusal.RetryAfter);
        }

        try
        {
            // Signing failures, such as an unreadable key, happen here, before anything is sent.
            using var request = BuildRequest(state, endpoint, parameters, key, out var signedAt);
            var call = new Call(state, endpoint, key, clientOrderId, SettlesAt(endpoint, signedAt));
            var started = Stopwatch.GetTimestamp();
            HttpResponseMessage response;
            byte[] body;

            // A trade ignores the caller once it's sent, and has its own limit instead, so that a shutdown
            // can't cut it off (NFR-REL-04). A read follows the caller.
            using (var timeout = endpoint.Trade ? new CancellationTokenSource(TradeTimeout) : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (!endpoint.Trade)
                {
                    timeout.CancelAfter(ReadTimeout);
                }

                try
                {
                    response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token);
                    body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!endpoint.Trade && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Anything once the send has begun, from a timeout to a response that can't be decoded.
                    _logger.LogError(
                        exception,
                        "Binance {Server} {Method} {Endpoint} got no usable answer in {LatencyMs:0.0} ms, so its outcome is unknown, client order {ClientOrderId}",
                        server,
                        endpoint.Method.Method,
                        endpoint.Path,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        clientOrderId);

                    throw Unknown(call, $"{ExchangeServers.Describe(server)} {endpoint.Path} got no usable answer, so its outcome is unknown.", code: null, exception);
                }
            }

            using (response)
            {
                return Handle(call, finalAttempt, response, body, Stopwatch.GetElapsedTime(started));
            }
        }
        finally
        {
            state.Release(endpoint, credentialId);
        }
    }

    private (byte[]? Body, bool Retry) Handle(Call call, bool finalAttempt, HttpResponseMessage response, byte[] body, TimeSpan latency)
    {
        var (state, endpoint, key, clientOrderId, _) = call;
        var server = state.Server;
        var now = time.GetUtcNow();
        var (usedWeight, weightLimit) = state.Observe(response.Headers, key?.CredentialId, now);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            LogResponse(LogLevel.Information, call, status, latency, usedWeight, weightLimit, code: null);

            return (body, false);
        }

        var error = ReadError(body);
        var retryAfter = RetryAfter(response, now);
        var description = ExchangeServers.Describe(server);

        switch (status)
        {
            case 418:
            {
                // Binance's own time is the same for every request in flight, so it's preferred. A time in
                // the past can only be a clock gone wrong, and the ban is real, so it falls back.
                var until = BannedUntil(error.Msg, state.ClockOffset) is { } stated && stated > now
                    ? stated
                    : retryAfter is { } wait ? now + wait : now + DefaultBan;
                LogResponse(LogLevel.Error, call, status, latency, usedWeight, weightLimit, error.Code);

                if (state.Ban(until, now))
                {
                    _logger.LogError(
                        "Binance {Server} banned this server's address until {BannedUntil:O}. Requests to it are halted until then",
                        server,
                        until);
                    Notify(new ExchangeBan(server, until));
                }

                throw new ExchangeException(server, ExchangeFailure.Banned, $"{description} has banned this server's address.", error.Code, until - now);
            }

            case 429:
            {
                var until = error.Code == TooManyOrders && key is not null
                    ? state.BackOffKey(key.CredentialId, retryAfter, now)
                    : state.BackOff(retryAfter, now);
                LogResponse(LogLevel.Warning, call, status, latency, usedWeight, weightLimit, error.Code);

                throw new ExchangeException(server, ExchangeFailure.Throttled, $"{description} refused the request: {Message(error)}", error.Code, until - now);
            }

            case >= 400 and < 500 when status != 408 && !OutcomeUnknown(error):
                if (error.Code == TimestampOutsideWindow && key is not null && !finalAttempt)
                {
                    LogResponse(LogLevel.Warning, call, status, latency, usedWeight, weightLimit, error.Code);

                    return (null, true);
                }

                LogResponse(LogLevel.Warning, call, status, latency, usedWeight, weightLimit, error.Code);

                throw new ExchangeException(server, ExchangeFailure.Rejected, $"{description} rejected the request: {Message(error)}", error.Code);
        }

        // A server error, a timeout at Binance, a code that says Binance doesn't know either, or anything
        // else unexpected, such as a redirect, which is never followed. The request arrived, so it may
        // have taken effect (EXT-BIN-05).
        LogResponse(LogLevel.Error, call, status, latency, usedWeight, weightLimit, error.Code);

        throw Unknown(call, $"{description} answered HTTP {status.ToString(CultureInfo.InvariantCulture)}, so the outcome is unknown: {Message(error)}", error.Code);
    }

    /// <summary>
    /// An unknown outcome. For an order, it's remembered until Binance can no longer accept it, so that a
    /// query can't say it doesn't exist too early (FR-EXE-05).
    /// </summary>
    private ExchangeException Unknown(Call call, string message, int? code, Exception? innerException = null)
    {
        if (call is { Endpoint.PlacesOrder: true, Key: { } key, ClientOrderId: { } clientOrderId, SettlesAt: { } settlesAt })
        {
            call.State.RecordUnsettled(key.CredentialId, clientOrderId, settlesAt, time.GetUtcNow());
        }

        return new ExchangeException(call.State.Server, ExchangeFailure.Unknown, message, code, innerException: innerException, settlesAt: call.SettlesAt);
    }

    /// <summary>
    /// Proof that Binance doesn't know what happened, or that an order with this client order ID exists,
    /// which is just as unknown to the caller.
    /// </summary>
    private static bool OutcomeUnknown(ErrorBody error) =>
        error.Code is { } code
        && (UnknownOutcomeCodes.Contains(code)
            || code == DuplicateClientOrderId
            || (code == NewOrderRejected && error.Msg?.Contains("Duplicate order", StringComparison.OrdinalIgnoreCase) == true));

    /// <summary>When a signed trade can no longer take effect, by this server's clock: its receive window, and a margin.</summary>
    private DateTimeOffset? SettlesAt(BinanceEndpoint endpoint, DateTimeOffset? signedAt) =>
        endpoint.Trade && signedAt is { } at ? at + TimeSpan.FromMilliseconds(settings.RecvWindowMs) + SettleMargin : null;

    /// <param name="signedAt">When the timestamp was taken, by this server's clock, for a signed request.</param>
    private HttpRequestMessage BuildRequest(
        ServerState state,
        BinanceEndpoint endpoint,
        IReadOnlyList<(string Name, string Value)> parameters,
        ExchangeKey? key,
        out DateTimeOffset? signedAt)
    {
        var query = new StringBuilder();
        signedAt = null;

        foreach (var (name, value) in parameters)
        {
            query.Append(name).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
        }

        if (key is not null)
        {
            var now = time.GetUtcNow();
            signedAt = now;
            query.Append("timestamp=").Append((now + state.ClockOffset).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
                .Append("&recvWindow=").Append(settings.RecvWindowMs.ToString(CultureInfo.InvariantCulture));

            // Signed exactly as sent, with the signature last.
            var signature = RequestSigner.Sign(key, query.ToString());
            query.Append("&signature=").Append(signature);
        }
        else if (query.Length > 0)
        {
            query.Length--;
        }

        var address = state.BaseUrl.GetLeftPart(UriPartial.Authority) + endpoint.Path + (query.Length > 0 ? "?" + query : "");
        var request = new HttpRequestMessage(endpoint.Method, new Uri(address, UriKind.Absolute));

        // .NET's handler sends a request with no body again, up to three times, when its connection
        // drops before an answer. For an order that would be a second order. An empty body stops it,
        // and RetryTests proves that against a real socket.
        if (endpoint.Method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent([]);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, key.ApiKey.Reveal());
        }

        return request;
    }

    private void LogResponse(LogLevel level, Call call, int status, TimeSpan latency, int usedWeight, int weightLimit, int? code) =>
        _logger.Log(
            level,
            "Binance {Server} {Method} {Endpoint} returned {StatusCode} in {LatencyMs:0.0} ms, weight {UsedWeight}/{WeightLimit}, client order {ClientOrderId}, code {ErrorCode}",
            call.State.Server,
            call.Endpoint.Method.Method,
            call.Endpoint.Path,
            status,
            latency.TotalMilliseconds,
            usedWeight,
            weightLimit,
            call.ClientOrderId,
            code);

    /// <summary>Observers must return at once and never throw, but one that does can't break the request path.</summary>
    private void Notify(ExchangeBan ban)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnBanned(ban);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telling {Observer} about the ban on {Server} failed", observer.GetType().Name, ban.Server);
            }
        }
    }

    /// <summary>
    /// A successful body this version can't read means Binance changed its format (ASM-03). An order it
    /// answered was accepted, so its outcome is unknown rather than rejected.
    /// </summary>
    private static T Read<T>(ServerState state, BinanceEndpoint endpoint, byte[] body)
    {
        try
        {
            return BinanceJson.Read<T>(body);
        }
        catch (JsonException exception)
        {
            throw new ExchangeException(
                state.Server,
                endpoint.Trade ? ExchangeFailure.Unknown : ExchangeFailure.Rejected,
                $"{ExchangeServers.Describe(state.Server)} {endpoint.Path} returned a body this version can't read.",
                innerException: exception);
        }
    }

    private static ErrorBody ReadError(byte[] body)
    {
        try
        {
            return body.Length > 0 ? JsonSerializer.Deserialize<ErrorBody>(body, BinanceJson.Options) ?? new ErrorBody() : new ErrorBody();
        }
        catch (JsonException)
        {
            return new ErrorBody();
        }
    }

    private static string Message(ErrorBody error)
    {
        var message = string.IsNullOrWhiteSpace(error.Msg) ? "no message" : error.Msg.Trim();

        if (message.Length > MaximumMessageLength)
        {
            message = message[..MaximumMessageLength];
        }

        return error.Code is { } code ? $"{code.ToString(CultureInfo.InvariantCulture)} {message}" : message;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } when delta > TimeSpan.Zero => delta,
            { Date: { } date } when date > now => date - now,
            _ => null,
        };

    /// <summary>Binance says "IP banned until 1700000000000." in its own time, which becomes this server's.</summary>
    private static DateTimeOffset? BannedUntil(string? message, TimeSpan clockOffset)
    {
        var match = message is null ? null : BannedUntilPattern().Match(message);

        return match is { Success: true } && long.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) - clockOffset
            : null;
    }

    [GeneratedRegex(@"banned until (\d{12,14})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BannedUntilPattern();

    /// <summary>One request as sent.</summary>
    /// <param name="SettlesAt">For a signed trade, when it can no longer take effect.</param>
    private sealed record Call(ServerState State, BinanceEndpoint Endpoint, ExchangeKey? Key, string? ClientOrderId, DateTimeOffset? SettlesAt);
}
