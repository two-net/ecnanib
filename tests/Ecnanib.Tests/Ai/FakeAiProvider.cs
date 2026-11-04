using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Ecnanib.Tests.Ai;

/// <summary>A request the pipeline sent, as it went on the wire.</summary>
/// <param name="Key">The test adapter's key header, for telling credentials apart.</param>
/// <param name="Headers">Every request header, comma-joined, by its lower-case name.</param>
public sealed record RecordedAiRequest(HttpMethod Method, Uri Uri, string? Key, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Host => Uri.Host;

    public string Path => Uri.AbsolutePath;

    /// <summary>The page a model list asked for, or null for the first.</summary>
    public string? Page =>
        Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == "page")
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();
}

/// <summary>
/// Stands in for every AI provider's server, speaking <see cref="TestAdapter"/>'s format. It answers each
/// method and path as a test says, and anything else with a 404. It counts the requests in flight per
/// key, which is how a test tells credentials apart.
/// </summary>
public sealed class FakeAiProvider : HttpMessageHandler
{
    public const string KeyHeader = "x-test-key";

    private readonly ConcurrentQueue<RecordedAiRequest> _requests = new();
    private readonly ConcurrentDictionary<string, Func<RecordedAiRequest, CancellationToken, Task<HttpResponseMessage>>> _responders = new();
    private readonly Lock _flight = new();
    private readonly Dictionary<string, int> _inFlight = [];
    private readonly Dictionary<string, int> _maxInFlight = [];

    public IReadOnlyList<RecordedAiRequest> Requests => [.. _requests];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string type, string message, params (string Name, string Value)[] headers) =>
        Json(JsonSerializer.Serialize(new { error = new { type, message } }), status, headers);

    /// <param name="stop">"end", "max_tokens" or "refusal".</param>
    public static HttpResponseMessage Completion(string text = "{}", long inputTokens = 10, long outputTokens = 5, string stop = "end") =>
        Json(JsonSerializer.Serialize(new { text, input_tokens = inputTokens, output_tokens = outputTokens, stop }));

    /// <param name="next">The next page's token, or null on the last page.</param>
    public static HttpResponseMessage Models(string? next, params string[] ids) =>
        Json(JsonSerializer.Serialize(new { models = ids.Select(id => new { id, name = $"Model {id}" }), next }));

    public IReadOnlyList<RecordedAiRequest> WithKey(string key) => [.. Requests.Where(request => request.Key == key)];

    public int InFlight(string key)
    {
        lock (_flight)
        {
            return _inFlight.GetValueOrDefault(key);
        }
    }

    public int MaxInFlight(string key)
    {
        lock (_flight)
        {
            return _maxInFlight.GetValueOrDefault(key);
        }
    }

    public void On(HttpMethod method, string path, Func<RecordedAiRequest, HttpResponseMessage> respond) =>
        _responders[Key(method, path)] = (request, _) => Task.FromResult(respond(request));

    public void On(HttpMethod method, string path, Func<RecordedAiRequest, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _responders[Key(method, path)] = respond;

    /// <summary>Answers with each response in turn, and then with the last one.</summary>
    public void OnSequence(HttpMethod method, string path, params Func<RecordedAiRequest, HttpResponseMessage>[] responses)
    {
        var calls = -1;

        On(method, path, request => responses[Math.Min(Interlocked.Increment(ref calls), responses.Length - 1)](request));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.TryGetValues(KeyHeader, out var keys) ? keys.Single() : null;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(", ", header.Value));
        var recorded = new RecordedAiRequest(request.Method, request.RequestUri!, key, body, headers);
        _requests.Enqueue(recorded);

        lock (_flight)
        {
            var now = _inFlight[key ?? ""] = _inFlight.GetValueOrDefault(key ?? "") + 1;
            _maxInFlight[key ?? ""] = Math.Max(_maxInFlight.GetValueOrDefault(key ?? ""), now);
        }

        try
        {
            var response = _responders.TryGetValue(Key(request.Method, recorded.Path), out var respond)
                ? await respond(recorded, cancellationToken)
                : Error(HttpStatusCode.NotFound, "not_found", "No fake answers this path.");
            response.RequestMessage = request;

            return response;
        }
        finally
        {
            lock (_flight)
            {
                _inFlight[key ?? ""]--;
            }
        }
    }

    private static string Key(HttpMethod method, string path) => $"{method.Method} {path}";
}
