using System.Security.Claims;

namespace Ecnanib.Platform.Logging;

/// <summary>Gives every event written while a request is handled the request's correlation properties (NFR-LOG-3).</summary>
public static class RequestCorrelation
{
    /// <summary>
    /// Opens a scope around the rest of the pipeline with the request id. Add it first, so that
    /// authentication and error handling run inside the scope.
    /// </summary>
    public static IApplicationBuilder UseRequestCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestMiddleware>();

    /// <summary>
    /// Opens a scope around the rest of the pipeline with the user id, when the request is signed
    /// in. Add it right after authentication, which is what knows the user, and before error
    /// handling, so that a logged error names the user too.
    /// </summary>
    public static IApplicationBuilder UseUserCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<UserMiddleware>();

    private sealed class RequestMiddleware(RequestDelegate next, ILogger<RequestMiddleware> logger)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            using (logger.BeginCorrelation(new Correlation(RequestId: context.TraceIdentifier)))
            {
                await next(context);
            }
        }
    }

    private sealed class UserMiddleware(RequestDelegate next, ILogger<UserMiddleware> logger)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            var user = context.User.Identity is { IsAuthenticated: true }
                ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                : null;

            using (logger.BeginCorrelation(new Correlation(UserId: user)))
            {
                await next(context);
            }
        }
    }
}
