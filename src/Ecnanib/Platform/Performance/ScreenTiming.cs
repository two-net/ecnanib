using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Platform.Performance;

/// <summary>
/// Measures how long the server takes to answer a screen (NFR-PRF-1), and registers what keeps
/// the times. See "Screen response times" in docs/performance.md.
/// </summary>
public static class ScreenTiming
{
    /// <summary>The area that the time of a screen is logged under.</summary>
    internal const string Area = "Ecnanib.Platform.Performance.ScreenTiming";

    /// <summary>Registers what counts the times of the screens, and says them to the operator status screen.</summary>
    internal static IServiceCollection AddScreenTiming(this IServiceCollection services)
    {
        // The clock is a service so that a test can put another one in its place.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ScreenTimesMeasure>();
        services.AddSingleton<IScreenTimes>(provider => provider.GetRequiredService<ScreenTimesMeasure>());

        return services;
    }

    /// <summary>
    /// Times the rest of the pipeline for every request that asks for a screen, and counts the
    /// time when the screen was shown. Add it right after the correlation of the request, so
    /// that it measures who is signed in, the page, and what an error is answered with, and
    /// logs with the properties of the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A screen is a page that was asked for and shown</b>: a GET whose endpoint is a page of
    /// the Web UI, answered with a success. A page that follows what changes asks for itself
    /// again in the same way, so that counts too.
    /// </para>
    /// <para>
    /// <b>Nothing else is counted.</b> Not an answer that sends the browser on, such as a page
    /// for signed-in users that a visitor asks for, and not one that an error ended: neither
    /// showed the screen, and both are quick. Not what a form does, which takes what it does: a
    /// password that is hashed, a key that its provider is asked about. And nothing that is no
    /// page: a file of the application, the API, a download, the connection of a page that follows.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseScreenTiming(this IApplicationBuilder app) =>
        app.UseMiddleware<TimingMiddleware>();

    private sealed class TimingMiddleware(RequestDelegate next, ScreenTimesMeasure times, TimeProvider clock, ILoggerFactory loggers)
    {
        private readonly ILogger _logger = loggers.CreateLogger(Area);

        public async Task InvokeAsync(HttpContext context)
        {
            // Asked now, before the rest of the pipeline: an answer that is written by another
            // endpoint, as the page of an error is, is still an answer to what was asked for.
            if (!HttpMethods.IsGet(context.Request.Method)
                || context.GetEndpoint() is not RouteEndpoint endpoint
                || endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is null
                || endpoint.RoutePattern.RawText is not { } route)
            {
                await next(context);

                return;
            }

            var began = clock.GetTimestamp();

            await next(context);

            if (context.Response.StatusCode is < 200 or >= 300)
            {
                return;
            }

            var took = clock.GetElapsedTime(began);

            times.Count(route, took);

            _logger.LogDebug("The screen {Route} was answered in {DurationMs} ms", route, Math.Round(took.TotalMilliseconds, 1));
        }
    }
}
