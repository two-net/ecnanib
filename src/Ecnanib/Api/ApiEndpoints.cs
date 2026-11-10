namespace Ecnanib.Api;

/// <summary>
/// The HTTP API, served by the same process as the UI. Feature endpoints join the <c>/api</c> group.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")));

        return endpoints;
    }
}

/// <summary>Liveness answer: the process is up and serving requests.</summary>
public sealed record HealthResponse(string Status);
