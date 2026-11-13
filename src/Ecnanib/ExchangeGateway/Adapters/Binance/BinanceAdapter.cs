using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// Binance, behind the common interface: Spot and USDⓈ-margined perpetual Futures, each on the
/// real exchange and in Binance's test environment, at the addresses of the configuration file
/// (FR-BIN-1, FR-BIN-2). So far it reads the clock and asks about a credential. It has no
/// operation that moves funds (FR-BIN-9).
/// </summary>
/// <remarks>
/// <para>
/// A call that needs a credential is signed: the query is hashed with the secret key
/// (HMAC-SHA256), and the hash is sent with it, beside the API key in a header. The secret key
/// itself is never sent. Neither key is in a log event: the log of outbound calls leaves out
/// the query and the headers.
/// </para>
/// <para>
/// What a credential may do is asked differently in the two environments, because Binance's
/// test environment has no call that reports the permissions of a key. See
/// <see cref="CheckKeyAsync"/>.
/// </para>
/// </remarks>
internal sealed class BinanceAdapter(IHttpClientFactory clients, BinanceSettings settings, TimeProvider clock) : IExchangeAdapter
{
    /// <summary>How long Binance may take to answer a call that a user waits for with a form.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    // For how long after its timestamp Binance takes a signed call, in milliseconds. It is
    // Binance's own default, said aloud.
    private const int ReceiveWindow = 5000;

    // The errors that Binance answers a credential with which it does not take: an API key that
    // it does not know or that is not one, a signature that the secret key did not make, and a
    // key that may not do this from here, which is also what a key restricted to other addresses
    // gets.
    private static readonly int[] Refusals = [-1002, -1022, -2008, -2014, -2015];

    public async Task<DateTimeOffset> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken)
    {
        var path = market switch
        {
            Market.Spot => "api/v3/time",
            Market.Futures => "fapi/v1/time",
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        var client = clients.CreateClient(ExchangeGatewayModule.HttpClientName);
        using var response = await client.GetAsync(AddressOf(settings.BaseUrl(market, mode), path), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        return DateTimeOffset.FromUnixTimeMilliseconds(answer.RootElement.GetProperty("serverTime").GetInt64());
    }

    /// <summary>
    /// Asks Binance about a credential. On the real exchange it is one call, to the permissions
    /// of the API key, which says everything: whether the key can withdraw, whether it is
    /// restricted to addresses, and whether it may trade on each market. The test environment has
    /// no such call, so there the account of each market is read, which says whether the
    /// credential may trade on it, and nothing about withdrawals or addresses.
    /// </summary>
    public Task<ExchangeKeyCheck> CheckKeyAsync(TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(secretKey);

        return mode switch
        {
            TradingMode.Live => CheckLiveKeyAsync(apiKey, secretKey, cancellationToken),
            TradingMode.Demo => CheckDemoKeyAsync(apiKey, secretKey, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    private async Task<ExchangeKeyCheck> CheckLiveKeyAsync(string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        // The call is of Binance's account API, which the address of the Spot market serves.
        var answer = await AskAsync(
            settings.BaseUrl(Market.Spot, TradingMode.Live), "sapi/v1/account/apiRestrictions", apiKey, secretKey, cancellationToken);

        if (answer.Verdict != KeyVerdict.Accepted)
        {
            return new ExchangeKeyCheck(answer.Verdict);
        }

        // Without the word on withdrawals there is no verdict: a key is never taken for one that
        // cannot withdraw because the answer did not say (FR-KEY-4, NFR-LEG-2).
        if (Flag(answer.Body, "enableWithdrawals") is not { } canWithdraw)
        {
            return ExchangeKeyCheck.Unreachable;
        }

        return new ExchangeKeyCheck(
            KeyVerdict.Accepted,
            SpotTrading: Flag(answer.Body, "enableSpotAndMarginTrading") ?? false,
            FuturesTrading: Flag(answer.Body, "enableFutures") ?? false,
            CanWithdraw: canWithdraw,
            IpRestricted: Flag(answer.Body, "ipRestrict"));
    }

    private async Task<ExchangeKeyCheck> CheckDemoKeyAsync(string apiKey, string secretKey, CancellationToken cancellationToken)
    {
        var spot = await AskAsync(
            settings.BaseUrl(Market.Spot, TradingMode.Demo), "api/v3/account", apiKey, secretKey, cancellationToken, "omitZeroBalances=true&");
        var futures = await AskAsync(
            settings.BaseUrl(Market.Futures, TradingMode.Demo), "fapi/v1/accountConfig", apiKey, secretKey, cancellationToken);

        // A market that did not answer leaves its permission unknown, so there is no verdict.
        if (spot.Verdict == KeyVerdict.Unreachable || futures.Verdict == KeyVerdict.Unreachable)
        {
            return ExchangeKeyCheck.Unreachable;
        }

        if (spot.Verdict == KeyVerdict.Refused && futures.Verdict == KeyVerdict.Refused)
        {
            return ExchangeKeyCheck.Refused;
        }

        // A market that refuses a credential which the other one takes is a market the
        // credential may not trade on.
        return new ExchangeKeyCheck(
            KeyVerdict.Accepted,
            SpotTrading: spot.Verdict == KeyVerdict.Accepted && (Flag(spot.Body, "canTrade") ?? false),
            FuturesTrading: futures.Verdict == KeyVerdict.Accepted && (Flag(futures.Body, "canTrade") ?? false));
    }

    /// <summary>Makes one signed call that reads, and says what the answer means for the credential.</summary>
    private async Task<Answer> AskAsync(
        Uri baseUrl, string path, string apiKey, string secretKey, CancellationToken cancellationToken, string parameters = "")
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var query = $"{parameters}recvWindow={ReceiveWindow.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{AddressOf(baseUrl, path)}?{query}&signature={Sign(query, secretKey)}");
            request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", apiKey);

            var client = clients.CreateClient(ExchangeGatewayModule.HttpClientName);
            using var response = await client.SendAsync(request, limit.Token);

            var body = await ReadAsync(response, limit.Token);

            if (response.IsSuccessStatusCode)
            {
                // An answer that is not what Binance sends is no verdict.
                return body is { } read ? new Answer(KeyVerdict.Accepted, read) : Answer.Unreachable;
            }

            var refused = response.StatusCode == HttpStatusCode.Unauthorized
                || (body is { } error
                    && error.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out var number)
                    && Refusals.Contains(number));

            // Anything else says nothing about the credential: a limit on requests, a clock that
            // is off, a block of the service's address, or a fault of the exchange.
            return refused ? Answer.Refused : Answer.Unreachable;
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log, with why.
            return Answer.Unreachable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Binance took longer than the limit. The caller did not cancel.
            return Answer.Unreachable;
        }
    }

    /// <summary>The body of an answer as the JSON object that Binance sends, or null when it is something else.</summary>
    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            return answer.RootElement.ValueKind == JsonValueKind.Object ? answer.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A yes or no of an answer, or null when the answer does not have it.</summary>
    private static bool? Flag(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string AddressOf(Uri baseUrl, string path) => $"{baseUrl.AbsoluteUri.TrimEnd('/')}/{path}";

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

    /// <summary>What one call said: the verdict, and the body of an answer that took the credential.</summary>
    private readonly record struct Answer(KeyVerdict Verdict, JsonElement Body)
    {
        public static Answer Refused { get; } = new(KeyVerdict.Refused, default);

        public static Answer Unreachable { get; } = new(KeyVerdict.Unreachable, default);
    }
}
