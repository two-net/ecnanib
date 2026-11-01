using System.Diagnostics;

namespace Ecnanib.Platform.Logging;

/// <summary>
/// FR-LOG-06. It records the route pattern and nothing else from the request, on every endpoint, so
/// bodies, headers, query strings and raw paths can't reach a log and there's no list of
/// authentication endpoints to keep current. Blazor circuit traffic isn't HTTP and doesn't pass here.
/// </summary>
internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
{
    /// <summary>The name the Logging:Overrides example in SRS 7.3 uses.</summary>
    private const string Category = "Http";

    private readonly ILogger _logger = loggerFactory.CreateLogger(Category);

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();

        // Routing has already run. This can't wait until afterwards, because status pages run the
        // pipeline again, which would log a failed request against the error page's route.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";

        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            Log(LogLevel.Error, exception, context, route, StatusCodes.Status500InternalServerError, started);
            throw;
        }

        var status = context.Response.StatusCode;
        var level = status >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Information;

        Log(level, null, context, route, status, started);
    }

    private void Log(LogLevel level, Exception? exception, HttpContext context, string route, int status, long started) =>
        _logger.Log(
            level,
            exception,
            "HTTP {Method} {Route} responded {StatusCode} in {DurationMs:0.0} ms",
            context.Request.Method,
            route,
            status,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
