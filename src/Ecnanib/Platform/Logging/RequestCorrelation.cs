using System.Security.Claims;

namespace Ecnanib.Platform.Logging;

/// <summary>Gives every event written while a request is handled the request's correlation properties (NFR-LOG-3).</summary>
public static class RequestCorrelation
{
    /// <summary>
    /// Opens a scope around the rest of the pipeline with the request id, and the user id when the
    /// request is signed in. Add it first, so that error handling runs inside the scope.
    /// </summary>
    /// <remarks>
    /// The user is the one known when the request arrives here. The host runs authentication
    /// before the application's own middleware, so that is the signed-in user.
    /// </remarks>
    public static IApplicationBuilder UseRequestCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<Middleware>();

    private sealed class Middleware(RequestDelegate next, ILogger<Middleware> logger)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            var user = context.User.Identity is { IsAuthenticated: true }
                ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                : null;

            using (logger.BeginCorrelation(new Correlation(RequestId: context.TraceIdentifier, UserId: user)))
            {
                await next(context);
            }
        }
    }
}
