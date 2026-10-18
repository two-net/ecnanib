using System.Net;
using System.Text;
using System.Text.Json;

namespace Ecnanib.Tests.Support;

/// <summary>One request the fake handler received, with the body buffered so tests can assert on headers and content.</summary>
public sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string[]> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(",", values) : null;

    public JsonElement Json => JsonDocument.Parse(Body ?? throw new InvalidOperationException("The request had no body.")).RootElement.Clone();
}

/// <summary>
/// Scripted HTTP responses: rules matched by predicate first, then a sequential queue. Every request is captured.
/// An unmatched request throws, naming the method and path.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<CapturedRequest> _requests = [];
    private readonly List<(Func<CapturedRequest, bool> Match, Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> Respond)> _rules = [];
    private readonly Queue<Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>> _queue = new();

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToArray();
            }
        }
    }

    public FakeHttpMessageHandler When(Func<CapturedRequest, bool> match, Func<CapturedRequest, HttpResponseMessage> respond)
    {
        lock (_lock)
        {
            _rules.Add((match, (r, _) => Task.FromResult(respond(r))));
        }

        return this;
    }

    public FakeHttpMessageHandler Enqueue(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        lock (_lock)
        {
            _queue.Enqueue(respond);
        }

        return this;
    }

    public FakeHttpMessageHandler Enqueue(HttpResponseMessage response) => Enqueue((_, _) => Task.FromResult(response));

    public FakeHttpMessageHandler Enqueue(Exception exception) => Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers; the client's timeout or the caller's token ends the request.</summary>
    public FakeHttpMessageHandler EnqueueHang() => Enqueue(async (_, ct) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("unreachable");
    });

    public static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType = "text/html") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = header.Value.ToArray();
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                headers[header.Key] = header.Value.ToArray();
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
        Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? respond;
        lock (_lock)
        {
            _requests.Add(captured);
            respond = _rules.FirstOrDefault(r => r.Match(captured)).Respond;
            if (respond is null && _queue.Count > 0)
                respond = _queue.Dequeue();
        }

        if (respond is null)
            throw new InvalidOperationException($"No fake response for {request.Method} {request.RequestUri!.AbsolutePath}");
        return await respond(captured, ct);
    }
}
