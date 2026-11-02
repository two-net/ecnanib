using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecnanib.Endpoints;

/// <summary>
/// NFR-OBS-01. Liveness runs no checks, because a database outage must not get the process restarted:
/// a start needs the database, so it would crash-loop. See docs/architecture.md.
/// </summary>
internal static class HealthEndpoints
{
    public const string Prefix = "/health";

    /// <summary>
    /// Not rate limited, so a limit can't fail a probe. DatabaseHealthCheck already runs one database
    /// probe at a time, whoever asks. Anonymous, because a probe has no account, and the answers are
    /// fixed values.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapHealthChecks($"{Prefix}/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync })
            .AllowAnonymous()
            .DisableRateLimiting();
        endpoints
            .MapHealthChecks($"{Prefix}/ready", new HealthCheckOptions { ResponseWriter = WriteAsync })
            .AllowAnonymous()
            .DisableRateLimiting();

        return endpoints;
    }

    /// <summary>
    /// These endpoints are public, and masking only guards log sinks, so this must never write an
    /// entry's Description or Exception: an exception's message can name hosts and databases.
    /// </summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new Dictionary<string, object> { ["status"] = report.Status.ToString() };

        foreach (var (name, value) in report.Entries.Values.SelectMany(entry => entry.Data))
        {
            body[name] = value;
        }

        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
