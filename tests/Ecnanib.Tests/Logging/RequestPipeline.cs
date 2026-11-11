using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Logging;

/// <summary>
/// Lets a test put code of its own into the application's request pipeline, where the application
/// has no endpoint that does what the test needs, such as one that fails.
/// </summary>
internal static class RequestPipeline
{
    /// <summary>
    /// Runs <paramref name="handler"/> for a request that no endpoint of the application takes. It
    /// runs at the end of the pipeline, so inside everything the application wraps a request in.
    /// The handler returns false for a request it leaves alone.
    /// </summary>
    public static IWebHostBuilder HandleAtTheEnd(this IWebHostBuilder host, Func<HttpContext, Task<bool>> handler) =>
        host.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new Filter(
            before: null,
            after: async (context, next) =>
            {
                if (!await handler(context))
                {
                    await next(context);
                }
            })));

    /// <summary>Runs <paramref name="prepare"/> on every request before the application's pipeline, as the host's own middleware does.</summary>
    public static IWebHostBuilder PrepareEachRequest(this IWebHostBuilder host, Action<HttpContext> prepare) =>
        host.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new Filter(
            before: (context, next) =>
            {
                prepare(context);
                return next(context);
            },
            after: null)));

    private sealed class Filter(
        Func<HttpContext, RequestDelegate, Task>? before,
        Func<HttpContext, RequestDelegate, Task>? after) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            if (before is not null)
            {
                app.Use(before);
            }

            next(app);

            if (after is not null)
            {
                app.Use(after);
            }
        };
    }
}
