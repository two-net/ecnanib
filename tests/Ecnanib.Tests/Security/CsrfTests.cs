using System.Net;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Security;

/// <summary>
/// NFR-SEC-03. Each state-changing endpoint needs a token, and a new JSON POST fails here until someone
/// decides. Metadata only says that a token is expected, so the theory below proves one is enforced.
/// </summary>
public sealed class CsrfTests(SharedApp factory, PostgresFixture postgres) : IClassFixture<SharedApp>
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
    [InlineData("/forgot-password")]
    [InlineData("/reset-password")]
    [InlineData("/sign-in/two-factor")]
    public async Task A_post_without_a_token_is_refused(string path)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = new FormUrlEncodedContent([new("Input.Email", "someone@example.test")]);

        using var response = await client.PostAsync(path, form, cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// In Production, because only that runs the exception handler as a visitor gets it. An endpoint's
    /// refusal goes through status pages, which re-execute the request and keep its method, so antiforgery
    /// would read a form that had already failed. That throws: an empty 500, and an Error anyone could write.
    /// </summary>
    [Fact]
    public async Task A_refused_sign_out_gets_a_status_page()
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var app = new AppFactory(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.CreatePublicClient();
        using var form = new FormUrlEncodedContent([new("Input.Email", "someone@example.test")]);

        using var response = await client.PostAsync("/sign-out", form, cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<h1", await response.Content.ReadAsStringAsync(cancellation), StringComparison.Ordinal);
    }

    /// <summary>At Error, anyone could write a database row per request.</summary>
    [Theory]
    [InlineData("/sign-in")]
    [InlineData("/register")]
    [InlineData("/sign-out")]
    [InlineData("/legal/accept")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password")]
    [InlineData("/sign-in/two-factor")]
    public async Task A_refused_post_logs_no_error(string path)
    {
        var cancellation = TestContext.Current.CancellationToken;

        await using var app = new LoggedApp(postgres.NewConnectionString(), environment: Environments.Production);
        using var client = app.App.CreatePublicClient();
        using var form = new FormUrlEncodedContent([new("Input.Email", "someone@example.test")]);

        using var response = await client.PostAsync(path, form, cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await app.StopAsync();

        Assert.Empty(
            from entry in app.FileEvents()
            where entry.GetProperty("Level").GetString() == "Error"
            select entry.GetProperty("RenderedMessage").GetString());
    }

    private static bool ChangesState(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
        || methods.HttpMethods.Except(SafeMethods, StringComparer.OrdinalIgnoreCase).Any();
}
