using Ecnanib.Platform.Logging;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Platform.Adapters.Http;

/// <summary>
/// Logs every HTTP call the application makes to another system, such as an AI provider or
/// Binance, with its duration, its outcome, and the usage the system reports (NFR-LOG-5). It is
/// part of every client the host's factory creates, and adapters can get a client nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// The address is logged without its query, and headers are not logged, because both carry keys
/// and signatures.
/// </para>
/// <para>
/// Request and response bodies are logged only when this area is at the debug level. To read a
/// body it is held in memory in full, so a response that would otherwise be read as it arrives
/// is then read at once.
/// </para>
/// </remarks>
internal sealed class OutboundCallLogger(
    ILogger<OutboundCallLogger> logger,
    IOptions<OutboundCallLogOptions> options) : IHttpClientAsyncLogger
{
    private const int LongestBodyLogged = 16_384;

    private readonly string[] _usageHeaderPrefixes = [.. options.Value.UsageHeaderPrefixes];

    public object? LogRequestStart(HttpRequestMessage request) => null;

    public async ValueTask<object?> LogRequestStartAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        await LogBodyAsync("request", request, request.Content, cancellationToken);

        return null;
    }

    public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) =>
        LogCall(request, response, error: null, elapsed);

    public async ValueTask LogRequestStopAsync(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan elapsed,
        CancellationToken cancellationToken = default)
    {
        LogCall(request, response, error: null, elapsed);

        await LogBodyAsync("response", request, response.Content, cancellationToken);
    }

    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed) =>
        LogCall(request, response, exception, elapsed);

    public ValueTask LogRequestFailedAsync(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed,
        CancellationToken cancellationToken = default)
    {
        LogCall(request, response, exception, elapsed);

        return ValueTask.CompletedTask;
    }

    private void LogCall(HttpRequestMessage request, HttpResponseMessage? response, Exception? error, TimeSpan elapsed)
    {
        var status = response is null ? (int?)null : (int)response.StatusCode;
        var failed = error is not null || status >= 400;
        var outcome = error switch
        {
            OperationCanceledException => "Canceled",
            _ => failed ? "Failed" : "Succeeded",
        };

        using var usage = BeginUsage(response);

        logger.Log(
            failed ? LogLevel.Warning : LogLevel.Information,
            error,
            "Outbound call {Method} {Host}{Path} {Outcome} with status {StatusCode} in {DurationMs} ms",
            request.Method.Method,
            request.RequestUri?.Host,
            PathOf(request),
            outcome,
            status,
            Math.Round(elapsed.TotalMilliseconds, 1));
    }

    /// <summary>Adds the usage the other system reports in its response headers to the event, as <c>Usage</c>.</summary>
    private IDisposable? BeginUsage(HttpResponseMessage? response)
    {
        if (response is null || _usageHeaderPrefixes.Length == 0)
        {
            return null;
        }

        var usage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            if (_usageHeaderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                usage[name] = string.Join(", ", values);
            }
        }

        return usage.Count == 0
            ? null
            : logger.BeginScope(new List<KeyValuePair<string, object?>> { new("Usage", usage) });
    }

    private async ValueTask LogBodyAsync(string kind, HttpRequestMessage request, HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null || !logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        // Held in memory, so that whoever reads the body next still gets all of it.
        await content.LoadIntoBufferAsync(cancellationToken);
        var body = await content.ReadAsStringAsync(cancellationToken);
        if (body.Length == 0)
        {
            return;
        }

        var length = body.Length;
        if (length > LongestBodyLogged)
        {
            body = string.Concat(body.AsSpan(0, LongestBodyLogged), "…");
        }

        logger.LogDebug(
            "Outbound call {Method} {Host}{Path} {BodyOf} body of {BodyLength} characters: {Body}",
            request.Method.Method,
            request.RequestUri?.Host,
            PathOf(request),
            kind,
            length,
            body);
    }

    private static string? PathOf(HttpRequestMessage request) =>
        request.RequestUri is { IsAbsoluteUri: true } address ? address.AbsolutePath : null;
}

/// <summary>Registers the log of outbound calls.</summary>
internal static class OutboundCallLogging
{
    /// <summary>
    /// Makes <see cref="OutboundCallLogger"/> the logger of every client the host's factory
    /// creates, in place of the default ones, which print whole request addresses. It logs each
    /// attempt, nearest to the network, so a retried call shows every try.
    /// </summary>
    public static IServiceCollection AddOutboundCallLogging(this IServiceCollection services)
    {
        services.AddOptions<OutboundCallLogOptions>();
        services.AddSingleton<OutboundCallLogger>();
        services.ConfigureHttpClientDefaults(http => http.RemoveAllLoggers().AddLogger<OutboundCallLogger>());

        return services;
    }
}
