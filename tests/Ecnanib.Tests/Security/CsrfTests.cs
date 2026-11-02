using System.Net;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-03. Each state-changing endpoint needs a token, and a new JSON POST fails here until someone
/// decides. Metadata only says that a token is expected, so the theory below proves one is enforced.
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

    /// <summary>UseAntiforgery only records its result, so an endpoint that reads no form would pass the test above.</summary>
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/register")]
    [InlineData("/sign-out")]
    [InlineData("/legal/accept")]
    public async Task A_post_without_a_token_is_refused(string path)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = new FormUrlEncodedContent([new("Input.Email", "someone@example.test")]);

        using var response = await client.PostAsync(path, form, cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static bool ChangesState(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
        || methods.HttpMethods.Except(SafeMethods, StringComparer.OrdinalIgnoreCase).Any();
}
