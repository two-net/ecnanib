using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Ecnanib.Tests.Exchange;

/// <summary>A request the adapter sent, as it went on the wire.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ApiKey)
{
    public string Host => Uri.Host;

    public string Path => Uri.AbsolutePath;

    /// <summary>The query as sent, still percent-encoded.</summary>
    public string Query => Uri.Query.TrimStart('?');

    /// <summary>Each parameter, decoded, in the order sent.</summary>
    public IReadOnlyList<(string Name, string Value)> Parameters =>
    [
        .. Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(pair => (pair[0], Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : ""))),
    ];

    public string? Parameter(string name) =>
        Parameters.Where(parameter => parameter.Name == name).Select(parameter => parameter.Value).FirstOrDefault();

    /// <summary>What was signed: everything before the signature, exactly as sent.</summary>
    public string SignedPayload => Query[..Query.IndexOf("&signature=", StringComparison.Ordinal)];
}

/// <summary>
/// Stands in for every Binance server. It answers each method and path with whatever a test set, and
/// the time endpoints with its clock plus <see cref="Skew"/>. Anything else gets a 404. Every response
/// reports a used weight of 1 unless the test's response sets one.
/// </summary>
public sealed class FakeBinance(TimeProvider clock) : HttpMessageHandler
{
    public const string SpotLive = "spot-live.binance.test";
    public const string SpotDemo = "spot-demo.binance.test";
    public const string FuturesLive = "futures-live.binance.test";
    public const string FuturesDemo = "futures-demo.binance.test";
    public const string MarketData = "market-data.binance.test";

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    /// <summary>How far Binance's clock is ahead of the application's.</summary>
    public TimeSpan Skew { get; set; }

    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>The settings for these servers, which every test application gets.</summary>
    public static Dictionary<string, string?> Settings() => new()
    {
        ["Binance:Spot:LiveBaseUrl"] = $"https://{SpotLive}",
        ["Binance:Spot:DemoBaseUrl"] = $"https://{SpotDemo}",
        ["Binance:Futures:LiveBaseUrl"] = $"https://{FuturesLive}",
        ["Binance:Futures:DemoBaseUrl"] = $"https://{FuturesDemo}",
        ["Binance:MarketDataBaseUrl"] = $"https://{MarketData}",
    };

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Error(HttpStatusCode status, int code, string message, params (string Name, string Value)[] headers) =>
        Json($$"""{"code":{{code}},"msg":"{{message}}"}""", status, headers);

    public IReadOnlyList<RecordedRequest> To(string path) => [.. Requests.Where(request => request.Path == path)];

    public void On(HttpMethod method, string path, Func<RecordedRequest, HttpResponseMessage> respond) =>
        _responders[Key(method, path)] = (request, _) => Task.FromResult(respond(request));

    public void On(HttpMethod method, string path, Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _responders[Key(method, path)] = respond;

    /// <summary>Answers with each response in turn, and then with the last one.</summary>
    public void OnSequence(HttpMethod method, string path, params Func<RecordedRequest, HttpResponseMessage>[] responses)
    {
        var calls = -1;

        On(method, path, request => responses[Math.Min(Interlocked.Increment(ref calls), responses.Length - 1)](request));
    }

    public long ServerTime() => (clock.GetUtcNow() + Skew).ToUnixTimeMilliseconds();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("X-MBX-APIKEY", out var keys) ? keys.Single() : null);
        _requests.Enqueue(recorded);

        HttpResponseMessage response;

        if (_responders.TryGetValue(Key(request.Method, recorded.Path), out var respond))
        {
            response = await respond(recorded, cancellationToken);
        }
        else if (recorded.Path is "/api/v3/time" or "/fapi/v1/time")
        {
            response = Json($$"""{"serverTime":{{ServerTime()}}}""");
        }
        else
        {
            response = Error(HttpStatusCode.NotFound, -1, "No fake answers this path.");
        }

        if (!response.Headers.Any(header => header.Key.StartsWith("X-MBX-USED-WEIGHT-", StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "1");
        }

        response.RequestMessage = request;

        return response;
    }

    private static string Key(HttpMethod method, string path) => $"{method.Method} {path}";
}
