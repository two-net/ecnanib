using System.Text.Json;
using System.Text.Json.Serialization;
using Ecnanib.Health.Abstractions;
using Microsoft.AspNetCore.Diagnostics;

namespace Ecnanib.Health;

public static class HealthEndpointRouteBuilderExtensions
{
    public const string HealthPath = "/health";

    /// <summary>camelCase names, lowerCamel enum values ("degraded", "notStarted", "claude"), ISO 8601 instants.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>
    /// Maps <c>GET</c> and <c>HEAD /health</c> (NFR-OBS-01): the <see cref="HealthDocument"/> of the report
    /// <see cref="ISystemHealth"/> gathers, as JSON, 200 for Healthy and Degraded and 503 for Unhealthy, so a reverse
    /// proxy's upstream check or a monitor needs the status code alone. Anonymous on purpose: the document carries
    /// states and times only, never a name, a message or a count that describes users, and the signed-in status page
    /// has the detail. <c>Cache-Control: no-store</c>, every answer being fresh. A HEAD gets the status and the headers
    /// without the body. Any other method is 405 with <c>Allow: GET, HEAD</c>. The status-code pages are off for this
    /// endpoint: a 503 or a 405 is the answer, not an error to re-execute as the not-found page (which, for a POST, would
    /// turn into the antiforgery check's 400).
    /// </summary>
    public static IEndpointConventionBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.Map(HealthPath, async (HttpContext http, ISystemHealth health) =>
        {
            if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
                pages.Enabled = false;
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            {
                http.Response.Headers.Allow = "GET, HEAD";
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            var report = await health.GetAsync(http.RequestAborted).ConfigureAwait(false);
            var document = HealthDocument.From(report);
            var code = StatusCodeFor(document.Status);
            http.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsHead(http.Request.Method))
            {
                http.Response.StatusCode = code;
                http.Response.ContentType = "application/json; charset=utf-8";
                return Results.Empty;
            }

            return Results.Json(document, Json, statusCode: code);
        }).AllowAnonymous();
    }

    internal static int StatusCodeFor(HealthState state) =>
        state == HealthState.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
}
