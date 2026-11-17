using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// The one exchange that every call to Binance makes: a request to the address that the
/// configuration file gives for the market and the environment, when the limiter has room for
/// it, within a time limit, signed where the call needs a credential (FR-BIN-1, FR-BIN-2,
/// FR-BIN-4). What differs between calls is which <see cref="BinanceEndpoint"/> it is and how
/// its answer reads. See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// A call that needs a credential is signed: the query is hashed with the secret key
/// (HMAC-SHA256), and the hash is sent with it, beside the API key in a header. The secret key
/// itself is never sent. Neither key is in a log event: the log of outbound calls leaves out the
/// query and the headers, and nothing here hands a key to a logger.
/// </para>
/// <para>
/// What happens on the network leaves here as an <see cref="ExchangeCallOutcome"/>, and never as
/// an error of the network, which nothing outside an adapter may name. Only a caller that cancels
/// ends the call with an error.
/// </para>
/// <para>
/// Every call passes <see cref="BinanceLimiter"/> first, because this is the one place that
/// sends. A call that the limiter does not let through is not sent, and one that Binance answers
/// with its signal of too many requests tells the limiter, which then sends nothing for a while.
/// Both end as <see cref="ExchangeCallOutcome.RateLimited"/>.
/// </para>
/// <para>
/// The call is made once. Whether it is made again is the caller's to decide.
/// </para>
/// <para>
/// A call that places an order, or takes one off the exchange, is not given up by its caller
/// once it is sent (FR-BIN-6). A caller that cancels ends the wait for a turn at the limiter, and
/// then nothing was sent. From the moment that the call is sent it runs to its answer or to its
/// time limit, whatever its caller does: a call that was dropped half way would leave nobody
/// knowing whether the exchange took the order. What such a call was answered with is said
/// beside its outcome, the status and the number of Binance's error, so that what places the
/// order can tell a refusal, after which there is no order, from an end that leaves it unknown.
/// </para>
/// <para>
/// A call that Binance refuses for the time that it carries tells <see cref="BinanceClock"/>,
/// the watch on the clock, which pauses new entries there (FR-BIN-5). It ends as any other
/// failure does.
/// </para>
/// </remarks>
internal sealed class BinanceCall(
    IHttpClientFactory clients,
    BinanceSettings settings,
    BinanceLimiter limiter,
    BinanceClock watch,
    TimeProvider clock,
    ILogger<BinanceCall> logger)
{
    /// <summary>How long Binance may take to answer a call.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    // For how long after its timestamp Binance takes a signed call, in milliseconds. It is
    // Binance's own default, said aloud.
    private const int ReceiveWindow = 5000;

    // What Binance answers with when it has blocked the address of the service, which it does
    // when its answer to too many requests, 429, is ignored.
    private const HttpStatusCode Banned = (HttpStatusCode)418;

    // The errors that Binance answers a credential with which it does not take: an API key that
    // it does not know or that is not one, a signature that the secret key did not make, and a
    // key that may not do this from here, which is also what a key restricted to other addresses
    // gets.
    private static readonly int[] Refusals = [-1002, -1022, -2008, -2014, -2015];

    // The error that Binance answers a signed call with whose time is outside of what it allows:
    // a second or more after its own, or longer before it than the call says it may be taken.
    private const int TimeRefused = -1021;

    /// <summary>Makes a call that needs no credential, and says how it ended.</summary>
    /// <param name="endpoint">The call.</param>
    /// <param name="mode">The environment, which says with the market of the call which address it goes to.</param>
    /// <param name="read">
    /// What the answer says, or null when it is not what Binance sends to this call. It is given
    /// the answer to a success only.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<BinanceAnswer<T>> AskAsync<T>(
        BinanceEndpoint endpoint,
        TradingMode mode,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken) =>
        SendAsync(endpoint, mode, userId: null, apiKey: null, secretKey: null, read, cancellationToken);

    /// <summary>
    /// Makes a call that is signed with a credential, and says how it ended. A call that changes
    /// something at the exchange cannot be cancelled once it is sent: see the remarks of the class.
    /// </summary>
    /// <param name="endpoint">The call.</param>
    /// <param name="mode">The environment that the credential is for.</param>
    /// <param name="userId">Whose call it is, for the limiter, which gives the users whose calls wait their turns.</param>
    /// <param name="apiKey">The user's API key, which is sent in a header.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the call and is never sent.</param>
    /// <param name="read">
    /// What the answer says, or null when it is not what Binance sends to this call. It is given
    /// the answer to a success only.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the call. Of a call that changes something at the exchange it cancels the wait
    /// for a turn only, before anything is sent.
    /// </param>
    public Task<BinanceAnswer<T>> AskSignedAsync<T>(
        BinanceEndpoint endpoint,
        TradingMode mode,
        Guid userId,
        string apiKey,
        string secretKey,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken)
    {
        // Said without what was given: a key is in no error.
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        // No user is the service itself, whose calls need no credential.
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A call that is signed with a credential is a user's, and says whose.", nameof(userId));
        }

        return SendAsync(endpoint, mode, userId, apiKey, secretKey, read, cancellationToken);
    }

    private async Task<BinanceAnswer<T>> SendAsync<T>(
        BinanceEndpoint endpoint,
        TradingMode mode,
        Guid? userId,
        string? apiKey,
        string? secretKey,
        Func<JsonElement, T?> read,
        CancellationToken cancellationToken)
    {
        // The environment says which address: a credential signs for the one it was given for.
        var baseUrl = settings.BaseUrl(endpoint.Market, mode);
        var address = $"{baseUrl.AbsoluteUri.TrimEnd('/')}/{endpoint.Path}";

        // The limiter says when: at once, or when the turn of the call comes. A call that it
        // holds back for longer than a call waits is not sent. That is not in the log of
        // outbound calls, which has what was sent, so it is said here.
        if (!await limiter.EnterAsync(endpoint, mode, userId, cancellationToken))
        {
            logger.LogWarning(
                "The call {Path} on {Market} {Environment} was not sent to Binance: the limit on requests has no room for it in time",
                endpoint.Path,
                endpoint.Market.ToString(),
                mode.ToString());

            return Without(ExchangeCallOutcome.RateLimited);
        }

        // Signed now, and not before the wait: Binance takes a call for a few seconds after the
        // time that it says it was made at.
        var query = secretKey is null ? endpoint.Query : Signed(endpoint.Query, secretKey);

        // A call that changes something at the exchange is the caller's to cancel until here,
        // where nothing is sent yet. From here on it is not: see the remarks of the class.
        var held = endpoint.Changes;
        if (held)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        // The limit runs on the application's clock, and covers the sending and the reading of
        // the answer.
        using var limit = new CancellationTokenSource(Limit, clock);
        using var either = held
            ? CancellationTokenSource.CreateLinkedTokenSource(limit.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        // Whether Binance answered that it has had too many requests. However the call ends
        // after that, that is how it ended.
        var throttled = false;

        // The status of the answer, once there is one.
        int? status = null;

        try
        {
            // Every parameter is in the query, also of a call that is no GET: the request has no body.
            using var request = new HttpRequestMessage(endpoint.Method, query.Length == 0 ? address : $"{address}?{query}");
            if (apiKey is not null)
            {
                request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", apiKey);
            }

            var client = clients.CreateClient(ExchangeGatewayModule.HttpClientName);

            // When the call was sent, by the application's clock, and how long the answer took,
            // which is there in full when the client returns. The wait for a turn before it and
            // the reading of the answer after it are not part of that: the watch on the clock
            // goes by the middle of it.
            var sentAt = clock.GetUtcNow();
            var sent = clock.GetTimestamp();
            using var response = await client.SendAsync(request, either.Token);
            var took = clock.GetElapsedTime(sent);
            status = (int)response.StatusCode;

            // The limiter hears of every answer: of the signal, which stops the calls, and
            // otherwise of what Binance has counted against the service.
            throttled = response.StatusCode is HttpStatusCode.TooManyRequests or Banned;
            if (throttled)
            {
                limiter.Throttled(endpoint, mode, banned: response.StatusCode == Banned, RetryAfterOf(response));
            }
            else
            {
                // But for a call whose answer Binance says not to go by.
                limiter.Answered(endpoint, mode, endpoint.ReportsUsage ? CountedBy(response, endpoint.Limit) : null);
            }

            var body = await ReadAsync(response, either.Token);

            if (response.IsSuccessStatusCode)
            {
                if (body is { } answer && read(answer) is { } value)
                {
                    return new BinanceAnswer<T>(ExchangeCallOutcome.Succeeded, value, sentAt, took, status);
                }

                // The status says that the call went well, so the log of outbound calls does too.
                // This says that it gave nothing all the same: the page of something in between,
                // or an answer of a kind that this code does not know.
                logger.LogWarning(
                    "The answer of Binance to {Path} on {Market} {Environment} is not what the call asks for: {Reason}",
                    endpoint.Path,
                    endpoint.Market.ToString(),
                    mode.ToString(),
                    body is null ? "NotJson" : "NotExpected");

                return Without(ExchangeCallOutcome.Failed, status);
            }

            var code = CodeOf(body);
            if (code is { } number)
            {
                // The log of outbound calls has the status, which is the same for most of
                // Binance's errors. Which error it is, is in the body, which that log writes only
                // at the debug level. Its text is left there: a number is no secret.
                logger.LogWarning(
                    "Binance answered {Path} on {Market} {Environment} with its error {BinanceCode}",
                    endpoint.Path,
                    endpoint.Market.ToString(),
                    mode.ToString(),
                    number);
            }

            // Binance's own word that the clock of the service is not near its own. The watch on
            // the clock hears of it, and pauses new entries there until the clock is read again.
            if (code == TimeRefused)
            {
                watch.Refused(endpoint.Market, mode);
            }

            if (throttled)
            {
                return Without(ExchangeCallOutcome.RateLimited, status, code);
            }

            // Anything but a refusal says nothing about the credential: a clock that is off, a
            // block of the service's address, or a fault of the exchange.
            var refused = response.StatusCode == HttpStatusCode.Unauthorized
                || (code is { } error && Refusals.Contains(error));

            return Without(refused ? ExchangeCallOutcome.AuthenticationFailed : ExchangeCallOutcome.Failed, status, code);
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log of outbound calls, with why.
            return Without(ExchangeCallOutcome.Failed);
        }
        catch (IOException)
        {
            // The answer broke off while it was read.
            return Without(throttled ? ExchangeCallOutcome.RateLimited : ExchangeCallOutcome.Failed, status);
        }
        catch (OperationCanceledException) when (held || !cancellationToken.IsCancellationRequested)
        {
            // Binance took longer than the limit. The caller did not cancel, or could not.
            return Without(throttled ? ExchangeCallOutcome.RateLimited : ExchangeCallOutcome.TimedOut, status);
        }

        static BinanceAnswer<T> Without(ExchangeCallOutcome outcome, int? status = null, int? code = null) =>
            new(outcome, default, Status: status, Code: code);
    }

    /// <summary>
    /// The query of a call that needs a credential, as it is sent. The call says when it was
    /// made, by the application's clock. What is signed is the query as it is sent, and the
    /// signature is the last of it.
    /// </summary>
    private string Signed(string own, string secretKey)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var first = own.Length == 0 ? string.Empty : $"{own}&";
        var query = $"{first}recvWindow={ReceiveWindow.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";

        return $"{query}&signature={Sign(query, secretKey)}";
    }

    /// <summary>
    /// For how long Binance says to send nothing, with its signal of too many requests, or null
    /// when it does not say. It says it in seconds, and a moment would be read as well.
    /// </summary>
    private TimeSpan? RetryAfterOf(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } wait } => wait,
        { Date: { } until } => until - clock.GetUtcNow(),
        _ => null,
    };

    /// <summary>
    /// The request weight that Binance has counted against the address of the service in its
    /// minute, as an answer says it in the header of its limit, or null when it does not.
    /// </summary>
    private static int? CountedBy(HttpResponseMessage response, BinanceLimit limit) =>
        response.Headers.TryGetValues(limit.UsageHeader, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var weight)
                ? weight
                : null;

    /// <summary>The body of an answer as the JSON that Binance sends, or null when it is something else.</summary>
    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            return answer.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The number that Binance gives an error, or null when the answer has none.</summary>
    private static int? CodeOf(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } error
            && error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var number)
                ? number
                : null;

    /// <summary>The signature of a query: its HMAC-SHA256 under the secret key, in hexadecimal.</summary>
    private static string Sign(string query, string secretKey)
    {
        var key = Encoding.UTF8.GetBytes(secretKey);

        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(query)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>What one call to Binance gave.</summary>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Value">What the answer says. It is there when the call succeeded, and never otherwise.</param>
/// <param name="SentAt">When the call was sent, by the application's clock. It is there when the call succeeded.</param>
/// <param name="Took">How long the answer took from then, without the wait for a turn at the limiter. It is there when the call succeeded.</param>
/// <param name="Status">
/// The status of the answer, or null when there was none: the call was not sent, no connection
/// was made or it broke, or Binance took longer than the limit before it answered.
/// </param>
/// <param name="Code">The number that Binance gave its error, or null when the answer has none.</param>
internal readonly record struct BinanceAnswer<T>(
    ExchangeCallOutcome Outcome, T? Value, DateTimeOffset SentAt = default, TimeSpan Took = default, int? Status = null, int? Code = null);
