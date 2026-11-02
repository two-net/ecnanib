using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-03. Nothing here posts a form yet, so this proves the rule that #9's endpoints will meet:
/// each state-changing endpoint needs a token, and a new JSON POST fails here until someone decides.
/// </summary>
public sealed class CsrfTests(SharedApp factory) : IClassFixture<SharedApp>
{
    private static readonly string[] SafeMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace];

    /// <summary>SignalR's own connections carry no form, and health changes nothing whatever the method.</summary>
    private static readonly string[] NotForms = ["/_blazor", "/health"];

    [Fact]
    public void Every_state_changing_endpoint_needs_an_antiforgery_token()
    {
        factory.Start();

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.Contains(endpoints, endpoint => endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is not null);

        var unprotected =
            from endpoint in endpoints
            where ChangesState(endpoint)
                && !NotForms.Any(prefix => endpoint.RoutePattern.RawText!.StartsWith(prefix, StringComparison.Ordinal))
                && endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is not { RequiresValidation: true }
            select endpoint.DisplayName;

        Assert.Empty(unprotected);
    }

    private static bool ChangesState(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
        || methods.HttpMethods.Except(SafeMethods, StringComparer.OrdinalIgnoreCase).Any();
}
