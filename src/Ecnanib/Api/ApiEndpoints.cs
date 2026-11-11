namespace Ecnanib.Api;

/// <summary>
/// The HTTP API, served by the same process as the UI. Feature endpoints join the <c>/api</c> group.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>The path every API endpoint is under.</summary>
    internal const string Prefix = "/api";

    /// <summary>Registers what the API needs besides its endpoints.</summary>
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddExceptionHandler<ApiErrorResponder>();

        return services;
    }

    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(Prefix);

        api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")));

        return endpoints;
    }
}

/// <summary>Liveness answer: the process is up and serving requests.</summary>
public sealed record HealthResponse(string Status);
