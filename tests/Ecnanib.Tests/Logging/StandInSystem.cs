using System.Net;
using System.Text;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Stands for a system that the application calls over HTTP, such as an AI provider or Binance:
/// it keeps what it was sent, and answers as the test says, in place of the network.
/// </summary>
internal sealed class StandInSystem : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
    private readonly List<ReceivedCall> _calls = [];

    /// <param name="answer">What the system answers a request with. It may fail, as a connection does.</param>
    public StandInSystem(Func<HttpRequestMessage, HttpResponseMessage> answer)
        : this((request, _) => Task.FromResult(answer(request)))
    {
    }

    /// <param name="answer">
    /// What the system answers a request with, in its own time: it may wait, and is told when the
    /// caller gives up.
    /// </param>
    public StandInSystem(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

    /// <summary>The calls that reached the system, in the order they came.</summary>
    public IReadOnlyList<ReceivedCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>
    /// The application with this system behind a named client of the host's factory, which is
    /// where an adapter gets its client.
    /// </summary>
    /// <param name="clientName">The name the adapter asks the factory for.</param>
    /// <param name="settings">The configuration file, when it is not the usual one.</param>
    /// <param name="services">Further services of the test's own, such as a clock.</param>
    public LoggedApplication Behind(string clientName, SettingsJson? settings = null, Action<IServiceCollection>? services = null) =>
        new(
            settings ?? LoggedApplication.Settings(),
            host => host.ConfigureServices(registered =>
            {
                registered.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => this);
                services?.Invoke(registered);
            }));

    /// <summary>An answer with a body of JSON.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An answer without a body.</summary>
    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = await ReceivedCall.OfAsync(request, cancellationToken);

        lock (_calls)
        {
            _calls.Add(call);
        }

        return await _answer(request, cancellationToken);
    }

    // The factory disposes a handler when it has had it for a while. This one is the test's.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>One call that a <see cref="StandInSystem"/> was sent.</summary>
/// <param name="Method">The method, such as <c>GET</c>.</param>
/// <param name="Address">The whole address, with its query.</param>
/// <param name="Headers">The headers that the caller set, those of the body included, by name without regard to case.</param>
/// <param name="Body">The body, as text, or null when the call had none.</param>
internal sealed record ReceivedCall(string Method, Uri Address, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary>The address without its query: the scheme, the host, and the path.</summary>
    public string Endpoint => Address.GetLeftPart(UriPartial.Path);

    /// <summary>The parameters of the query, in the order they were sent.</summary>
    public IReadOnlyList<(string Name, string Value)> Query =>
        [
            .. Address.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Select(pair => (pair[0], pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty)),
        ];

    public static async Task<ReceivedCall> OfAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());

        return new ReceivedCall(
            request.Method.Method,
            request.RequestUri!,
            headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
    }
}
